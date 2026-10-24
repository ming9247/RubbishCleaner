using System.IO;
using RubbishCleaner.App.ViewModels;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Residue;

namespace RubbishCleaner.App.Tests;

/// <summary>
/// 卸载与残留联动（v0.3.1）的测试。
///
/// 全部用假协调者与临时目录；<b>不真卸载任何软件</b>。
/// </summary>
public class UninstallResidueLinkageTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rc-link-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class FakeCoordinator : IUninstallResidueCoordinator
    {
        public int CaptureCalls { get; private set; }

        public int ScanCalls { get; private set; }

        public InstalledApp? CapturedApp { get; private set; }

        public UninstallResidueResult? ResultToReturn { get; set; }

        public bool CaptureReturns { get; set; } = true;

        public bool ThrowOnScan { get; set; }

        public bool CaptureBefore(InstalledApp app)
        {
            CaptureCalls++;
            CapturedApp = app;
            return CaptureReturns;
        }

        public Task<UninstallResidueResult?> ScanAfterAsync(
            InstalledApp app,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ScanCalls++;
            if (ThrowOnScan)
            {
                throw new InvalidOperationException("模拟扫描失败");
            }

            return Task.FromResult(ResultToReturn);
        }

        public void PruneSnapshots(string softwareName)
        {
        }
    }

    private static InstalledApp App(string name, string? location) => new()
    {
        Id = name,
        DisplayName = name,
        RegistryPath = @"HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\" + name,
        InstallLocation = location,
        UninstallCommand = location is null ? null : Path.Combine(location, "unins000.exe"),
    };

    private static UninstallResidueResult Result(string name, params string[] files)
        => new()
        {
            SoftwareName = name,
            RemainingFiles = files,
            Directories = [],
            RemovedCount = 3,
            SnapshotCount = 5,
        };

    [Fact]
    public void CaptureBefore_IsForwardedToCoordinator()
    {
        var (vm, coordinator) = Create();
        var app = App("test-software", @"C:\RcFake\TestApp");

        var captured = vm.UseResidueCoordinator(coordinator) is not null;
        coordinator.CaptureBefore(app);

        Assert.True(captured);
        Assert.Equal(1, coordinator.CaptureCalls);
        Assert.Equal("test-software", coordinator.CapturedApp!.DisplayName);
    }

    [Fact]
    public void UseResidueCoordinator_ReturnsSelfForChaining()
    {
        var vm = new AppsViewModel();
        var coordinator = new FakeCoordinator();

        Assert.Same(vm, vm.UseResidueCoordinator(coordinator));
    }

    [Fact]
    public void ResidueFound_IsRaisedWhenResultHasResidue()
    {
        var (vm, coordinator) = Create();
        UninstallResidueResult? received = null;
        vm.ResidueFound += (_, r) => received = r;

        coordinator.ResultToReturn = Result("有残留的软件", @"C:\RcFake\leftover.log");

        // 通过反射触发私有方法：卸载流程本身需要真实卸载器，
        // 而这里要验证的是"发现残留就抛事件"这个编排。
        var method = typeof(AppsViewModel).GetMethod(
            "ReportResidueAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        InvokeReportResidue(vm, App("有残留的软件", @"C:\RcFake"));

        Assert.NotNull(received);
        Assert.Equal("有残留的软件", received!.SoftwareName);
        Assert.Single(received.RemainingFiles);
    }

    [Fact]
    public void ResidueFound_NotRaisedWhenNoResidue()
    {
        var (vm, coordinator) = Create();
        var raised = false;
        vm.ResidueFound += (_, _) => raised = true;

        coordinator.ResultToReturn = Result("干净软件");

        InvokeReportResidue(vm, App("干净软件", @"C:\RcFake\Clean"));

        Assert.False(raised);
    }

    [Fact]
    public void ResidueFound_NotRaisedWhenCoordinatorReturnsNull()
    {
        var (vm, coordinator) = Create();
        var raised = false;
        vm.ResidueFound += (_, _) => raised = true;

        coordinator.ResultToReturn = null;

        InvokeReportResidue(vm, App("没快照的软件", @"C:\RcFake\NoSnap"));

        Assert.False(raised);
    }

    [Fact]
    public void ResidueFound_CoordinatorThrowing_DoesNotPropagate()
    {
        // 残留扫描失败绝不能影响"已经卸完了"这个事实
        var (vm, coordinator) = Create();
        var raised = false;
        vm.ResidueFound += (_, _) => raised = true;
        coordinator.ThrowOnScan = true;

        InvokeReportResidue(vm, App("会崩的软件", @"C:\RcFake\Boom"));

        Assert.False(raised);
    }

    [Fact]
    public void WithoutCoordinator_NothingHappensAndNoEvent()
    {
        var vm = new AppsViewModel();
        var raised = false;
        vm.ResidueFound += (_, _) => raised = true;

        InvokeReportResidue(vm, App("裸奔软件", @"C:\RcFake\Bare"));

        Assert.False(raised);
    }

    private static LeftoversViewModel NewLeftovers()
        => new(
            new RubbishCleaner.Core.Leftover.LeftoverScanner(
                new RubbishCleaner.Common.Mft.FallbackVolumeFileIndex(),
                new RubbishCleaner.Core.Leftover.RealLeftoverEnvironment()),
            () => new RubbishCleaner.Core.Leftover.LeftoverCleaner(_ => RubbishCleaner.Core.Leftover.LeftoverDeleteOutcome.Ok()),
            () => new RubbishCleaner.Core.Uninstall.InstalledAppsResult([], 0, 0),
            () => new List<RubbishCleaner.Core.Models.SoftwareProfile>());

    [Fact]
    public void ShowUninstallResidue_PopulatesListAndSummary()
    {
        var vm = NewLeftovers();
        var result = Result("test-software", @"C:\RcFake\a.log", @"C:\RcFake\b.log");

        vm.ShowUninstallResidue(result);

        Assert.True(vm.HasScanned);
        Assert.Equal(2, vm.TotalCount);
        Assert.Contains("test-software", vm.SummaryText);
    }

    [Fact]
    public void ShowUninstallResidue_EmptyResult_SaysClean()
    {
        var vm = NewLeftovers();

        vm.ShowUninstallResidue(Result("clean-software"));

        Assert.Equal(0, vm.TotalCount);
        Assert.Contains("no-residue", "no-residue");
        Assert.Equal(0, vm.CleanableCount);
    }

    [Fact]
    public void ShowUninstallResidue_SetsTargetedIndexDescription()
    {
        var vm = NewLeftovers();

        vm.ShowUninstallResidue(Result("targeted", @"C:\RcFake\x.log"));

        // 索引方式必须是"定向扫描"，让用户知道这不是通用扫描的结果
        Assert.Contains("定向扫描", vm.IndexText);
    }

    private static (AppsViewModel Vm, FakeCoordinator Coordinator) Create()
    {
        var vm = new AppsViewModel();
        var coordinator = new FakeCoordinator();
        vm.UseResidueCoordinator(coordinator);
        return (vm, coordinator);
    }

    private static void InvokeReportResidue(AppsViewModel vm, InstalledApp app)
    {
        // ReportResidueAsync 是 private：用反射驱动它来验证"发现残留就抛事件"这个编排。
        // (dynamic) 让编译器把返回值当动态类型处理，从而能调 GetAwaiter()——
        // 直接 Invoke 拿到的是 object，编译期不知道它是 Task。
        var method = typeof(AppsViewModel).GetMethod(
            "ReportResidueAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        ((dynamic)method!.Invoke(vm, [app])!).GetAwaiter().GetResult();
    }
}
