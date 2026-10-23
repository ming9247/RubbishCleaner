using System.Diagnostics;
using RubbishCleaner.Common.Win32;
using RubbishCleaner.Core.Abstractions;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Detection;

/// <summary>
/// 进程事实采集器。
/// 关键点：窗口信息通过一次 <see cref="WindowInspector.Capture"/> 全量枚举后查表，
/// 避免对每个进程重复枚举窗口。
/// </summary>
public sealed class ProcessFactsProvider : IProcessFactsProvider
{
    private readonly SignatureVerifier _verifier;
    private readonly Lazy<ProcessSnapshot> _snapshot;

    public ProcessFactsProvider(SignatureVerifier? verifier = null)
    {
        _verifier = verifier ?? new SignatureVerifier();
        // 延迟到首次访问才枚举，避免只做文件校验时浪费一次全量进程遍历。
        _snapshot = new Lazy<ProcessSnapshot>(ProcessSnapshot.Capture, isThreadSafe: true);
    }

    public bool IsElevated => PrivilegeHelper.IsElevated;

    public ProcessFacts GetFacts(string? executablePath, string fallbackName)
    {
        var signature = _verifier.Verify(executablePath);
        var publisher = SignatureVerifier.ExtractPublisher(signature.Subject);
        var description = TryGetFileDescription(executablePath);

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return ProcessFacts.NotRunning(fallbackName, null) with
            {
                SignatureValid = signature.IsSigned,
                IsMicrosoftSigned = signature.IsMicrosoft,
                SignerSubject = signature.Subject,
                Publisher = publisher,
                FileDescription = description,
            };
        }

        var snapshot = _snapshot.Value;
        if (!snapshot.ByPath.TryGetValue(executablePath, out var info))
        {
            // 进程未运行：用文件版本信息里的原始名称（而非文件路径），
            // 否则 "SearchIndexer.exe" 会被当成 "SearchIndexer" 而错过伪装检测。
            var staticName = ResolveStaticName(executablePath, description, fallbackName);
            return ProcessFacts.NotRunning(staticName, executablePath) with
            {
                SignatureValid = signature.IsSigned,
                IsMicrosoftSigned = signature.IsMicrosoft,
                SignerSubject = signature.Subject,
                Publisher = publisher,
                FileDescription = description,
            };
        }

        return new ProcessFacts(
            ProcessId: info.Id,
            ProcessName: info.Name,
            ExecutablePath: executablePath,
            IsRunning: true,
            HasVisibleWindow: info.HasVisibleWindow,
            HasTaskbarButton: info.HasTaskbarButton,
            SignatureValid: signature.IsSigned,
            IsMicrosoftSigned: signature.IsMicrosoft,
            SignerSubject: signature.Subject,
            Publisher: publisher,
            FileDescription: description);
    }

    /// <summary>
    /// 未运行进程的"真实进程名"推断：优先用文件版本信息里的原始文件名
    /// （<c>SearchIndexer.exe</c> → <c>SearchIndexer</c>），拿不到再退回调用方给的名称。
    /// 这直接影响"是否伪装系统进程"的判定准确性。
    /// </summary>
    private static string ResolveStaticName(string executablePath, string? description, string fallbackName)    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            var original = info.OriginalFilename;
            if (!string.IsNullOrWhiteSpace(original))
            {
                var name = Path.GetFileNameWithoutExtension(original.Trim());
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }
        catch
        {
            // 忽略：退回 fallbackName
        }

        return fallbackName;
    }

    private static string? TryGetFileDescription(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileDescription;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>一次性的进程 + 窗口快照。</summary>
    private sealed class ProcessSnapshot
    {
        public required Dictionary<string, WindowEntry> ByPath { get; init; }

        public readonly record struct WindowEntry(int Id, string Name, bool HasVisibleWindow, bool HasTaskbarButton);

        public static ProcessSnapshot Capture()
        {
            var windows = WindowInspector.Capture();
            var byPath = new Dictionary<string, WindowEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var process in ProcessHelper.Enumerate())
            {
                if (string.IsNullOrWhiteSpace(process.ExecutablePath))
                {
                    continue;
                }

                var hasWindow = windows.TryGetValue(process.Id, out var facts);
                var isUwpHost = WindowInspector.IsUwpFrameHost(process.Name);

                // 同一 exe 可能有多开：只要有一个实例有窗口，就认为"有界面"。
                var entry = new WindowEntry(
                    process.Id,
                    process.Name,
                    HasVisibleWindow: hasWindow || isUwpHost,
                    HasTaskbarButton: hasWindow && facts.HasTaskbarButton);

                if (byPath.TryGetValue(process.ExecutablePath, out var existing))
                {
                    if (existing.HasVisibleWindow && !entry.HasVisibleWindow)
                    {
                        continue;
                    }

                    byPath[process.ExecutablePath] = new WindowEntry(
                        entry.Id,
                        entry.Name,
                        existing.HasVisibleWindow || entry.HasVisibleWindow,
                        existing.HasTaskbarButton || entry.HasTaskbarButton);
                }
                else
                {
                    byPath[process.ExecutablePath] = entry;
                }
            }

            return new ProcessSnapshot { ByPath = byPath };
        }
    }
}
