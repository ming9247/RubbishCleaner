using RubbishCleaner.App.ViewModels;
using RubbishCleaner.Core.BackupHistory;

namespace RubbishCleaner.App.Tests;

/// <summary>
/// 备份历史页的测试（v0.6）。
///
/// 全部注入假 IBackupHistoryStore，不碰真实备份目录、绝不真 import。
/// 两条核心断言：① 回滚必须经过二次确认（未确认直接抛，视图层负责弹窗）；
/// ② 这一页除 Rollback 外不得有任何写命令（反射锁死）。
/// </summary>
public class BackupHistoryViewModelTests
{
    private static BackupHistoryEntry Entry(
        string name = "测试备份",
        bool canRollback = true,
        DateTimeOffset? at = null) => new()
    {
        FilePath = @"C:\backup\" + name + ".reg",
        FileName = name + ".reg",
        KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{DEADBEEF-0000-0000-0000-000000000000}",
        DisplayName = name,
        BackedUpAt = at ?? new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero),
        FileSizeBytes = 1024,
        RollbackBlockReason = canRollback ? null : "目标键当前已存在",
    };

    private sealed class FakeStore : IBackupHistoryStore
    {
        public List<BackupHistoryEntry> Entries { get; set; } = [];
        public string? DegradedReason { get; set; }
        public List<BackupHistoryEntry> RolledBack { get; } = [];
        public BackupRollbackResult RollbackResult { get; set; } =
            new() { Entry = Entry("回滚结果"), Success = true, Message = "已写回" };

        public Task<BackupHistoryResult> ListAsync(
            IProgress<string>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new BackupHistoryResult
            {
                Entries = Entries, Directory = @"C:\backup", DegradedReason = DegradedReason,
            });

        public Task<BackupRollbackResult> RollbackAsync(
            BackupHistoryEntry entry, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            RolledBack.Add(entry);
            return Task.FromResult(RollbackResult);
        }
    }

    private static void SetEntries(BackupHistoryViewModel vm, BackupHistoryStore? _ = null) { }

    [Fact]
    public async Task Refresh_Empty_ShowsEmptySummary()
    {
        var vm = new BackupHistoryViewModel(new FakeStore());

        await vm.RefreshAsync();

        Assert.Empty(vm.Entries);
        Assert.Contains("还没有备份", vm.SummaryText);
    }

    [Fact]
    public async Task Refresh_WithEntries_ShowsCountsAndRollableState()
    {
        var store = new FakeStore { Entries = [Entry("可回滚"), Entry("被占用", canRollback: false)] };
        var vm = new BackupHistoryViewModel(store);

        await vm.RefreshAsync();

        Assert.Equal(2, vm.Entries.Count);
        Assert.Contains("2 个备份", vm.SummaryText);
        Assert.Contains("1 个可回滚", vm.SummaryText);
        Assert.All(vm.Entries, e => Assert.Equal(e.CanRollback ? "可回滚" : "目标键当前已存在", e.StateText));
    }

    [Fact]
    public async Task Refresh_Degraded_ShowsReason()
    {
        var vm = new BackupHistoryViewModel(new FakeStore { DegradedReason = "3 个文件无法识别" });

        await vm.RefreshAsync();

        Assert.Contains("3 个文件无法识别", vm.StatusText);
    }

    [Fact]
    public async Task Rollback_WithoutConfirmation_Throws()
    {
        var store = new FakeStore();
        var vm = new BackupHistoryViewModel(store);
        var entry = Entry("未确认");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => vm.RollbackAsync(entry, confirmed: false));
        Assert.Empty(store.RolledBack);
    }

    [Fact]
    public async Task Rollback_Confirmed_CallsStoreAndUpdatesStatus()
    {
        var store = new FakeStore();
        var vm = new BackupHistoryViewModel(store);
        var entry = Entry("确认过的");

        await vm.RollbackAsync(entry, confirmed: true);

        var rolled = Assert.Single(store.RolledBack);
        Assert.Equal(entry.FilePath, rolled.FilePath);
        Assert.NotNull(vm.LastRollback);
        Assert.True(vm.LastRollback!.Success);
        Assert.Contains("已写回", vm.StatusText);
    }

    [Fact]
    public async Task Rollback_Failure_ShowsStoreMessage()
    {
        var store = new FakeStore
        {
            RollbackResult = new BackupRollbackResult
            {
                Entry = Entry("失败用例"),
                Success = false, Message = "目标键已存在（…），为避免覆盖未执行回滚；请人工核对该键内容",
            },
        };
        var vm = new BackupHistoryViewModel(store);

        await vm.RollbackAsync(Entry("失败用例"), confirmed: true);

        Assert.False(vm.LastRollback!.Success);
        Assert.Contains("未执行回滚", vm.StatusText);
    }

    [Fact]
    public async Task Rollback_Success_TriggersRefresh()
    {
        // 回滚改变键的存在性 → 列表必须刷新（刷新后 Entries 会被 Store 的当前值重建）
        var store = new FakeStore();
        var vm = new BackupHistoryViewModel(store);
        await vm.RefreshAsync();
        store.Entries = [Entry("回滚后重查")]; // Store 侧状态变化

        await vm.RollbackAsync(Entry("触发刷新"), confirmed: true);

        Assert.Single(vm.Entries);
        Assert.Equal("回滚后重查", vm.Entries[0].DisplayName);
    }

    [Fact]
    public void RollbackCommand_RequiresSelectedRollableEntry()
    {
        var vm = new BackupHistoryViewModel(new FakeStore());

        Assert.False(vm.RollbackCommand.CanExecute(null));
    }

    /// <summary>
    /// 铁律：这一页除 Rollback 外不得出现任何写操作命令。
    /// 反射扫所有 ICommand 属性与公开方法名，出现 Clean / Delete / Fix / Repair 即失败
    /// （Rollback 是本页的唯一写操作，白名单放行）。
    /// </summary>
    [Fact]
    public void ViewModel_HasNoCleanupCommands_ExceptRollback()
    {
        var type = typeof(BackupHistoryViewModel);
        var forbidden = new[] { "Clean", "Delete", "Fix", "Repair" };

        foreach (var p in type.GetProperties())
        {
            if (typeof(System.Windows.Input.ICommand).IsAssignableFrom(p.PropertyType))
            {
                foreach (var word in forbidden)
                {
                    Assert.False(p.Name.Contains(word, StringComparison.OrdinalIgnoreCase),
                        $"备份历史页出现了写操作命令 {p.Name}（本页唯一允许的写操作是 Rollback）");
                }
            }
        }

        foreach (var m in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (m.DeclaringType == type)
            {
                foreach (var word in forbidden)
                {
                    Assert.False(m.Name.Contains(word, StringComparison.OrdinalIgnoreCase),
                        $"备份历史页出现了写操作方法 {m.Name}（本页唯一允许的写操作是 Rollback）");
                }
            }
        }
    }
}