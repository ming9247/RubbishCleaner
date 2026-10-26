using System.Net;
using System.Net.Sockets;
using System.Text;
using RubbishCleaner.Common.Net;
using Xunit;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// HttpDownloader 重试策略单元测试(v0.8)。
/// 覆盖实测缺陷 3：原先只重试网络异常，5xx/408/429 这类瞬时 HTTP 失败会直接返回。
/// 现在要求：5xx、408、429 参与指数退避重试，其余 4xx 视为永久失败立即返回。
/// 测试用本机回环 TcpListener 起一个极简 HTTP 服务，真实走 DownloadFileAsync 的重试循环。
/// </summary>
public class HttpDownloaderRetryTests
{
    [Theory]
    [InlineData(503)] // 5xx 服务端错误
    [InlineData(408)] // 请求超时
    [InlineData(429)] // 请求过多
    public async Task DownloadFileAsync_WhenTransientStatusThenSuccess_RetriesAndSucceeds(int transientStatusCode)
    {
        // Arrange：第一次返回瞬时失败状态码，第二次返回 200 + 正文
        const string body = @"{""version"":2,""whitelist"":[],""blacklist"":[]}";
        var responses = new (int StatusCode, string Body)[]
        {
            (transientStatusCode, string.Empty),
            (200, body)
        };

        using var server = new LoopbackHttpServer(responses);
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var destinationPath = Path.Combine(tempDir, "trust-table.json");

        using var downloader = new HttpDownloader(5, 2);

        try
        {
            // Act
            var result = await downloader.DownloadFileAsync(server.Url, destinationPath);

            // Assert：重试一次后成功，落盘内容为 200 的正文
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(destinationPath, result.FilePath);
            Assert.Equal((long)body.Length, result.FileSizeBytes);
            Assert.Equal(2, server.RequestCount);
            Assert.Equal(body, await File.ReadAllTextAsync(destinationPath));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task DownloadFileAsync_WhenNotFound_DoesNotRetry()
    {
        // Arrange：404 属于永久失败，应立即返回且只请求一次
        using var server = new LoopbackHttpServer(new[] { (404, string.Empty) });
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var destinationPath = Path.Combine(tempDir, "trust-table.json");

        using var downloader = new HttpDownloader(5, 2);

        try
        {
            // Act
            var result = await downloader.DownloadFileAsync(server.Url, destinationPath);

            // Assert：失败信息保持 "HTTP 404: ..." 格式，且没有重试
            Assert.False(result.Success);
            Assert.Contains("HTTP 404", result.ErrorMessage);
            Assert.Equal(1, server.RequestCount);
            Assert.False(File.Exists(destinationPath));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task DownloadFileAsync_WhenTransientStatusExhaustsRetries_ReturnsLastHttpError()
    {
        // Arrange：服务端一直 503，maxRetries=1 → 共请求 2 次
        using var server = new LoopbackHttpServer(new[] { (503, string.Empty) });
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var destinationPath = Path.Combine(tempDir, "trust-table.json");

        using var downloader = new HttpDownloader(5, 1);

        try
        {
            // Act
            var result = await downloader.DownloadFileAsync(server.Url, destinationPath);

            // Assert：重试耗尽后返回最后一次 HTTP 错误，而不是空消息
            Assert.False(result.Success);
            Assert.Contains("HTTP 503", result.ErrorMessage);
            Assert.Equal(2, server.RequestCount);
            Assert.False(File.Exists(destinationPath));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    /// 极简回环 HTTP 服务：按请求顺序返回预设响应，并统计请求次数。
    /// 预设用尽后重复最后一条响应。
    /// </summary>
    private sealed class LoopbackHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly (int StatusCode, string Body)[] _responses;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private int _requestCount;

        public LoopbackHttpServer((int StatusCode, string Body)[] responses)
        {
            _responses = responses;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _acceptLoop = AcceptLoopAsync();
        }

        public string Url
        {
            get
            {
                var endpoint = (IPEndPoint)_listener.LocalEndpoint;
                return $"http://127.0.0.1:{endpoint.Port}/trust-table.json";
            }
        }

        public int RequestCount => Volatile.Read(ref _requestCount);

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                using (client)
                {
                    await HandleAsync(client);
                }
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            var index = Interlocked.Increment(ref _requestCount) - 1;
            if (index >= _responses.Length)
            {
                index = _responses.Length - 1;
            }

            var (statusCode, body) = _responses[index];
            var bodyBytes = Encoding.UTF8.GetBytes(body);

            try
            {
                var stream = client.GetStream();

                // 读到请求头结束(\r\n\r\n)即可，GET 无正文
                var buffer = new byte[1024];
                var requestBuilder = new StringBuilder();
                var headerComplete = false;
                while (requestBuilder.Length < 8192)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), _cts.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    requestBuilder.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    if (requestBuilder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        headerComplete = true;
                        break;
                    }
                }

                if (!headerComplete)
                {
                    return;
                }

                var header = $"HTTP/1.1 {statusCode} {ReasonPhrase(statusCode)}\r\n" +
                             "Content-Type: application/json\r\n" +
                             $"Content-Length: {bodyBytes.Length}\r\n" +
                             "Connection: close\r\n" +
                             "\r\n";
                var headerBytes = Encoding.ASCII.GetBytes(header);

                await stream.WriteAsync(headerBytes, _cts.Token);
                if (bodyBytes.Length > 0)
                {
                    await stream.WriteAsync(bodyBytes, _cts.Token);
                }

                await stream.FlushAsync(_cts.Token);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                // 服务已停止，忽略
            }
            catch (IOException)
            {
                // 客户端提前断开，忽略
            }
            catch (ObjectDisposedException)
            {
                // 服务已停止，忽略
            }
        }

        private static string ReasonPhrase(int statusCode) => statusCode switch
        {
            200 => "OK",
            404 => "Not Found",
            408 => "Request Timeout",
            429 => "Too Many Requests",
            503 => "Service Unavailable",
            _ => "Error"
        };

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _acceptLoop.GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // 关闭过程中的异常忽略
            }

            _cts.Dispose();
        }
    }
}
