using RubbishCleaner.Common.Win32;
using RubbishCleaner.Core.Abstractions;

namespace RubbishCleaner.Core.Detection;

/// <summary>基于 <see cref="SignatureVerifier"/> 的实现，带路径级缓存。</summary>
public sealed class FileSignatureProvider : IFileSignatureProvider
{
    private readonly SignatureVerifier _verifier = new();

    public SignatureFacts? Verify(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var info = _verifier.Verify(filePath);
        return new SignatureFacts(
            IsSigned: info.IsSigned,
            IsMicrosoft: info.IsMicrosoft,
            Subject: info.Subject,
            Publisher: SignatureVerifier.ExtractPublisher(info.Subject));
    }

    public string? ComputeSha256(string filePath) => SignatureVerifier.TryComputeSha256(filePath);
}
