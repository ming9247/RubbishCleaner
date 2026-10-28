using RubbishCleaner.App.ViewModels;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Uninstall;

namespace RubbishCleaner.App.Tests;

/// <summary>
/// 批量卸载（v0.9.3）的测试。
///
/// 背景：用户真机反馈「多选了几个软件，点『卸载选中的软件』只卸了第一个，
/// 第一个的卸载进程结束后不会接着启动第二个」。根因是卸载命令只处理 <c>Selected</c> 单个条目，
/// 而且列表根本没有开多选（SelectionMode 默认 Single）。
///
/// 四条约定：
/// ① 多选时逐个执行、**严格串行**（同一时刻最多一个卸载进程）；
/// ② 一个条目预检查不通过只跳过它，不能拖垮整批；
/// ③ 只确认一次，且确认文案里列出每个软件的真实命令；
/// ④ 取消确认时一个都不执行。
///
/// 执行与枚举都通过 internal 构造注入，测试里绝不启动真实卸载程序。
/// </summary>
public class AppsBatchUninstallTests
{
    /// <summary>可观察的执行器：记录调用顺序与并发峰值。</summary>
    private sealed class FakeExecutor
    {
        private int _running;

        public List<string?> Commands { get; } = [];

        public int MaxConcurrent { get; private set; }

        public Func<string?, UninstallOutcome> Handler { get; set; } =
            _ => new UninstallOutcome(true, true, 0, "卸载程序已正常结束", TimeSpan.Zero);

        public UninstallOutcome Execute(string? command)
        {
            _running++;
            MaxConcurrent = Math.Max(MaxConcurrent, _running);
            Commands.Add(command);
            try
            {
                return Handler(command);
            }
            finally
            {
                _running--;
            }
        }
    }

    /// <summary>卸载程序存在于磁盘上的假条目——预检查会通过（notepad 永远在）。</summary>
    private static InstalledApp App(string name) => new()
    {
        Id = name,
        DisplayName = name,
        RegistryPath = @"HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\" + name,
        UninstallCommand = $@"C:\Windows\System32\notepad.exe /uninstall {name}",
    };

    private static (AppsViewModel Vm, FakeExecutor Executor, List<string> Notifications) Create(params InstalledApp[] apps)
    {
        var executor = new FakeExecutor();
        var vm = new AppsViewModel(
            () => new InstalledAppsResult(apps, apps.Length, 0),
            executor.Execute)
        {
            ConfirmHandler = (_, _) => true,
        };
        var notifications = new List<string>();
        vm.NotifyHandler = (title, message) => notifications.Add(title + "|" + message);
        return (vm, executor, notifications);
    }

    private static async Task LoadAsync(AppsViewModel vm) => await vm.RefreshAsync();

    [Fact]
    public async Task Selection_UpdatesButtonText()
    {
        var (vm, _, _) = Create(App("A"), App("B"));
        await LoadAsync(vm);

        Assert.Equal("卸载选中的软件", vm.UninstallButtonText);
        Assert.False(vm.HasSelection);

        vm.SetSelection(vm.FilteredApps.Take(2));

        Assert.True(vm.HasSelection);
        Assert.Equal("卸载选中的 2 个软件", vm.UninstallButtonText);
    }

    [Fact]
    public async Task MultiSelect_UninstallsEverySelectedAppInOrder_OneAtATime()
    {
        var (vm, executor, _) = Create(App("Alpha"), App("Bravo"), App("Charlie"));
        await LoadAsync(vm);
        // 给每次执行加 40ms 延迟：如果实现是并发启动的，MaxConcurrent 会 > 1 而被这条测试抓到。
        executor.Handler = _ =>
        {
            Thread.Sleep(40);
            return new UninstallOutcome(true, true, 0, "卸载程序已正常结束", TimeSpan.Zero);
        };

        vm.SetSelection(vm.FilteredApps.Take(3));
        await vm.UninstallSelectedAsync();

        Assert.Equal(3, executor.Commands.Count);
        Assert.Equal(1, executor.MaxConcurrent);            // 严格串行：同一时刻只有一个卸载进程
        // 顺序必须与选择顺序一致（前一个结束后才启动下一个）
        Assert.Contains("Alpha", executor.Commands[0]);
        Assert.Contains("Bravo", executor.Commands[1]);
        Assert.Contains("Charlie", executor.Commands[2]);
    }

    [Fact]
    public async Task MultiSelect_BlockedItemIsSkipped_OthersStillRun()
    {
        // 第二个条目的卸载程序不存在 → 预检查拦住，只跳过它。
        var blocked = new InstalledApp
        {
            Id = "Broken",
            DisplayName = "Broken",
            RegistryPath = @"HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\Broken",
            UninstallCommand = @"C:\RcNotExist\unins000.exe",
        };
        var (vm, executor, notifications) = Create(App("Alpha"), blocked, App("Charlie"));
        await LoadAsync(vm);

        vm.SetSelection(vm.FilteredApps.Take(3));
        await vm.UninstallSelectedAsync();

        Assert.Equal(2, executor.Commands.Count);           // 两个可卸的都执行了
        Assert.Contains("已跳过", vm.StatusText);
        Assert.Contains("Broken", vm.StatusText);
        Assert.Contains(notifications, n => n.Contains("Broken"));
    }

    [Fact]
    public async Task CancelConfirmation_RunsNothing()
    {
        var (vm, executor, _) = Create(App("Alpha"), App("Bravo"));
        await LoadAsync(vm);
        vm.ConfirmHandler = (_, _) => false;

        vm.SetSelection(vm.FilteredApps.Take(2));
        await vm.UninstallSelectedAsync();

        Assert.Empty(executor.Commands);
        Assert.Equal("已取消卸载", vm.StatusText);
    }

    [Fact]
    public async Task ConfirmMessage_ListsEveryCommand()
    {
        var (vm, _, _) = Create(App("Alpha"), App("Bravo"));
        await LoadAsync(vm);
        string? captured = null;
        vm.ConfirmHandler = (_, message) => { captured = message; return false; };

        vm.SetSelection(vm.FilteredApps.Take(2));
        await vm.UninstallSelectedAsync();

        Assert.NotNull(captured);
        Assert.Contains("Alpha", captured);
        Assert.Contains("Bravo", captured);
        Assert.Contains("依次启动 2 个软件", captured);
        Assert.Contains("notepad.exe", captured);
    }

    [Fact]
    public async Task SingleSelection_KeepsOldBehaviour()
    {
        var (vm, executor, _) = Create(App("Alpha"));
        await LoadAsync(vm);

        vm.SetSelection(vm.FilteredApps.Take(1));
        await vm.UninstallSelectedAsync();

        Assert.Single(executor.Commands);
        Assert.Equal("卸载程序已结束", vm.StatusText);
    }

    [Fact]
    public async Task FailedItem_IsReportedAndDoesNotStopTheRest()
    {
        var (vm, executor, _) = Create(App("Alpha"), App("Bravo"));
        await LoadAsync(vm);
        var calls = 0;
        executor.Handler = _ => ++calls == 1
            ? new UninstallOutcome(true, false, null, "卸载程序超时未结束", TimeSpan.Zero)
            : new UninstallOutcome(true, true, 0, "卸载程序已正常结束", TimeSpan.Zero);

        vm.SetSelection(vm.FilteredApps.Take(2));
        await vm.UninstallSelectedAsync();

        Assert.Equal(2, executor.Commands.Count);           // 第一个失败也要继续第二个
        Assert.Contains("失败 1 个", vm.StatusText);
        Assert.Contains("Alpha", vm.StatusText);
    }
}
