using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RubbishCleaner.Common.Win32;

/// <summary>文件签名信息。</summary>
public sealed record SignatureInfo(
    bool IsSigned,
    string? Subject,
    string? Issuer,
    bool IsMicrosoft,
    string? Error,
    string Method = "none")
{
    public static readonly SignatureInfo Unsigned = new(false, null, null, false, null);
}

/// <summary>
/// Authenticode 签名校验（带路径级缓存）。
///
/// 关键设计：<b>必须同时识别内嵌签名与目录签名（catalog）</b>。
/// 只用 <see cref="X509Certificate.CreateFromSignedFile(string)"/> 会把 Windows 自带的
/// <c>spoolsv.exe</c>、<c>SearchIndexer.exe</c> 等系统组件判成"无签名"，进而误报为高风险。
/// 详见 ADR-009。
/// </summary>
public sealed class SignatureVerifier
{
    /// <summary>目录签名取不到证书主体时使用的占位说明。</summary>
    public const string CatalogSubjectPlaceholder = "CN=Microsoft Windows (catalog)";

    private readonly ConcurrentDictionary<string, SignatureInfo> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string SystemRoot =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>校验并缓存结果；同一路径只解析一次。</summary>
    public SignatureInfo Verify(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return SignatureInfo.Unsigned;
        }

        return _cache.GetOrAdd(filePath, static path =>
        {
            try
            {
                if (!File.Exists(path))
                {
                    return new SignatureInfo(false, null, null, false, "文件不存在");
                }

                // 1) 内嵌签名：能直接读到证书主体，信息最完整。
                var embedded = TryReadEmbedded(path);
                if (embedded is not null)
                {
                    return embedded;
                }

                // 2) 目录签名：Windows 系统组件的常见形态。
                var catalog = AuthenticodeVerifier.Verify(path);
                if (catalog.IsValid)
                {
                    // 目录签名无法直接取到证书主体，因此用"是否位于 %SystemRoot%"作为微软归属判据。
                    // 这是刻意的保守近似：非系统目录的文件即使有目录签名也不会被当成微软。
                    var isMicrosoft = IsUnderSystemRoot(path);
                    return new SignatureInfo(
                        IsSigned: true,
                        Subject: isMicrosoft ? CatalogSubjectPlaceholder : null,
                        Issuer: null,
                        IsMicrosoft: isMicrosoft,
                        Error: null,
                        Method: "catalog");
                }

                return SignatureInfo.Unsigned;
            }
            catch (Exception ex)
            {
                return new SignatureInfo(false, null, null, false, ex.Message);
            }
        });
    }

    private static SignatureInfo? TryReadEmbedded(string path)
    {
        try
        {
            using var certificate = X509Certificate.CreateFromSignedFile(path);
            using var certificate2 = new X509Certificate2(certificate);
            var subject = certificate2.Subject;

            return new SignatureInfo(
                IsSigned: true,
                Subject: subject,
                Issuer: certificate2.Issuer,
                IsMicrosoft: IsMicrosoftSubject(subject),
                Error: null,
                Method: "embedded");
        }
        catch (CryptographicException)
        {
            // 没有内嵌签名：交给目录签名校验，不能在这里就判为"未签名"。
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsUnderSystemRoot(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.StartsWith(SystemRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>从 Subject 中提取发行商可读名（O= 优先，其次 CN=）。</summary>
    public static string? ExtractPublisher(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        if (string.Equals(subject, CatalogSubjectPlaceholder, StringComparison.Ordinal))
        {
            return "Microsoft Windows（系统目录签名）";
        }

        var organization = ExtractField(subject, "O");
        if (!string.IsNullOrWhiteSpace(organization))
        {
            return organization;
        }

        var commonName = ExtractField(subject, "CN");
        return string.IsNullOrWhiteSpace(commonName) ? null : commonName;
    }

    public static bool IsMicrosoftSubject(string? subject)
        => subject is not null
           && (subject.Contains("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
               || subject.Contains("CN=Microsoft Windows", StringComparison.OrdinalIgnoreCase)
               || subject.Contains("O=Microsoft Windows", StringComparison.OrdinalIgnoreCase));

    /// <summary>计算文件 SHA256（小写十六进制），供确认表精确匹配使用。</summary>
    public static string? TryComputeSha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractField(string subject, string field)
    {
        // 形如 "CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, ..."
        foreach (var part in subject.Split(','))
        {
            var trimmed = part.Trim();
            var index = trimmed.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            if (trimmed.AsSpan(0, index).Trim().Equals(field, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(index + 1)..].Trim().Trim('"');
            }
        }

        return null;
    }
}
