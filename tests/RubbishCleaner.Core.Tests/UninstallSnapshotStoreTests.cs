using System.IO;
using System.Text.Json;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Residue;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// 卸载前快照的采集与持久化测试（v0.3.1）。
///
/// 全部用 %TEMP% 下自建目录；<b>绝不触碰用户真实软件目录</b>。
/// </summary>
public class UninstallSnapshotStoreTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rc-snapshot-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static InstalledApp App(string name, string? installLocation) => new()
    {
        Id = name,
        DisplayName = name,
        RegistryPath = @"HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\" + name,
        InstallLocation = installLocation,
        UninstallCommand = installLocation is null ? null : Path.Combine(installLocation, "unins000.exe"),
        Publisher = "测试厂商",
    };

    [Fact]
    public void TakeSnapshot_RecordsFilesAndDirectories()
    {
        var root = TempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            File.WriteAllText(Path.Combine(root, "app.exe"), new string('x', 100));
            File.WriteAllText(Path.Combine(root, "bin", "lib.dll"), new string('y', 50));

            var snapshot = UninstallSnapshotStore.TakeSnapshot(App("测试软件", root));

            Assert.NotNull(snapshot);
            Assert.Equal("测试软件", snapshot!.SoftwareName);
            Assert.Equal(2, snapshot.FileCount);
            Assert.Equal(1, snapshot.DirectoryCount);
            Assert.Equal(150, snapshot.TotalBytes);
            Assert.Contains(snapshot.Entries, e => e.RelativePath == "app.exe" && !e.IsDirectory && e.SizeBytes == 100);
            Assert.Contains(snapshot.Entries, e => e.RelativePath == "bin" && e.IsDirectory);
            Assert.Contains(snapshot.Entries, e => e.RelativePath == Path.Combine("bin", "lib.dll") && e.SizeBytes == 50);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void TakeSnapshot_NoInstallLocation_ReturnsNull()
    {
        Assert.Null(UninstallSnapshotStore.TakeSnapshot(App("无路径软件", null)));
        Assert.Null(UninstallSnapshotStore.TakeSnapshot(App("空白路径软件", "   ")));
    }

    [Fact]
    public void TakeSnapshot_MissingDirectory_ReturnsNull()
    {
        Assert.Null(UninstallSnapshotStore.TakeSnapshot(App("不存在软件", @"C:\NoSuchDir\RcTest")));
    }

    [Fact]
    public void TakeSnapshot_EmptyDirectory_ReturnsNull()
    {
        var root = TempDir();
        try
        {
            Assert.Null(UninstallSnapshotStore.TakeSnapshot(App("空目录软件", root)));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsExactly()
    {
        var root = TempDir();
        var store = TempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "a.exe"), "aaa");
            File.WriteAllText(Path.Combine(root, Path.Combine("sub", "b.dll")), "bbbb");

            var snapshot = UninstallSnapshotStore.TakeSnapshot(App("往返测试", root))!;
            var path = UninstallSnapshotStore.Save(snapshot, store);

            Assert.True(File.Exists(path));

            var loaded = UninstallSnapshotStore.Load(path);

            Assert.NotNull(loaded);
            Assert.Equal(snapshot.SoftwareName, loaded!.SoftwareName);
            Assert.Equal(snapshot.InstallLocation, loaded.InstallLocation);
            Assert.Equal(snapshot.FileCount, loaded.FileCount);
            Assert.Equal(snapshot.DirectoryCount, loaded.DirectoryCount);
            Assert.Equal(snapshot.TotalBytes, loaded.TotalBytes);
            Assert.Equal(snapshot.Entries.Count, loaded.Entries.Count);
            Assert.All(snapshot.Entries, e =>
                Assert.Contains(loaded.Entries, l =>
                    l.RelativePath == e.RelativePath && l.IsDirectory == e.IsDirectory && l.SizeBytes == e.SizeBytes));
        }
        finally
        {
            Cleanup(root);
            Cleanup(store);
        }
    }

    [Fact]
    public void Load_MissingOrCorruptFile_ReturnsNull()
    {
        var dir = TempDir();
        try
        {
            Assert.Null(UninstallSnapshotStore.Load(Path.Combine(dir, "nope.json")));
            Assert.Null(UninstallSnapshotStore.Load(""));

            var broken = Path.Combine(dir, "broken.json");
            File.WriteAllText(broken, "{ this is not json ");
            Assert.Null(UninstallSnapshotStore.Load(broken));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void LoadLatest_ReturnsMostRecentSnapshot()
    {
        var root = TempDir();
        var store = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.exe"), "aaa");

            var older = UninstallSnapshotStore.TakeSnapshot(App("最新测试", root))!;
            var olderPath = UninstallSnapshotStore.Save(older with { TakenAt = DateTimeOffset.Now.AddMinutes(-5) }, store);

            var newer = UninstallSnapshotStore.TakeSnapshot(App("最新测试", root))!;
            var newerPath = UninstallSnapshotStore.Save(newer, store);

            var latest = UninstallSnapshotStore.LoadLatest(store, "最新测试");

            Assert.NotNull(latest);
            Assert.Equal(newer.TakenAt, latest!.TakenAt);
            Assert.True(latest.TakenAt >= older.TakenAt);
        }
        finally
        {
            Cleanup(root);
            Cleanup(store);
        }
    }

    [Fact]
    public void LoadLatest_UnknownSoftware_ReturnsNull()
    {
        var store = TempDir();
        try
        {
            Assert.Null(UninstallSnapshotStore.LoadLatest(store, "从没卸过的软件"));
        }
        finally
        {
            Cleanup(store);
        }
    }

    [Fact]
    public void Prune_KeepsOnlyRecentSnapshotsPerSoftware()
    {
        var store = TempDir();
        try
        {
            var baseTime = DateTimeOffset.Now;
            for (var i = 0; i < 5; i++)
            {
                UninstallSnapshotStore.Save(
                    new UninstallSnapshot(
                        "清理测试", @"C:\Fake", baseTime.AddMinutes(-i),
                        [new SnapshotEntry("a.exe", false, 1)]),
                    store);
            }

            Assert.Equal(5, Directory.GetFiles(store, "*.json").Length);

            var removed = UninstallSnapshotStore.Prune(store, keepPerSoftware: 2);

            Assert.Equal(3, removed);
            Assert.Equal(2, Directory.GetFiles(store, "*.json").Length);

            // 留下的应该是最新的两份
            var latest = UninstallSnapshotStore.LoadLatest(store, "清理测试");
            Assert.Equal(baseTime, latest!.TakenAt);
        }
        finally
        {
            Cleanup(store);
        }
    }

    [Fact]
    public void Prune_KeepsDifferentSoftwaresSeparately()
    {
        var store = TempDir();
        try
        {
            var t = DateTimeOffset.Now;
            UninstallSnapshotStore.Save(new UninstallSnapshot("软件甲", @"C:\A", t, []), store);
            UninstallSnapshotStore.Save(new UninstallSnapshot("软件甲", @"C:\A", t.AddMinutes(-1), []), store);
            UninstallSnapshotStore.Save(new UninstallSnapshot("软件乙", @"C:\B", t, []), store);
            UninstallSnapshotStore.Save(new UninstallSnapshot("软件乙", @"C:\B", t.AddMinutes(-1), []), store);
            UninstallSnapshotStore.Save(new UninstallSnapshot("软件乙", @"C:\B", t.AddMinutes(-2), []), store);

            var removed = UninstallSnapshotStore.Prune(store, keepPerSoftware: 2);

            // 软件甲 2 份全留，软件乙 删 1 份
            Assert.Equal(1, removed);
            Assert.Equal(4, Directory.GetFiles(store, "*.json").Length);
            Assert.NotNull(UninstallSnapshotStore.LoadLatest(store, "软件甲"));
            Assert.NotNull(UninstallSnapshotStore.LoadLatest(store, "软件乙"));
        }
        finally
        {
            Cleanup(store);
        }
    }

    [Fact]
    public void Save_SanitizesIllegalFileNameCharacters()
    {
        var store = TempDir();
        try
        {
            var snapshot = new UninstallSnapshot(
                "带/斜杠\\和:冒号的软件", @"C:\Fake", DateTimeOffset.Now, []);

            var path = UninstallSnapshotStore.Save(snapshot, store);

            Assert.True(File.Exists(path));
            Assert.DoesNotContain('/', Path.GetFileName(path));
            Assert.DoesNotContain(':', Path.GetFileName(path));
        }
        finally
        {
            Cleanup(store);
        }
    }

    [Fact]
    public void TakeSnapshot_UnreadableSubtree_DoesNotThrow()
    {
        var root = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, "ok.exe"), "ok");

            // 造一个名字极长的深层目录，让枚举中途失败也不影响整体快照
            var deep = Path.Combine(root, new string('d', 200));
            Directory.CreateDirectory(deep);

            var snapshot = UninstallSnapshotStore.TakeSnapshot(App("部分可读软件", root));

            // 至少采到了能采的部分，没有抛异常
            Assert.NotNull(snapshot);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Json_UsesRelaxedEncoderSoChineseNamesStayReadable()
    {
        var store = TempDir();
        try
        {
            var path = UninstallSnapshotStore.Save(
                new UninstallSnapshot("微信开发者工具", @"C:\Fake", DateTimeOffset.Now, []),
                store);

            var json = File.ReadAllText(path);

            // 中文不应该被转义成 \uXXXX，否则人工排查快照时全是乱码
            Assert.Contains("微信开发者工具", json);
            Assert.DoesNotContain("\\u5FAE", json);
        }
        finally
        {
            Cleanup(store);
        }
    }
}
