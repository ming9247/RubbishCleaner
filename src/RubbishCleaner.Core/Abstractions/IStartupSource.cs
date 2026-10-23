using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Abstractions;

/// <summary>扫描上下文：向数据源提供环境与进度回调。</summary>
public sealed class ScanContext
{
    public required bool IsElevated { get; init; }

    public required IProgress<ScanProgress>? Progress { get; init; }

    public required CancellationToken CancellationToken { get; init; }

    /// <summary>签名校验器（带缓存），数据源共享以避免重复解析。</summary>
    public required IFileSignatureProvider Signatures { get; init; }
}

/// <summary>自启动数据源。</summary>
public interface IStartupSource
{
    StartupSourceKind Kind { get; }

    /// <summary>是否需要管理员权限才能获得完整结果。</summary>
    bool RequiresElevation { get; }

    Task<SourceResult> CollectAsync(ScanContext context);
}

/// <summary>文件签名校验能力。</summary>
public interface IFileSignatureProvider
{
    /// <summary>校验单个文件；返回 null 表示无法判定。</summary>
    SignatureFacts? Verify(string? filePath);

    /// <summary>计算 SHA256（小写十六进制）；失败返回 null。</summary>
    string? ComputeSha256(string filePath);
}

/// <summary>签名事实。</summary>
public sealed record SignatureFacts(bool IsSigned, bool IsMicrosoft, string? Subject, string? Publisher);

/// <summary>进程事实提供者。</summary>
public interface IProcessFactsProvider
{
    /// <summary>采集指定可执行文件的运行期事实；文件未运行时返回 <see cref="ProcessFacts.IsRunning"/> = false 的事实。</summary>
    ProcessFacts GetFacts(string? executablePath, string fallbackName);

    /// <summary>当前是否以管理员身份运行。</summary>
    bool IsElevated { get; }
}

/// <summary>桌面 / 开始菜单快捷方式索引，用于判断"是否有可见入口"。</summary>
public interface IShortcutIndex
{
    /// <summary>桌面是否存在于目标可执行文件相关的快捷方式。</summary>
    bool HasDesktopShortcut(string? executablePath, string displayName);

    /// <summary>开始菜单是否存在于目标可执行文件相关的快捷方式。</summary>
    bool HasStartMenuEntry(string? executablePath, string displayName);
}
