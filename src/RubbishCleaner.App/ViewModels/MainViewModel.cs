using System.Collections.ObjectModel;
using System.IO;
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
        ExportReportCommand = new RelayCommand(ExportReport, () => Profiles.Count > 0);
        ShowAllCommand = new RelayCommand(() => ApplyFilter(null));
        ShowHighRiskCommand = new RelayCommand(() => ApplyFilter(RiskLevel.HighRisk));
        ShowSuspiciousCommand = new RelayCommand(() => ApplyFilter(RiskLevel.Suspicious));
        ShowNormalCommand = new RelayCommand(() => ApplyFilter(RiskLevel.Normal));
    }

    public ObservableCollection<SoftwareItemViewModel> Profiles { get; } = [];

    public ObservableCollection<SoftwareItemViewModel> FilteredProfiles { get; } = [];

    public ICommand ScanCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand RelaunchElevatedCommand { get; }

    public ICommand OpenLogFolderCommand { get; }

    public ICommand OpenContainingFolderCommand { get; }

    public ICommand ExportReportCommand { get; }

    public ICommand ShowAllCommand { get; }

    public ICommand ShowHighRiskCommand { get; }

    public ICommand ShowSuspiciousCommand { get; }

    public ICommand ShowNormalCommand { get; }

    public bool IsElevated { get; }

    public string TrustTableInfo { get; }

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
            CountsText = $"共 {result.Profiles.Count} 项 · 高风险 {result.HighRiskCount} · 疑似 {result.SuspiciousCount} · 正常 {result.NormalCount} · 启动点 {result.TotalStartupEntries}";

            StatusText = result.DegradedSources.Count > 0
                ? $"扫描完成，但有 {result.DegradedSources.Count} 个数据源不完整"
                : "扫描完成";

            if (result.DegradedSources.Count > 0)
            {
                ErrorText = "部分数据源不可用：" + string.Join("；", result.DegradedSources.Select(s => $"{s.Kind.ToDisplayName()} - {s.Error}"));
            }

            OnPropertyChanged(nameof(HighRiskCount));
            OnPropertyChanged(nameof(SuspiciousCount));
            OnPropertyChanged(nameof(NormalCount));
            StageText = string.Empty;
            Log.Info($"扫描完成：{result.Profiles.Count} 项，用时 {result.Duration.TotalMilliseconds:F0}ms");
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

    private void ApplyFilter(RiskLevel? level)
    {
        _filterLevel = level;
        FilteredProfiles.Clear();
        foreach (var profile in Profiles.Where(p => level is null || p.Level == level))
        {
            FilteredProfiles.Add(profile);
        }

        StatusText = level is null
            ? "显示全部"
            : $"仅显示「{level.Value.ToDisplayName()}」";
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

    private void ExportReport()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"RubbishCleaner-报告-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            var lines = new List<string>
            {
                "RubbishCleaner 扫描报告",
                $"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"运行环境：{EnvironmentReport.Replace(Environment.NewLine, " ")}",
                $"统计：{CountsText}",
                new string('-', 80),
            };

            foreach (var item in Profiles)
            {
                lines.Add($"[{item.LevelText}] {item.DisplayName}  分数 {item.TotalScore}");
                lines.Add($"  路径   : {item.ExecutablePath ?? "（未知）"}");
                lines.Add($"  启动方式: {item.StartupSummary}");
                lines.Add($"  发行商 : {item.Publisher ?? "（无签名）"}");
                foreach (var evidence in item.Evidences)
                {
                    lines.Add($"  · {evidence.Message}");
                }

                lines.Add(string.Empty);
            }

            File.WriteAllLines(path, lines);
            StatusText = $"报告已导出：{path}";
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            ErrorText = $"导出报告失败：{ex.Message}";
        }
    }
}
