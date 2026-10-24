using RubbishCleaner.App.ViewModels;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Uninstall;

namespace RubbishCleaner.App.Tests;

/// <summary>
/// 软件管理页（v0.2）的 ViewModel 测试。
///
/// 用真实注册表数据驱动，只断言"性质"而非具体软件，
/// 这样在任何机器上都能通过，同时能真实覆盖整条链路。
/// </summary>
public class AppsViewModelTests
{
    [Fact]
    public async Task Refresh_LoadsAppsFromRealRegistry()
    {
        var vm = new AppsViewModel();

        await vm.RefreshAsync();

        Assert.True(vm.HasLoaded);
        Assert.NotEmpty(vm.Apps);
        Assert.Equal(vm.Apps.Count, vm.FilteredApps.Count);   // 无筛选时两者一致
        Assert.Contains("共", vm.SummaryText);
    }

    [Fact]
    public async Task Refresh_NoAppLacksDisplayNameOrUninstallCommand()
    {
        var vm = new AppsViewModel();
        await vm.RefreshAsync();

        Assert.All(vm.Apps, app =>
        {
            Assert.False(string.IsNullOrWhiteSpace(app.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(app.UninstallCommandText));
        });
    }

    [Fact]
    public async Task Refresh_FiltersOutSystemComponents()
    {
        var vm = new AppsViewModel();
        await vm.RefreshAsync();

        // 摘要里应说明有过滤，让用户知道"不是所有注册表条目都列出来了"
        Assert.Contains("已过滤", vm.SummaryText);
    }

    [Fact]
    public async Task Filter_ByName_MatchesPartialKeyword()
    {
        var vm = new AppsViewModel();
        await vm.RefreshAsync();

        if (vm.Apps.Count == 0)
        {
            return;
        }

        var sample = vm.Apps[0].DisplayName;
        var keyword = sample.Length > 3 ? sample[..3] : sample;

        vm.FilterText = keyword;

        Assert.NotEmpty(vm.FilteredApps);
        Assert.All(vm.FilteredApps, a =>
            Assert.True(a.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                        || a.Publisher.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Filter_NoMatch_ProducesEmptyList()
    {
        var vm = new AppsViewModel();
        await vm.RefreshAsync();

        vm.FilterText = "zzz-这个软件名一定不存在-zzz";

        Assert.Empty(vm.FilteredApps);
    }

    [Fact]
    public async Task Filter_Clearing_RemovesFilterEffect()
    {
        var vm = new AppsViewModel();
        await vm.RefreshAsync();
        var total = vm.Apps.Count;

        vm.FilterText = "zzz-nothing-zzz";
        Assert.Empty(vm.FilteredApps);

        vm.FilterText = string.Empty;
        Assert.Equal(total, vm.FilteredApps.Count);
    }

    [Fact]
    public async Task UninstallCommand_RequiresSelection()
    {
        var vm = new AppsViewModel();
        await vm.RefreshAsync();

        Assert.Null(vm.Selected);
        Assert.False(vm.UninstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task UninstallCommand_EnabledOnlyForUninstallableApp()
    {
        var vm = new AppsViewModel();
        await vm.RefreshAsync();

        var uninstallable = vm.Apps.FirstOrDefault(a => a.CanUninstall);
        if (uninstallable is null)
        {
            return;
        }

        vm.Selected = uninstallable;

        Assert.True(vm.UninstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task BlockedApp_IsNotUninstallableAndExplainsWhy()
    {
        var vm = new AppsViewModel();
        await vm.RefreshAsync();

        foreach (var app in vm.Apps.Where(a => a.IsBlocked))
        {
            Assert.False(app.CanUninstall);
            Assert.NotNull(app.BlockReason);
            Assert.NotEmpty(app.BlockReason!);
        }
    }

    [Fact]
    public void BeforeRefresh_HasNotLoaded()
    {
        var vm = new AppsViewModel();

        Assert.False(vm.HasLoaded);
        Assert.Empty(vm.Apps);
    }
}
