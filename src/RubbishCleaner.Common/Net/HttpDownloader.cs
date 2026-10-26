using System.Net;

namespace RubbishCleaner.Common.Net;

/// <summary>
/// HTTP 文件下载器。
/// 支持超时、进度回调、简单重试。
/// 重试范围：网络异常/超时，以及 5xx、408、429 这类瞬时性 HTTP 失败（指数退避）；
/// 其余 4xx 属于永久失败(路径写错、文件已删除等)，立即返回不重试。
/// </summary>
public class HttpDownloader : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly int _timeoutSeconds;
    private readonly int _maxRetries;

    /// <summary>
    /// 创建下载器实例。
    /// </summary>
    /// <param name="timeoutSeconds">单次请求超时(默认30秒)</param>
    /// <param name="maxRetries">失败重试次数(默认2次)</param>
    public HttpDownloader(int timeoutSeconds = 30, int maxRetries = 2)
    {
        _timeoutSeconds = timeoutSeconds;
        _maxRetries = Math.Max(0, maxRetries);
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
        // GitHub要求User-Agent
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "RubbishCleaner/0.7.0");
    }

    /// <summary>
    /// 下载文件到本地路径。
    /// </summary>
    /// <param name="url">下载URL</param>
    /// <param name="destinationPath">目标文件路径</param>
    /// <param name="progress">进度回调(已下载字节数, 总字节数)</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>下载结果</returns>
    public virtual async Task<DownloadResult> DownloadFileAsync(
        string url,
        string destinationPath,
        IProgress<(long Downloaded, long? Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Exception? lastException = null;
        string? lastHttpError = null;

        for (int attempt = 0; attempt <= _maxRetries; attempt++)
        {
            if (attempt > 0)
            {
                // 重试前等待
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken);
            }

            try
            {
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                
                if (!response.IsSuccessStatusCode)
                {
                    var httpError = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";

                    // 5xx 与 408/429 视为瞬时失败，交给外层指数退避重试；
                    // 其余 4xx 是永久失败(URL 写错、文件已删除)，立即返回不浪费重试。
                    if (IsRetryableStatusCode(response.StatusCode))
                    {
                        lastHttpError = httpError;
                        continue;
                    }

                    return new DownloadResult
                    {
                        Success = false,
                        ErrorMessage = httpError
                    };
                }

                var totalBytes = response.Content.Headers.ContentLength;
                var downloadedBytes = 0L;

                // 确保目标目录存在
                var directory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // 下载到临时文件,完成后再移动
                var tempPath = destinationPath + ".tmp";
                try
                {
                    using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                    using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        var buffer = new byte[8192];
                        int bytesRead;

                        while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                        {
                            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                            downloadedBytes += bytesRead;
                            progress?.Report((downloadedBytes, totalBytes));
                        }
                    }

                    // 移动到最终位置
                    if (File.Exists(destinationPath))
                    {
                        File.Delete(destinationPath);
                    }
                    File.Move(tempPath, destinationPath);

                    return new DownloadResult
                    {
                        Success = true,
                        FilePath = destinationPath,
                        FileSizeBytes = downloadedBytes
                    };
                }
                finally
                {
                    // 清理临时文件
                    if (File.Exists(tempPath))
                    {
                        try { File.Delete(tempPath); } catch { }
                    }
                }
            }
            catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
            {
                return new DownloadResult
                {
                    Success = false,
                    ErrorMessage = "下载已取消"
                };
            }
            catch (Exception ex)
            {
                lastException = ex;
                // 继续重试
            }
        }

        // 可重试的 HTTP 状态码重试耗尽时，返回最后一次的 HTTP 错误(保持 "HTTP {code}: {reason}" 格式)
        if (lastHttpError is not null && lastException is null)
        {
            return new DownloadResult
            {
                Success = false,
                ErrorMessage = lastHttpError
            };
        }

        return new DownloadResult
        {
            Success = false,
            ErrorMessage = $"下载失败(已重试{_maxRetries}次): {lastException?.Message}"
        };
    }

    /// <summary>
    /// 判断状态码是否值得重试：5xx 服务端错误、408 请求超时、429 请求过多属于瞬时失败。
    /// </summary>
    private static bool IsRetryableStatusCode(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code >= 500 || code == 408 || code == 429;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

/// <summary>
/// 下载结果。
/// </summary>
public sealed record DownloadResult
{
    /// <summary>是否成功</summary>
    public required bool Success { get; init; }

    /// <summary>成功时:下载后的文件路径</summary>
    public string? FilePath { get; init; }

    /// <summary>成功时:文件大小(字节)</summary>
    public long FileSizeBytes { get; init; }

    /// <summary>失败时:错误消息</summary>
    public string? ErrorMessage { get; init; }
}
