using System.IO;
using Microsoft.Win32;
using RubbishCleaner.App.ViewModels;
using RubbishCleaner.Core.Leftover;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.RegistryCleanup;
using RubbishCleaner.Core.Uninstall;

namespace RubbishCleaner.App.Tests;

/// <summary>
/// 残留页的注册表区块（v0.4）视图模型测试。
///
/// 与 <see cref="LeftoversViewModelTests"/> 同一套路：<b>全部依赖都注入假实现</b>，
/// 断言的重点不是"注册表能不能删"（那是 Core 的事），而是
/// "扫到几条 / 按钮说话了没有 / 该不该弹确认 / 用户取消后有没有动手 / 清理完有没有重扫"。
///
/// 关于"清理成功"这条路径的说明：<see cref="OrphanCleaner"/> 的备份是它自己去读真实注册表键，
/// 没有可注入的备份钩子（备份成功与否是安全闸门的一部分，不该被测试绕过）。
/// 因此这里只在 <c>HKCU\...\Uninstall</c> 下建一个<b>临时测试键</b>让备份能成功读出来，
/// 并且：<b>不写 DisplayName</b>（Windows「应用和功能」不会显示无名称条目）、
/// <b>不写 WindowsInstaller=1</b>（本工具的扫描器只认 MSI 条目，不会被它误扫到），
/// 真正的"删键"动作也仍然走注入的假委托，测试结束在 <see cref="FakeRegistryCleaner.Dispose"/> 里删干净。
/// 也就是说：不删任何真实条目，也不产生任何会出现在用户界面上的注册表残留。
/// </summary>
public class RegistryLeftoversViewModelTests
{
    private const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>本机真实存在的 Uninstall 路径（仅用于让备份能读到一个真键）。</summary>
    private static string KeyPath(string productCode) => @$"{UninstallRoot}\{productCode}";

    /// <summary>生成一个格式合法的产品 GUID（闸门 1 要求严格 {8-4-4-4-12}）。</summary>
    private static string NewGuid() => "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";

    private static OrphanUninstallEntry Entry(
        string? productCode = null,
        string displayName = "测试孤儿软件",
        string? version = "1.0.0",
        string? publisher = "测试发行商",
        string? blockReason = null,
        uint state = 1)
        => new()
        {
            ProductCode = productCode ?? NewGuid(),
            DisplayName = displayName,
            DisplayVersion = version,
            Publisher = publisher,
            Hive = RegistryHive.CurrentUser,
            View = RegistryView.Registry64,
            UninstallRootPath = UninstallRoot,

            // 1 = ABSENT：清理器的闸门 4 要求这个状态才允许进计划
            MsiState = state,
            Evidence = ["Windows Installer 报告该产品未安装：ABSENT（未安装）"],
            BlockReason = blockReason,
        };

    private static OrphanScanResult Scan(params OrphanUninstallEntry[] entries) => new()
    {
        Entries = entries,
        ScannedCount = entries.Length + 12,
        Duration = TimeSpan.FromMilliseconds(80),
    };

    /// <summary>假扫描器：按调用次数返回预设结果，可注入异常。</summary>
    private sealed class FakeRegistryScanner(params OrphanScanResult[] results) : IOrphanUninstallScanner
    {
        private int _calls;

        /// <summary>扫描被调用了几次（清理后自动重扫靠它验证）。</summary>
        public int Calls => Volatile.Read(ref _calls);

        /// <summary>非空时扫描直接抛异常，用于验证错误反馈。</summary>
        public Exception? Failure { get; set; }

        public Task<OrphanScanResult> ScanAsync(
            IProgress<OrphanScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            progress?.Report(new OrphanScanProgress("测试扫描中", 0, 1));

            if (Failure is not null)
            {
                return Task.FromException<OrphanScanResult>(Failure);
            }

            var result = results.Length == 0
                ? Scan()
                : results[Math.Min(index, results.Length - 1)];

            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// 假清理器宿主。
    ///
    /// <see cref="OrphanCleaner"/> 是具体类（不是接口），但它的所有副作用都能注入：
    /// MSI 状态查询、已安装枚举、"键是否存在"、"删键"。
    /// 只有备份那一步是内部直读真实注册表，所以需要配合 <see cref="CreateRealKey"/> 造一个临时真键。
    /// </summary>
    private sealed class FakeRegistryCleaner : IDisposable
    {
        private readonly HashSet<string> _existingKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _createdKeys = [];

        private readonly object _gate = new();

        /// <summary>被"删掉"的子键路径（全部是注入的假动作，真实注册表不动）。</summary>
        public List<string> DeletedSubKeys { get; } = [];

        /// <summary>清理器工厂被调用了几次（用来验证"没有可清理项时不该创建清理器"）。</summary>
        public int FactoryCalls { get; private set; }

        public string BackupDirectory { get; } =
            Path.Combine(Path.GetTempPath(), "rc-app-reg-" + Guid.NewGuid().ToString("N")[..8]);

        /// <summary>让"键存在"闸门通过（备份与删除前各查一次）。</summary>
        public void MarkKeyExisting(string productCode)
        {
            lock (_gate)
            {
                _existingKeys.Add(KeyPath(productCode));
            }
        }

        /// <summary>
        /// 造一个真实的临时 Uninstall 键，只为让备份能读出内容。
        /// 故意不写 DisplayName / WindowsInstaller：前者会让它出现在「应用和功能」，
        /// 后者会让本工具自己的扫描器把它当成 MSI 条目。
        /// </summary>
        public void CreateRealKey(string productCode)
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath(productCode));
            key.SetValue("RubbishCleanerTestMarker", "临时测试键，测试结束即删除", RegistryValueKind.String);
            lock (_gate)
            {
                _createdKeys.Add(productCode);
            }
        }

        public OrphanCleaner Build()
        {
            lock (_gate)
            {
                FactoryCalls++;
            }

            return new OrphanCleaner(
                BackupDirectory,
                _ => 1u,                                                // 执行前复核：仍是 ABSENT
                () => new HashSet<string>(StringComparer.OrdinalIgnoreCase),   // 不在已安装枚举里
                (hive, view, path) =>
                {
                    lock (_gate)
                    {
                        DeletedSubKeys.Add(path);

                        // 删完就不存在了 → 闸门 7（复核）才能通过
                        _existingKeys.Remove(path);
                    }
                },
                (hive, view, path) =>
                {
                    lock (_gate)
                    {
                        return _existingKeys.Contains(path);
                    }
                });
        }

        public void Dispose()
        {
            foreach (var code in _createdKeys)
            {
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(KeyPath(code), throwOnMissingSubKey: false);
                }
                catch
                {
                }
            }

            try
            {
                if (Directory.Exists(BackupDirectory))
                {
                    Directory.Delete(BackupDirectory, true);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class Recorder
    {
        public int ConfirmCalls { get; private set; }

        public int NotifyCalls { get; private set; }

        public string? LastConfirmTitle { get; private set; }

        public string? LastConfirmMessage { get; private set; }

        public string? LastNotifyTitle { get; private set; }

        public string? LastNotifyMessage { get; private set; }

        public bool ConfirmResult { get; set; } = true;

        public void Attach(LeftoversViewModel vm)
        {
            vm.ConfirmHandler = (title, message) =>
            {
                ConfirmCalls++;
                LastConfirmTitle = title;
                LastConfirmMessage = message;
                return ConfirmResult;
            };

            vm.NotifyHandler = (title, message) =>
            {
                NotifyCalls++;
                LastNotifyTitle = title;
                LastNotifyMessage = message;
            };
        }
    }

    private static LeftoversViewModel Create(
        FakeRegistryScanner scanner,
        FakeRegistryCleaner cleaner,
        Recorder recorder)
    {
        var vm = new LeftoversViewModel(
            new NoopLeftoverScanner(),
            () => throw new InvalidOperationException("本组测试不该触碰文件残留清理器"),
            () => new InstalledAppsResult([], 0, 0),
            () => [],
            scanner,
            cleaner.Build);

        recorder.Attach(vm);
        return vm;
    }

    /// <summary>文件残留扫描器用不到，但也必须是可控的假实现（避免误触真实磁盘）。</summary>
    private sealed class NoopLeftoverScanner : RubbishCleaner.Core.Leftover.ILeftoverScanner
    {
        public Task<LeftoverScanResult> ScanAsync(
            LeftoverScanOptions options,
            IProgress<LeftoverScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new LeftoverScanResult());
    }

    /// <summary>等一个异步命令（async void）把状态改到位；比固定 Task.Delay 稳且快。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"等待超时：{because}");
    }

    // ==================== 计数与按钮文案 ====================

    [Fact]
    public async Task ScanRegistry_NoEntries_CountsAreZeroAndButtonSaysNoResidue()
    {
        using var cleaner = new FakeRegistryCleaner();
        var recorder = new Recorder();
        var vm = Create(new FakeRegistryScanner(Scan()), cleaner, recorder);

        await vm.ScanRegistryAsync();

        Assert.True(vm.HasRegistryScanned);
        Assert.False(vm.ShowRegistryEmptyState);
        Assert.False(vm.HasRegistryResidue);
        Assert.Equal(0, vm.RegistryCount);
        Assert.Equal(0, vm.RegistryCleanableCount);
        Assert.Equal("无注册表残留", vm.RegistryButtonText);
        Assert.False(vm.CanCleanRegistry);

        // 扫过但为空要明确说"注册表干净"，而不是又回到"还没检查"
        Assert.Contains("干净", vm.RegistrySummaryText);
        Assert.Contains("12", vm.RegistrySummaryText);
    }

    [Fact]
    public async Task ScanRegistry_OneCleanableEntry_CountsAndButtonTextMatch()
    {
        using var cleaner = new FakeRegistryCleaner();
        var vm = Create(new FakeRegistryScanner(Scan(Entry(displayName: "只剩一条"))), cleaner, new Recorder());

        await vm.ScanRegistryAsync();

        Assert.True(vm.HasRegistryResidue);
        Assert.Equal(1, vm.RegistryCount);
        Assert.Equal(1, vm.RegistryCleanableCount);
        Assert.Equal("清理 1 个注册表残留", vm.RegistryButtonText);
        Assert.True(vm.CanCleanRegistry);
        Assert.Equal("只剩一条", vm.RegistryItems[0].DisplayName);
        Assert.Contains("注册表残留 1 条、可清理 1 条", vm.RegistrySummaryText);
    }

    [Fact]
    public async Task ScanRegistry_MultipleEntries_BlockedOnesAreShownButNotCleanable()
    {
        using var cleaner = new FakeRegistryCleaner();
        var scanner = new FakeRegistryScanner(Scan(
            Entry(displayName: "可清理甲"),
            Entry(displayName: "可清理乙"),
            Entry(displayName: "被拦下", blockReason: "位于系统保护路径")));

        var vm = Create(scanner, cleaner, new Recorder());

        await vm.ScanRegistryAsync();

        Assert.Equal(3, vm.RegistryCount);
        Assert.Equal(2, vm.RegistryCleanableCount);
        Assert.True(vm.CanCleanRegistry);
        Assert.Contains("清理 2 个", vm.RegistryButtonText);

        // 语义文案要同时给出"总条数 / 可清理数 / 被拦下的条数"
        Assert.Contains("注册表残留 3 条、可清理 2 条", vm.RegistrySummaryText);
        Assert.Contains("1 条被安全闸门拦下", vm.RegistrySummaryText);

        var blocked = vm.RegistryItems.Single(i => i.DisplayName == "被拦下");
        Assert.True(blocked.IsBlocked);
        Assert.Equal("位于系统保护路径", blocked.BlockReason);
    }

    [Fact]
    public async Task ScanRegistry_AllBlocked_ButtonSaysNothingCleanableInsteadOfNoResidue()
    {
        // 扫到 1 条但不可清理 ≠ 没有残留；按钮文案必须区分，
        // 否则用户会以为扫描没生效（文件残留页在 v0.1.4 踩过同类问题）。
        using var cleaner = new FakeRegistryCleaner();
        var vm = Create(
            new FakeRegistryScanner(Scan(Entry(blockReason: "MSI 状态不是 ABSENT"))),
            cleaner,
            new Recorder());

        await vm.ScanRegistryAsync();

        Assert.Equal(1, vm.RegistryCount);
        Assert.Equal(0, vm.RegistryCleanableCount);
        Assert.Equal("无可清理项", vm.RegistryButtonText);
        Assert.False(vm.CanCleanRegistry);
    }

    [Fact]
    public void BeforeRegistryScan_ShowsEmptyStateAndDisablesClean()
    {
        using var cleaner = new FakeRegistryCleaner();
        var vm = Create(new FakeRegistryScanner(), cleaner, new Recorder());

        Assert.False(vm.HasRegistryScanned);
        Assert.True(vm.ShowRegistryEmptyState);
        Assert.Contains("还没有检查注册表", vm.RegistrySummaryText);
        Assert.False(vm.CanCleanRegistry);
        Assert.Null(vm.SelectedRegistry);
    }

    [Fact]
    public void FourArgumentConstructor_StillWorksAndUsesProductionRegistryDependencies()
    {
        // 构造函数后两个参数带默认值：MainViewModel 与既有单测的 4 参数调用不能被破坏。
        var vm = new LeftoversViewModel(
            new NoopLeftoverScanner(),
            () => throw new InvalidOperationException("不该用到"),
            () => new InstalledAppsResult([], 0, 0),
            () => []);

        Assert.NotNull(vm.ScanRegistryCommand);
        Assert.NotNull(vm.CleanRegistryCommand);
        Assert.Equal(0, vm.RegistryCount);
        Assert.False(vm.CanCleanRegistry);
        Assert.False(vm.HasRegistryScanned);
    }

    // ==================== 扫描异常 ====================

    [Fact]
    public async Task ScanRegistryCommand_Throws_ReportsErrorAndResetsScanning()
    {
        using var cleaner = new FakeRegistryCleaner();
        var scanner = new FakeRegistryScanner { Failure = new InvalidOperationException("模拟 MsiEnumProducts 失败") };
        var vm = Create(scanner, cleaner, new Recorder());

        vm.ScanRegistryCommand.Execute(null);

        await WaitUntilAsync(() => vm.LastScanError is not null && !vm.IsScanning, "扫描异常应被命令捕获并复位 IsScanning");

        Assert.Contains("注册表扫描失败", vm.LastScanError!);
        Assert.Contains("模拟 MsiEnumProducts 失败", vm.LastScanError!);

        // 状态栏也要有反馈：只设 LastScanError 的话用户看到的仍是"正在检查…"
        Assert.Contains("注册表扫描失败", vm.StatusText);
        Assert.False(vm.IsScanning);
        Assert.False(vm.CanCleanRegistry);
    }

    // ==================== 清理：不该动手的情况 ====================

    [Fact]
    public async Task CleanRegistry_NothingCleanable_DoesNotConfirmAndDoesNotCreateCleaner()
    {
        using var cleaner = new FakeRegistryCleaner();
        var recorder = new Recorder();
        var vm = Create(
            new FakeRegistryScanner(Scan(Entry(blockReason: "不是标准 GUID"))),
            cleaner,
            recorder);

        await vm.ScanRegistryAsync();
        await vm.CleanRegistryAsync();

        Assert.Equal(0, recorder.ConfirmCalls);
        Assert.Equal(1, recorder.NotifyCalls);
        Assert.Equal("没有可清理项", recorder.LastNotifyTitle);
        Assert.Contains("不满足自动清理条件", recorder.LastNotifyMessage!);
        Assert.Contains("不是标准 GUID", recorder.LastNotifyMessage!);

        // 没有可清理项时连清理器都不该创建（工厂零调用）
        Assert.Equal(0, cleaner.FactoryCalls);
        Assert.Empty(cleaner.DeletedSubKeys);
    }

    [Fact]
    public async Task CleanRegistry_NoResidueAtAll_NotifiesAndDoesNothing()
    {
        using var cleaner = new FakeRegistryCleaner();
        var recorder = new Recorder();
        var vm = Create(new FakeRegistryScanner(Scan()), cleaner, recorder);

        await vm.ScanRegistryAsync();
        await vm.CleanRegistryAsync();

        Assert.Equal(0, recorder.ConfirmCalls);
        Assert.Equal("没有可清理项", recorder.LastNotifyTitle);
        Assert.Contains("没有注册表残留", recorder.LastNotifyMessage!);
        Assert.Equal(0, cleaner.FactoryCalls);
    }

    [Fact]
    public async Task CleanRegistry_UserCancels_ExecutesNothing()
    {
        using var cleaner = new FakeRegistryCleaner();
        var entry = Entry();
        cleaner.MarkKeyExisting(entry.ProductCode);

        var recorder = new Recorder { ConfirmResult = false };
        var scanner = new FakeRegistryScanner(Scan(entry));
        var vm = Create(scanner, cleaner, recorder);

        await vm.ScanRegistryAsync();

        // 取消前必须先弹出确认框，且确认文案要说清"会备份、只删条目"
        Assert.True(vm.CanCleanRegistry);
        await vm.CleanRegistryAsync();

        Assert.Equal(1, recorder.ConfirmCalls);
        Assert.Equal("确认清理注册表残留", recorder.LastConfirmTitle);
        Assert.Contains("删除前会自动导出 .reg 备份", recorder.LastConfirmMessage!);

        Assert.Empty(cleaner.DeletedSubKeys);
        Assert.Equal(0, recorder.NotifyCalls);
        Assert.Contains("已取消", vm.StatusText);

        // 取消后不再重扫：列表保持原样，扫描器只被调用过 1 次
        Assert.Equal(1, scanner.Calls);
    }

    // ==================== 清理：成功与部分失败 ====================

    [Fact]
    public async Task CleanRegistry_AllSucceed_ReportsSuccessAndNotifiesBackupLocation()
    {
        using var cleaner = new FakeRegistryCleaner();
        var entry = Entry(displayName: "可清理孤儿");
        cleaner.MarkKeyExisting(entry.ProductCode);
        cleaner.CreateRealKey(entry.ProductCode);

        var recorder = new Recorder();
        var vm = Create(new FakeRegistryScanner(Scan(entry), Scan()), cleaner, recorder);

        await vm.ScanRegistryAsync();
        await vm.CleanRegistryAsync();

        Assert.Equal(1, recorder.ConfirmCalls);
        Assert.Equal(1, recorder.NotifyCalls);
        Assert.Equal("注册表清理完成", recorder.LastNotifyTitle);
        Assert.Contains(".reg", recorder.LastNotifyMessage!);
        Assert.Contains("registry-backups", recorder.LastNotifyMessage!);

        Assert.Contains("已删除 1 个孤儿条目", vm.StatusText);
        Assert.Single(cleaner.DeletedSubKeys);
    }

    [Fact]
    public async Task CleanRegistry_PartialFailure_DistinguishesStatusAndNotification()
    {
        using var cleaner = new FakeRegistryCleaner();
        var ok = Entry(displayName: "能清掉的");
        var failing = Entry(displayName: "备份会失败的");

        cleaner.MarkKeyExisting(ok.ProductCode);
        cleaner.CreateRealKey(ok.ProductCode);

        // 第二条只让"键存在"闸门通过，却不造真实键 → 备份必然失败 → 该条记为失败
        cleaner.MarkKeyExisting(failing.ProductCode);

        var recorder = new Recorder();
        var vm = Create(new FakeRegistryScanner(Scan(ok, failing), Scan()), cleaner, recorder);

        await vm.ScanRegistryAsync();
        await vm.CleanRegistryAsync();

        Assert.Equal(1, recorder.NotifyCalls);
        Assert.Equal("部分条目清理失败", recorder.LastNotifyTitle);
        Assert.Contains("备份会失败的", recorder.LastNotifyMessage!);

        // 部分失败时的状态文案必须与"全部成功"明显不同
        Assert.Contains("成功 1 个，失败 1 个", vm.StatusText);
        Assert.DoesNotContain("已删除 1 个孤儿条目", vm.StatusText);
        Assert.Single(cleaner.DeletedSubKeys);
    }

    [Fact]
    public async Task CleanRegistry_OnlyCleanableEntriesEnterThePlan()
    {
        using var cleaner = new FakeRegistryCleaner();
        var cleanable = Entry(displayName: "可清理");
        var blocked = Entry(displayName: "不可清理", blockReason: "位于系统保护路径");

        cleaner.MarkKeyExisting(cleanable.ProductCode);
        cleaner.CreateRealKey(cleanable.ProductCode);
        cleaner.MarkKeyExisting(blocked.ProductCode);
        cleaner.CreateRealKey(blocked.ProductCode);

        var vm = Create(new FakeRegistryScanner(Scan(cleanable, blocked), Scan()), cleaner, new Recorder());

        await vm.ScanRegistryAsync();
        await vm.CleanRegistryAsync();

        // 被安全闸门拦下的条目不能出现在清理动作里
        Assert.Single(cleaner.DeletedSubKeys);
        Assert.Contains(cleanable.ProductCode, cleaner.DeletedSubKeys[0]);
        Assert.DoesNotContain(blocked.ProductCode, cleaner.DeletedSubKeys[0]);
    }

    // ==================== 筛选（与文件残留共用同一套条件） ====================

    [Fact]
    public async Task Filter_ByProductCode_OnlyNarrowsTheViewNotTheCounts()
    {
        using var cleaner = new FakeRegistryCleaner();
        var target = Entry(displayName: "要搜的软件");
        var other = Entry(displayName: "无关软件");

        var vm = Create(new FakeRegistryScanner(Scan(target, other)), cleaner, new Recorder());

        await vm.ScanRegistryAsync();
        Assert.Equal(2, vm.FilteredRegistryItems.Count);

        // 用产品码搜索：注册表条目的名字常常是脏数据，GUID 往往才是用户手上唯一的线索
        vm.FilterText = target.ProductCode;

        Assert.Single(vm.FilteredRegistryItems);
        Assert.Equal("要搜的软件", vm.FilteredRegistryItems[0].DisplayName);

        // 筛选只影响展示，不能改变"多少条 / 可清理多少条"
        Assert.Equal(2, vm.RegistryCount);
        Assert.Equal(2, vm.RegistryCleanableCount);
        Assert.False(vm.ShowRegistryFilterEmpty);
    }

    [Fact]
    public async Task Filter_MatchesNothing_ShowsExplicitFilterEmptyState()
    {
        using var cleaner = new FakeRegistryCleaner();
        var vm = Create(new FakeRegistryScanner(Scan(Entry(displayName: "某软件"))), cleaner, new Recorder());

        await vm.ScanRegistryAsync();
        vm.FilterText = "绝对匹配不到的关键词";

        Assert.Empty(vm.FilteredRegistryItems);

        // 筛选空 ≠ 没残留：区块标题仍写"残留 1 条"，下面要给一句"当前筛选下没有"
        Assert.True(vm.ShowRegistryFilterEmpty);
        Assert.True(vm.ShowRegistryList);
        Assert.Equal(1, vm.RegistryCount);
    }

    [Fact]
    public async Task Filter_ShowOnlyCleanable_ExcludesBlockedRegistryRows()
    {
        using var cleaner = new FakeRegistryCleaner();
        var vm = Create(
            new FakeRegistryScanner(Scan(
                Entry(displayName: "可清理"),
                Entry(displayName: "被拦下", blockReason: "不是标准 GUID"))),
            cleaner,
            new Recorder());

        await vm.ScanRegistryAsync();
        vm.ShowOnlyCleanable = true;

        Assert.Single(vm.FilteredRegistryItems);
        Assert.True(vm.FilteredRegistryItems[0].CanDelete);

        // 恢复"全部残留"后应重新出现两条
        vm.ShowOnlyCleanable = false;
        Assert.Equal(2, vm.FilteredRegistryItems.Count);
    }

    [Fact]
    public async Task Filter_DoesNotReduceWhatCleanRegistryActuallyCleans()
    {
        // 关键安全语义：筛选是界面行为，绝不能反过来缩小/放大真实清理范围。
        using var cleaner = new FakeRegistryCleaner();
        var a = Entry(displayName: "甲");
        var b = Entry(displayName: "乙");

        foreach (var entry in new[] { a, b })
        {
            cleaner.MarkKeyExisting(entry.ProductCode);
            cleaner.CreateRealKey(entry.ProductCode);
        }

        var vm = Create(new FakeRegistryScanner(Scan(a, b), Scan()), cleaner, new Recorder());

        await vm.ScanRegistryAsync();

        // 只显示一条，但清理应当两条都覆盖（用户确认框里也看得到两条）
        vm.FilterText = "甲";
        Assert.Single(vm.FilteredRegistryItems);

        await vm.CleanRegistryAsync();

        Assert.Equal(2, cleaner.DeletedSubKeys.Count);
    }

    // ==================== 清理后自动重扫 ====================

    [Fact]
    public async Task CleanRegistry_RescansAfterClean_AndClearsSelection()
    {
        using var cleaner = new FakeRegistryCleaner();
        var entry = Entry();
        cleaner.MarkKeyExisting(entry.ProductCode);
        cleaner.CreateRealKey(entry.ProductCode);

        var scanner = new FakeRegistryScanner(Scan(entry), Scan());
        var vm = Create(scanner, cleaner, new Recorder());

        await vm.ScanRegistryAsync();
        vm.SelectedRegistry = vm.RegistryItems[0];
        Assert.True(vm.HasSelectedRegistry);

        await vm.CleanRegistryAsync();

        // 1 次手动扫描 + 1 次清理后自动重扫
        Assert.Equal(2, scanner.Calls);

        // 重扫会重建列表，选中项必须跟着复位（否则详情区指向已不存在的行）
        Assert.Null(vm.SelectedRegistry);
        Assert.False(vm.HasSelectedRegistry);
        Assert.Equal(0, vm.RegistryCount);
    }
}
