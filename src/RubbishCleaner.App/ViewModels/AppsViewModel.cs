using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using RubbishCleaner.App.Infrastructure;
using RubbishCleaner.Common.Logging;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Uninstall;

namespace RubbishCleaner.App.ViewModels;

/// <summary>单个已安装软件的展示项。</summary>
public sealed class AppItemViewModel(InstalledApp app)
{
    public InstalledApp App { get; } = app;

    public string DisplayName => App.DisplayName;

    public string? DisplayVersion => App.DisplayVersion;

    public string VersionText => string.IsNullOrWhiteSpace(App.DisplayVersion) ? "—" : App.DisplayVersion!;

    public string Publisher => App.Publisher ?? "（未知发行商）";

    public string ScopeText => App.Scope == AppScope.Machine ? "所有用户" : "当前用户";

    public string ArchText => App.Is32Bit ? "32 位" : "64 位";

    public string SizeText => App.EstimatedSizeKb > 0
        ? App.EstimatedSizeKb >= 1024
            ? $"{App.EstimatedSizeKb / 1024.0:F1} MB"
            : $"{App.EstimatedSizeKb} KB"
        : "—";

    /// <summary>注册表文本被污染的条目标记，提示用户"名字可能不完整"。</summary>
    public bool HasCorruptedText => App.HasCorruptedText;

    public string KindText => App.IsMsi ? "MSI" : "EXE";

    /// <summary>卸载前检查结果（惰性求值：会查进程，不必每次构造都跑）。</summary>
    private UninstallPrecheck? _precheck;

    public UninstallPrecheck Precheck => _precheck ??= UninstallPrechecker.Check(App);

    /// <summary>是否允许执行卸载（预检查通过）。</summary>
    public bool CanUninstall => App.CanUninstall && Precheck.CanProceed;

    public bool CanUninstallSilently => App.CanUninstallSilently;

    /// <summary>不能卸载的原因（用于列表提示与工具提示）。</summary>
    public string? BlockReason => Precheck.BlockReason;

    public bool IsBlocked => Precheck.BlockReason is not null;

    /// <summary>卸载前的警告（如进程正在运行、注册表文本异常）。</summary>
    public string WarningText => Precheck.Warnings is { Count: > 0 }
        ? string.Join("；", Precheck.Warnings)
        : string.Empty;

    public bool HasWarning => Precheck.Warnings is { Count: > 0 };

    /// <summary>卸载命令预览，让用户在执行前能看到到底会跑什么。</summary>
    public string UninstallCommandText => App.UninstallCommand ?? "（无）";
}

/// <summary>
/// 软件管理视图模型（v0.2）。
///
/// 与"启动项"页分开：两者关注点不同——
/// 启动项页回答"什么在开机时偷偷跑"，软件页回答"我装了什么、怎么卸掉"。
/// </summary>
public sealed class AppsViewModel : ObservableObject
{
    private readonly InstalledAppEnumerator _enumerator = new();
    private readonly UninstallExecutor _executor = new();

    private bool _isLoading;
    private string _statusText = "点击「刷新列表」读取已安装软件";
    private string _summaryText = string.Empty;
    private string? _errorText;
    private string _filterText = string.Empty;
    private bool _hasLoaded;
    private AppItemViewModel? _selected;

    public AppsViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsLoading)
        {
            OnError = ex =>
            {
                Log.Error("读取已安装软件失败", ex);
                ErrorText = $"读取失败：{ex.Message}";
                IsLoading = false;
            },
        };

        UninstallCommand = new AsyncRelayCommand(UninstallSelectedAsync, () => Selected is { CanUninstall: true } && !IsLoading)
        {
            OnError = ex =>
            {
                Log.Error("卸载失败", ex);
                ErrorText = $"卸载失败：{ex.Message}";
            },
        };

        PruneCommand = new RelayCommand(() => FilterText = string.Empty);
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

    public ObservableCollection<AppItemViewModel> Apps { get; } = [];

    public ObservableCollection<AppItemViewModel> FilteredApps { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand UninstallCommand { get; }

    /// <summary>清空搜索框。</summary>
    public ICommand PruneCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool HasLoaded
    {
        get => _hasLoaded;
        private set => SetProperty(ref _hasLoaded, value);
    }

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

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
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

    public AppItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>读取已安装软件列表。</summary>
    public async Task RefreshAsync()
    {
        IsLoading = true;
        ErrorText = null;
        StatusText = "正在读取已安装软件…";

        try
        {
            var result = await Task.Run(() => _enumerator.Enumerate()).ConfigureAwait(true);

            Apps.Clear();
            foreach (var app in result.Apps)
            {
                Apps.Add(new AppItemViewModel(app));
            }

            ApplyFilter();
            HasLoaded = true;

            var corrupted = result.Apps.Count(a => a.HasCorruptedText);
            SummaryText = $"共 {result.Apps.Count} 个软件"
                          + (result.FilteredOut > 0 ? $"（已过滤 {result.FilteredOut} 个系统组件/无卸载命令项）" : string.Empty)
                          + (corrupted > 0 ? $" · {corrupted} 个条目注册表文本异常" : string.Empty);

            StatusText = result.HasError
                ? "读取完成，但部分注册表位置不可访问"
                : "读取完成";

            if (result.HasError)
            {
                ErrorText = result.Error;
            }

            Log.Info($"已安装软件：{result.Apps.Count} 个（原始 {result.TotalRawEntries}）");
        }
        finally
        {
            IsLoading = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// 卸载选中的软件。
    ///
    /// <b>不代劳的部分</b>：卸载程序本身由软件的安装器负责，
    /// 我们只负责"确认 → 启动 → 等待 → 提示结果"，不做任何强制删除。
    /// </summary>
    private async Task UninstallSelectedAsync()
    {
        var item = Selected;
        if (item is null || !item.App.CanUninstall)
        {
            return;
        }

        // 预检查：不通过就明确拒绝，并把手动处置办法告诉用户
        var precheck = item.Precheck;
        if (!precheck.CanProceed)
        {
            Log.Warn($"拒绝卸载 {item.DisplayName}：{precheck.BlockReason}");
            StatusText = $"无法卸载「{item.DisplayName}」";
            NotifyHandler("无法卸载", precheck.BlockReason ?? "未通过卸载前检查");
            return;
        }

        // 有进程在跑就提醒：卸载程序遇到占用通常会失败或留残留
        if (precheck.Warnings is { Count: > 0 })
        {
            var proceed = ConfirmHandler("卸载前请注意",
                $"「{item.DisplayName}」存在以下情况：\n\n"
                + string.Join('\n', precheck.Warnings.Select(w => "· " + w))
                + "\n\n仍要继续吗？");
            if (!proceed)
            {
                StatusText = "已取消卸载";
                return;
            }
        }

        var message =
            $"即将启动「{item.DisplayName}」的卸载程序：\n\n{item.UninstallCommandText}\n\n" +
            "接下来会由该软件自己的卸载程序接管，请按它的提示完成卸载。\n" +
            "本工具不会替你强制删除任何文件。";

        // 卸载不可逆，必须二次确认，且把实际会执行的命令摆出来
        if (!ConfirmHandler("确认卸载", message))
        {
            StatusText = "已取消卸载";
            return;
        }

        StatusText = $"正在等待「{item.DisplayName}」的卸载程序结束…";

        var outcome = await Task.Run(() => _executor.Execute(item.App.UninstallCommand)).ConfigureAwait(true);

        Log.Info($"卸载 {item.DisplayName}：Started={outcome.Started} Completed={outcome.Completed} Exit={outcome.ExitCode} {outcome.Message}");

        StatusText = outcome.Completed
            ? $"「{item.DisplayName}」卸载程序已结束"
            : $"「{item.DisplayName}」{outcome.Message}";

        NotifyHandler(outcome.Completed ? "卸载程序已结束" : "卸载未完成", outcome.Message);

        // 卸载后刷新列表，让用户看到真实变化
        await RefreshAsync();
    }

    private void ApplyFilter()
    {
        FilteredApps.Clear();

        var keyword = FilterText.Trim();
        foreach (var app in Apps)
        {
            if (keyword.Length == 0
                || app.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || app.Publisher.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                FilteredApps.Add(app);
            }
        }
    }
}
