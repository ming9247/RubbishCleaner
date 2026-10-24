using RubbishCleaner.App.ViewModels;
using RubbishCleaner.Core.Cleanup;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.App.Tests;

/// <summary>
/// 「残留总数」与「可清理数」的语义区分测试。
///
/// 起因：v0.1.3 里左侧显示"残留启动项（4）"，但点清理提示"无可清理项"——
/// 因为那 4 条全是服务类。数字和可操作性不一致会让用户困惑。
/// 修复后：筛选按钮显示总数，清理按钮只显示可清理数。
/// </summary>
public class OrphanCountSemanticsTests
{
    [Fact]
    public async Task OrphanCount_IncludesNonCleanableEntries()
    {
        var vm = new MainViewModel();
        await vm.ScanAsync();

        // 总数永远 >= 可清理数
        Assert.True(vm.OrphanCount >= vm.CleanableCount,
            $"残留总数 {vm.OrphanCount} 不应小于可清理数 {vm.CleanableCount}");
    }

    [Fact]
    public async Task CleanableCount_OnlyCountsRunKindsWithAnchor()
    {
        var vm = new MainViewModel();
        await vm.ScanAsync();

        var expected = vm.Profiles
            .SelectMany(p => p.Profile.StartupEntries)
            .Count(e => e.IsOrphan
                        && e.RegistryAnchor is not null
                        && e.Source is StartupSourceKind.RegistryRunHkcu
                            or StartupSourceKind.RegistryRunHklm
                            or StartupSourceKind.RegistryRunWow64);

        Assert.Equal(expected, vm.CleanableCount);
    }

    [Fact]
    public async Task CanCleanOrphans_DependsOnCleanableCountNotTotal()
    {
        var vm = new MainViewModel();
        await vm.ScanAsync();

        // 关键回归：只有服务类残留时，清理按钮必须不可用
        if (vm.OrphanCount > 0 && vm.CleanableCount == 0)
        {
            Assert.False(vm.CanCleanOrphans);
            Assert.False(vm.CleanOrphansCommand.CanExecute(null));
            // 文案要直说"无需自动清理"，而不是让用户反复点
            Assert.Equal("无需自动清理", vm.CleanOrphansText);
        }
    }

    [Fact]
    public async Task CleanOrphansText_ShowsCountWhenCleanable()
    {
        var vm = new MainViewModel();
        await vm.ScanAsync();

        if (vm.CleanableCount > 0)
        {
            Assert.Contains($"（{vm.CleanableCount}）", vm.CleanOrphansText);
        }
    }

    [Fact]
    public void CleanOrphansText_BeforeScan_HasNoCount()
    {
        var vm = new MainViewModel();

        Assert.Equal("清理残留启动项", vm.CleanOrphansText);
        Assert.False(vm.CanCleanOrphans);
    }
}

/// <summary>手动处理指引在 ViewModel 层的呈现。</summary>
public class ManualFixViewModelTests
{
    private static SoftwareProfile ProfileWithServiceOrphan()
        => new()
        {
            Key = "svc",
            DisplayName = "WsxService",
            ExecutablePath = @"D:\nexus\Winstep\WsxService",
            StartupEntries =
            [
                new StartupEntry
                {
                    Id = "svc",
                    Source = StartupSourceKind.WindowsService,
                    Name = "Winstep Xtreme Service",
                    Location = @"HKLM\SYSTEM\CurrentControlSet\Services\Winstep Xtreme Service",
                    ExecutablePath = @"D:\nexus\Winstep\WsxService",
                    TargetExistsOverride = false,
                },
            ],
            LiveProcess = ProcessFacts.NotRunning("WsxService", @"D:\nexus\Winstep\WsxService"),
            Score = new RiskScore(30, RiskLevel.Normal, []),
        };

    [Fact]
    public void ServiceOrphan_PopulatesManualFixWithCommand()
    {
        var vm = new SoftwareItemViewModel(ProfileWithServiceOrphan());

        Assert.True(vm.HasManualFixes);
        Assert.Single(vm.ManualFixes);
        Assert.True(vm.ManualFixes[0].HasCommand);
        Assert.Contains("sc.exe delete", vm.ManualFixes[0].Command);
    }

    [Fact]
    public void RunOrphan_HasNoManualFix()
    {
        // Run 类残留由清理按钮统一处理，不应出现在"需手动处理"里
        var profile = new SoftwareProfile
        {
            Key = "run",
            DisplayName = "App",
            ExecutablePath = @"D:\Gone\app.exe",
            StartupEntries =
            [
                new StartupEntry
                {
                    Id = "run",
                    Source = StartupSourceKind.RegistryRunHkcu,
                    Name = "App",
                    Location = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
                    ExecutablePath = @"D:\Gone\app.exe",
                    TargetExistsOverride = false,
                    RegistryAnchor = new RegistryAnchor(
                        Microsoft.Win32.RegistryHive.CurrentUser,
                        Microsoft.Win32.RegistryView.Default,
                        @"Software\Microsoft\Windows\CurrentVersion\Run", "App"),
                },
            ],
            Score = new RiskScore(30, RiskLevel.Normal, []),
        };

        var vm = new SoftwareItemViewModel(profile);

        Assert.False(vm.HasManualFixes);
        Assert.Empty(vm.ManualFixes);
    }
}
