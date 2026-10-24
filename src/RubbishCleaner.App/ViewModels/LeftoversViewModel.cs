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

    public LeftoversViewModel()
        : this(
            LeftoverScanner.CreateDefault(),
            () => new LeftoverCleaner(
                path => ToOutcome(RecycleBin.MoveToRecycleBin(path))),
            DefaultInstalledApps,
            () => [])
    {
    }

    public LeftoversViewModel(
        ILeftoverScanner scanner,
        Func<LeftoverCleaner> cleanerFactory,
        Func<InstalledAppsResult> installedAppsProvider,
        Func<IReadOnlyList<SoftwareProfile>> startupProfilesProvider)
    {
        _scanner = scanner;
        _cleanerFactory = cleanerFactory;
        _installedAppsProvider = installedAppsProvider;
        _startupProfilesProvider = startupProfilesProvider;

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