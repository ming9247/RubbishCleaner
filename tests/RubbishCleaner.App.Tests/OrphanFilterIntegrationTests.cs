using RubbishCleaner.App.ViewModels;
using RubbishCleaner.Core.Detection;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Trust;

namespace RubbishCleaner.App.Tests;

/// <summary>
/// 用真实扫描结果驱动 <see cref="MainViewModel"/>，验证「残留启动项」筛选端到端可用。
///
/// 为什么不用 UI 自动化点按钮：主程序以管理员权限运行，
/// 普通权限的合成鼠标输入会被 UIPI 丢弃，跨完整性级别的点击不可靠。
/// 直接驱动 ViewModel 同样能覆盖筛选逻辑，且稳定得多。
/// </summary>
public class OrphanFilterIntegrationTests
{
    [Fact]
    public async Task RealScan_OrphanFilter_ShowsOnlyProfilesWithResidue()
    {
        // MainViewModel 构造时会真实扫描一次；这里直接复用它的扫描命令
        var vm = new MainViewModel();
        await vm.ScanAsync();

        Assert.NotEmpty(vm.Profiles);

        if (vm.OrphanCount == 0)
        {
            // 干净的机器上也可能没有残留，此时筛选应给出空列表而不是报错
            vm.ShowOrphanCommand.Execute(null);
            Assert.Empty(vm.FilteredProfiles);
            return;
        }

        vm.ShowOrphanCommand.Execute(null);

        Assert.NotEmpty(vm.FilteredProfiles);
        Assert.All(vm.FilteredProfiles, p => Assert.True(p.OrphanCount > 0));
        Assert.Equal(vm.Profiles.Count(p => p.OrphanCount > 0), vm.FilteredProfiles.Count);
    }

    [Fact]
    public async Task RealScan_OrphanProfiles_AreMarkedAsResidueAndNotHighRisk()
    {
        var vm = new MainViewModel();
        await vm.ScanAsync();

        foreach (var profile in vm.Profiles.Where(p => p.IsOrphaned))
        {
            // 失效启动项不应被判高风险——程序根本不存在，没有风险可言
            Assert.NotEqual(RiskLevel.HighRisk, profile.Level);
            Assert.StartsWith("启动项已失效", profile.StateText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RealScan_CountsText_MentionsResidueWhenPresent()
    {
        var vm = new MainViewModel();
        await vm.ScanAsync();

        if (vm.OrphanCount > 0)
        {
            Assert.Contains("残留", vm.CountsText);
        }
    }

    [Fact]
    public async Task RealScan_SwitchBackToAll_RestoresFullList()
    {
        var vm = new MainViewModel();
        await vm.ScanAsync();
        var total = vm.Profiles.Count;

        vm.ShowOrphanCommand.Execute(null);
        vm.ShowAllCommand.Execute(null);

        Assert.Equal(total, vm.FilteredProfiles.Count);
    }
}
