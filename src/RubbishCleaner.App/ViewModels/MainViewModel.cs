using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using RubbishCleaner.App.Infrastructure;
using RubbishCleaner.Common.Diagnostics;
using RubbishCleaner.Common.Logging;
using RubbishCleaner.Core.Detection;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Trust;

namespace RubbishCleaner.App.ViewModels;

/// <summary>主窗口视图模型。</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly StartupScanner _scanner;
    private readonly RubbishCleaner.Core.Cleanup.StartupCleaner _cleaner = new();
    private CancellationTokenSource? _cts;

    private bool _isScanning;
    private string _statusText = "点击「开始扫描」检测开机自启动项";
    private string _stageText = string.Empty;
    private double _progress;
    private bool _hasScanned;
    private string? _errorText;
    private RiskLevel? _filterLevel;
    private bool _filterOrphanOnly;
    private SoftwareItemViewModel? _selected;
    private string _environmentReport = string.Empty;
    private string _durationText = string.Empty;
    private string _countsText = string.Empty;

    public MainViewModel()
    {
        var trustTable = TrustTableLoader.Load();
        _scanner = StartupScanner.CreateDefault(trustTable);
        TrustTableInfo = trustTable.Source;
        IsElevated = _scanner.IsElevated;
        EnvironmentReport = EnvironmentProbe.ToReport(EnvironmentProbe.Capture());

        // 默认用 MessageBox 做确认与提示；测试可替换为可控委托，避免弹窗阻塞。
        ConfirmHandler = ConfirmWithMessageBox;
        NotifyHandler = NotifyWithMessageBox;

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsScanning)
        {
            OnError = ex =>
            {
                Log.Error("扫描失败", ex);
                ErrorText = $"扫描失败：{ex.Message}";
                IsScanning = false;
            },
        };

        CancelCommand = new RelayCommand(Cancel, () => IsScanning);
        RelaunchElevatedCommand = new RelayCommand(RelaunchElevated, () => !IsElevated);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        OpenContainingFolderCommand = new RelayCommand(OpenContainingFolder, p => p is SoftwareItemViewModel { ExecutablePath: not null });
        ExportCsvCommand = new RelayCommand(ExportCsv, () => Profiles.Count > 0);
        ExportHtmlCommand = new RelayCommand(ExportHtml, () => Profiles.Count > 0);
        ShowAllCommand = new RelayCommand(() => ApplyFilter(null));
        ShowHighRiskCommand = new RelayCommand(() => ApplyFilter(RiskLevel.HighRisk));
        ShowSuspiciousCommand = new RelayCommand(() => ApplyFilter(RiskLevel.Suspicious));
        ShowNormalCommand = new RelayCommand(() => ApplyFilter(RiskLevel.Normal));
        ShowOrphanCommand = new RelayCommand(ApplyOrphanFilter, () => OrphanCount > 0);

        // 清理残留启动项：只对失效项生效，且需要用户二次确认。
        CleanOrphansCommand = new AsyncRelayCommand(CleanOrphansAsync, () => CanCleanOrphans)
        {
            OnError = ex =>
            {
                Log.Error("清理残留启动项失败", ex);
                ErrorText = $"清理失败：{ex.Message}";
            },
        };

        OpenBackupFolderCommand = new RelayCommand(OpenBackupFolder);
        SortCommand = new RelayCommand(SortBy);

        Sort.PropertyChanged += OnSortChanged;

        // 残留扫描复用「已安装软件」页的枚举器，并在扫描时取当前启动项扫描结果做交叉验证。
        Leftovers = new LeftoversViewModel(
            RubbishCleaner.Core.Leftover.LeftoverScanner.CreateDefault(),
            () => new RubbishCleaner.Core.Leftover.LeftoverCleaner(
                path =>
                {
                    var result = RubbishCleaner.Common.Win32.RecycleBin.MoveToRecycleBin(path);
                    return result.Success
                        ? RubbishCleaner.Core.Leftover.LeftoverDeleteOutcome.Ok(result.Message)
                        : RubbishCleaner.Core.Leftover.LeftoverDeleteOutcome.Fail(result.Message);
                }),
            () =>
            {
                // 复用「已安装软件」页已有的枚举结果，避免两次读注册表之间软件被卸载导致结论矛盾；
                // 但 ClaimPaths（含被过滤条目的路径证据）必须重新枚举才能拿到，
                // 所以这里总是走一次全新的 Enumerate()，代价约 100ms。
                var result = new RubbishCleaner.Core.Uninstall.InstalledAppEnumerator().Enumerate();

                // 若软件页已经加载过，用它的列表覆盖，保证两个页签看到同一批软件。
                if (Apps.Apps.Count > 0)
                {
                    return result with { Apps = Apps.Apps.Select(a => a.App).ToList() };
                }

                return result;
            },
            () => Profiles.Select(p => p.Profile).ToList());

        // v0.3.1：卸载前自动快照、卸载后自动定向扫描，结果送到残留页。
        // 软件页与残留页互不知道对方，全部经由这里中转。
        Apps.UseResidueCoordinator(new UninstallResidueCoordinator(
            installedAppsProvider: () =>
            {
                var result = new RubbishCleaner.Core.Uninstall.InstalledAppEnumerator().Enumerate();
                if (Apps.Apps.Count > 0)
                {
                    return (IReadOnlyList<RubbishCleaner.Core.Models.InstalledApp>)
                        Apps.Apps.Select(a => a.App).ToList();
                }

                return result.Apps;
            }));
        Apps.ResidueFound += OnResidueFound;
    }

    /// <summary>残留页是否需要提醒用户查看（用于切换页签时的角标/提示）。</summary>
    private bool _hasPendingResidueHint;

    /// <summary>是否有"卸载后发现残留"的待查看提示。</summary>
    public bool HasPendingResidueHint
    {
        get => _hasPendingResidueHint;
        private set => SetProperty(ref _hasPendingResidueHint, value);
    }

    private void OnResidueFound(object? sender, RubbishCleaner.Core.Residue.UninstallResidueResult result)
    {
        // 必须在 UI 线程上操作 ObservableCollection；该事件由卸载命令的
        // async  continuation 抛出，理论上已在 UI 线程，但显式Dispatcher更稳。
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => ApplyResidueResult(result));
        }
        else
        {
            ApplyResidueResult(result);
        }
    }

    private void ApplyResidueResult(RubbishCleaner.Core.Residue.UninstallResidueResult result)
    {
        Leftovers.ShowUninstallResidue(result);
        HasPendingResidueHint = true;
        Log.Info($"已把「{result.SoftwareName}」的卸载残留送到残留扫描页");
    }

    /// <summary>软件管理页的视图模型（v0.2）。</summary>
    public AppsViewModel Apps { get; } = new();

    /// <summary>
    /// 残留扫描页的视图模型（v0.3）。
    ///
    /// 三条数据依赖都从这里注入，而不是让 LeftoversViewModel 自己去读：
    /// <list type="bullet">
    /// <item>已安装软件列表 —— 与「已安装软件」页共享同一份读取逻辑；</item>
    /// <item>上一次启动项扫描结果 —— 用于"失效启动项 → 残留目录"的交叉验证。</item>
    /// </list>
    /// 这样两个页签看到的是<b>同一份事实</b>，不会出现"软件页说还装着、残留页说已被卸载"的自相矛盾。
    /// </summary>
    public LeftoversViewModel Leftovers { get; }

    /// <summary>
    /// 系统一致性报告页的视图模型（v0.5）。
    ///
    /// 最小接线：只在这里暴露，扫描器走 IntegrityViewModel 自己的可选注入默认实现。
    /// <b>v0.5 的产物是只读报告，永不自动清理</b>——这里同样只读暴露，不做任何二次包装。
    /// </summary>
    public IntegrityViewModel Integrity { get; } = new();

    /// <summary>
    /// 备份历史页的视图模型（v0.6）。
    ///
    /// 最小接线同 <see cref="Integrity"/>。这一页唯一的写操作是「回滚」（reg import
    /// 把删除前的键原样写回），由 BackupHistoryViewModel 的二次确认 + Store 的
    /// 三层防线保护；本类不做任何二次包装。
    /// </summary>
    public BackupHistoryViewModel BackupHistory { get; } = new();

    /// <summary>
    /// 设置页的视图模型（v0.7）：联网开关（默认关）、特征库在线更新、备份保留数量。
    ///
    /// 最小接线同 <see cref="BackupHistory"/>：设置加载/保存在 AppSettings，
    /// 更新逻辑在 TrustTableUpdater，清理逻辑在 IBackupHistoryStore——本类不做二次包装。
    /// </summary>
    public SettingsViewModel Settings { get; } = new();

    public ObservableCollection<SoftwareItemViewModel> Profiles { get; } = [];

    public ObservableCollection<SoftwareItemViewModel> FilteredProfiles { get; } = [];

    /// <summary>排序状态；表头点击后由此驱动重排。</summary>
    public SortState Sort { get; } = new();

    public ICommand ScanCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand RelaunchElevatedCommand { get; }

    public ICommand OpenLogFolderCommand { get; }

    public ICommand OpenContainingFolderCommand { get; }

    public ICommand ExportCsvCommand { get; }

    public ICommand ExportHtmlCommand { get; }

    public ICommand ShowAllCommand { get; }

    /// <summary>清理所有失效启动项（会先备份，再删除）。</summary>
    public ICommand CleanOrphansCommand { get; }

    /// <summary>打开备份目录。</summary>
    public ICommand OpenBackupFolderCommand { get; }

    public ICommand ShowHighRiskCommand { get; }

    public ICommand ShowSuspiciousCommand { get; }

    public ICommand ShowNormalCommand { get; }

    /// <summary>「仅看残留」筛选命令。</summary>
    public ICommand ShowOrphanCommand { get; }

    /// <summary>
    /// 失效启动项总数（含不可自动清理的类型，例如服务）。
    ///
    /// 注意与 <see cref="CleanableCount"/> 的区别：这个数字只用于"筛出并展示"，
    /// <b>不能</b>用来判断清理按钮是否可用——否则会出现"显示 4 条却点不了清理"的困惑（实测踩过）。
    /// </summary>
    public int OrphanCount => Profiles.Sum(p => p.OrphanCount);

    /// <summary>
    /// 可自动清理的残留条数（只统计 Run/RunOnce 且有注册表坐标的）。
    /// 服务与计划任务的残留会在列表里显示，但不计入这个数字，因为它们只能手动处理。
    /// </summary>
    public int CleanableCount => Profiles
        .SelectMany(p => p.Profile.StartupEntries)
        .Count(e => e.IsOrphan
                    && e.RegistryAnchor is not null
                    && e.Source is StartupSourceKind.RegistryRunHkcu
                        or StartupSourceKind.RegistryRunHklm
                        or StartupSourceKind.RegistryRunWow64);

    /// <summary>「筛选」按钮文案：只反映总数，因为它只负责筛出展示。</summary>
    public string OrphanFilterText => OrphanCount > 0 ? $"残留启动项（{OrphanCount}）" : "残留启动项";

    /// <summary>
    /// 「清理」按钮文案：把"总数"和"可清理数"都写出来，
    /// 这样残留全是服务类时会显示"清理残留启动项（可清理 0）"，
    /// 用户一眼就知道为什么点不动。
    /// </summary>
    public string CleanOrphansText
    {
        get
        {
            if (CleanableCount > 0)
            {
                return $"清理 {CleanableCount} 条";
            }

            // 有残留但都不能自动清理：直说"无可清理"，避免用户反复点击
            return OrphanCount > 0 ? "无需自动清理" : "清理残留启动项";
        }
    }

    /// <summary>清理按钮是否可用：只看可清理数，不看残留总数。</summary>
    public bool CanCleanOrphans => CleanableCount > 0 && !IsScanning;

    /// <summary>表头点击命令，参数为 <see cref="SortColumn"/>。</summary>
    public ICommand SortCommand { get; }

    /// <summary>确认对话框：返回 true 表示用户确认执行。可注入以便测试。</summary>
    public Func<string, string, bool> ConfirmHandler { get; set; }

    /// <summary>结果提示：可注入以便测试。</summary>
    public Action<string, string> NotifyHandler { get; set; }

    /// <summary>上次清理的备份文件路径（供 UI 提示"可还原"）。</summary>
    public string? LastBackupPath { get; private set; }

    /// <summary>备份目录：放在 LocalAppData 下，避免污染用户桌面。</summary>
    public static string BackupDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RubbishCleaner",
        "backups");

    public bool IsElevated { get; }

    public string TrustTableInfo { get; }

    // 表头文本。
    // 注意：不能写成 Content="{Binding XIndicator, StringFormat=等级 {0}}"——
    // ContentControl.Content 是 object 类型，Binding.StringFormat 只在目标属性为 string 时生效，
    // 结果会丢掉列名只剩箭头（实测踩过）。因此在这里直接拼好完整字符串。
    public string ScoreHeader => Compose("风险分", SortColumn.Score);

    public string LevelHeader => Compose("等级", SortColumn.Level);

    public string NameHeader => Compose("程序", SortColumn.Name);

    public string StartupHeader => Compose("启动方式", SortColumn.StartupSource);

    public string PublisherHeader => Compose("发行商 / 状态", SortColumn.Publisher);

    public string TrustHeader => Compose("特征库", SortColumn.Trust);

    private string Compose(string title, SortColumn column)
    {
        var indicator = Sort.IndicatorFor(column);
        return string.IsNullOrEmpty(indicator) ? title : $"{title} {indicator}";
    }

    public string EnvironmentReport
    {
        get => _environmentReport;
        private set => SetProperty(ref _environmentReport, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                OnPropertyChanged(nameof(IsNotScanning));
            }
        }
    }

    public bool IsNotScanning => !IsScanning;

    public bool HasScanned
    {
        get => _hasScanned;
        private set => SetProperty(ref _hasScanned, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string StageText
    {
        get => _stageText;
        private set => SetProperty(ref _stageText, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => SetProperty(ref _durationText, value);
    }

    public string CountsText
    {
        get => _countsText;
        private set => SetProperty(ref _countsText, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public SoftwareItemViewModel? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public int HighRiskCount => Profiles.Count(p => p.Level == RiskLevel.HighRisk);

    public int SuspiciousCount => Profiles.Count(p => p.Level == RiskLevel.Suspicious);

    public int NormalCount => Profiles.Count(p => p.Level == RiskLevel.Normal);

    public async Task ScanAsync()
    {
        IsScanning = true;
        HasScanned = false;
        ErrorText = null;
        Profiles.Clear();
        FilteredProfiles.Clear();
        StatusText = "正在扫描…";
        Progress = 0;
        _cts = new CancellationTokenSource();

        var progress = new Progress<ScanProgress>(p =>
        {
            Progress = p.Percent;
            StageText = string.IsNullOrWhiteSpace(p.Detail) ? p.Stage : $"{p.Stage}（{p.Detail}）";
        });

        try
        {
            var result = await _scanner.ScanAsync(progress, _cts.Token).ConfigureAwait(true);

            foreach (var profile in result.Profiles)
            {
                Profiles.Add(new SoftwareItemViewModel(profile));
            }

            ApplyFilter(_filterLevel);
            HasScanned = true;

            DurationText = $"{result.Duration.TotalSeconds:F1}s";

            var orphanCount = Profiles.Sum(p => p.OrphanCount);
            CountsText = $"共 {result.Profiles.Count} 项 · 高风险 {result.HighRiskCount} · 疑似 {result.SuspiciousCount} · 正常 {result.NormalCount} · 启动点 {result.TotalStartupEntries}"
                         + (orphanCount > 0 ? $" · 残留 {orphanCount}" : string.Empty);

            StatusText = result.DegradedSources.Count > 0
                ? $"扫描完成，但有 {result.DegradedSources.Count} 个数据源不完整"
                : orphanCount > 0
                    ? $"扫描完成，发现 {orphanCount} 条失效启动项（可自动清理 {CleanableCount} 条）"
                    : "扫描完成";

            if (result.DegradedSources.Count > 0)
            {
                ErrorText = "部分数据源不可用：" + string.Join("；", result.DegradedSources.Select(s => $"{s.Kind.ToDisplayName()} - {s.Error}"));
            }

            OnPropertyChanged(nameof(HighRiskCount));
            OnPropertyChanged(nameof(SuspiciousCount));
            OnPropertyChanged(nameof(NormalCount));
            OnPropertyChanged(nameof(OrphanCount));
            OnPropertyChanged(nameof(CleanableCount));
            OnPropertyChanged(nameof(OrphanFilterText));
            OnPropertyChanged(nameof(CleanOrphansText));
            OnPropertyChanged(nameof(CanCleanOrphans));

            // RelayCommand 的 CanExecute 依赖 OrphanCount，扫描前它是 0（按钮禁用），
            // 扫描后必须强制重新求值，否则按钮会一直灰着（实测踩过）。
            CommandManager.InvalidateRequerySuggested();

            StageText = string.Empty;
            Log.Info($"扫描完成：{result.Profiles.Count} 项，残留 {orphanCount} 条，用时 {result.Duration.TotalMilliseconds:F0}ms");
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消扫描";
            StageText = string.Empty;
        }
        finally
        {
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "正在停止…";
    }

    private void SortBy(object? parameter)
    {
        if (parameter is SortColumn column)
        {
            Sort.Toggle(column);
        }
        else if (parameter is string name && Enum.TryParse<SortColumn>(name, ignoreCase: true, out var parsed))
        {
            Sort.Toggle(parsed);
        }
    }

    private void OnSortChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ScoreHeader));
        OnPropertyChanged(nameof(LevelHeader));
        OnPropertyChanged(nameof(NameHeader));
        OnPropertyChanged(nameof(StartupHeader));
        OnPropertyChanged(nameof(PublisherHeader));
        OnPropertyChanged(nameof(TrustHeader));

        // 只重排当前可见列表，不改变底层 Profiles 的顺序（避免过滤切换时顺序丢失）
        ApplySorting();
    }

    private void ApplyFilter(RiskLevel? level)
    {
        _filterLevel = level;
        _filterOrphanOnly = false;
        RebuildFilteredList();

        StatusText = level is null
            ? "显示全部"
            : $"仅显示「{level.Value.ToDisplayName()}」";
    }

    /// <summary>只看启动项失效的残留记录。</summary>
    private void ApplyOrphanFilter()
    {
        _filterLevel = null;
        _filterOrphanOnly = true;
        RebuildFilteredList();

        StatusText = OrphanCount == 0
            ? "没有发现失效的启动项"
            : CleanableCount == OrphanCount
                ? $"仅显示残留启动项（{OrphanCount} 条），全部可自动清理"
                : $"仅显示残留启动项（{OrphanCount} 条，其中 {CleanableCount} 条可自动清理，其余需手动处理）";
    }

    /// <summary>
    /// 清理所有失效启动项。
    ///
    /// 安全设计：<b>先备份，备份失败就不删</b>。
    /// 备份是一个可双击还原的 .reg 文件，放在 LocalAppData\RubbishCleaner\backups。
    /// </summary>
    private async Task CleanOrphansAsync()
    {
        var plan = _cleaner.BuildPlan(Profiles.Select(p => p.Profile));

        if (plan.IsEmpty)
        {
            NotifyHandler("没有可清理的项",
                plan.Skipped.Count > 0
                    ? "发现失效启动项，但不属于可自动清理的类型：\n\n"
                      + string.Join('\n', plan.Skipped.Select(s => $"· {s.EntryName}：{s.Reason}"))
                    : "当前没有失效的启动项。");
            return;
        }

        // 二次确认：把"要删什么"逐条摆出来，而不是只给一个总数。
        var detail = string.Join('\n', plan.Targets.Select(t => $"· {t.Anchor.FullPath}"));
        var message =
            $"即将删除以下 {plan.Count} 条失效启动项：\n\n{detail}\n\n" +
            "这些启动项指向的文件已不存在，删除后不会再被开机加载。\n" +
            "删除前会自动备份到本地，可随时还原。";

        if (!ConfirmHandler("确认清理残留启动项", message))
        {
            StatusText = "已取消清理";
            return;
        }

        // 备份：失败就中止，绝不裸删。
        string backupPath;
        try
        {
            backupPath = _cleaner.WriteBackup(plan, BackupDirectory, DateTime.Now);
            LastBackupPath = backupPath;
            Log.Info($"启动项备份已生成：{backupPath}");
        }
        catch (Exception ex)
        {
            Log.Error("生成启动项备份失败，已中止清理", ex);
            NotifyHandler("无法备份，已取消清理",
                $"生成备份文件失败，为了安全没有执行任何删除：\n\n{ex.Message}");
            ErrorText = "备份失败，已取消清理（未修改注册表）";
            return;
        }

        var results = _cleaner.Execute(plan);
        var ok = results.Count(r => r.Success);
        var failed = results.Where(r => !r.Success).ToList();

        foreach (var result in results)
        {
            Log.Info($"清理 {result.Target.Anchor.FullPath}：{(result.Success ? "成功" : "失败 - " + result.Message)}");
        }

        StatusText = failed.Count == 0
            ? $"已清理 {ok} 条失效启动项，备份：{Path.GetFileName(backupPath)}"
            : $"清理完成：成功 {ok} 条，失败 {failed.Count} 条";

        if (failed.Count > 0)
        {
            NotifyHandler("部分项清理失败",
                string.Join('\n', failed.Select(f => $"· {f.Target.EntryName}：{f.Message}"))
                + $"\n\n备份文件：\n{backupPath}");
        }
        else
        {
            NotifyHandler("清理完成",
                $"已删除 {ok} 条失效启动项。\n\n备份文件（双击可还原）：\n{backupPath}");
        }

        // 重新扫描，让界面反映真实状态
        await ScanAsync();
    }

    /// <summary>按当前筛选条件重建可见列表，并套用排序。</summary>
    private void RebuildFilteredList()
    {
        FilteredProfiles.Clear();

        foreach (var profile in Profiles)
        {
            var matchesLevel = _filterLevel is null || profile.Level == _filterLevel;
            var matchesOrphan = !_filterOrphanOnly || profile.OrphanCount > 0;

            if (matchesLevel && matchesOrphan)
            {
                FilteredProfiles.Add(profile);
            }
        }

        ApplySorting();
    }

    /// <summary>按当前 <see cref="Sort"/> 状态对可见列表排序。</summary>
    private void ApplySorting()
    {
        if (FilteredProfiles.Count == 0)
        {
            return;
        }

        var byKey = FilteredProfiles.ToDictionary(p => p.Profile, p => p);
        var sorted = SoftwareSorter.Sort(FilteredProfiles.Select(p => p.Profile), Sort.Column, Sort.Direction);

        FilteredProfiles.Clear();
        foreach (var profile in sorted)
        {
            if (byKey.TryGetValue(profile, out var vm))
            {
                FilteredProfiles.Add(vm);
            }
        }
    }

    private void RelaunchElevated()
    {
        if (!RubbishCleaner.Common.Win32.PrivilegeHelper.TryRelaunchElevated())
        {
            ErrorText = "提权被取消，当前仍以标准权限运行。";
            return;
        }

        System.Windows.Application.Current.Shutdown();
    }

    private void OpenBackupFolder()
    {
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(BackupDirectory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ErrorText = $"无法打开备份目录：{ex.Message}";
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.Directory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ErrorText = $"无法打开日志目录：{ex.Message}";
        }
    }

    private void OpenContainingFolder(object? parameter)
    {
        if (parameter is not SoftwareItemViewModel { ExecutablePath: { } path })
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else
            {
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
                }
                else
                {
                    ErrorText = "目标文件与目录均不存在。";
                }
            }
        }
        catch (Exception ex)
        {
            ErrorText = $"无法打开目录：{ex.Message}";
        }
    }

    // ---------------- 报告导出 ----------------

    private void ExportCsv() => Export(ReportFormat.Csv);

    private void ExportHtml() => Export(ReportFormat.Html);

    /// <summary>
    /// 导出报告：生成交给 <see cref="ReportWriter"/>（可测试），
    /// 这里只负责选目录、写文件与打开资源管理器（副作用留在 ViewModel）。
    /// </summary>
    private void Export(ReportFormat format)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var path = ReportWriter.WriteTo(
                desktop,
                Profiles.Select(p => p.Profile),
                format,
                CountsText,
                TrustTableInfo,
                DateTime.Now);

            StatusText = $"报告已导出：{path}";
            Log.Info($"导出报告：{path}");
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            ErrorText = $"导出报告失败：{ex.Message}";
            Log.Error("导出报告失败", ex);
        }
    }

    /// <summary>
    /// 默认确认对话框。抽成方法而不是内联，是为了让测试能用委托替换掉它——
    /// 否则任何清理测试都会卡在模态弹窗上。
    /// </summary>
    private static bool ConfirmWithMessageBox(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning,
               MessageBoxResult.Cancel) == MessageBoxResult.OK;

    private static void NotifyWithMessageBox(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}