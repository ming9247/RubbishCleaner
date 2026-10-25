using System.Collections.ObjectModel;
using System.Windows.Input;
using RubbishCleaner.App.Infrastructure;
using RubbishCleaner.Common.Logging;
using RubbishCleaner.Core.Integrity;

namespace RubbishCleaner.App.ViewModels;

/// <summary>服务孤儿列表中的一行。只做字段转发与中文展示文案，判定逻辑全部在 Core。</summary>
public sealed class ServiceOrphanItemViewModel(ServiceOrphanEntry entry)
{
    /// <summary>原始条目：详情区需要完整的证据链，不只看展示字段。</summary>
    public ServiceOrphanEntry Entry { get; } = entry;

    public string ServiceName => Entry.ServiceName;

    public string DisplayName => string.IsNullOrWhiteSpace(Entry.DisplayName) ? "（无显示名）" : Entry.DisplayName!;

    public string StartModeText => Entry.StartModeText;

    public string StateText => Entry.StateText;

    /// <summary>解析后确认不存在的路径。整行最关键的信息，用户要拿它去手动核对。</summary>
    public string ResolvedPath => Entry.ResolvedPath;

    public string ImagePathRaw => Entry.ImagePathRaw;

    public IReadOnlyList<string> Evidence => Entry.Evidence;

    /// <summary>手动处置命令（<c>sc delete 服务名</c>）。只给命令不代劳。</summary>
    public string ManualCommand => Entry.ManualCommand;
}

/// <summary>状态不一致列表中的一行。</summary>
public sealed class AppStateIssueItemViewModel(AppStateIssue issue)
{
    /// <summary>原始条目：详情区要展示完整的证据与建议。</summary>
    public AppStateIssue Issue { get; } = issue;

    /// <summary>不一致类型的中文说明。映射放在 VM 而不是让 Core 出中文：
    /// Core 只认得判据语义，"这条对用户意味着什么"是展示层的事。</summary>
    public string KindText => Issue.Kind switch
    {
        AppStateIssueKind.InstallLocationMissing => "安装目录已消失",
        AppStateIssueKind.CachedPackageMissing => "缓存安装包已被清理",
        _ => "状态不一致",
    };

    public string DisplayName => Issue.DisplayName;

    public string ProductCode => string.IsNullOrWhiteSpace(Issue.ProductCode) ? "—" : Issue.ProductCode!;

    public IReadOnlyList<string> Evidence => Issue.Evidence;

    public IReadOnlyList<string> Suggestions => Issue.Suggestions;

    /// <summary>详情区的建议合并成一段可复制文本，用户能整段粘走慢慢看。</summary>
    public string SuggestionsText => string.Join(Environment.NewLine, Issue.Suggestions);
}

/// <summary>
/// 系统一致性报告页（v0.5）。
///
/// 与残留页并列的第四个页签，回答的问题是：
/// <b>"系统自己是不是已经记错了？"</b>——服务表里挂着已消失的可执行文件、
/// Windows Installer 认为还装着但文件已经被清掉的软件。
///
/// <para><b>产品承诺（改这份代码前必须先读这段）</b>：本页是 v0.5 两份调研
/// （docs/technical/v0.5-孤儿CLSID调研.md / v0.5-孤儿文件关联调研.md）的最终结论——
/// 只做<b>只读报告，永不自动清理</b>。所以这个 ViewModel 里<b>不存在</b>清理命令、
/// 清理方法，连预留接口都没有（v0.3 的做法是让危险的事在类型上不可表达，这里沿用）。
/// 报告只提供手动命令与建议文本，处置权完全交给用户。</para>
///
/// <para>两个扫描器都走接口注入而不是就地 new：真实扫描要遍历服务表与读 MSI 数据库，
/// 注入假实现才能让"0 条 / 多条 / 异常 / 取消"这些分支在单测里触发。</para>
/// </summary>
public sealed class IntegrityViewModel : ObservableObject
{
    private readonly IServiceOrphanScanner _serviceScanner;
    private readonly IAppStateScanner _appStateScanner;
    private CancellationTokenSource? _cts;

    /// <summary>最近一次服务扫描结果（SummaryText 与覆盖面数字的数据源）。</summary>
    private ServiceOrphanScanResult? _serviceResult;

    /// <summary>最近一次状态扫描结果。</summary>
    private AppStateScanResult? _appStateResult;

    private bool _isScanning;
    private bool _hasScanned;
    private string _statusText = "点击「检查系统一致性」只读检查服务表与已安装软件记录";
    private string? _errorText;
    private string? _degradedText;
    private ServiceOrphanItemViewModel? _selectedService;
    private AppStateIssueItemViewModel? _selectedIssue;

    public IntegrityViewModel()
        : this(null, null)
    {
    }

    /// <summary>
    /// 注入两个扫描器。参数带默认值（传 <c>null</c> 即用生产实现），
    /// 与 <see cref="LeftoversViewModel"/> 的可选注入风格一致：既有调用点无需改动即可编译。
    /// </summary>
    public IntegrityViewModel(IServiceOrphanScanner? serviceScanner, IAppStateScanner? appStateScanner)
    {
        // 两个扫描器的生产实现都在 Core.Integrity（只读）。
        // 服务扫描器读真实注册表与 SCM，状态扫描器读真实 MSI 数据库——
        // 都走可选注入：测试路径注入假实现，生产路径用这里的默认值。
        _serviceScanner = serviceScanner ?? new Core.Integrity.ServiceOrphanScanner();
        _appStateScanner = appStateScanner ?? new Core.Integrity.AppStateScanner(DefaultInstalledApps);

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsScanning)
        {
            OnError = ex =>
            {
                Log.Error("系统一致性检查失败", ex);
                ErrorText = $"检查失败：{ex.Message}";
                IsScanning = false;
            },
        };

        CancelCommand = new RelayCommand(Cancel, () => IsScanning);

        // 导出：有结果才可用。写文件到桌面并打开资源管理器（对齐残留页导出的 UX）。
        ExportCsvCommand = new RelayCommand(
            () => Export(Infrastructure.ReportFormat.Csv),
            () => _serviceResult is not null && _appStateResult is not null && !IsScanning);
        ExportHtmlCommand = new RelayCommand(
            () => Export(Infrastructure.ReportFormat.Html),
            () => _serviceResult is not null && _appStateResult is not null && !IsScanning);

        // 手动命令复制：让用户把命令拿到自己开的管理员终端里自己执行，工具不代劳。
        CopyTextCommand = new RelayCommand(
            p => TryCopy(p?.ToString() ?? string.Empty),
            p => p is string s && s.Length > 0);
    }

    /// <summary>
    /// 默认的已安装软件来源，与 <see cref="LeftoversViewModel.DefaultInstalledApps"/> 同一策略：
    /// 读不到时返回空结果而不是抛——枚举失败只应让结果降级，不应让整个检查页崩掉。
    /// </summary>
    private static Core.Uninstall.InstalledAppsResult DefaultInstalledApps()
    {
        try
        {
            return new Core.Uninstall.InstalledAppEnumerator().Enumerate();
        }
        catch (Exception ex)
        {
            Log.Error("一致性检查读取已安装软件失败", ex);
            return new Core.Uninstall.InstalledAppsResult([], 0, 0, ex.Message);
        }
    }

    /// <summary>
    /// 导出一致性报告（CSV/HTML）。
    ///
    /// <para>与既有残留页导出同一套 UX：Content 由 XAML 传格式，弹保存框与写文件在
    /// <see cref="IntegrityReportWriter.WriteTo"/>（纯函数）+ 调用方完成。</para>
    /// </summary>
    public string ExportReport(
        Infrastructure.ReportFormat format, ServiceOrphanScanResult services, AppStateScanResult appStates)
    {
        var summary = SummaryText;
        return IntegrityReportWriter.Build(services, appStates, format, summary, DateTime.Now);
    }

    /// <summary>最近一次服务孤儿扫描结果（导出与测试用；未扫描时为 null）。</summary>
    public ServiceOrphanScanResult? LastServiceResult => _serviceResult;

    /// <summary>最近一次状态不一致扫描结果（导出与测试用；未扫描时为 null）。</summary>
    public AppStateScanResult? LastAppStateResult => _appStateResult;

    /// <summary>导出为 CSV（写到桌面并打开所在目录，对齐残留页导出的 UX）。</summary>
    public ICommand ExportCsvCommand { get; }

    /// <summary>导出为 HTML（同上）。</summary>
    public ICommand ExportHtmlCommand { get; }

    /// <summary>导出的统一入口（副作用留在这里，Build 保持纯函数）。</summary>
    private void Export(ReportFormat format)
    {
        var services = _serviceResult;
        var appStates = _appStateResult;
        if (services is null || appStates is null)
        {
            StatusText = "还没有检查结果可导出，请先执行检查";
            return;
        }

        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var path = IntegrityReportWriter.WriteTo(
                desktop, services, appStates, format, SummaryText, DateTime.Now);

            StatusText = $"一致性报告已导出：{path}";
            Log.Info($"导出一致性报告：{path}");
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            ErrorText = $"导出失败：{ex.Message}";
            Log.Error("导出一致性报告失败", ex);
        }
    }

    /// <summary>执行一次只读一致性检查：两个扫描器<b>串行</b>跑，分别汇报进度与结果。</summary>
    public async Task ScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;
        _cts = new CancellationTokenSource();
        HasScanned = false;
        ErrorText = null;
        DegradedText = null;
        StatusText = "正在检查系统一致性…";
        ServiceItems.Clear();
        IssueItems.Clear();
        SelectedService = null;
        SelectedIssue = null;
        _serviceResult = null;
        _appStateResult = null;
        NotifyCountsChanged();

        try
        {
            var progress = new Progress<string>(text => StatusText = text);

            // 两个方向彼此独立，一个失败不应吞掉另一个的结果：
            // 每个方向单独 try/catch，失败写进 ErrorText 后继续跑下一个。
            var serviceError = (Exception?)null;

            try
            {
                var serviceResult = await _serviceScanner.ScanAsync(progress, _cts.Token).ConfigureAwait(true);
                _serviceResult = serviceResult;

                foreach (var entry in serviceResult.Entries)
                {
                    ServiceItems.Add(new ServiceOrphanItemViewModel(entry));
                }

                NotifyCountsChanged();
                Log.Info($"服务孤儿检查：{serviceResult.ScannedCount} 个服务中发现 {serviceResult.Entries.Count} 个孤儿，用时 {serviceResult.Duration.TotalMilliseconds:F0}ms");
            }
            catch (OperationCanceledException)
            {
                throw;  // 用户取消不属于"方向失败"，直接抛给外层的取消分支
            }
            catch (Exception ex)
            {
                serviceError = ex;
                Log.Error("服务孤儿检查失败", ex);
            }

            StatusText = "正在检查已安装软件与文件记录…";

            try
            {
                var appStateResult = await _appStateScanner.ScanAsync(progress, _cts.Token).ConfigureAwait(true);
                _appStateResult = appStateResult;

                foreach (var issue in appStateResult.Entries)
                {
                    IssueItems.Add(new AppStateIssueItemViewModel(issue));
                }

                NotifyCountsChanged();
                Log.Info($"状态一致性检查：{appStateResult.CheckedCount} 个软件中发现 {appStateResult.Entries.Count} 处不一致，用时 {appStateResult.Duration.TotalMilliseconds:F0}ms");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("状态一致性检查失败", ex);
                AppendError("文件与注册表状态检查失败：" + ex.Message);
            }

            // 第一个方向的失败在这里补报：直接写在第一个 catch 里会被第二个方向的
            // ScanAsync 开头对 ErrorText 的使用顺序搞乱，集中到结果汇报前统一追加。
            if (serviceError is not null)
            {
                AppendError("服务孤儿检查失败：" + serviceError.Message);
            }

            HasScanned = true;
            NotifyCountsChanged();

            // 降级说明每次扫描都要重建：沿用上一轮的会让用户以为这次也降级了。
            var degradedParts = new List<string>();
            if (_serviceResult?.IsDegraded == true)
            {
                degradedParts.Add(_serviceResult.DegradedReason!);
            }

            if (_appStateResult?.IsDegraded == true)
            {
                degradedParts.Add(_appStateResult.DegradedReason!);
            }

            DegradedText = degradedParts.Count == 0 ? null : string.Join("\n", degradedParts);

            StatusText = ErrorText is not null
                ? "检查完成，但部分数据源不可用（见下方错误说明）"
                : HasIssues
                    ? $"检查完成：发现 {ServiceCount + IssueCount} 处一致性问题（服务孤儿 {ServiceCount}、状态不一致 {IssueCount}）"
                    : $"检查完成：已检查 {ServiceCheckedCount} 个服务、{AppStateCheckedCount} 个已安装软件记录，未发现一致性问题";

            Log.Info($"系统一致性检查完成：服务孤儿 {ServiceCount}，状态不一致 {IssueCount}");
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消检查";
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
        // 提示要写在取消动作<b>之前</b>：CTS.Cancel 默认会<b>内联执行</b>已注册的延续回调，
        // 若扫描器正好挂在这个 token 上，整条取消链（ScanAsync 的取消分支，含"已取消检查"）
        // 会在 _cts.Cancel() 这一行同步跑完——之后任何覆盖都会把最终状态盖回去。
        // 两种时序都能得到正确的最终文案：
        // · 同步完成 → "正在停止…"被取消分支覆盖成"已取消检查"；
        // · 扫描器稍后响应 → "正在停止…"保留，等 ScanAsync 自己收尾。
        StatusText = "正在停止…";

        try
        {
            _cts?.Cancel();
        }
        catch (OperationCanceledException)
        {
            // 回调沿 await 链同步抛回的取消异常：善后已由 ScanAsync 的取消分支完成，
            // 这里不能让异常冲出命令边界——AsyncRelayCommand 之外没有兜底，会砸掉 UI 线程。
        }
    }

    /// <summary>写进 ErrorText（追加而不是覆盖：两个方向可能同时失败，都要能被看到）。</summary>
    private void AppendError(string message)
        => ErrorText = string.IsNullOrWhiteSpace(ErrorText) ? message : ErrorText + "\n" + message;

    public ObservableCollection<ServiceOrphanItemViewModel> ServiceItems { get; } = [];

    public ObservableCollection<AppStateIssueItemViewModel> IssueItems { get; } = [];

    public ICommand ScanCommand { get; }

    public ICommand CancelCommand { get; }

    /// <summary>复制任意文本到剪贴板（参数为文本）。本页只有复制命令，没有、也不允许有清理命令。</summary>
    public ICommand CopyTextCommand { get; }

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

    /// <summary>
    /// 是否扫过一次。
    ///
    /// 与 <see cref="HasIssues"/> 分开：<b>"还没扫"</b>和<b>"扫过且系统是干净的"</b>
    /// 是两件完全不同的事，共用一个判断会把两者压成同一句"没有数据"（残留页 HasScanned 同理）。
    /// </summary>
    public bool HasScanned
    {
        get => _hasScanned;
        private set
        {
            if (SetProperty(ref _hasScanned, value))
            {
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(ShowCleanState));
                OnPropertyChanged(nameof(ShowResultState));
            }
        }
    }

    /// <summary>空状态：还没扫过（提示"去点检查"）。</summary>
    public bool ShowEmptyState => !HasScanned;

    /// <summary>"干净"状态：扫过、两类问题都是 0 条（这是好消息，要单独说）。</summary>
    public bool ShowCleanState => HasScanned && !HasIssues;

    /// <summary>"有内容"状态：至少有一类问题要展示。</summary>
    public bool ShowResultState => HasScanned && HasIssues;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public string? DegradedText
    {
        get => _degradedText;
        private set => SetProperty(ref _degradedText, value);
    }

    /// <summary>服务孤儿条数。</summary>
    public int ServiceCount => ServiceItems.Count;

    /// <summary>状态不一致条数。</summary>
    public int IssueCount => IssueItems.Count;

    /// <summary>是否发现任何一致性问题。</summary>
    public bool HasIssues => ServiceCount > 0 || IssueCount > 0;

    private int ServiceCheckedCount => _serviceResult?.ScannedCount ?? 0;

    private int ServiceSkippedCount => _serviceResult?.SkippedCount ?? 0;

    private int AppStateCheckedCount => _appStateResult?.CheckedCount ?? 0;

    /// <summary>
    /// 语义化总结文案，学 v0.4 的 RegistrySummaryText：把
    /// <b>"检查了多少条、发现几条、为什么只报告不清理"</b>一次说清楚。
    /// 拼好的完整中文句子放在 VM 而不是让 XAML 用 Run 拼，改文案时只动一处。
    /// </summary>
    public string SummaryText
    {
        get
        {
            if (!HasScanned)
            {
                return "还没有检查";
            }

            if (!HasIssues)
            {
                return $"系统一致：已检查 {ServiceCheckedCount} 个服务（另跳过 {ServiceSkippedCount} 个非判定对象）"
                     + $"、{AppStateCheckedCount} 个已安装软件记录，没有发现问题";
            }

            return $"发现 {ServiceCount} 个服务孤儿、{IssueCount} 处状态不一致"
                 + $"（已检查 {ServiceCheckedCount} 个服务、{AppStateCheckedCount} 个已安装软件记录）。"
                 + "本页只读报告、永不自动清理，请按各条目的手动命令自行处置。";
        }
    }

    /// <summary>v0.5 的产品承诺，在 UI 上明说。文案集中在这里而不是散在 XAML 各处。</summary>
    public string OnlyReadNotice
        => "本页只做只读报告，永不自动清理任何服务或注册表条目——每条报告都附带手动命令与建议，处置权在你。";

    public ServiceOrphanItemViewModel? SelectedService
    {
        get => _selectedService;
        set
        {
            if (SetProperty(ref _selectedService, value))
            {
                OnPropertyChanged(nameof(HasSelectedService));
            }
        }
    }

    public AppStateIssueItemViewModel? SelectedIssue
    {
        get => _selectedIssue;
        set
        {
            if (SetProperty(ref _selectedIssue, value))
            {
                OnPropertyChanged(nameof(HasSelectedIssue));
            }
        }
    }

    public bool HasSelectedService => SelectedService is not null;

    public bool HasSelectedIssue => SelectedIssue is not null;

    /// <summary>列表与统计变化后统一通知：漏掉任何一条派生属性都会出现"数据对了但界面不刷新"。</summary>
    private void NotifyCountsChanged()
    {
        OnPropertyChanged(nameof(ServiceCount));
        OnPropertyChanged(nameof(IssueCount));
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(ShowCleanState));
        OnPropertyChanged(nameof(ShowResultState));
        OnPropertyChanged(nameof(SummaryText));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// 复制文本到剪贴板，失败只记日志不弹窗：剪贴板被其他进程占用在 Windows 上是常态，
    /// 为一次复制打断用户不值得；命令可注入测试（CopyTextCommand 走的 RelayCommand 参数就是文本本身）。
    /// </summary>
    private void TryCopy(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Log.Error("复制文本到剪贴板失败", ex);
        }
    }
}
