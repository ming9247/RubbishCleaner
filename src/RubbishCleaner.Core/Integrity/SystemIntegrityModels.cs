using System.Runtime.InteropServices;

namespace RubbishCleaner.Core.Integrity;

/// <summary>
/// 系统一致性报告（v0.5）——<b>只读报告，永不自动清理</b>。
///
/// <para>为什么这一版只有报告没有清理按钮：v0.5 的两份调研（孤儿 CLSID / 孤儿文件关联）
/// 用真机数据证明「看似孤儿」的键里混着 Windows 按需注册、活软件的升级残留、
/// MSIX 的按需解析，误删的后果不可逆。服务 ImagePath 与「文件/注册表状态不一致」
/// 是仅有的两个<b>可判定</b>方向，但同样只报告——因为「服务表该不该由第三方工具删」
/// 是产品决策，不是技术决策。</para>
///
/// <para>与 v0.4 的关系：v0.4 清的是「点卸载会报错」的 Uninstall 孤儿条目（有 MSI API 背书），
/// 可以清理；v0.5 报告的这两类<b>没有任何 API 背书</b>，所以只展示。</para>
/// </summary>

/// <summary>
/// 一条「服务的可执行文件已不存在」的报告。
///
/// <para>判据：服务的 <c>ImagePath</c> 解析成 Win32 路径后文件不存在。
/// 服务表由 SCM（服务控制管理器）维护，没有权限陷阱、没有"按需注册"——
/// 这是 v0.5 两份调研里唯一判定可靠的方向。</para>
///
/// <para><b>刻意不做 Delete 按钮的原因</b>：服务表里混着驱动（内核形态路径）、
/// svchost 宿主、以及被系统按需启用的功能组件；文件缺失也可能只是"功能未启用"。
/// 报告 + 给出手动命令，处置权完全交给用户（ADR-019 的服务类惯例）。</para>
/// </summary>
public sealed record ServiceOrphanEntry
{
    /// <summary>服务名（注册表键名），如 <c>wuauserv</c>。</summary>
    public required string ServiceName { get; init; }

    /// <summary>显示名（可能为空）。</summary>
    public string? DisplayName { get; init; }

    /// <summary>ImagePath 原始值（未展开），写进证据链便于复核。</summary>
    public required string ImagePathRaw { get; init; }

    /// <summary>解析出的 Win32 路径（展开环境变量、去掉内核前缀），已确认不存在。</summary>
    public required string ResolvedPath { get; init; }

    /// <summary>启动类型（自动/手动/禁用/引导/系统/按需）。</summary>
    public required string StartModeText { get; init; }

    /// <summary>运行状态（运行中/已停止）。</summary>
    public required string StateText { get; init; }

    /// <summary>判定依据（逐条可读）。</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>手动处置指引（如 <c>sc delete &lt;name&gt;</c>），只给命令不代劳。</summary>
    public string ManualCommand => $"sc delete {ServiceName}";

    /// <summary>v0.5 的服务孤儿<b>永远不可自动清理</b>——写死为 false 而不是做成可配置，</summary>
    /// <remarks>防止未来有人把它接上清理按钮（与 v0.3「代码里不存在永久删除分支」同一手法）。</remarks>
    public bool CanAutoClean => false;
}

/// <summary>服务孤儿扫描结果。</summary>
public sealed record ServiceOrphanScanResult
{
    public IReadOnlyList<ServiceOrphanEntry> Entries { get; init; } = [];

    /// <summary>检查过的服务总数（让用户知道"看了多少条才找出这些"）。</summary>
    public int ScannedCount { get; init; }

    /// <summary>因判据跳过的服务数（驱动形态路径、svchost 宿主等，见扫描器注释）。</summary>
    public int SkippedCount { get; init; }

    /// <summary>降级说明：非空表示结果可能不完整。</summary>
    public string? DegradedReason { get; init; }

    public bool IsDegraded => !string.IsNullOrWhiteSpace(DegradedReason);

    public TimeSpan Duration { get; init; }
}

/// <summary>
/// 一条「文件与注册表状态不一致」的报告。
///
/// <para>来源是 v0.4 的真机案例（CC Switch）：文件已被清掉，但 Windows Installer 仍认为产品已安装，
/// 于是「应用和功能」里留着一条点卸载会失败的条目。v0.4 只能清掉它判定的孤儿 Uninstall 条目，
/// 对这种"半死不活"状态没有交代——v0.5 把它显式报告出来。</para>
/// </summary>
public sealed record AppStateIssue
{
    /// <summary>不一致类型。</summary>
    public required AppStateIssueKind Kind { get; init; }

    /// <summary>软件显示名（来自 Uninstall 条目或 MSI 产品名）。</summary>
    public required string DisplayName { get; init; }

    /// <summary>产品码（MSI）；非 MSI 来源时为空。</summary>
    public string? ProductCode { get; init; }

    /// <summary>判定依据（逐条可读）。</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>给用户的建议（自然语言，可包含可复制的手动命令）。</summary>
    public IReadOnlyList<string> Suggestions { get; init; } = [];

    /// <summary>只读报告，永不自动处置。</summary>
    public bool CanAutoFix => false;
}

/// <summary>不一致的类型。</summary>
public enum AppStateIssueKind
{
    /// <summary>MSI 说产品已安装，但 Uninstall 条目记录的 InstallLocation 目录已不存在。</summary>
    InstallLocationMissing,

    /// <summary>MSI 说产品已安装，但缓存的安装包（LocalPackage 指向的 .msi）已被清理。</summary>
    /// <remarks>这条<b>常见且通常无害</b>（很多清理工具会清 C:\Windows\Installer），</remarks>
    /// <remarks>报告里要明确说"通常无害"，避免吓到用户。</remarks>
    CachedPackageMissing,
}

/// <summary>文件与注册表状态一致性的扫描结果。</summary>
public sealed record AppStateScanResult
{
    public IReadOnlyList<AppStateIssue> Entries { get; init; } = [];

    /// <summary>检查过的已安装软件总数。</summary>
    public int CheckedCount { get; init; }

    public string? DegradedReason { get; init; }

    public bool IsDegraded => !string.IsNullOrWhiteSpace(DegradedReason);

    public TimeSpan Duration { get; init; }
}

/// <summary>服务孤儿扫描器（只读）。</summary>
public interface IServiceOrphanScanner
{
    Task<ServiceOrphanScanResult> ScanAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>文件与注册表状态一致性扫描器（只读）。</summary>
public interface IAppStateScanner
{
    Task<AppStateScanResult> ScanAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}