using Microsoft.Win32;
using RubbishCleaner.Core.BackupHistory;
using RubbishCleaner.Core.RegistryCleanup;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// 备份历史扫描与回滚的测试（v0.6）。
///
/// <b>绝不真 import</b>：reg.exe 一律注入假委托；「复核」用的键存在性也是注入的
/// （HKCU 真实键仅用于验证 MapPhysicalKeyPath 的纯函数行为，不参与回滚链路）。
/// 临时目录与临时键在测试内自建自清理。
/// </summary>
public class BackupHistoryStoreTests : IDisposable
{
    private const string TestRoot = @"Software\RubbishCleanerTests";

    private readonly string _dir;
    private readonly List<string> _tempKeys = [];

    public BackupHistoryStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rc-bakhist-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
        foreach (var k in _tempKeys)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(k, false); } catch { }
        }
    }

    // ---------- 测试数据工厂 ----------

    /// <summary>写一个 UTF-16LE+BOM 的 .reg（与 RegistryKeyBackupWriter 同编码）。</summary>
    private string WriteReg(string fileName, string content)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content, new System.Text.UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        return path;
    }

    private static string BuildRegContent(string physicalPath, string? displayName) => string.Join("\r\n",
        "Windows Registry Editor Version 5.00",
        "",
        $"[{physicalPath}]",
        displayName is null ? "" : $"\"DisplayName\"=\"{displayName}\"",
        "\"WindowsInstaller\"=dword:00000001",
        "");

    private const string OrphanGuid = "{DEADBEEF-9999-8888-7777-666655554444}";
    private const string PhysicalPath64 = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + OrphanGuid;
    private const string PhysicalPath32 = @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" + OrphanGuid;

    private BackupHistoryStore Build(
        Func<string, bool>? fileExists = null,
        Func<string, int>? importRunner = null,
        Func<string, RegistryView, string, bool>? preRollbackKeyExists = null,
        Func<string, (RegistryView, string)?>? mapper = null,
        Func<string, RegistryView, string, bool>? postImportKeyExists = null)
        => new(
            _dir,
            fileExists ?? (_ => true),
            importRunner ?? (_ => 0),
            mapper ?? (Func<string, (RegistryView, string)?>)BackupHistoryStore.MapPhysicalKeyPath,
            preRollbackKeyExists ?? ((_, _, _) => false),
            postImportKeyExists ?? ((_, _, _) => true));

    // ---------- ListAsync：解析 ----------

    [Fact]
    public async Task List_NormalBackup_ParsesKeyPathAndDisplayName()
    {
        WriteReg($"orphan-{OrphanGuid}-20261025120000.reg", BuildRegContent(PhysicalPath64, "测试孤儿软件"));

        var result = await Build().ListAsync();

        Assert.Null(result.DegradedReason);
        var e = Assert.Single(result.Entries);
        Assert.Equal(PhysicalPath64, e.KeyPath);
        Assert.Equal("测试孤儿软件", e.DisplayName);
        Assert.True(e.CanRollback, "默认注入里键不存在、文件存在，应可回滚");
        Assert.True(e.BackedUpAt.Year >= 2026);
    }

    [Fact]
    public async Task List_32BitBackup_KeepsWow6432NodePhysicalPath()
    {
        WriteReg($"orphan-{OrphanGuid}-20261025120001.reg", BuildRegContent(PhysicalPath32, "32 位孤儿"));

        var result = await Build().ListAsync();

        Assert.Equal(PhysicalPath32, Assert.Single(result.Entries).KeyPath);
    }

    [Fact]
    public async Task List_FileWithoutTimestamp_FallsBackToLastWriteTime()
    {
        var path = WriteReg($"orphan-{OrphanGuid}.reg", BuildRegContent(PhysicalPath64, "无时间戳"));
        var written = DateTimeOffset.Now.AddMinutes(-3);
        File.SetLastWriteTime(path, written.DateTime);

        var result = await Build().ListAsync();

        var e = Assert.Single(result.Entries);
        Assert.True(Math.Abs((e.BackedUpAt - written).TotalMinutes) < 1,
            $"应回退到文件修改时间 {written}，实际 {e.BackedUpAt}");
    }

    [Fact]
    public async Task List_MissingDisplayName_YieldsNullNotReference()
    {
        WriteReg($"orphan-{OrphanGuid}-20261025120002.reg", BuildRegContent(PhysicalPath64, null));

        var result = await Build().ListAsync();

        Assert.Null(Assert.Single(result.Entries).DisplayName);
    }

    [Fact]
    public async Task List_FileWithoutSectionHeader_IsCountedAsUnreadable()
    {
        WriteReg("orphan-{DEADBE00-0000-0000-0000-000000000000}-20261025120003.reg", "不是 reg 内容");

        var result = await Build().ListAsync();

        Assert.Empty(result.Entries);
        Assert.NotNull(result.DegradedReason);
        Assert.Contains("1 个文件", result.DegradedReason);
    }

    [Fact]
    public async Task List_NonOrphanFiles_AreIgnored()
    {
        File.WriteAllText(Path.Combine(_dir, "readme.txt"), "说明文件");
        WriteReg("random-backup.reg", BuildRegContent(PhysicalPath64, null));

        var result = await Build().ListAsync();

        // random-backup.reg 没有 orphan- 前缀 → 不识别；readme.txt 也不是 .reg
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task List_DirectoryMissing_IsDegradedWithReason()
    {
        var store = new BackupHistoryStore(Path.Combine(Path.GetTempPath(), "rc-no-such-dir-" + Guid.NewGuid().ToString("N")[..6]));

        var result = await store.ListAsync();

        Assert.Empty(result.Entries);
        Assert.True(result.IsDegraded);
        Assert.Contains("不存在", result.DegradedReason);
    }

    [Fact]
    public async Task List_EntriesOrderedByTimeDescending()
    {
        WriteReg($"orphan-{OrphanGuid}-20261025120000.reg", BuildRegContent(PhysicalPath64, "早"));
        WriteReg($"orphan-{{AAAA1111-2222-3333-4444-555566667777}}-20261025130000.reg".Replace("{{", "{").Replace("}}", "}"), BuildRegContent(PhysicalPath64, "晚"));

        var result = await Build().ListAsync();

        Assert.Equal(2, result.Entries.Count);
        Assert.True(result.Entries[0].BackedUpAt >= result.Entries[1].BackedUpAt);
    }

    // ---------- MapPhysicalKeyPath：ADR-049 的逆运算 ----------

    [Fact]
    public void Map_Hklm64View_ReturnsOriginalSubPath()
    {
        var mapped = BackupHistoryStore.MapPhysicalKeyPath(PhysicalPath64);

        Assert.NotNull(mapped);
        Assert.Equal(RegistryView.Registry64, mapped.Value.View);
        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + OrphanGuid, mapped.Value.SubKeyPath);
    }

    [Fact]
    public void Map_Wow6432Node_MapsToRegistry32AndStripsWow()
    {
        var mapped = BackupHistoryStore.MapPhysicalKeyPath(PhysicalPath32);

        Assert.NotNull(mapped);
        Assert.Equal(RegistryView.Registry32, mapped.Value.View);
        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + OrphanGuid, mapped.Value.SubKeyPath);
    }

    [Fact]
    public void Map_Hkcu_KeepsPathAsIs()
    {
        var mapped = BackupHistoryStore.MapPhysicalKeyPath(@"HKEY_CURRENT_USER\Software\Foo");

        Assert.NotNull(mapped);
        Assert.Equal(RegistryView.Registry64, mapped.Value.View);
        Assert.Equal(@"Software\Foo", mapped.Value.SubKeyPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"HKEY_USERS\.DEFAULT\Foo")]
    public void Map_UnsupportedPaths_ReturnNull(string? path)
    {
        Assert.Null(BackupHistoryStore.MapPhysicalKeyPath(path!));
    }

    [Fact]
    public void Map_RealRegistryRoundTrip_Wow6432NodeMapsTo32BitView()
    {
        // 真机验证映射的正确性：WOW6432Node 物理路径映射回 32 位视图后，
        // 用该视图打开应能读到同一个键的内容（本机 HKLM\32 有 241 个子键，取一个真实的）。
        var real32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
            .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        if (real32 is null)
        {
            return; // 无法读取时跳过（理论不该发生）
        }

        using (real32)
        {
            var guid = real32.GetSubKeyNames().FirstOrDefault(n =>
                n.StartsWith('{') && n.EndsWith('}'));
            if (guid is null)
            {
                return;
            }

            var mapped = BackupHistoryStore.MapPhysicalKeyPath(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" + guid);

            Assert.NotNull(mapped);
            Assert.Equal(RegistryView.Registry32, mapped.Value.View);
            using var via32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, mapped.Value.View)
                .OpenSubKey(mapped.Value.SubKeyPath);
            Assert.NotNull(via32);
        }
    }

    // ---------- RollbackAsync：三层防线 ----------

    [Fact]
    public async Task Rollback_BackupFileMissing_FailsWithoutCallingImport()
    {
        var path = WriteReg($"orphan-{OrphanGuid}-20261025120004.reg", BuildRegContent(PhysicalPath64, "会消失"));
        File.Delete(path);

        var imported = new List<string>();
        var store = Build(
            fileExists: _ => false, // 文件已删（防线 1 的注入）
            importRunner: p => { imported.Add(p); return 0; });

        var result = await store.RollbackAsync(new BackupHistoryEntry
        {
            FilePath = path, FileName = Path.GetFileName(path),
            KeyPath = PhysicalPath64, BackedUpAt = DateTimeOffset.Now, FileSizeBytes = 1,
        });

        Assert.False(result.Success);
        Assert.Contains("已不存在", result.Message);
        Assert.Empty(imported); // 防线 1：连 reg 都不该被调
    }

    [Fact]
    public async Task Rollback_TargetKeyExists_IsRejectedToAvoidOverwrite()
    {
        var path = WriteReg($"orphan-{OrphanGuid}-20261025120005.reg", BuildRegContent(PhysicalPath64, "新键"));
        var imported = new List<string>();
        var store = Build(
            importRunner: p => { imported.Add(p); return 0; },
            preRollbackKeyExists: (_, _, _) => true); // 目标键被人建回来了

        var result = await store.RollbackAsync(new BackupHistoryEntry
        {
            FilePath = path, FileName = Path.GetFileName(path),
            KeyPath = PhysicalPath64, BackedUpAt = DateTimeOffset.Now, FileSizeBytes = 1,
        });

        Assert.False(result.Success);
        Assert.Contains("已存在", result.Message);
        Assert.Contains("未执行回滚", result.Message);
        Assert.Empty(imported);
    }

    [Fact]
    public async Task Rollback_ImportFails_PropagatesExitCode()
    {
        var path = WriteReg($"orphan-{OrphanGuid}-20261025120006.reg", BuildRegContent(PhysicalPath64, null));
        var store = Build(importRunner: _ => 1);

        var result = await store.RollbackAsync(new BackupHistoryEntry
        {
            FilePath = path, FileName = Path.GetFileName(path),
            KeyPath = PhysicalPath64, BackedUpAt = DateTimeOffset.Now, FileSizeBytes = 1,
        });

        Assert.False(result.Success);
        Assert.Contains("退出码 1", result.Message);
    }

    [Fact]
    public async Task Rollback_ImportSucceedsButKeyStillMissing_IsReportedAsFailure()
    {
        var path = WriteReg($"orphan-{OrphanGuid}-20261025120007.reg", BuildRegContent(PhysicalPath64, null));
        // import 报成功，但复核发现键没回来——写操作必须以注册表实际状态为准
        var store = Build(importRunner: _ => 0, postImportKeyExists: (_, _, _) => false);

        var result = await store.RollbackAsync(new BackupHistoryEntry
        {
            FilePath = path, FileName = Path.GetFileName(path),
            KeyPath = PhysicalPath64, BackedUpAt = DateTimeOffset.Now, FileSizeBytes = 1,
        });

        Assert.False(result.Success);
        Assert.Contains("复核", result.Message);
    }

    [Fact]
    public async Task Rollback_SuccessPath_PassesFileToImportAndVerifies()
    {
        var path = WriteReg($"orphan-{OrphanGuid}-20261025120008.reg", BuildRegContent(PhysicalPath64, null));
        var imported = new List<string>();
        var store = Build(
            importRunner: p => { imported.Add(p); return 0; },
            postImportKeyExists: (_, _, p) => p.Contains(OrphanGuid)); // 复核：键回来了

        var result = await store.RollbackAsync(new BackupHistoryEntry
        {
            FilePath = path, FileName = Path.GetFileName(path),
            KeyPath = PhysicalPath64, BackedUpAt = DateTimeOffset.Now, FileSizeBytes = 1,
        });

        Assert.True(result.Success, result.Message);
        var file = Assert.Single(imported);
        Assert.EndsWith(".reg", file);
        Assert.NotNull(result.LogHint);
    }

    [Fact]
    public async Task Rollback_32BitBackup_UsesRegistry32ViewForVerification()
    {
        var path = WriteReg($"orphan-{OrphanGuid}-20261025120009.reg", BuildRegContent(PhysicalPath32, null));
        var checkedViews = new List<RegistryView>();
        var store = Build(
            importRunner: _ => 0,
            mapper: (Func<string, (RegistryView, string)?>)BackupHistoryStore.MapPhysicalKeyPath,
            postImportKeyExists: (path, v, p) => { checkedViews.Add(v); return true; });

        var result = await store.RollbackAsync(new BackupHistoryEntry
        {
            FilePath = path, FileName = Path.GetFileName(path),
            KeyPath = PhysicalPath32, BackedUpAt = DateTimeOffset.Now, FileSizeBytes = 1,
        });

        Assert.True(result.Success, result.Message);
        Assert.Contains(RegistryView.Registry32, checkedViews);
    }

    [Fact]
    public async Task Rollback_ProgressReported()
    {
        var path = WriteReg($"orphan-{OrphanGuid}-20261025120010.reg", BuildRegContent(PhysicalPath64, null));
        var stages = new List<string>();
        var store = Build();

        await store.RollbackAsync(new BackupHistoryEntry
        {
            FilePath = path, FileName = Path.GetFileName(path),
            KeyPath = PhysicalPath64, BackedUpAt = DateTimeOffset.Now, FileSizeBytes = 1,
        }, new Progress<string>(s => stages.Add(s)));

        Assert.Contains(stages, s => s.Contains("回滚"));
    }

    // ---------- 真机只读回归 ----------

    [Fact]
    public async Task List_RealBackupDirectory_ReadOnlySmoke()
    {
        // 用真实默认目录（可能为空）——只读，验证生产构造与编码路径不炸
        var store = new BackupHistoryStore();

        var result = await store.ListAsync();

        Assert.NotNull(result);
        Assert.All(result.Entries, e => Assert.True(e.FileSizeBytes > 0));
    }

    [Fact]
    public async Task Rollback_CancellationRequested_Throws()
    {
        var path = WriteReg($"orphan-{OrphanGuid}-20261025120011.reg", BuildRegContent(PhysicalPath64, null));
        var store = Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.RollbackAsync(new BackupHistoryEntry
            {
                FilePath = path, FileName = Path.GetFileName(path),
                KeyPath = PhysicalPath64, BackedUpAt = DateTimeOffset.Now, FileSizeBytes = 1,
            }, cancellationToken: cts.Token));
    }
}