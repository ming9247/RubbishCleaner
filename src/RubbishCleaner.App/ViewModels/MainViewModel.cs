using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
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
        SortCommand = new RelayCommand(SortBy);

        Sort.PropertyChanged += OnSortChanged;
    }

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

    public ICommand ShowHighRiskCommand { get; }

    public ICommand ShowSuspiciousCommand { get; }

    public ICommand ShowNormalCommand { get; }

    /// <summary>「仅看残留」筛选命令。</summary>
    public ICommand ShowOrphanCommand { get; }

    /// <summary>失效启动项总数（用于按钮文案与可用性）。</summary>
    public int OrphanCount => Profiles.Sum(p => p.OrphanCount);

    /// <summary>「清理」分组按钮文案，带实时计数。</summary>
    public string OrphanFilterText => OrphanCount > 0 ? $"残留启动项（{OrphanCount}）" : "残留启动项";

    /// <summary>表头点击命令，参数为 <see cref="SortColumn"/>。</summary>
    public ICommand SortCommand { get; }

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
                    ? $"扫描完成，发现 {orphanCount} 条失效启动项（可清理）"
                    : "扫描完成";

            if (result.DegradedSources.Count > 0)
            {
                ErrorText = "部分数据源不可用：" + string.Join("；", result.DegradedSources.Select(s => $"{s.Kind.ToDisplayName()} - {s.Error}"));
            }

            OnPropertyChanged(nameof(HighRiskCount));
            OnPropertyChanged(nameof(SuspiciousCount));
            OnPropertyChanged(nameof(NormalCount));
            OnPropertyChanged(nameof(OrphanCount));
            OnPropertyChanged(nameof(OrphanFilterText));

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

        StatusText = OrphanCount > 0
            ? $"仅显示残留启动项（{OrphanCount} 条），可安全清理"
            : "没有发现失效的启动项";
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
}