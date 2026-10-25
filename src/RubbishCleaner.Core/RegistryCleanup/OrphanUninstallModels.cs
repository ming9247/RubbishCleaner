using Microsoft.Win32;

namespace RubbishCleaner.Core.RegistryCleanup;

/// <summary>
/// 一条「孤儿 Uninstall 注册表条目」（v0.4）。
///
/// <b>什么叫孤儿</b>：Windows Installer 自己说"这个产品没装"（
/// <c>MsiQueryProductState</c> 返回 ABSENT 且 <c>MsiEnumProducts</c> 里没有它），
/// 但「应用和功能」列表里还留着它的条目——用户点卸载会报错 1639，是纯噪音。
///
/// <b>判据为什么不能用"注册表键在不在"</b>：2026-10-25 真机调研发现
/// CC Switch 产品完全有效（state=INSTALLED、LocalPackage 存在），
/// 但 <c>Classes\Installer\Products\{GUID}</c> 与
/// <c>UserData\&lt;SID&gt;\Products\{GUID}</c> <b>都不存在</b>，
/// <c>reg query /s</c> 也搜不到任何含该 GUID 的键。
/// 若按"Products 键不存在 = 僵尸"判定，会把一个好好安装着的软件判成孤儿。
///
/// 因此判定只能<b>信 Windows Installer 自己的 API</b>。
/// </summary>
public sealed record OrphanUninstallEntry
{
    /// <summary>产品 GUID（含花括号），同时也是注册表子键名。</summary>
    public required string ProductCode { get; init; }

    /// <summary>DisplayName（可能为空或含脏数据）。</summary>
    public required string DisplayName { get; init; }

    public string? DisplayVersion { get; init; }

    public string? Publisher { get; init; }

    public string? InstallLocation { get; init; }

    /// <summary>所在注册表 hive 与视图，删除时必须用同一个。</summary>
    public required RegistryHive Hive { get; init; }

    public required RegistryView View { get; init; }

    /// <summary>Uninstall 根键路径（不含产品 GUID）。</summary>
    public required string UninstallRootPath { get; init; }

    /// <summary>人类可读的完整路径，例如 <c>HKLM\...\Uninstall\{GUID}</c>。</summary>
    public string FullPath => $@"{HiveName}\{UninstallRootPath}\{ProductCode}";

    public string HiveName => Hive switch
    {
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.CurrentUser => "HKCU",
        _ => Hive.ToString(),
    };

    /// <summary><c>MsiQueryProductState</c> 的原始返回值，写进证据链便于排查。</summary>
    public uint MsiState { get; init; }

    /// <summary>判定依据（逐条可读文字），直接展示给用户。</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>
    /// MSI 状态的中文说明（用于 UI 展示与证据链）。
    ///
    /// 这里转发一次而不是让 UI 直接调 <c>MsiInterop</c>：
    /// 后者是 <c>internal</c>（P/Invoke 细节不该越过 Core 边界），
    /// 而"状态怎么读"是领域知识，属于模型层。
    /// </summary>
    public string MsiStateText => MsiInterop.DescribeState(MsiState);

    /// <summary>不能删除的原因；null 表示允许。</summary>
    public string? BlockReason { get; init; }

    public bool CanDelete => BlockReason is null;

    public override string ToString() => $"{DisplayName} ({ProductCode})";
}

/// <summary>注册表孤儿扫描的进度。</summary>
public sealed record OrphanScanProgress(string Stage, int Completed = 0, int Total = 0, string? Detail = null);

/// <summary>一次注册表孤儿扫描的结果。</summary>
public sealed class OrphanScanResult
{
    public IReadOnlyList<OrphanUninstallEntry> Entries { get; init; } = [];

    /// <summary>扫描过的 MSI 条目总数（用于让用户知道"看了多少条才找出这些"）。</summary>
    public int ScannedCount { get; init; }

    /// <summary>降级说明：非空表示结果可能不完整。</summary>
    public string? DegradedReason { get; init; }

    public bool IsDegraded => !string.IsNullOrWhiteSpace(DegradedReason);

    public TimeSpan Duration { get; init; }

    public int CleanableCount => Entries.Count(e => e.CanDelete);
}

/// <summary>孤儿注册表条目的扫描器。</summary>
public interface IOrphanUninstallScanner
{
    /// <summary>扫描；只读，不改任何注册表。</summary>
    Task<OrphanScanResult> ScanAsync(
        IProgress<OrphanScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}