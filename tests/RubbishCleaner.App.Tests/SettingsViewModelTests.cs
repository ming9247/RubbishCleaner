using System.IO;
using RubbishCleaner.App.Infrastructure;
using RubbishCleaner.App.ViewModels;
using RubbishCleaner.Core.BackupHistory;

namespace RubbishCleaner.App.Tests;

/// <summary>
/// 设置页的测试（v0.7）。
///
/// 三条核心约定：
/// ① 联网必须默认关闭（opt-in）——开关关闭时更新按钮不可用；
/// ② 备份清理的 keepCount 来自界面输入且会持久化；非法输入不执行；
/// ③ 清理结果如实回显，失败时显示失败文件。
/// 全部注入假实现：不联网、不碰真实备份目录。
/// </summary>
public class SettingsViewModelTests
{
    // 联网相关测试只验证"开关关闭时绝不发起请求/按钮禁用"：TrustTableUpdater 的
    // Check/Update 不是 virtual，无法伪造结果；伪造下载器属于 Core 测试已覆盖的范围。

    private sealed class FakeStore : IBackupHistoryStore
    {
        public List<BackupHistoryEntry> Entries { get; set; } = [];
        public BackupCleanupResult CleanResult { get; set; } = new()
        {
            Success = true, Message = "已清理", TotalBackups = 5, KeptBackups = 3, RemovedBackups = 2,
        };
        public List<int> CleanCalls { get; } = [];

        public Task<BackupHistoryResult> ListAsync(
            IProgress<string>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new BackupHistoryResult { Entries = Entries, Directory = @"C:\backup" });

        public Task<BackupRollbackResult> RollbackAsync(
            BackupHistoryEntry entry, IProgress<string>? progress = null, CancellationToken ct = default)
            => throw new NotSupportedException("设置页测试不回滚");

        public Task<BackupCleanupResult> CleanOldBackupsAsync(
            int keepCount = 30, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            CleanCalls.Add(keepCount);
            return Task.FromResult(CleanResult);
        }
    }

    private static AppSettings TempSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rc-settings-test-{Guid.NewGuid():N}.json");
        return new AppSettings { SettingsPathOverride = path };
    }

    [Fact]
    public void Networking_DefaultsToOff()
    {
        var vm = new SettingsViewModel(backupStore: new FakeStore(), settings: TempSettings());

        Assert.False(vm.EnableNetworking);
        Assert.Contains("联网已关闭", vm.NetworkNotice);
    }

    [Fact]
    public async Task CheckUpdate_WhenNetworkingOff_DoesNothing()
    {
        var vm = new SettingsViewModel(backupStore: new FakeStore(), settings: TempSettings());

        await vm.CheckUpdateAsync();

        // 状态不变、无错误——关键是绝不发起网络请求（updater 工厂都没被调用）。
        Assert.Equal("还没有检查过更新", vm.UpdateStatusText);
    }

    [Fact]
    public async Task UpdateNow_WhenNetworkingOff_DoesNothing()
    {
        var vm = new SettingsViewModel(backupStore: new FakeStore(), settings: TempSettings());

        await vm.UpdateNowAsync();

        Assert.Equal("还没有检查过更新", vm.UpdateStatusText);
    }

    [Fact]
    public async Task CheckUpdate_CommandCannotExecuteWhenNetworkingOff()
    {
        var vm = new SettingsViewModel(backupStore: new FakeStore(), settings: TempSettings());

        Assert.False(vm.CheckUpdateCommand.CanExecute(null));
        Assert.False(vm.UpdateNowCommand.CanExecute(null));

        vm.EnableNetworking = true;
        Assert.True(vm.CheckUpdateCommand.CanExecute(null));
        Assert.True(vm.UpdateNowCommand.CanExecute(null));
    }

    [Fact]
    public void EnableNetworking_PersistsToSettingsFile()
    {
        var settings = TempSettings();
        var vm = new SettingsViewModel(backupStore: new FakeStore(), settings: settings);

        vm.EnableNetworking = true;

        // 落盘验证：新读出来的实例同样是 true。
        var reloaded = AppSettings.Load(settings.SettingsPathOverride);
        Assert.True(reloaded.EnableNetworking);
    }

    [Fact]
    public async Task CleanOldBackups_ValidCount_UsesCountAndShowsResult()
    {
        var store = new FakeStore();
        var vm = new SettingsViewModel(backupStore: store, settings: TempSettings())
        {
            KeepBackupCountText = "15",
            ConfirmCleanOldBackups = _ => true,
        };
        store.Entries = Enumerable.Range(0, 20)
            .Select(i => new BackupHistoryEntry
            {
                FilePath = @"C:\backup\orphan-x-" + i + ".reg",
                FileName = "orphan-x-" + i + ".reg",
                KeyPath = @"HKEY_CURRENT_USER\Software\X" + i,
                BackedUpAt = DateTimeOffset.Now.AddMinutes(-i),
                FileSizeBytes = 100,
            })
            .ToList();

        await vm.CleanOldBackupsAsync();

        var call = Assert.Single(store.CleanCalls);
        Assert.Equal(15, call);
        Assert.Contains("保留 3 个", vm.UpdateStatusText);
        Assert.Contains("2 个已移入回收站", vm.UpdateStatusText);
    }

    [Fact]
    public async Task CleanOldBackups_NotExceeding_KeepsWithoutCallingStore()
    {
        var store = new FakeStore();
        var vm = new SettingsViewModel(backupStore: store, settings: TempSettings())
        {
            KeepBackupCountText = "30",
            ConfirmCleanOldBackups = _ => throw new InvalidOperationException("不应弹确认框"),
        };
        store.Entries = [];

        await vm.CleanOldBackupsAsync();

        Assert.Empty(store.CleanCalls);
        Assert.Contains("无需清理", vm.UpdateStatusText);
    }

    [Fact]
    public async Task CleanOldBackups_InvalidCount_ShowsErrorAndSkips()
    {
        var store = new FakeStore();
        var vm = new SettingsViewModel(backupStore: store, settings: TempSettings())
        {
            KeepBackupCountText = "abc",
        };

        await vm.CleanOldBackupsAsync();

        Assert.Empty(store.CleanCalls);
        Assert.Contains("正整数", vm.ErrorText);
    }

    [Fact]
    public async Task CleanOldBackups_NotConfirmed_DoesNotCallStore()
    {
        var store = new FakeStore();
        var vm = new SettingsViewModel(backupStore: store, settings: TempSettings())
        {
            KeepBackupCountText = "3",
            ConfirmCleanOldBackups = _ => false,
        };
        store.Entries = Enumerable.Range(0, 10)
            .Select(i => new BackupHistoryEntry
            {
                FilePath = @"C:\backup\orphan-y-" + i + ".reg",
                FileName = "orphan-y-" + i + ".reg",
                KeyPath = @"HKEY_CURRENT_USER\Software\Y" + i,
                BackedUpAt = DateTimeOffset.Now.AddMinutes(-i),
                FileSizeBytes = 100,
            })
            .ToList();

        await vm.CleanOldBackupsAsync();

        Assert.Empty(store.CleanCalls);
        Assert.Contains("已取消", vm.UpdateStatusText);
    }

    [Fact]
    public async Task CleanOldBackups_StoreFailure_ShowsMessage()
    {
        var store = new FakeStore
        {
            CleanResult = new BackupCleanupResult
            {
                Success = false, Message = "2 个文件移入回收站失败",
                FailedFiles = ["orphan-a.reg", "orphan-b.reg"],
            },
        };
        var vm = new SettingsViewModel(backupStore: store, settings: TempSettings())
        {
            KeepBackupCountText = "3",
            ConfirmCleanOldBackups = _ => true,
        };
        store.Entries = Enumerable.Range(0, 10)
            .Select(i => new BackupHistoryEntry
            {
                FilePath = @"C:\backup\orphan-z-" + i + ".reg",
                FileName = "orphan-z-" + i + ".reg",
                KeyPath = @"HKEY_CURRENT_USER\Software\Z" + i,
                BackedUpAt = DateTimeOffset.Now.AddMinutes(-i),
                FileSizeBytes = 100,
            })
            .ToList();

        await vm.CleanOldBackupsAsync();

        Assert.Single(store.CleanCalls);
        Assert.Contains("清理未完成", vm.UpdateStatusText);
        Assert.Contains("orphan-a.reg", vm.UpdateStatusText);
    }

    [Fact]
    public async Task CleanOldBackups_PersistsKeepCount()
    {
        var settings = TempSettings();
        var store = new FakeStore();
        var vm = new SettingsViewModel(backupStore: store, settings: settings)
        {
            KeepBackupCountText = "12",
            ConfirmCleanOldBackups = _ => true,
        };
        store.Entries = [];

        await vm.CleanOldBackupsAsync();  // 无需清理，但合法输入已持久化

        var reloaded = AppSettings.Load(settings.SettingsPathOverride);
        Assert.Equal(12, reloaded.KeepBackupCount);
    }
}
