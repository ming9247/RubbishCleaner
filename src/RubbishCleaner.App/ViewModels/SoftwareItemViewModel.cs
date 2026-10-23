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
        ScoreItems = profile.Score.Items
            .Where(i => i.Hit)
            .Select(i => $"+{i.Weight} {i.Description}")
            .ToList();
    }

    public SoftwareProfile Profile { get; }

    public string DisplayName => Profile.DisplayName;

    public string? ExecutablePath => Profile.ExecutablePath;

    public RiskLevel Level => Profile.Score.Level;

    public string LevelText => Level.ToDisplayName();

    public int TotalScore => Profile.Score.Total;

    public string ScoreText => Profile.Score.Suppressed
        ? $"{TotalScore}（已压制）"
        : TotalScore.ToString();

    public string StartupSummary => Profile.StartupSummary;

    public string Publisher => Profile.Publisher ?? "（无签名）";

    public string TrustText => Profile.Trust switch
    {
        TrustVerdict.Safe => "白名单",
        TrustVerdict.Malicious => "黑名单",
        _ => "未收录",
    };

    public bool IsRunning => Profile.IsRunning;

    public string RunningText => IsRunning ? "运行中" : "未运行";

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
