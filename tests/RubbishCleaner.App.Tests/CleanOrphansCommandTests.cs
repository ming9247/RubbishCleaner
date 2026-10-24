using RubbishCleaner.App.ViewModels;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.App.Tests;

/// <summary>
/// 清理命令的 ViewModel 层测试。
///
/// 通过注入 <see cref="MainViewModel.ConfirmHandler"/> / <see cref="MainViewModel.NotifyHandler"/>
/// 绕开模态弹窗 —— 否则测试会阻塞在 MessageBox 上（这是把对话框抽成委托的直接原因）。
/// </summary>
public class CleanOrphansCommandTests
{
    [Fact]
    public async Task CleanOrphans_WhenUserCancels_DeletesNothing()
    {
        var vm = new MainViewModel();
        await vm.ScanAsync();

        // 只有存在"可自动清理"的残留（Run 类）时，才会走到确认这一步。
        // 服务类残留会被 BuildPlan 拒绝，因此这里要精确判断。
        var cleanableCount = vm.Profiles
            .SelectMany(p => p.Profile.StartupEntries)
            .Count(e => e.IsOrphan && e.RegistryAnchor is not null
                        && e.Source is StartupSourceKind.RegistryRunHkcu
                            or StartupSourceKind.RegistryRunHklm
                            or StartupSourceKind.RegistryRunWow64);

        if (cleanableCount == 0)
        {
            return;   // 本机没有 Run 类残留，无法验证取消路径
        }

        var confirmCalled = 0;
        var notifyCalled = 0;
        vm.ConfirmHandler = (_, _) =>
        {
            confirmCalled++;
            return false;   // 用户点了取消
        };
        vm.NotifyHandler = (_, _) => notifyCalled++;

        var orphansBefore = vm.OrphanCount;
        vm.CleanOrphansCommand.Execute(null);

        // 命令是 async void，给它一点时间跑完
        await Task.Delay(300);

        Assert.Equal(1, confirmCalled);
        Assert.Equal(0, notifyCalled);                 // 取消后不应弹结果框
        Assert.Equal(orphansBefore, vm.OrphanCount);   // 残留数量不变
    }

    [Fact]
    public async Task CleanOrphans_WhenNothingToClean_DoesNotAskForConfirmation()
    {
        var vm = new MainViewModel();
        await vm.ScanAsync();

        // 关键：OrphanCount > 0 不代表"可清理"——
        // 例如服务类残留（Winstep）会被 BuildPlan 拒绝，
        // 此时不应弹确认框，而应告知"不属于可自动清理的类型"。
        var confirmCalled = false;
        string? notified = null;
        vm.ConfirmHandler = (_, _) => { confirmCalled = true; return true; };
        vm.NotifyHandler = (_, m) => notified = m;

        // 判断是否存在"可自动清理"的残留：只有 Run 类才算
        var cleanable = vm.Profiles
            .SelectMany(p => p.Profile.StartupEntries)
            .Any(e => e.IsOrphan && e.RegistryAnchor is not null
                      && e.Source is StartupSourceKind.RegistryRunHkcu
                          or StartupSourceKind.RegistryRunHklm
                          or StartupSourceKind.RegistryRunWow64);

        vm.CleanOrphansCommand.Execute(null);
        await Task.Delay(300);

        if (cleanable)
        {
            // 有可清理项时应该先问一句
            Assert.True(confirmCalled);
            return;
        }

        Assert.False(confirmCalled);
        Assert.NotNull(notified);
    }

    [Fact]
    public void CleanOrphans_CanExecute_IsFalseBeforeScan()
    {
        var vm = new MainViewModel();

        // 还没扫描，没有残留，命令应当不可用
        Assert.False(vm.CleanOrphansCommand.CanExecute(null));
    }

    [Fact]
    public void BackupDirectory_IsUnderLocalAppData()
    {
        var dir = MainViewModel.BackupDirectory;

        Assert.Contains("RubbishCleaner", dir);
        Assert.Contains("backups", dir);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            dir,
            StringComparison.OrdinalIgnoreCase);
    }
}
