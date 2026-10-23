namespace RubbishCleaner.Core.Models;

/// <summary>
/// 以可执行文件为单位聚合后的"一个软件"，是评分与展示的最小单元。
/// 一个软件可能有多个自启动点位（例如同时存在 Run 键与服务），按点位评分会导致重复展示与分数虚高。
/// </summary>
public sealed class SoftwareProfile
{
    /// <summary>归一化后的分组键（小写可执行文件路径，或 source:name 兜底）。</summary>
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    public string? ExecutablePath { get; init; }

    public IReadOnlyList<StartupEntry> StartupEntries { get; init; } = [];

    /// <summary>运行期事实；为 null 表示既未运行也无法获取文件信息。</summary>
    public ProcessFacts? LiveProcess { get; init; }

    public bool HasDesktopShortcut { get; init; }

    public bool HasStartMenuEntry { get; init; }

    public TrustVerdict Trust { get; init; } = TrustVerdict.Unknown;

    /// <summary>确认表中命中的条目说明（用于 UI 展示）。</summary>
    public string? TrustReason { get; init; }

    public RiskScore Score { get; set; } = RiskScore.Empty;

    public IReadOnlyList<Evidence> Evidences { get; set; } = [];

    /// <summary>所有启动方式的可读描述，例如 "注册表 Run（当前用户）、Windows 服务"。</summary>
    public string StartupSummary => StartupEntries.Count == 0
        ? "无"
        : string.Join("、", StartupEntries.Select(e => e.Source.ToDisplayName()).Distinct());

    public string? Publisher => LiveProcess?.Publisher ?? StartupEntries
        .Select(e => e.Publisher)
        .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));

    public bool IsRunning => LiveProcess?.IsRunning == true;

    /// <summary>
    /// 是否为"纯服务型"程序：所有自启动点都是 Windows 服务。
    /// 服务按设计就没有窗口、桌面图标与开始菜单入口，若照常计分会把
    /// NVIDIA / Intel / Autodesk 这类正规驱动服务全部推成高风险（实测踩过）。
    /// </summary>
    public bool IsServiceOnly => StartupEntries.Count > 0
        && StartupEntries.All(e => e.Source == StartupSourceKind.WindowsService);

    public override string ToString() => $"{DisplayName} ({Score.Total}/{Score.Level})";
}
