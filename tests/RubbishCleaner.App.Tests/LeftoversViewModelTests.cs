using System.IO;
using RubbishCleaner.App.ViewModels;
using RubbishCleaner.Common.Mft;
using RubbishCleaner.Core.Leftover;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Uninstall;

namespace RubbishCleaner.App.Tests;

/// <summary>
/// 残留扫描页（v0.3）的视图模型测试。
///
/// 全部注入假扫描器与假删除器：<b>绝不在测试里真删磁盘</b>，
/// 但要覆盖"按钮可用性"与"删除走的是回收站、失败不谎报"这两条关键语义。
/// </summary>
public class LeftoversViewModelTests
{
    private sealed class FakeScanner(params LeftoverItem[] items) : ILeftoverScanner
    {
        public int Calls { get; private set; }

        public LeftoverScanResult? LastResult { get; private set; }

        public string Description { get; set; } = "测试索引";

        public string? Degraded { get; set; }

        public Task<LeftoverScanResult> ScanAsync(
            LeftoverScanOptions options,
            IProgress<LeftoverScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            progress?.Report(new LeftoverScanProgress("测试中", 0, 1));
            LastResult = new LeftoverScanResult
            {
                Items = items,
                IndexDescription = Description,
                DegradedReason = Degraded,
                Duration = TimeSpan.FromMilliseconds(120),
            };
            return Task.FromResult(LastResult);
        }
    }

    private static LeftoverItem Item(
        string path,
        LeftoverConfidence confidence = LeftoverConfidence.High,
        string? blockReason = null,
        long bytes = 1024)
        => new()
        {
            Id = path.ToLowerInvariant(),
            DirectoryPath = path,
            Kind = LeftoverKind.OrphanedUninstallerDirectory,
            Confidence = confidence,
            TotalBytes = bytes,
            FileCount = 2,
            SoftwareName = Path.GetFileName(path),
            BlockReason = blockReason,
            Evidence = ["目录内仍保留卸载程序：unins000.exe"],
        };

    private static (LeftoversViewModel Vm, List<string> Deleted, FakeScanner Scanner) Create(
        LeftoverItem[] items,
        Func<string, LeftoverDeleteOutcome>? delete = null,
        IReadOnlyList<InstalledApp>? apps = null,
        bool exists = true,
        IReadOnlyList<string>? claimPaths = null)
    {
        var deleted = new List<string>();
        var scanner = new FakeScanner(items);
        var vm = new LeftoversViewModel(
            scanner,
            () => new LeftoverCleaner(
                delete ?? (path =>
                {
                    deleted.Add(path);
                    return LeftoverDeleteOutcome.Ok();
                }),
                _ => exists),
            () => new InstalledAppsResult(apps ?? [], 0, 0) { ClaimPaths = claimPaths ?? [] },
            () => []);

        vm.ConfirmHandler = (_, _) => true;
        vm.NotifyHandler = (_, _) => { };

        return (vm, deleted, scanner);
    }

    [Fact]
    public async Task Scan_PopulatesItemsAndCounters()
    {
        var (vm, _, _) = Create(
        [
            Item(@"C:\Program Files\A", bytes: 100),
            Item(@"C:\Program Files\B", bytes: 200),
        ]);

        await vm.ScanAsync();

        Assert.True(vm.HasScanned);
        Assert.False(vm.ShowEmptyState);
        Assert.Equal(2, vm.TotalCount);
        Assert.Equal(2, vm.CleanableCount);
        Assert.Equal(300, vm.TotalBytes);
        Assert.Equal(2, vm.FilteredItems.Count);
    }

    [Fact]
    public async Task Scan_SummaryMentionsCountAndSize()
    {
        var (vm, _, _) = Create([Item(@"C:\Program Files\A", bytes: 2 * 1024 * 1024)]);

        await vm.ScanAsync();

        Assert.Contains("1 项", vm.SummaryText);
        Assert.Contains("MB", vm.SummaryText);
        Assert.Equal("测试索引", vm.IndexText);
    }

    [Fact]
    public async Task Scan_BlockedItemIsShownButNotCountedAsCleanable()
    {
        // 关键语义（v0.1.4 踩过的同类问题）：展示数与可操作数必须分开
        var (vm, _, _) = Create(
        [
            Item(@"C:\Program Files\Ok"),
            Item(@"C:\Windows\SysWOW64\Bad", blockReason: "位于系统保护路径"),
        ]);

        await vm.ScanAsync();

        Assert.Equal(2, vm.TotalCount);
        Assert.Equal(1, vm.CleanableCount);
        Assert.True(vm.CanClean);
        Assert.Contains("清理 1 项", vm.CleanButtonText);
    }

    [Fact]
    public async Task Scan_NoCleanable_DisablesCleanButtonAndExplains()
    {
        var (vm, _, _) = Create([Item(@"C:\Windows\SysWOW64\Bad", blockReason: "系统保护路径")]);

        await vm.ScanAsync();

        Assert.Equal(0, vm.CleanableCount);
        Assert.False(vm.CanClean);
        Assert.Equal("没有可清理项", vm.CleanButtonText);
    }

    [Fact]
    public async Task Scan_NonHighConfidence_IsNotCleanable()
    {
        var (vm, _, _) = Create(
        [
            Item(@"C:\Program Files\Guess", LeftoverConfidence.Low),
            Item(@"C:\Program Files\Maybe", LeftoverConfidence.Medium),
        ]);

        await vm.ScanAsync();

        Assert.Equal(2, vm.TotalCount);
        Assert.Equal(0, vm.CleanableCount);
    }

    [Fact]
    public async Task Scan_DegradedResultIsSurfaced()
    {
        var (vm, _, scanner) = Create([Item(@"C:\Program Files\A")]);
        scanner.Degraded = "C: 卷枚举失败";

        await vm.ScanAsync();

        Assert.True(vm.HasDegraded);
        Assert.Contains("C: 卷枚举失败", vm.DegradedText!);
    }

    [Fact]
    public async Task Scan_WithoutInstalledApps_WarnsThatVerdictIsUnreliable()
    {
        // 读不到已安装软件列表时，所有带卸载程序的目录都会被判成"无人认领"，
        // 这是真实存在的误删风险，UI 必须显式提醒。
        var (vm, _, _) = Create([Item(@"C:\Program Files\A")], apps: []);

        await vm.ScanAsync();

        Assert.True(vm.HasDegraded);
        Assert.Contains("已安装软件列表", vm.DegradedText!);
    }

    [Fact]
    public async Task Clean_MovesCleanableDirectoriesThroughInjectedDeleter()
    {
        var (vm, deleted, _) = Create(
        [
            Item(@"C:\Program Files\A"),
            Item(@"C:\Program Files\Blocked", blockReason: "系统保护路径"),
        ]);

        await vm.ScanAsync();
        await vm.CleanAsync();

        Assert.Equal([@"C:\Program Files\A"], deleted);
    }

    [Fact]
    public async Task Clean_NothingCleanable_DoesNotDeleteAndNotifies()
    {
        var (vm, deleted, _) = Create([Item(@"C:\Program Files\Blocked", blockReason: "系统保护路径")]);
        var notified = false;
        vm.NotifyHandler = (_, _) => notified = true;

        await vm.ScanAsync();
        await vm.CleanAsync();

        Assert.Empty(deleted);
        Assert.True(notified);
    }

    [Fact]
    public async Task Clean_UserCancels_DeletesNothing()
    {
        var (vm, deleted, _) = Create([Item(@"C:\Program Files\A")]);
        vm.ConfirmHandler = (_, _) => false;

        await vm.ScanAsync();
        await vm.CleanAsync();

        Assert.Empty(deleted);
        Assert.Contains("已取消", vm.StatusText);
    }

    [Fact]
    public async Task Clean_FailureIsReportedAndNotCountedAsSuccess()
    {
        var (vm, _, _) = Create(
            [Item(@"C:\Program Files\Busy")],
            delete: _ => LeftoverDeleteOutcome.Fail("文件被另一个程序占用"));

        await vm.ScanAsync();
        await vm.CleanAsync();

        Assert.Contains("失败", vm.StatusText);
    }

    [Fact]
    public async Task Filter_ByKeyword_MatchesPathAndSoftwareName()
    {
        var (vm, _, _) = Create(
        [
            Item(@"C:\Program Files\AdobeSuite"),
            Item(@"C:\Program Files\Other"),
        ]);

        await vm.ScanAsync();
        vm.FilterText = "adobe";

        Assert.Single(vm.FilteredItems);
        Assert.Contains("Adobe", vm.FilteredItems[0].DirectoryPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Filter_ShowOnlyCleanable_ExcludesBlockedItems()
    {
        var (vm, _, _) = Create(
        [
            Item(@"C:\Program Files\Ok"),
            Item(@"C:\Windows\SysWOW64\Bad", blockReason: "系统保护路径"),
        ]);

        await vm.ScanAsync();
        vm.ShowOnlyCleanable = true;

        Assert.Single(vm.FilteredItems);
        Assert.True(vm.FilteredItems[0].CanDelete);
    }

    [Fact]
    public void BeforeScan_ShowsEmptyStateAndCleanButtonDisabled()
    {
        var (vm, _, _) = Create([]);

        Assert.False(vm.HasScanned);
        Assert.True(vm.ShowEmptyState);
        Assert.Equal(0, vm.CleanableCount);
        Assert.False(vm.CanClean);
    }

    [Fact]
    public async Task Scan_RescansAfterClean_RefreshesList()
    {
        var (vm, _, scanner) = Create([Item(@"C:\Program Files\A")]);

        await vm.ScanAsync();
        await vm.CleanAsync();

        // 清理后必须重新扫描，否则列表里会留着已经不存在的行
        Assert.Equal(2, scanner.Calls);
    }
}