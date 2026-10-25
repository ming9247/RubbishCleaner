using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using RubbishCleaner.App.Infrastructure;
using RubbishCleaner.Common.Formatting;
using RubbishCleaner.Common.Logging;
using RubbishCleaner.Common.Win32;
using RubbishCleaner.Core.Leftover;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Uninstall;

namespace RubbishCleaner.App.ViewModels;

/// <summary>残留列表中的一行。</summary>
public sealed class LeftoverItemViewModel(LeftoverItem item)
{
    public LeftoverItem Item { get; } = item;

    public string DirectoryPath => Item.DirectoryPath;

    public string DisplayName => Item.DisplayName;

    public string KindText => Item.Kind switch
    {
        LeftoverKind.OrphanedUninstallerDirectory => "卸载残留",
        LeftoverKind.StartupTrailDirectory => "启动项残留",
        _ => "疑似残留",
    };

    public string ConfidenceText => Item.Confidence switch
    {
        LeftoverConfidence.High => "确信",
        LeftoverConfidence.Medium => "较可能",
        _ => "仅提示",
    };

    public string SizeText => Item.TotalBytes > 0 ? ByteSize.Format(Item.TotalBytes) : "—";

    public string FileCountText => Item.FileCount > 0 ? $"{Item.FileCount} 个文件" : "—";

    /// <summary>最后一次写入距今多久；"很久没动过"是残留的辅助信号。</summary>
    public string LastWriteText => Item.LastWriteTime is { } time
        ? FormatAge(DateTimeOffset.Now - time)
        : "—";

    public string SoftwareName => string.IsNullOrWhiteSpace(Item.SoftwareName) ? "（未知）" : Item.SoftwareName!;

    public bool CanDelete => Item.CanDelete;

    public string? BlockReason => Item.BlockReason;

    public bool IsBlocked => Item.BlockReason is not null;

    public IReadOnlyList<string> Evidence => Item.Evidence;

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 365)
        {
            return $"{age.TotalDays / 365:F1} 年前";
        }

        if (age.TotalDays >= 1)
        {
            return $"{age.TotalDays:F0} 天前";
        }

        if (age.TotalHours >= 1)
        {
            return $"{age.TotalHours:F0} 小时前";
        }

        return $"{Math.Max(1, age.TotalMinutes):F0} 分钟前";
    }
}

/// <summary>注册表残留中的一行（v0.4）。</summary>
public sealed class RegistryOrphanItemViewModel(Core.RegistryCleanup.OrphanUninstallEntry entry)
{
    public Core.RegistryCleanup.OrphanUninstallEntry Entry { get; } = entry;

    public string DisplayName => Entry.DisplayName;

    public string VersionText => string.IsNullOrWhiteSpace(Entry.DisplayVersion) ? "—" : Entry.DisplayVersion!;

    public string Publisher => string.IsNullOrWhiteSpace(Entry.Publisher) ? "（未知发行商）" : Entry.Publisher!;

    public string ProductCode => Entry.ProductCode;

    public string FullPath => Entry.FullPath;

    /// <summary>MSI 状态的中文说明，写进列表让用户能自己复核。</summary>
    public string StateText => Entry.MsiStateText;

    public IReadOnlyList<string> Evidence => Entry.Evidence;

    public bool CanDelete => Entry.CanDelete;

    public string? BlockReason => Entry.BlockReason;

    /// <summary>是否被安全闸门拦下（UI 用它决定要不要显示"不可自动清理"徽标与原因）。</summary>
    public bool IsBlocked => Entry.BlockReason is not null;

    /// <summary>所在 hive（HKLM / HKCU）：同一份「应用和功能」列表会跨两个 hive，用户复核时需要知道去哪找。</summary>
    public string HiveText => Entry.HiveName;
}

/// <summary>
/// 残留扫描页（v0.3）。
///
/// 与"启动项检测""已安装软件"并列的第三个页签，回答的问题是：
/// <b>"我卸载过的东西，还剩下多少垃圾？"</b>
///
/// 设计上的三条硬约束：
/// <list type="number">
/// <item>
/// <b>只对高置信度项提供一键清理</b>。中/低置信度只展示、不代劳——
/// 它们的证据强度不足以支撑"帮你删掉一整个目录"。
/// </item>
/// <item>
/// <b>清理 = 移入回收站</b>，没有永久删除的入口。UI 上要明说，用户才敢点。
/// </item>
/// <item>
/// <b>扫描方式如实呈现</b>。管理员下是 USN 直读（秒级），标准用户下是目录遍历（慢）。
/// 不能让用户以为两种情况扫的是同样的东西。
/// </item>
/// </list>
/// </summary>
public sealed class LeftoversViewModel : ObservableObject
{
    private readonly ILeftoverScanner _scanner;
    private readonly Func<LeftoverCleaner> _cleanerFactory;

    /// <summary>
    /// 注册表孤儿条目扫描器（v0.4）。
    ///
    /// 与 <see cref="_scanner"/> 同理走构造注入而不是就地 new：注册表扫描要读真实 MSI 数据库，
    /// 硬编码字段会让"扫到 0 条 / 扫到多条 / 扫描抛异常"这些分支在单测里根本触发不到（这是 v0.4.0 的实际状态）。
    /// </summary>
    private readonly Core.RegistryCleanup.IOrphanUninstallScanner _registryScanner;

    /// <summary>注册表清理器工厂（v0.4）；备份目录默认在 %LocalAppData%\RubbishCleaner\registry-backups。</summary>
    private readonly Func<Core.RegistryCleanup.OrphanCleaner> _registryCleanerFactory;

    /// <summary>最近一次注册表扫描的结果。</summary>
    private Core.RegistryCleanup.OrphanScanResult? _registryScan;

    /// <summary>注册表残留项（全部，判定真相）。</summary>
    public ObservableCollection<RegistryOrphanItemViewModel> RegistryItems { get; } = [];

    /// <summary>
    /// 注册表残留项（界面绑定的这一份）。
    ///
    /// 与文件残留共用同一套筛选条件（关键词 + 只看可清理）。v0.4.0 里注册表区块是直接绑
    /// <see cref="RegistryItems"/> 的，导致用户输入关键词时只有文件列表在变、注册表那一栏纹丝不动，
    /// 看起来像筛选没生效——同一页里两套筛选行为是不该出现的。
    /// </summary>
    public ObservableCollection<RegistryOrphanItemViewModel> FilteredRegistryItems { get; } = [];
    private readonly Func<InstalledAppsResult> _installedAppsProvider;
    private readonly Func<IReadOnlyList<SoftwareProfile>> _startupProfilesProvider;

    private bool _isScanning;
    private bool _hasScanned;
    private string _statusText = "点击「扫描残留」查找已卸载软件留下的目录";
    private string _summaryText = string.Empty;
    private string _indexText = string.Empty;
    private string? _degradedText;
    private string _filterText = string.Empty;
    private bool _showOnlyCleanable;
    private LeftoverItemViewModel? _selected;
    private string? _lastScanError;

    /// <summary>是否扫过一次注册表（与文件残留的 HasScanned 同理，用来区分"没扫"和"扫了但为空"）。</summary>
    private bool _hasRegistryScanned;

    /// <summary>当前选中的注册表行。</summary>
    private RegistryOrphanItemViewModel? _selectedRegistry;

    public LeftoversViewModel()
        : this(
            LeftoverScanner.CreateDefault(),
            () => new LeftoverCleaner(
                path => ToOutcome(RecycleBin.MoveToRecycleBin(path))),
            DefaultInstalledApps,
            () => [])
    {
    }

    /// <summary>
    /// 注入全部外部依赖。
    ///
    /// 后两个参数带默认值（传 <c>null</c> 即用生产实现），所以既有调用点
    /// （<see cref="MainViewModel"/> 的 4 参数构造以及既有单测）无需改动即可编译。
    /// 注册表这部分单独用命名空间前缀引用，是因为它与文件残留同属"残留"，但实现分属不同子系统。
    /// </summary>
    public LeftoversViewModel(
        ILeftoverScanner scanner,
        Func<LeftoverCleaner> cleanerFactory,
        Func<InstalledAppsResult> installedAppsProvider,
        Func<IReadOnlyList<SoftwareProfile>> startupProfilesProvider,
        Core.RegistryCleanup.IOrphanUninstallScanner? registryScanner = null,
        Func<Core.RegistryCleanup.OrphanCleaner>? registryCleanerFactory = null)
    {
        _scanner = scanner;
        _cleanerFactory = cleanerFactory;
        _installedAppsProvider = installedAppsProvider;
        _startupProfilesProvider = startupProfilesProvider;
        _registryScanner = registryScanner ?? new Core.RegistryCleanup.OrphanUninstallScanner();
        _registryCleanerFactory = registryCleanerFactory ?? (() => new Core.RegistryCleanup.OrphanCleaner());

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsScanning)
        {
            OnError = ex =>
            {
                Log.Error("残留扫描失败", ex);
                LastScanError = $"扫描失败：{ex.Message}";
                IsScanning = false;
            },
        };

        CleanCommand = new AsyncRelayCommand(CleanAsync, () => CleanableCount > 0 && !IsScanning)
        {
            OnError = ex =>
            {
                Log.Error("清理残留失败", ex);
                StatusText = $"清理失败：{ex.Message}";
            },
        };

        OpenFolderCommand = new RelayCommand(OpenContainingFolder, p => p is LeftoverItemViewModel);

        // v0.4：注册表孤儿条目
        ScanRegistryCommand = new AsyncRelayCommand(ScanRegistryAsync, () => !IsScanning)
        {
            OnError = ex =>
            {
                Log.Error("注册表残留扫描失败", ex);
                LastScanError = $"注册表扫描失败：{ex.Message}";

                // 除 LastScanError 之外还要改 StatusText：注册表区块的底部状态栏只绑了 StatusText，
                // 只设错误字段的话用户点完扫描会看到状态栏停在"正在检查…"不动，误以为还在跑。
                StatusText = $"注册表扫描失败：{ex.Message}";
                IsScanning = false;
            },
        };

        CleanRegistryCommand = new AsyncRelayCommand(CleanRegistryAsync, () => CanCleanRegistry)
        {
            OnError = ex =>
            {
                Log.Error("注册表残留清理失败", ex);
                StatusText = $"注册表清理失败：{ex.Message}";
            },
        };

        ShowAllCommand = new RelayCommand(() => ShowOnlyCleanable = false);
        ShowCleanableCommand = new RelayCommand(() => ShowOnlyCleanable = true);
        ClearFilterCommand = new RelayCommand(() => FilterText = string.Empty);
    }

    /// <summary>确认对话框（可注入以便测试）。</summary>
    public Func<string, string, bool> ConfirmHandler { get; set; } =
        (title, message) => System.Windows.MessageBox.Show(
            message, title, System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.Cancel)
            == System.Windows.MessageBoxResult.OK;

    /// <summary>结果提示（可注入以便测试）。</summary>
    public Action<string, string> NotifyHandler { get; set; } =
        (title, message) => System.Windows.MessageBox.Show(
            message, title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

    public ObservableCollection<LeftoverItemViewModel> Items { get; } = [];

    public ObservableCollection<LeftoverItemViewModel> FilteredItems { get; } = [];

    public ICommand ScanCommand { get; }

    public ICommand CleanCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public ICommand ShowAllCommand { get; }

    public ICommand ShowCleanableCommand { get; }

    public ICommand ClearFilterCommand { get; }

    /// <summary>扫描注册表孤儿条目（v0.4）。</summary>
    public ICommand ScanRegistryCommand { get; }

    /// <summary>清理注册表孤儿条目（v0.4）。</summary>
    public ICommand CleanRegistryCommand { get; }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                OnPropertyChanged(nameof(IsNotScanning));
                OnPropertyChanged(nameof(CleanButtonText));
            }
        }
    }

    public bool IsNotScanning => !IsScanning;

    public bool HasScanned
    {
        get => _hasScanned;
        private set
        {
            if (SetProperty(ref _hasScanned, value))
            {
                OnPropertyChanged(nameof(ShowEmptyState));
            }
        }
    }

    /// <summary>空状态提示：没扫描过与扫描后无结果，要说不同的话。</summary>
    public bool ShowEmptyState => !HasScanned;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    /// <summary>使用的索引方式（"USN 索引…" 或 "目录遍历回退…"），如实呈现给用户。</summary>
    public string IndexText
    {
        get => _indexText;
        private set => SetProperty(ref _indexText, value);
    }

    public string? DegradedText
    {
        get => _degradedText;
        private set
        {
            if (SetProperty(ref _degradedText, value))
            {
                OnPropertyChanged(nameof(HasDegraded));
            }
        }
    }

    public bool HasDegraded => !string.IsNullOrWhiteSpace(DegradedText);

    public string? LastScanError
    {
        get => _lastScanError;
        private set => SetProperty(ref _lastScanError, value);
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                ApplyFilter();
                ApplyRegistryFilter();
            }
        }
    }

    /// <summary>是否只看"可自动清理"的项。</summary>
    public bool ShowOnlyCleanable
    {
        get => _showOnlyCleanable;
        set
        {
            if (SetProperty(ref _showOnlyCleanable, value))
            {
                ApplyFilter();
                ApplyRegistryFilter();
            }
        }
    }

    public LeftoverItemViewModel? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    /// <summary>全部残留项数（含不可清理的）。</summary>
    public int TotalCount => Items.Count;

    /// <summary>
    /// 可自动清理的条数。
    ///
    /// 与启动项页同样的教训（v0.1.4 踩过）：<b>展示数</b>与<b>可操作数</b>必须分开。
    /// 否则会出现"显示 12 条残留，清理按钮却是灰的"的困惑。
    ///
    /// 注意条件有<b>两个</b>：通过安全闸门（<c>CanDelete</c>）<b>且</b>置信度为 High。
    /// 只看前者会把"名字像但没硬证据"的低置信度项也算进按钮数字，
    /// 用户点下去却发现实际只清了 3 个（实测踩过这个偏差）。
    /// </summary>
    public int CleanableCount => Items.Count(i => i.CanDelete && i.Item.Confidence == LeftoverConfidence.High);

    public long TotalBytes => Items.Sum(i => i.Item.TotalBytes);

    public string TotalSizeText => ByteSize.Format(TotalBytes);

    public string CleanButtonText => CleanableCount > 0
        ? $"清理 {CleanableCount} 项（{ByteSize.Format(CleanableItems.Sum(i => i.Item.TotalBytes))}）"
        : "没有可清理项";

    /// <summary>真正会被清理的项：高置信度且通过安全闸门。</summary>
    private IEnumerable<LeftoverItemViewModel> CleanableItems
        => Items.Where(i => i.CanDelete && i.Item.Confidence == LeftoverConfidence.High);

    public bool CanClean => CleanableCount > 0 && !IsScanning;

    /// <summary>注册表残留条数（孤儿 Uninstall 条目）。</summary>
    public int RegistryCount => RegistryItems.Count;

    /// <summary>可清理的注册表条数（通过安全闸门、即 <c>BlockReason</c> 为空）。</summary>
    public int RegistryCleanableCount => RegistryItems.Count(i => i.CanDelete);

    public bool HasRegistryResidue => RegistryItems.Count > 0;

    /// <summary>
    /// 是否已经扫过一次注册表。
    ///
    /// 必须与 <see cref="HasRegistryResidue"/> 分开：<b>"还没扫"</b>和<b>"扫过但一条都没有"</b>
    /// 对用户是两件完全不同的事——前者要提示他去点扫描，后者要恭喜他注册表是干净的。
    /// 只用一个 <c>Count == 0</c> 判断会把这两种状态压成一句话（文件残留那边就是靠 HasScanned 区分的）。
    /// </summary>
    public bool HasRegistryScanned
    {
        get => _hasRegistryScanned;
        private set
        {
            if (SetProperty(ref _hasRegistryScanned, value))
            {
                // 三个派生可见性都依赖它：漏通知会导致"第一次扫描完成后列表不显示"这种
                // 只在真实 UI 上才看得见的问题（属性本身对，界面不刷新）。
                OnPropertyChanged(nameof(ShowRegistryEmptyState));
                OnPropertyChanged(nameof(ShowRegistryCleanState));
                OnPropertyChanged(nameof(ShowRegistryList));
                OnPropertyChanged(nameof(ShowRegistryFilterEmpty));
                OnPropertyChanged(nameof(RegistrySummaryText));
            }
        }
    }

    /// <summary>注册表区块的空状态：还没扫过（提示"去点扫描"）。</summary>
    public bool ShowRegistryEmptyState => !HasRegistryScanned;

    /// <summary>注册表区块的"干净"状态：扫过、但一条孤儿都没有（这是好消息，要单独说）。</summary>
    public bool ShowRegistryCleanState => HasRegistryScanned && RegistryCount == 0;

    /// <summary>注册表区块的"有内容"状态：有条目可显示。</summary>
    public bool ShowRegistryList => HasRegistryScanned && RegistryCount > 0;

    /// <summary>
    /// 注册表筛选后是不是空的。
    ///
    /// 单独一条属性是为了给"筛选把条目全滤掉了"一句明确提示：
    /// 否则用户会看到区块标题写着"残留 5 条"、下面却是白板，以为界面坏了。
    /// </summary>
    public bool ShowRegistryFilterEmpty => ShowRegistryList && FilteredRegistryItems.Count == 0;

    /// <summary>
    /// 注册表区块的语义化小标题：<b>"注册表残留 N 条、可清理 M 条"</b>。
    ///
    /// 单独给一条属性而不是让 XAML 里拼 Run：一是中文里"条"这类量词与数字的语序写死在视图里难改，
    /// 二是这句话同时要说明"一共看了多少条 MSI 条目"，否则用户会以为扫了全盘注册表。
    /// </summary>
    public string RegistrySummaryText
    {
        get
        {
            if (!HasRegistryScanned)
            {
                return "还没有检查注册表";
            }

            var scanned = _registryScan?.ScannedCount ?? 0;

            if (RegistryCount == 0)
            {
                return $"注册表干净：已检查 {scanned} 个 MSI 条目，没有发现孤儿";
            }

            var text = $"注册表残留 {RegistryCount} 条、可清理 {RegistryCleanableCount} 条";

            var blocked = RegistryCount - RegistryCleanableCount;
            if (blocked > 0)
            {
                text += $"（其余 {blocked} 条被安全闸门拦下）";
            }

            return text;
        }
    }

    /// <summary>
    /// 注册表清理按钮文案。
    ///
    /// 三种状态必须给三种话，而不是"可清理数为 0 就写无残留"：
    /// 扫到 3 条但全被安全闸门拦下时，按钮若写「无注册表残留」，用户会以为扫描没生效。
    /// 文件残留那边同样是这个教训（扫描到但不可清理 ≠ 没扫到）。
    /// </summary>
    public string RegistryButtonText => RegistryCleanableCount > 0
        ? $"清理 {RegistryCleanableCount} 个注册表残留"
        : RegistryCount > 0
            ? "无可清理项"
            : "无注册表残留";

    public bool CanCleanRegistry => RegistryCleanableCount > 0 && !IsScanning;

    /// <summary>
    /// 当前选中的注册表行（UI 用它显示完整键路径与被拦下的原因）。
    ///
    /// 注册表条目不像目录那样能"打开所在文件夹"，用户唯一的复核手段就是在界面上看到键路径，
    /// 所以选中态必须能拿到整行而不是只有展示名。
    /// </summary>
    public RegistryOrphanItemViewModel? SelectedRegistry
    {
        get => _selectedRegistry;
        set
        {
            if (SetProperty(ref _selectedRegistry, value))
            {
                OnPropertyChanged(nameof(HasSelectedRegistry));
            }
        }
    }

    /// <summary>是否有选中的注册表行（供详情区显示/隐藏）。</summary>
    public bool HasSelectedRegistry => SelectedRegistry is not null;

    /// <summary>
    /// 显示一次<b>定向</b>扫描结果（v0.3.1 卸载联动）。
    ///
    /// 与 <see cref="ScanAsync"/> 的区别：这份清单来自"刚刚卸载了 X"这个已知事实，
    /// 因此每一条都是<b>确定性残留</b>而不是启发式猜测。UI 上要能看出这个区别，
    /// 否则用户会以为通用扫描漏报了别的东西。
    /// </summary>
    public void ShowUninstallResidue(Core.Residue.UninstallResidueResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        Items.Clear();
        FilteredItems.Clear();

        foreach (var item in result.Directories)
        {
            Items.Add(new LeftoverItemViewModel(item));
        }

        // 文件级残留单独归组：它们是"卸载器没删干净的具体文件"，
        // 与目录级残留的处置方式不同（删目录 vs 删文件），不能混在一个列表里让用户勾。
        foreach (var file in result.RemainingFiles)
        {
            Items.Add(new LeftoverItemViewModel(new Core.Models.LeftoverItem
            {
                Id = file.ToLowerInvariant(),
                DirectoryPath = file,
                Kind = Core.Models.LeftoverKind.StartupTrailDirectory,
                Confidence = Core.Models.LeftoverConfidence.High,
                SoftwareName = result.SoftwareName,
                Evidence =
                [
                    $"「{result.SoftwareName}」卸载前该文件存在，卸载后仍未被卸载程序删除",
                    "属于卸载器漏删的具体文件，可单独移入回收站",
                ],
            }));
        }

        ApplyFilter();
        HasScanned = true;

        IndexText = "定向扫描（卸载后对比）";
        SummaryText = result.Directories.Count == 0 && result.RemainingFiles.Count == 0
            ? $"「{result.SoftwareName}」卸载得很干净，没有发现残留"
            : $"「{result.SoftwareName}」卸载后残留：{result.RemainingFiles.Count} 个文件"
              + (result.Directories.Count > 0 ? $"、{result.Directories.Count} 个目录" : string.Empty)
              + $"（卸载器已清理 {result.RemovedCount}/{result.SnapshotCount} 项）";

        StatusText = $"「{result.SoftwareName}」卸载完成，以上是它留下的残留";

        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CleanableCount));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(TotalSizeText));
        OnPropertyChanged(nameof(CleanButtonText));
        OnPropertyChanged(nameof(CanClean));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>执行一次扫描。只读，不改任何文件。</summary>
    public async Task ScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;
        LastScanError = null;
        DegradedText = null;
        StatusText = "正在扫描残留…";
        Items.Clear();
        FilteredItems.Clear();
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CleanableCount));
        OnPropertyChanged(nameof(TotalBytes));

        try
        {
            var installed = _installedAppsProvider();
            var profiles = _startupProfilesProvider();

            var options = new LeftoverScanOptions
            {
                InstalledApps = installed.Apps,

                // 关键：把"被过滤掉的条目"的路径证据也带上。
                // 只看可展示的软件，会把 SystemComponent / 无名称组件的目录误判成残留。
                AdditionalClaimPaths = installed.ClaimPaths,
                StartupProfiles = profiles,
            };

            var progress = new Progress<LeftoverScanProgress>(p =>
            {
                StatusText = string.IsNullOrWhiteSpace(p.Detail) ? p.Stage : $"{p.Stage}（{p.Detail}）";
            });

            var result = await _scanner.ScanAsync(options, progress).ConfigureAwait(true);

            foreach (var item in result.Items)
            {
                Items.Add(new LeftoverItemViewModel(item));
            }

            ApplyFilter();
            HasScanned = true;

            IndexText = result.IndexDescription;
            DegradedText = result.DegradedReason;
            LastScanError = result.IsDegraded ? result.DegradedReason : null;

            SummaryText = result.Items.Count == 0
                ? $"没有发现残留（扫描用时 {result.Duration.TotalSeconds:F1} 秒）"
                : $"共 {result.Items.Count} 项残留，合计 {ByteSize.Format(result.TotalBytes)}"
                  + $"（其中 {result.CleanableCount} 项可清理，用时 {result.Duration.TotalSeconds:F1} 秒）";

            StatusText = result.Items.Count == 0
                ? "扫描完成，没有发现残留目录"
                : result.CleanableCount == 0
                    ? $"扫描完成，发现 {result.Items.Count} 项残留，但都不建议自动清理"
                    : $"扫描完成，发现 {result.Items.Count} 项残留（可清理 {result.CleanableCount} 项）";

            // 已安装软件列表为空时，所有带卸载程序的目录都会被判成"无人认领"，
            // 结果会明显地不可信。必须显式提醒，而不是让用户照单全清（这是真实风险）。
            // 注意要<b>追加</b>而不是覆盖：索引降级的原因同样重要，两者可能同时发生。
            if (installed.Apps.Count == 0 && result.Items.Count > 0)
            {
                const string warning = "未能读取已安装软件列表，本次判定依据不完整，请先到「已安装软件」页刷新后再扫一次。";
                DegradedText = string.IsNullOrWhiteSpace(DegradedText)
                    ? warning
                    : DegradedText + "\n" + warning;
            }

            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(CleanableCount));
            OnPropertyChanged(nameof(TotalBytes));
            OnPropertyChanged(nameof(TotalSizeText));
            OnPropertyChanged(nameof(CleanButtonText));
            OnPropertyChanged(nameof(CanClean));
            CommandManager.InvalidateRequerySuggested();

            Log.Info($"残留扫描：{result.Items.Count} 项，可清理 {result.CleanableCount} 项，索引={result.IndexDescription}，用时 {result.Duration.TotalMilliseconds:F0}ms");
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// 清理：把可清理的残留目录<b>移入回收站</b>。
    ///
    /// 三步确认：先由清理器生成计划（只有高置信度且通过安全闸门的项会进计划），
    /// 再把计划逐条摆给用户看，最后执行并逐项报告结果。
    /// </summary>
    internal async Task CleanAsync()
    {
        var cleaner = _cleanerFactory();
        var plan = cleaner.BuildPlan(Items.Select(i => i.Item));

        if (plan.IsEmpty)
        {
            NotifyHandler("没有可清理项",
                Items.Count > 0
                    ? "发现的残留项都不满足自动清理条件：\n\n"
                      + string.Join('\n', Items.Where(i => i.IsBlocked).Take(10)
                          .Select(i => $"· {i.DirectoryPath}：{i.BlockReason}"))
                    : "当前没有残留项。");
            return;
        }

        // 把"要删哪些目录、总共多大"逐条摆出来，而不是只给一个总数。
        var shown = plan.Targets.Take(20).ToList();
        var detail = string.Join('\n', shown.Select(t => $"· {t.DirectoryPath}（{ByteSize.Format(t.TotalBytes)}）"));
        if (plan.Targets.Count > shown.Count)
        {
            detail += $"\n… 以及另外 {plan.Targets.Count - shown.Count} 项";
        }

        var message =
            $"即将把以下 {plan.Count} 个残留目录移入回收站（合计 {ByteSize.Format(plan.TotalBytes)}）：\n\n{detail}\n\n" +
            "这些目录里还留着卸载程序，或已被失效的启动项指向，且没有任何已安装软件认领它们。\n\n" +
            "· 只移入回收站，不会永久删除——随时可以从回收站还原\n" +
            "· 如果某个目录其实是你在用的软件，请在回收站里右键「还原」";

        if (!ConfirmHandler("确认清理残留目录", message))
        {
            StatusText = "已取消清理";
            return;
        }

        StatusText = "正在移入回收站…";
        var results = await Task.Run(() => cleaner.Execute(plan)).ConfigureAwait(true);

        var ok = results.Count(r => r.Success);
        var failed = results.Where(r => !r.Success).ToList();

        foreach (var result in results)
        {
            Log.Info($"清理残留 {result.Target.DirectoryPath}：{(result.Success ? "成功" : "失败 - " + result.Message)}");
        }

        var summary = failed.Count == 0
            ? $"已把 {ok} 项残留移入回收站（可随时还原）"
            : $"清理完成：成功 {ok} 项，失败 {failed.Count} 项";

        StatusText = summary;

        NotifyHandler(
            failed.Count == 0 ? "清理完成" : "部分项清理失败",
            failed.Count == 0
                ? $"已把 {ok} 项残留移入回收站，合计 {ByteSize.Format(plan.TotalBytes)}。\n\n如需还原，请打开「回收站」右键选择「还原」。"
                : string.Join('\n', failed.Select(f => $"· {f.Target.DirectoryPath}：{f.Message}"))
                  + $"\n\n成功 {ok} 项。");

        // 重新扫描，让界面反映真实状态（而不是把已删的行留在列表里）。
        // 重扫会把 StatusText 改成"扫描完成…"，所以之后要把清理结论写回去——
        // 用户刚点完清理，最该看到的是清理结果而不是"扫描完成"。
        await ScanAsync();
        StatusText = summary;
    }

    /// <summary>
    /// 扫描注册表孤儿条目（v0.4）。
    ///
    /// 与文件残留分开扫描是刻意的：两者判据完全不同（文件靠硬痕迹，注册表靠 MSI API），
    /// 混在一次扫描里会让用户分不清"这次扫的是磁盘还是注册表"。
    /// </summary>
    public async Task ScanRegistryAsync()
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;
        LastScanError = null;
        StatusText = "正在检查注册表中的孤儿卸载条目…";
        RegistryItems.Clear();

        // 选中项要跟着清掉：它指向的对象刚被 Clear 掉，留着会让详情区显示一条已经不存在的条目。
        SelectedRegistry = null;
        ApplyRegistryFilter();
        NotifyRegistryChanged();

        try
        {
            var progress = new Progress<Core.RegistryCleanup.OrphanScanProgress>(p =>
            {
                StatusText = string.IsNullOrWhiteSpace(p.Detail) ? p.Stage : $"{p.Stage}（{p.Detail}）";
            });

            var result = await _registryScanner.ScanAsync(progress).ConfigureAwait(true);
            _registryScan = result;

            foreach (var entry in result.Entries)
            {
                RegistryItems.Add(new RegistryOrphanItemViewModel(entry));
            }

            // 先置 true 再通知：RegistrySummaryText 与三个可见性属性都依赖它，
            // 顺序反了首屏会先闪一下"还没有检查注册表"，而且列表要等下次通知才出现。
            HasRegistryScanned = true;

            // 筛选条件要延续：用户在扫描前输入的关键词，扫描完成后仍然生效。
            ApplyRegistryFilter();
            NotifyRegistryChanged();

            StatusText = result.Entries.Count == 0
                ? $"注册表检查完成：{result.ScannedCount} 个 MSI 条目全部有效，没有孤儿条目"
                : $"注册表检查完成：{result.ScannedCount} 个 MSI 条目中发现 {result.Entries.Count} 个孤儿";

            // 降级说明要覆盖而不是保留上一次的：文件残留那边的 DegradedText 可能来自上一轮文件扫描，
            // 直接 Append 会让用户以为注册表扫描也出了那个问题。
            if (result.IsDegraded)
            {
                DegradedText = result.DegradedReason;
            }

            Log.Info($"注册表孤儿扫描：{result.ScannedCount} 条中 {result.Entries.Count} 个孤儿，用时 {result.Duration.TotalMilliseconds:F0}ms");
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// 清理注册表孤儿条目：逐条备份 → 删键 → 复核。
    ///
    /// 与文件残留的 <see cref="CleanAsync"/> 有一处刻意不同：<b>没有可清理项时连清理器都不创建</b>。
    /// 文件那边是先建 cleaner 再让 BuildPlan 返回空计划，那是因为"空计划"本身有诊断价值（能列出被闸门拦下的原因）；
    /// 注册表的门禁在扫描阶段就已经把 <c>BlockReason</c> 写在条目上了，UI 也直接展示，
    /// 再走一遍工厂只会多一次对备份目录/MSI 的探测——尤其是工厂在生产环境里会触碰真实注册表读取路径。
    /// </summary>
    public async Task CleanRegistryAsync()
    {
        if (RegistryCleanableCount == 0)
        {
            NotifyHandler(
                "没有可清理项",
                RegistryCount == 0
                    ? "当前没有注册表残留。"
                    : "发现的注册表条目都不满足自动清理条件：\n\n"
                      + string.Join('\n', RegistryItems.Where(i => !i.CanDelete).Take(10)
                          .Select(i => $"· {i.DisplayName}：{i.BlockReason}")));
            return;
        }

        var cleaner = _registryCleanerFactory();
        var entries = RegistryItems.Where(i => i.CanDelete).Select(i => i.Entry).ToList();
        var plan = cleaner.BuildPlan(entries);

        // 兜底：UI 计数与清理器判定不一致时（例如条目在两者之间失效），仍按"不弹确认、不动手"处理。
        if (plan.IsEmpty)
        {
            NotifyHandler("没有可清理项", "当前没有可自动清理的注册表残留。");
            return;
        }

        var detail = string.Join('\n', plan.Targets.Take(15).Select(t => $"· {t.DisplayName}（{t.ProductCode}）"));
        if (plan.Targets.Count > 15)
        {
            detail += $"\n… 以及另外 {plan.Targets.Count - 15} 个";
        }

        var message =
            $"即将删除以下 {plan.Count} 个「应用和功能」中的孤儿条目：\n\n{detail}\n\n" +
            "这些条目在 Windows Installer 里已经不存在（点卸载会报错），删除它们只是清理噪音。\n\n" +
            "· 删除前会自动导出 .reg 备份到 %LocalAppData%\\RubbishCleaner\\registry-backups\n" +
            "· 备份文件双击即可还原\n" +
            "· 只删除 Uninstall 注册表条目，不动任何软件文件";

        if (!ConfirmHandler("确认清理注册表残留", message))
        {
            StatusText = "已取消注册表清理";
            return;
        }

        StatusText = "正在备份并删除…";
        var results = await Task.Run(() => cleaner.Execute(plan)).ConfigureAwait(true);

        var ok = results.Count(r => r.Success);
        var failed = results.Where(r => !r.Success).ToList();

        foreach (var r in results)
        {
            Log.Info($"注册表清理 {r.Target.FullPath}：{(r.Success ? "成功" : "失败 - " + r.Message)}");
        }

        var summary = failed.Count == 0
            ? $"已删除 {ok} 个孤儿条目（备份在 registry-backups 目录，可还原）"
            : $"注册表清理完成：成功 {ok} 个，失败 {failed.Count} 个";

        NotifyHandler(
            failed.Count == 0 ? "注册表清理完成" : "部分条目清理失败",
            failed.Count == 0
                ? $"已删除 {ok} 个孤儿条目。\n\n备份位置：%LocalAppData%\\RubbishCleaner\\registry-backups\n如需要还原，双击对应的 .reg 文件即可。"
                : string.Join('\n', failed.Select(f => $"· {f.Target.DisplayName}：{f.Message}")));

        // 重扫刷新
        await ScanRegistryAsync().ConfigureAwait(true);
        StatusText = summary;
    }

    private void NotifyRegistryChanged()
    {
        OnPropertyChanged(nameof(RegistryCount));
        OnPropertyChanged(nameof(RegistryCleanableCount));
        OnPropertyChanged(nameof(HasRegistryResidue));
        OnPropertyChanged(nameof(RegistrySummaryText));
        OnPropertyChanged(nameof(RegistryButtonText));
        OnPropertyChanged(nameof(CanCleanRegistry));
        OnPropertyChanged(nameof(ShowRegistryCleanState));
        OnPropertyChanged(nameof(ShowRegistryList));
        OnPropertyChanged(nameof(ShowRegistryFilterEmpty));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// 按同一套条件筛注册表条目：关键词匹配显示名 / 产品码 / 发行商 / 完整键路径。
    ///
    /// 比文件残留多了产品码与完整路径两个匹配字段：注册表条目的"名字"经常是脏数据或空的，
    /// 用户手上真正能拿来搜的往往就是 GUID 或键路径。
    /// </summary>
    private void ApplyRegistryFilter()
    {
        FilteredRegistryItems.Clear();

        var keyword = FilterText.Trim();
        foreach (var item in RegistryItems)
        {
            if (ShowOnlyCleanable && !item.CanDelete)
            {
                continue;
            }

            if (keyword.Length > 0
                && !item.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                && !item.ProductCode.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                && !item.Publisher.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                && !item.FullPath.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FilteredRegistryItems.Add(item);
        }

        OnPropertyChanged(nameof(ShowRegistryFilterEmpty));
    }

    private void ApplyFilter()
    {
        FilteredItems.Clear();

        var keyword = FilterText.Trim();
        foreach (var item in Items)
        {
            if (ShowOnlyCleanable && !item.CanDelete)
            {
                continue;
            }

            if (keyword.Length > 0
                && !item.DirectoryPath.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                && !item.SoftwareName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FilteredItems.Add(item);
        }
    }

    private void OpenContainingFolder(object? parameter)
    {
        if (parameter is not LeftoverItemViewModel item)
        {
            return;
        }

        try
        {
            var target = Directory.Exists(item.DirectoryPath)
                ? item.DirectoryPath
                : Path.GetDirectoryName(item.DirectoryPath) ?? item.DirectoryPath;

            if (!Directory.Exists(target))
            {
                NotifyHandler("目录已不存在", $"目标目录已不存在：\n{item.DirectoryPath}");
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LastScanError = $"无法打开目录：{ex.Message}";
        }
    }

    private static LeftoverDeleteOutcome ToOutcome(RecycleBinResult result)
        => result.Success
            ? LeftoverDeleteOutcome.Ok(result.Message)
            : LeftoverDeleteOutcome.Fail(result.Message);

    /// <summary>默认的已安装软件来源：每次扫描时重新读一遍注册表（约 100ms）。</summary>
    private static InstalledAppsResult DefaultInstalledApps()
    {
        try
        {
            return new InstalledAppEnumerator().Enumerate();
        }
        catch (Exception ex)
        {
            // 读不到时返回空结果而不是抛：扫描仍可进行，但 UI 会明确警告判定依据不完整。
            Log.Error("残留扫描读取已安装软件失败", ex);
            return new InstalledAppsResult([], 0, 0, ex.Message);
        }
    }
}