using System.Windows.Media;
using RubbishCleaner.App.Infrastructure;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.App.ViewModels;

/// <summary>列表中一行的视图模型（包装 <see cref="SoftwareProfile"/>，UI 不直接接触领域模型）。</summary>
public sealed class SoftwareItemViewModel : ObservableObject
{
    private bool _isExpanded;

    public SoftwareItemViewModel(SoftwareProfile profile)
    {
        Profile = profile;
        Evidences = profile.Evidences.Select(e => new EvidenceItemViewModel(e)).ToList();

        // 只展示真正计分的规则（"不适用"的项不算命中，否则会误导用户）
        ScoreItems = profile.Score.Items
            .Where(i => i.Hit && !i.NotApplicable)
            .Select(i => $"+{i.Weight} {i.Description}")
            .ToList();

        Icon = IconCache.Get(profile.ExecutablePath);
    }

    public SoftwareProfile Profile { get; }

    /// <summary>程序图标；取不到时为 null（UI 显示占位空白）。</summary>
    public ImageSource? Icon { get; }

    public string DisplayName => Profile.DisplayName;

    public string? ExecutablePath => Profile.ExecutablePath;

    public RiskLevel Level => Profile.Score.Level;

    public string LevelText => Level.ToDisplayName();

    public int TotalScore => Profile.Score.Total;

    public string ScoreText => Profile.Score.Suppressed
        ? $"{TotalScore}（已压制）"
        : TotalScore.ToString();

    public string StartupSummary => Profile.StartupSummary;

    /// <summary>排序用的启动方式键：取第一个启动项的类别名，保证稳定。</summary>
    public string StartupSortKey => Profile.StartupEntries.Count == 0
        ? string.Empty
        : Profile.StartupEntries[0].Source.ToDisplayName();

    public string Publisher => Profile.Publisher ?? "（无签名）";

    public TrustVerdict Trust => Profile.Trust;

    public string TrustText => Trust switch
    {
        TrustVerdict.Safe => "白名单",
        TrustVerdict.Malicious => "黑名单",
        _ => "未收录",
    };

    public bool IsRunning => Profile.IsRunning;

    public string RunningText => IsRunning ? "运行中" : "未运行";

    // ---------------- 失效启动项（残留） ----------------

    /// <summary>该软件的启动项是否全部失效（目标文件已不存在）。</summary>
    public bool IsOrphaned => Profile.IsOrphaned;

    /// <summary>失效启动项条数（含部分失效的情况）。</summary>
    public int OrphanCount => Profile.OrphanEntries.Count;

    /// <summary>
    /// 状态列文案。
    /// 失效项优先显示"残留"——它比"未运行"更有信息量，也直接告诉用户该清理。
    /// </summary>
    public string StateText
    {
        get
        {
            if (OrphanCount == 0)
            {
                return RunningText;
            }

            return OrphanCount >= Profile.StartupEntries.Count
                ? "启动项已失效"
                : $"{OrphanCount} 条启动项失效";
        }
    }

    /// <summary>失效启动项的路径清单，供详情面板与报告使用。</summary>
    public IReadOnlyList<string> OrphanPaths => Profile.OrphanEntries
        .Select(e => e.ExecutablePath ?? e.RawCommand ?? "（未知路径）")
        .ToList();

    public string HitSummary => $"{Profile.Score.HitCount}/{Profile.Score.RuleCount} 条规则命中";

    public IReadOnlyList<EvidenceItemViewModel> Evidences { get; }

    public IReadOnlyList<string> ScoreItems { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }
}

/// <summary>一条证据的展示项。</summary>
public sealed class EvidenceItemViewModel(Evidence evidence)
{
    public string Code { get; } = evidence.Code;

    public string Message { get; } = evidence.Message;

    public EvidenceSeverity Severity { get; } = evidence.Severity;

    public string SeverityText => Severity switch
    {
        EvidenceSeverity.Critical => "严重",
        EvidenceSeverity.Warning => "注意",
        _ => "信息",
    };
}
