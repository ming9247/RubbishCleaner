using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Scoring;

/// <summary>评分权重（与产品文档 7.1.3 一致，集中在此便于调参与审计）。</summary>
public sealed class ScoringWeights
{
    public int Autostart { get; init; } = 30;

    public int NoVisibleWindow { get; init; } = 25;

    public int NoDesktopIcon { get; init; } = 15;

    public int NoStartMenu { get; init; } = 15;

    /// <summary>v0.1 不启用：托盘枚举无公开 API（ADR-003）。</summary>
    public int NoTray { get; init; } = 15;

    public int Unsigned { get; init; } = 10;

    public int Masquerade { get; init; } = 10;

    /// <summary>
    /// 失效启动项（目标文件已不存在）。
    /// 权重为 0：它不是"风险"，而是"残留"——程序已经不在了，没有在跑，
    /// 也不会再影响系统。给它加分只会制造噪声。它的价值在于被<b>单独标记出来供清理</b>，
    /// 见 <see cref="SoftwareProfile.IsOrphaned"/>。
    /// </summary>
    public int OrphanStartup { get; init; } = 0;

    public int SuspiciousThreshold { get; init; } = 41;

    public int HighRiskThreshold { get; init; } = 71;

    public static ScoringWeights Default { get; } = new();
}

/// <summary>
/// 启发式评分器：把"是/否"特征转成加权分数，避免单一特征直接定性。
/// </summary>
public sealed class RiskScorer
{
    public const string RuleAutostart = "AUTOSTART";
    public const string RuleNoVisibleWindow = "NO_VISIBLE_WINDOW";
    public const string RuleNoDesktopIcon = "NO_DESKTOP_ICON";
    public const string RuleNoStartMenu = "NO_START_MENU";
    public const string RuleNoTray = "NO_TRAY";
    public const string RuleUnsigned = "UNSIGNED";
    public const string RuleMasquerade = "MASQUERADE";
    public const string RuleOrphanStartup = "ORPHAN_STARTUP";

    private readonly ScoringWeights _weights;

    public RiskScorer(ScoringWeights? weights = null) => _weights = weights ?? ScoringWeights.Default;

    /// <summary>对单个软件画像打分。信任表判定由调用方写入 <see cref="SoftwareProfile.Trust"/>。</summary>
    public RiskScore Score(SoftwareProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var items = new List<ScoreItem>(8);
        var process = profile.LiveProcess;
        var isRunning = process?.IsRunning == true;

        // 纯服务型程序（只通过 Windows 服务自启动）不参与"无窗口 / 无桌面图标 / 无开始菜单"三项：
        // 这三项对服务而言是设计使然，计分只会把正规驱动服务误判为高风险。
        var serviceOnly = profile.IsServiceOnly;

        // 失效启动项：目标文件已不存在，属于卸载/升级残留。
        // 这类记录不能套用针对"活着的程序"的规则——
        // 程序根本不存在，"未签名""无窗口""无图标"全都是伪信号，只会把它推成假的高风险，
        // 从而掩盖真正的信息："这条该清理"。
        var orphan = profile.IsOrphaned;

        // 1) 自启动：本工具的输入本身就是自启动项，因此几乎必然命中；保留规则是为了报告可读性。
        Add(items, RuleAutostart, "存在自启动项", _weights.Autostart, profile.StartupEntries.Count > 0);

        // 2) 无可见窗口：只在进程确实运行时才判定，未运行时无法证明"无界面"。
        //    未运行时标记为"不适用"，而不是"未命中"——否则会把"无法判断"错算成"已排除嫌疑"。
        var noVisibleWindow = isRunning && !process!.HasVisibleWindow;
        Add(items, RuleNoVisibleWindow, "后台常驻且无可见窗口", _weights.NoVisibleWindow, noVisibleWindow,
            notApplicable: !isRunning || serviceOnly || orphan);

        // 3) 无桌面图标
        Add(items, RuleNoDesktopIcon, "桌面无快捷方式", _weights.NoDesktopIcon,
            !profile.HasDesktopShortcut, notApplicable: serviceOnly || orphan);

        // 4) 无开始菜单项
        Add(items, RuleNoStartMenu, "开始菜单无入口", _weights.NoStartMenu,
            !profile.HasStartMenuEntry, notApplicable: serviceOnly || orphan);

        // 5) 无托盘图标：v0.1 无法可靠判定，标记为"不适用"，避免误伤（ADR-003）。
        Add(items, RuleNoTray, "无托盘图标（暂无法判定）", _weights.NoTray, false, notApplicable: true);

        // 6) 未签名 / 发行商未知。文件已不存在时无从校验签名，不能算作"未签名"。
        var unsigned = !orphan && (process is null || !process.SignatureValid);
        Add(items, RuleUnsigned, "数字签名缺失或发行商未知", _weights.Unsigned, unsigned, notApplicable: orphan);

        // 7) 进程名伪装系统进程
        var nameForCheck = process?.ProcessName ?? Path.GetFileNameWithoutExtension(profile.ExecutablePath ?? profile.DisplayName);
        var masquerade = NameSimilarity.LooksMasquerading(nameForCheck, out var matched);
        Add(items, RuleMasquerade, $"进程名伪装系统进程{FormatMatched(matched)}", _weights.Masquerade, masquerade);

        // 8) 失效启动项：权重 0（见 ScoringWeights.OrphanStartup）。
        //    不计分，但要让 UI 与报告能明确告诉用户"这条该清理"。
        Add(items, RuleOrphanStartup, "启动项目标文件已不存在（可清理）", _weights.OrphanStartup, orphan);

        var raw = items.Sum(i => i.Contribution);
        var total = Math.Clamp(raw, 0, 100);

        var level = Classify(total);

        // 白名单压制：微软签名 / 系统目录等命中白名单时强制归为正常，避免误杀正规软件。
        var suppressed = false;
        if (profile.Trust == TrustVerdict.Safe && level != RiskLevel.Normal)
        {
            level = RiskLevel.Normal;
            suppressed = true;
        }
        else if (profile.Trust == TrustVerdict.Malicious)
        {
            level = RiskLevel.HighRisk;
        }

        return new RiskScore(total, level, items, suppressed);
    }

    public RiskLevel Classify(int total) => total switch
    {
        _ when total >= _weights.HighRiskThreshold => RiskLevel.HighRisk,
        _ when total >= _weights.SuspiciousThreshold => RiskLevel.Suspicious,
        _ => RiskLevel.Normal,
    };

    private static string FormatMatched(string? matched)
        => string.IsNullOrWhiteSpace(matched) ? string.Empty : $"（疑似 {matched}）";

    private static void Add(
        List<ScoreItem> items,
        string ruleId,
        string description,
        int weight,
        bool hit,
        bool notApplicable = false)
        => items.Add(new ScoreItem(ruleId, description, weight, hit, notApplicable));
}
