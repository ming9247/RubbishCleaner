using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace RubbishCleaner.TrustE2E;

/// <summary>
/// TLS 连通性诊断。只在 <c>--tls-diag</c> 模式下运行，用于把
/// 「The SSL connection could not be established」这类顶层消息挖到最内层原因。
/// </summary>
internal static class TlsDiagnostics
{
    public static async Task<int> RunAsync(string url)
    {
        Console.WriteLine("=== TLS 诊断 ===");
        Console.WriteLine($"URL      : {url}");
        Console.WriteLine($"运行时   : .NET {Environment.Version} / {Environment.OSVersion}");
        Console.WriteLine($"64位进程 : {Environment.Is64BitProcess}");
        Console.WriteLine();

        Console.WriteLine("[1] 相关环境变量");
        foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "SSL_CERT_FILE", "SSL_CERT_DIR", "DOTNET_SYSTEM_NET_HTTP_USESOCKETSHTTPHANDLER", "DOTNET_SYSTEM_NET_SECURITY_NOREVOCATIONCHECKBYDEFAULT" })
        {
            Console.WriteLine($"  {key}={Environment.GetEnvironmentVariable(key) ?? "<null>"}");
        }

        var uri = new Uri(url);
        Console.WriteLine();
        Console.WriteLine($"[2] DNS 解析 {uri.Host}");
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(uri.Host);
            foreach (var a in addresses)
            {
                Console.WriteLine($"  {a.AddressFamily}: {a}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  DNS 失败: {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine($"[3] 原始 SslStream 握手 {uri.Host}:{uri.Port}");
        var callbackErrors = new List<string>();
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(uri.Host, uri.Port);
            Console.WriteLine($"  TCP 已连接: {tcp.Client.RemoteEndPoint}");
            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (sender, cert, chain, errors) =>
            {
                callbackErrors.Add(errors.ToString());
                Console.WriteLine($"  证书回调: errors={errors} subject={cert?.Subject ?? "<null>"} issuer={cert?.Issuer ?? "<null>"}");
                if (chain is not null)
                {
                    foreach (var element in chain.ChainElements)
                    {
                        Console.WriteLine($"    链: {element.Certificate.Subject} | {element.Certificate.Issuer}");
                    }
                    foreach (var status in chain.ChainStatus)
                    {
                        Console.WriteLine($"    链状态: {status.Status} {status.StatusInformation?.Trim()}");
                    }
                }
                return errors == SslPolicyErrors.None;
            });
            await ssl.AuthenticateAsClientAsync(uri.Host);
            Console.WriteLine($"  握手成功: {ssl.SslProtocol} / {ssl.NegotiatedCipherSuite}");
            Console.WriteLine($"  证书: {ssl.RemoteCertificate?.Subject}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  握手失败: {Describe(ex)}");
        }

        Console.WriteLine();
        Console.WriteLine("[4] HttpClient.GetAsync（默认 handler）");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Add("User-Agent", "RubbishCleaner-e2e-probe/0.8");
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            Console.WriteLine($"  HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  失败: {Describe(ex)}");
        }

        Console.WriteLine();
        Console.WriteLine("[5] HttpClient + SocketsHttpHandler 关闭证书吊销检查");
        try
        {
            var handler = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                    {
                        Console.WriteLine($"  回调 errors={errors}");
                        return errors == SslPolicyErrors.None;
                    },
                },
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Add("User-Agent", "RubbishCleaner-e2e-probe/0.8");
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            Console.WriteLine($"  HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  失败: {Describe(ex)}");
        }

        Console.WriteLine();
        Console.WriteLine("[6] 结论");
        Console.WriteLine(callbackErrors.Count == 0
            ? "  证书校验回调一次都没被调用 → 失败发生在更早的 TLS 握手阶段"
            : $"  证书校验回调被调用 {callbackErrors.Count} 次：{string.Join(" | ", callbackErrors)}");
        return 0;
    }

    private static string Describe(Exception ex)
    {
        var lines = new List<string>();
        var current = ex;
        var depth = 0;
        while (current is not null && depth < 8)
        {
            lines.Add($"[{depth}] {current.GetType().FullName}: {current.Message}");
            if (current is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    lines.Add($"    agg-inner: {inner.GetType().FullName}: {inner.Message}");
                }
            }
            current = current.InnerException;
            depth++;
        }
        return string.Join("\n      ", lines);
    }
}
