using System.Windows.Media;
using RubbishCleaner.App.Infrastructure;
using RubbishCleaner.Core.Cleanup;
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

        // 只有不可自动清理的残留才需要"手动处理指引"；
        // Run 类的失效项由"清理残留启动项"按钮统一处理，不必在这里重复。
        ManualFixes = profile.OrphanEntries
            .Select(ManualFixAdvisor.For)
            .Where(g => g is not null)
            .Select(g => new ManualFixItem(g!))
            .ToList();
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

    /// <summary>
    /// 需要手动处理的残留项及其可复制命令。
    ///
    /// 只有不可自动清理的类型（服务 / 计划任务 / 快捷方式）会出现在这里。
    /// UI 用只读文本框展示，方便用户直接复制，但不会替用户执行——
    /// 删服务可能影响依赖它的其他服务，风险必须由用户自己判断。
    /// </summary>
    public IReadOnlyList<ManualFixItem> ManualFixes { get; } = [];

    /// <summary>是否有需要手动处理的残留。</summary>
    public bool HasManualFixes => ManualFixes.Count > 0;

    public IReadOnlyList<EvidenceItemViewModel> Evidences { get; }

    public IReadOnlyList<string> ScoreItems { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }
}

/// <summary>一条"需要手动处理"的展示项（含可复制命令 + 一键打开对应管理界面）。</summary>
public sealed class ManualFixItem(ManualFixGuide guide)
{
    public string EntryName { get; } = guide.EntryName;

    /// <summary>应该打开哪个 Windows 管理界面。</summary>
    public ManualFixTargetKind Target { get; } = guide.Target;

    /// <summary>要在目标界面里定位的对象（任务名 / 服务名 / 快捷方式完整路径）。</summary>
    public string? TargetPath { get; } = guide.TargetPath;

    /// <summary>复制到剪贴板的值（任务名为规范化后的名字，服务名就是服务名）。</summary>
    public string? CopyValue => TargetPath;

    public bool HasCopyValue => !string.IsNullOrWhiteSpace(TargetPath);

    /// <summary>「打开 XXX」按钮的文案。</summary>
    public string OpenTargetText => Target switch
    {
        ManualFixTargetKind.WindowsService => "打开「服务」并复制服务名",
        ManualFixTargetKind.ScheduledTask => "打开「任务计划程序」并复制任务名",
        _ => "在文件夹中选中该快捷方式",
    };

    /// <summary>打开界面之后要做的两三步（新手照着做即可）。</summary>
    public string StepText => Target switch
    {
        ManualFixTargetKind.WindowsService =>
            "1) 在弹出的「服务」窗口里按名称排序，找到这条服务（服务名已复制到剪贴板）\n"
            + "2) 右键 → 属性，先核对「可执行文件的路径」确实是上面那个已不存在的程序\n"
            + "3) 确认无误后右键 → 停止；再用管理员身份运行上面那条 sc delete 命令",
        ManualFixTargetKind.ScheduledTask =>
            "1) 在弹出的「任务计划程序」里点左侧「任务计划程序库」，右侧列表按「名称」列排序\n"
            + "2) 按名字找到这条任务（任务名已复制到剪贴板；也可在「操作 → 查看 → 详细信息」里核对）\n"
            + "3) 右键 → 删除；或直接用管理员命令提示符执行上面那条 schtasks 命令",
        _ => "资源管理器会直接选中这个快捷方式，按 Delete 删除即可（不需要管理员权限）",
    };

    public string Title { get; } = guide.Title;

    public string Explanation { get; } = guide.Explanation;

    /// <summary>可复制的命令；为空时说明只需手工操作（例如删快捷方式）。</summary>
    public string? Command { get; } = guide.Command;

    public bool HasCommand { get; } = !string.IsNullOrWhiteSpace(guide.Command);

    /// <summary>补充提示，例如服务名与文件名不一致的提醒。</summary>
    public string? CommandHint { get; } = guide.CommandHint;

    public bool HasHint { get; } = !string.IsNullOrWhiteSpace(guide.CommandHint);
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
