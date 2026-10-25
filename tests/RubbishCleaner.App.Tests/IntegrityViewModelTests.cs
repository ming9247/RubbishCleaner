using System.Reflection;
using RubbishCleaner.App.ViewModels;
using RubbishCleaner.Core.Integrity;

namespace RubbishCleaner.App.Tests;

/// <summary>
/// 系统一致性报告页（v0.5）视图模型测试。
///
/// 与注册表残留测试同一套路：<b>两个扫描器全部注入假实现</b>，不碰真实服务表与 MSI 数据库。
/// 断言的重点不是"扫描器判得准不准"（那是 Core 的事），而是：
/// "两项一起跑了没有 / 0 条与多条的文案对不对 / 一个方向失败会不会吞掉另一个 / 取消后有没有复位"。
///
/// 另有一条 v0.5 特有的铁律断言：<b>ViewModel 上不允许出现任何清理类命令</b>——
/// 这是把产品承诺写进测试，防止未来有人"顺手"加一个清理按钮。
/// </summary>
public class IntegrityViewModelTests
{
    private static ServiceOrphanEntry ServiceEntry(string name = "TestOrphanSvc") => new()
    {
        ServiceName = name,
        DisplayName = name + " 显示名",
        ImagePathRaw = "%ProgramFiles%\\Ghost\\svc.exe",
        ResolvedPath = @"C:\Program Files\Ghost\svc.exe",
        StartModeText = "自动",
        StateText = "已停止",
        Evidence = ["ImagePath 指向的文件不存在", "服务表由 SCM 维护，无按需注册陷阱"],
    };

    private static ServiceOrphanScanResult ServiceScan(params ServiceOrphanEntry[] entries) => new()
    {
        Entries = entries,
        ScannedCount = entries.Length + 40,
        SkippedCount = 6,
        Duration = TimeSpan.FromMilliseconds(50),
    };

    private static AppStateIssue Issue(AppStateIssueKind kind = AppStateIssueKind.InstallLocationMissing) => new()
    {
        Kind = kind,
        DisplayName = "测试软件",
        ProductCode = "{11111111-2222-3333-4444-555555555555}",
        Evidence = ["MSI 报告已安装，但 InstallLocation 目录不存在"],
        Suggestions = ["确认软件确实已卸载后，可用 msiexec /x {GUID} 清掉登记"],
    };

    private static AppStateScanResult AppStateScan(params AppStateIssue[] issues) => new()
    {
        Entries = issues,
        CheckedCount = issues.Length + 30,
        Duration = TimeSpan.FromMilliseconds(40),
    };

    /// <summary>假服务扫描器：按调用次序返回预设结果，可注入异常与人工取消钩子。</summary>
    private sealed class FakeServiceScanner : IServiceOrphanScanner
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Exception? Failure { get; set; }

        /// <summary>
        /// 非空时完全接管扫描行为：测试用它模拟"挂住直到取消""中途观察状态"等场景。
        /// 与 <see cref="Failure"/> 互斥，同时设置时 Failure 优先。
        /// </summary>
        public Func<Task<ServiceOrphanScanResult>>? Impl { get; set; }

        public Task<ServiceOrphanScanResult> ScanAsync(
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            progress?.Report("测试：正在检查服务");

            if (Failure is not null)
            {
                return Task.FromException<ServiceOrphanScanResult>(Failure);
            }

            if (Impl is not null)
            {
                return Impl();
            }

            return Task.FromResult(ServiceScan());
        }
    }

    /// <summary>假状态扫描器：结构与假服务扫描器一致。</summary>
    private sealed class FakeAppStateScanner : IAppStateScanner
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Exception? Failure { get; set; }

        public Func<CancellationToken, Task<AppStateScanResult>>? Impl { get; set; }

        public Task<AppStateScanResult> ScanAsync(
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            progress?.Report("测试：正在检查已安装软件");

            if (Failure is not null)
            {
                return Task.FromException<AppStateScanResult>(Failure);
            }

            if (Impl is not null)
            {
                return Impl(cancellationToken);
            }

            return Task.FromResult(AppStateScan());
        }
    }

    private static IntegrityViewModel Create(FakeServiceScanner service, FakeAppStateScanner appState)
        => new(service, appState);

    // ---------------- 扫描结果 ----------------

    /// <summary>两项扫描器都要被调用，结果分别进两个列表。</summary>
    [Fact]
    public async Task Scan_RunsBothScanners_AndFillsBothLists()
    {
        var service = new FakeServiceScanner();
        var appState = new FakeAppStateScanner();
        var vm = Create(service, appState);

        await vm.ScanAsync();

        Assert.Equal(1, service.Calls);
        Assert.Equal(1, appState.Calls);
        Assert.Equal(0, vm.ServiceCount);
        Assert.Equal(0, vm.IssueCount);
        Assert.True(vm.HasScanned);
        Assert.True(vm.ShowCleanState);
        Assert.False(vm.ShowEmptyState);
    }

    [Fact]
    public async Task Scan_WithEntries_ShowsBothKinds_AndMentionsNoAutoClean()
    {
        var service = new FakeServiceScanner();
        var appState = new FakeAppStateScanner();
        var vm = Create(service, appState);
        service.Impl = () => Task.FromResult(ServiceScan(ServiceEntry("A"), ServiceEntry("B")));
        appState.Impl = _ => Task.FromResult(AppStateScan(Issue(), Issue(AppStateIssueKind.CachedPackageMissing)));

        await vm.ScanAsync();

        Assert.Equal(2, vm.ServiceCount);
        Assert.Equal(2, vm.IssueCount);
        Assert.True(vm.ShowResultState);
        Assert.False(vm.ShowCleanState);

        // v0.5 承诺必须出现在总结文案里：只报告、不清理。
        Assert.Contains("永不自动清理", vm.SummaryText);
        Assert.StartsWith("发现 2 个服务孤儿、2 处状态不一致", vm.SummaryText);

        // 列表行的展示字段要能自解释。
        var row = vm.ServiceItems[0];
        Assert.Equal("A", row.ServiceName);
        Assert.Equal(@"C:\Program Files\Ghost\svc.exe", row.ResolvedPath);
        Assert.Equal("sc delete A", row.ManualCommand);

        var issueRow = vm.IssueItems[1];
        Assert.Equal("缓存安装包已被清理", issueRow.KindText);
        Assert.Contains("msiexec", issueRow.SuggestionsText);
    }

    [Fact]
    public async Task Scan_EmptyResult_SummarySaysCheckedCounts()
    {
        var service = new FakeServiceScanner();
        var appState = new FakeAppStateScanner();
        var vm = Create(service, appState);

        await vm.ScanAsync();

        // 干净结论要交代"看了多少条才敢说干净"，不能只说"没有发现问题"。
        Assert.Contains("已检查 40 个服务", vm.SummaryText);
        Assert.Contains("30 个已安装软件", vm.SummaryText);
        Assert.Contains("跳过 6 个非判定对象", vm.SummaryText);
    }

    // ---------------- 只读承诺 ----------------

    /// <summary>
    /// v0.5 铁律：本页不得暴露任何清理类命令。
    /// 用反射扫公开属性，未来有人加 CleanCommand / FixCommand 之类的属性会直接挂掉这条测试。
    /// </summary>
    [Fact]
    public void ViewModel_ExposesNoCleanCommands()
    {
        var vm = Create(new FakeServiceScanner(), new FakeAppStateScanner());

        var commandProperties = vm.GetType().GetProperties()
            .Where(p => typeof(System.Windows.Input.ICommand).IsAssignableFrom(p.PropertyType))
            .Select(p => p.Name)
            .ToList();

        // v0.6 起含导出命令（Export 写文件到桌面，不是注册表写操作，属正当报告能力）
        Assert.Equal(new[] { nameof(IntegrityViewModel.ScanCommand), nameof(IntegrityViewModel.CancelCommand), nameof(IntegrityViewModel.CopyTextCommand), nameof(IntegrityViewModel.ExportCsvCommand), nameof(IntegrityViewModel.ExportHtmlCommand) }.OrderBy(n => n), commandProperties.OrderBy(n => n));

        foreach (var name in commandProperties)
        {
            Assert.DoesNotContain("Clean", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Fix", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Delete", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Repair", name, StringComparison.OrdinalIgnoreCase);
        }

        // 方法层面同样不许有清理动作（清理"预留接口"也在禁止之列）。
        // 只检查自己声明的方法并排除属性访问器：ShowCleanState 这类"干净状态"是正当展示语义，
        // 编译器生成的 get_ 前缀访问器会把它带进来，误伤不代表有清理动作。
        var methodNames = vm.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)   // 排除 get_ / set_ 属性访问器与事件方法
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain(methodNames, n => n.Contains("Clean", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(methodNames, n => n.Contains("Delete", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(methodNames, n => n.Contains("Fix", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(methodNames, n => n.Contains("Repair", StringComparison.OrdinalIgnoreCase));

        // 契约层的"不可自动清理"是写死的 false，UI 行模型要原样带出来，便于详情区展示徽标。
        Assert.False(ServiceEntry().CanAutoClean);
        Assert.False(Issue().CanAutoFix);
    }

    [Fact]
    public async Task OnlyReadNotice_IsAlwaysVisibleText()
    {
        var vm = Create(new FakeServiceScanner(), new FakeAppStateScanner());

        // 没扫过时承诺文案就在（UI 顶部常驻），扫过之后仍在。
        Assert.Contains("永不自动清理", vm.OnlyReadNotice);
        await vm.ScanAsync();
        Assert.Contains("永不自动清理", vm.OnlyReadNotice);
    }

    // ---------------- 异常与隔离 ----------------

    /// <summary>一个方向抛异常不能吞掉另一个方向的结果：服务失败，状态检查的结果照常展示。</summary>
    [Fact]
    public async Task Scan_ServiceFailure_StillShowsAppStateResults_AndReportsError()
    {
        var service = new FakeServiceScanner { Failure = new InvalidOperationException("服务表读不了") };
        var appState = new FakeAppStateScanner();
        var vm = Create(service, appState);
        appState.Impl = _ => Task.FromResult(AppStateScan(Issue()));

        await vm.ScanAsync();

        Assert.True(vm.HasScanned);
        Assert.Equal(0, vm.ServiceCount);
        Assert.Equal(1, vm.IssueCount);
        Assert.True(vm.ShowResultState);

        // 错误要明说，且状态文案不能停留在"正在检查…"。
        Assert.NotNull(vm.ErrorText);
        Assert.Contains("服务孤儿检查失败", vm.ErrorText);
        Assert.Contains("服务表读不了", vm.ErrorText);
        Assert.StartsWith("检查完成", vm.StatusText);
    }

    /// <summary>反过来：状态检查失败，服务结果照常展示，错误同样上报。</summary>
    [Fact]
    public async Task Scan_AppStateFailure_StillShowsServiceResults_AndReportsError()
    {
        var service = new FakeServiceScanner();
        var appState = new FakeAppStateScanner { Failure = new InvalidOperationException("MSI 数据库打不开") };
        var vm = Create(service, appState);
        service.Impl = () => Task.FromResult(ServiceScan(ServiceEntry()));

        await vm.ScanAsync();

        Assert.Equal(1, vm.ServiceCount);
        Assert.Equal(0, vm.IssueCount);
        Assert.Contains("状态检查失败", vm.ErrorText);
        Assert.Contains("MSI 数据库打不开", vm.ErrorText);
    }

    /// <summary>两个方向同时失败：错误要<b>两条都在</b>，不能互相覆盖。</summary>
    [Fact]
    public async Task Scan_BothFail_BothErrorsVisible()
    {
        var service = new FakeServiceScanner { Failure = new InvalidOperationException("服务失败") };
        var appState = new FakeAppStateScanner { Failure = new InvalidOperationException("状态失败") };
        var vm = Create(service, appState);

        await vm.ScanAsync();

        Assert.Contains("服务孤儿检查失败：服务失败", vm.ErrorText);
        Assert.Contains("文件与注册表状态检查失败：状态失败", vm.ErrorText);
    }

    // ---------------- 降级 ----------------

    [Fact]
    public async Task Scan_DegradedResults_ShowsDegradedText()
    {
        var service = new FakeServiceScanner();
        var appState = new FakeAppStateScanner();
        var vm = Create(service, appState);
        service.Impl = () => Task.FromResult(ServiceScan() with { DegradedReason = "部分服务读取被拒绝" });
        appState.Impl = _ => Task.FromResult(AppStateScan() with { DegradedReason = "MSI 枚举不完整" });

        await vm.ScanAsync();

        Assert.Contains("部分服务读取被拒绝", vm.DegradedText);
        Assert.Contains("MSI 枚举不完整", vm.DegradedText);
    }

    // ---------------- 进度与取消 ----------------

    /// <summary>扫描过程中进度文案要实时反映当前阶段（两项分别汇报）。</summary>
    [Fact]
    public async Task Scan_ReportsProgressFromBothPhases()
    {
        var service = new FakeServiceScanner();
        var appState = new FakeAppStateScanner();
        var vm = Create(service, appState);

        // 不在 Impl 里抓第一阶段的文案：假扫描器同步返回，Impl 执行时
        // ScanAsync 里"正在检查…"的赋值还没轮到（同步代码先走完当前语句）。
        // 改从两个阶段的交界处验证——第一阶段结束后、第二阶段刚开始时，
        // 状态文本必须已经切到"已安装软件"。
        var seenBetweenPhases = (string?)null;
        appState.Impl = ct =>
        {
            // 此时服务阶段已结束、状态检查刚开始，StatusText 应是第二阶段的提示。
            seenBetweenPhases = vm.StatusText;
            return Task.FromResult(AppStateScan());
        };

        await vm.ScanAsync();

        Assert.NotNull(seenBetweenPhases);
        Assert.Contains("已安装软件", seenBetweenPhases);
        Assert.StartsWith("检查完成", vm.StatusText);
    }

    /// <summary>取消后：IsScanning 复位、状态说明"已取消"，且已经拿到的部分结果保留。</summary>
    [Fact]
    public async Task Scan_CancelDuringSecondPhase_KeepsFirstPhaseResults()
    {
        var service = new FakeServiceScanner();
        var appState = new FakeAppStateScanner();
        var vm = Create(service, appState);

        service.Impl = () => Task.FromResult(ServiceScan(ServiceEntry()));
        appState.Impl = _ =>
        {
            // 模拟"扫描器在第二阶段响应取消"：真实扫描器检查到 token 取消时就是这样抛的
            //（扫描器拿到的是 VM 传入的 token，这里直接抛等价异常即可，不必真的取消 VM 的私有 CTS）。
            throw new OperationCanceledException("模拟第二阶段被取消");
        };

        await vm.ScanAsync();

        Assert.False(vm.IsScanning);
        Assert.Equal("已取消检查", vm.StatusText);
        // 第一个方向的成果不因第二个方向被取消而丢弃。
        Assert.Equal(1, vm.ServiceCount);
    }

    /// <summary>扫描中途点取消：通过 ViewModel 自己的 token 取消正在等待的扫描器。</summary>
    [Fact]
    public async Task CancelCommand_CancelsOngoingScan()
    {
        var service = new FakeServiceScanner();
        var appState = new FakeAppStateScanner();
        var vm = Create(service, appState);

        var sawVmToken = false;

        // 服务阶段秒回，状态阶段挂在扫描器收到的 token 上：
        // 这个 token 是 VM 自己的 CTS 发出来的，CancelCommand 取消的就是它——
        // 由此验证"取消命令真的能把取消信号传进扫描器"。
        service.Impl = () => Task.FromResult(ServiceScan(ServiceEntry()));
        appState.Impl = async ct =>
        {
            sawVmToken = true;
            var tcs = new TaskCompletionSource();
            await using var reg = ct.Register(() => tcs.TrySetResult());
            await tcs.Task;
            ct.ThrowIfCancellationRequested();
            return AppStateScan();
        };

        var scanTask = vm.ScanAsync();
        Assert.True(vm.IsScanning);

        vm.CancelCommand.Execute(null);

        // 取消可能同步完成（延续内联执行，直接落"已取消检查"），
        // 也可能扫描器稍后响应（停在"正在停止…"）——两种都是合法的中间态。
        Assert.True(
            vm.StatusText == "正在停止…" || vm.StatusText == "已取消检查",
            $"取消后的中间状态不合法：{vm.StatusText}");

        await scanTask;

        Assert.True(sawVmToken);
        Assert.False(vm.IsScanning);
        Assert.Equal("已取消检查", vm.StatusText);
        // 取消前已完成的服务阶段成果应当保留。
        Assert.Equal(1, vm.ServiceCount);
    }

    /// <summary>扫描结束后 IsScanning 必须复位——漏复位会让扫描按钮永远灰着（残留页踩过）。</summary>
    [Fact]
    public async Task Scan_AlwaysResetsIsScanning_EvenOnFailure()
    {
        var service = new FakeServiceScanner { Failure = new Exception("boom") };
        var appState = new FakeAppStateScanner { Failure = new Exception("boom2") };
        var vm = Create(service, appState);

        await vm.ScanAsync();

        Assert.False(vm.IsScanning);
        Assert.True(vm.IsNotScanning);
    }

    /// <summary>扫描中再次触发扫描应当被忽略（防重入）。</summary>
    [Fact]
    public async Task Scan_IgnoresReentrantCall()
    {
        var gate = new TaskCompletionSource();
        var service = new FakeServiceScanner();
        var appState = new FakeAppStateScanner();
        var vm = Create(service, appState);

        service.Impl = async () =>
        {
            await gate.Task;
            return ServiceScan();
        };

        var first = vm.ScanAsync();
        var second = vm.ScanAsync();
        await second;   // 第二次应立即返回

        gate.TrySetResult();
        await first;

        Assert.Equal(1, service.Calls);
    }

    // ---------------- 命令可用性 ----------------

    [Fact]
    public void CancelCommand_DisabledWhenNotScanning()
    {
        var vm = Create(new FakeServiceScanner(), new FakeAppStateScanner());

        Assert.False(vm.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task ScanCommand_DisabledWhileScanning()
    {
        var gate = new TaskCompletionSource();
        var service = new FakeServiceScanner();
        var appState = new FakeAppStateScanner();
        var vm = Create(service, appState);

        service.Impl = async () =>
        {
            await gate.Task;
            return ServiceScan();
        };

        var scanTask = vm.ScanAsync();
        Assert.False(vm.ScanCommand.CanExecute(null));

        gate.TrySetResult();
        await scanTask;
        Assert.True(vm.ScanCommand.CanExecute(null));
    }

    // ---------------- 行模型 ----------------

    [Fact]
    public void ServiceRow_MapsFieldsAndHandlesMissingDisplayName()
    {
        var entry = ServiceEntry() with { DisplayName = null };
        var row = new ServiceOrphanItemViewModel(entry);

        Assert.Equal("（无显示名）", row.DisplayName);
        Assert.Equal("自动", row.StartModeText);
        Assert.Equal("已停止", row.StateText);
        Assert.NotEmpty(row.Evidence);
    }

    [Fact]
    public void IssueRow_MapsKindTexts()
    {
        Assert.Equal("安装目录已消失",
            new AppStateIssueItemViewModel(Issue(AppStateIssueKind.InstallLocationMissing)).KindText);
        Assert.Equal("缓存安装包已被清理",
            new AppStateIssueItemViewModel(Issue(AppStateIssueKind.CachedPackageMissing)).KindText);
        Assert.Equal("—", new AppStateIssueItemViewModel(Issue() with { ProductCode = null }).ProductCode);
    }
}
