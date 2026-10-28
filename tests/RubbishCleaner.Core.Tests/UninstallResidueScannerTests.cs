using System.IO;
using RubbishCleaner.Common.Mft;
using RubbishCleaner.Core.Leftover;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Residue;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// 卸载后定向残留扫描测试（v0.3.1）。
///
/// 用假索引 + 临时目录驱动；<b>绝不触碰用户真实软件目录</b>。
/// </summary>
public class UninstallResidueScannerTests
{
    private const string InstallRoot = @"C:\Program Files\RcTestApp";

    private sealed class FakeIndex : IVolumeFileIndex
    {
        public FileIndexAvailability Availability => FileIndexAvailability.Available;

        public string? UnavailableReason => null;

        public string Description => "测试索引";

        public Task<bool> PrepareAsync(
            IReadOnlyCollection<string> rootPaths,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public IReadOnlyList<IndexedFile> FindFiles(string directoryPath, IReadOnlySet<string> fileNames) => [];

        public DirectoryStatistics GetStatistics(string directoryPath)
            => new(4096, 3, 1, new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero));

        public void Release()
        {
        }
    }

    private sealed class FakeEnvironment(params string[] existing) : ILeftoverEnvironment
    {
        public bool DirectoryExists(string path)
            => existing.Any(d => string.Equals(d, path, StringComparison.OrdinalIgnoreCase));

        public string? SelfDirectory => @"D:\Tools\RubbishCleaner";
    }

    private static UninstallSnapshot Snapshot(
        string name = "测试软件",
        string? installLocation = InstallRoot,
        params (string Path, bool IsDir, long Size)[] entries)
        => new(
            name,
            installLocation,
            DateTimeOffset.Now.AddSeconds(-1),
            entries.Length == 0
                ? [new SnapshotEntry("app.exe", false, 1024), new SnapshotEntry("bin", true, 0)]
                : entries.Select(e => new SnapshotEntry(e.Path, e.IsDir, e.Size)).ToList(),
            @"C:\Program Files\RcTestApp\unins000.exe",
            "测试厂商");

    private static UninstallResidueScanner Scanner(params string[] existingDirectories)
        => new(new FakeIndex(), new FakeEnvironment(existingDirectories));

    private static UninstallResidueOptions Options(
        IReadOnlyList<string>? extraRoots = null,
        IReadOnlyList<InstalledApp>? apps = null)
        => new()
        {
            ExtraRoots = extraRoots ?? [],
            InstalledApps = apps ?? [],
            SettleDelaySeconds = 0,
        };

    [Fact]
    public async Task ScanAsync_PartiallyRemoved_ReportsOnlyRemainingFiles()
    {
        var installRoot = Path.GetTempPath().TrimEnd('\\');
        var root = Path.Combine(installRoot, "RcScan_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(root);
            var kept = Path.Combine(root, "leftover.log");
            File.WriteAllText(kept, "still here");

            var snapshot = Snapshot("部分卸载软件", root,
                ("app.exe", false, 1024),
                ("leftover.log", false, 10),
                ("bin", true, 0));

            var result = await Scanner(root).ScanAsync(snapshot, Options());

            Assert.Single(result.RemainingFiles);
            Assert.Equal(kept, result.RemainingFiles[0]);

            // 注意：bin 是目录，第 1 步只核对文件，所以它不计入 removed。
            // removed 只数"卸载前存在、卸载后消失的<b>文件</b>"。
            Assert.Equal(1, result.RemovedCount);
            Assert.True(result.HasResidue);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task ScanAsync_InstallDirectoryStillExists_IsReportedAsResidue()
    {
        var root = Path.Combine(Path.GetTempPath(), "RcScan_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(root);
            var snapshot = Snapshot("目录还在", root, ("app.exe", false, 1024));

            var result = await Scanner(root).ScanAsync(snapshot, Options());

            Assert.Contains(result.Directories, d =>
                string.Equals(d.DirectoryPath, root, StringComparison.OrdinalIgnoreCase));
            var item = result.Directories.First(d =>
                string.Equals(d.DirectoryPath, root, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(LeftoverConfidence.High, item.Confidence);
            Assert.Contains(item.Evidence, e => e.Contains("安装目录"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task ScanAsync_NothingRemains_ReportsNoResidue()
    {
        var root = Path.Combine(Path.GetTempPath(), "RcGone_" + Guid.NewGuid().ToString("N")[..8]);
        var snapshot = Snapshot("卸得很干净", root, ("app.exe", false, 1024));

        var result = await Scanner().ScanAsync(snapshot, Options());

        Assert.False(result.HasResidue);
        Assert.Empty(result.RemainingFiles);
        Assert.Empty(result.Directories);
        Assert.Equal(1, result.RemovedCount);
    }

    [Fact]
    public async Task ScanAsync_NameMatchedDirectory_IsFound()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "RcData_" + Guid.NewGuid().ToString("N")[..8]);
        var target = Path.Combine(dataRoot, "测试软件");
        try
        {
            Directory.CreateDirectory(target);

            var snapshot = Snapshot("测试软件", InstallRoot, ("app.exe", false, 1024));

            // 注意：假环境必须同时知道 dataRoot 与 target——
            // 第 3 步会先检查"根目录是否存在"，再枚举其子目录。
            var result = await Scanner(dataRoot, target).ScanAsync(
                snapshot,
                Options(extraRoots: [dataRoot]));

            Assert.Contains(result.Directories, d =>
                string.Equals(d.DirectoryPath, target, StringComparison.OrdinalIgnoreCase));
            var item = result.Directories.First(d =>
                string.Equals(d.DirectoryPath, target, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(item.Evidence, e => e.Contains("匹配"));
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, true);
            }
        }
    }

    [Fact]
    public async Task ScanAsync_ShortChineseName_MatchesTwoCharacters()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "RcData_" + Guid.NewGuid().ToString("N")[..8]);
        var target = Path.Combine(dataRoot, "微信");
        try
        {
            Directory.CreateDirectory(target);

            // 假环境必须同时知道 dataRoot 与 target：第 3 步先检查根目录是否存在，再枚举子目录
            var result = await Scanner(dataRoot, target).ScanAsync(
                Snapshot("微信", InstallRoot),
                Options(extraRoots: [dataRoot]));

            Assert.Single(result.Directories);
            Assert.Contains("微信", result.Directories[0].DirectoryPath);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, true);
            }
        }
    }

    [Fact]
    public async Task ScanAsync_UnrelatedDirectory_IsNotMatched()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "RcData_" + Guid.NewGuid().ToString("N")[..8]);
        var other = Path.Combine(dataRoot, "完全无关的目录");
        try
        {
            Directory.CreateDirectory(other);

            var result = await Scanner(dataRoot, other).ScanAsync(
                Snapshot("测试软件", InstallRoot),
                Options(extraRoots: [dataRoot]));

            Assert.Empty(result.Directories);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, true);
            }
        }
    }

    [Fact]
    public async Task ScanAsync_SystemDirectory_IsBlockedButStillReported()
    {
        // 安装目录本身还在、但它位于系统保护路径下：
        // 必须报出来（用户该知道这里还有东西），但标 BlockReason 不让自动删。
        // 用 Common Files 而不是 Windows：它是"残留可能合法存在、但绝不能自动删"的典型位置。
        const string protectedRoot = @"C:\Program Files\Common Files";
        var snapshot = Snapshot("Common Files", protectedRoot, ("app.exe", false, 1024));

        var result = await Scanner(protectedRoot).ScanAsync(
            snapshot,
            Options());

        var item = Assert.Single(result.Directories);
        Assert.NotNull(item.BlockReason);
        Assert.Contains("拒绝删除", item.BlockReason);
        Assert.False(item.CanDelete);
    }

    [Fact]
    public async Task ScanAsync_NoInstallLocation_DoesNotThrow()
    {
        var result = await Scanner().ScanAsync(
            Snapshot("无路径软件", null),
            Options());

        Assert.False(result.HasResidue);
    }

    [Fact]
    public async Task ScanAsync_SettleDelay_CanBeSkippedForTests()
    {
        // SettleDelaySeconds=0 时不应该有 2 秒等待，否则测试套件会被拖慢
        var root = Path.Combine(Path.GetTempPath(), "RcFast_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(root);

            var started = DateTime.UtcNow;
            await Scanner(root).ScanAsync(Snapshot("快速测试", root), Options());
            var elapsed = DateTime.UtcNow - started;

            Assert.True(elapsed < TimeSpan.FromSeconds(1), $"不应该等待，实际 {elapsed.TotalMilliseconds:F0}ms");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task ScanAsync_DefaultOptions_SettleDelayIsTwoSeconds()
    {
        // 默认等 2 秒：卸载器主进程退出后后台还在删文件，
        // 立即扫会把"正在被删的文件"当成残留报出来。
        var options = new UninstallResidueOptions();
        Assert.Equal(2, options.SettleDelaySeconds);
        Assert.NotEmpty(options.ExtraRoots);
    }

    [Fact]
    public async Task ScanAsync_DuplicateDirectories_AreDeduplicated()
    {
        // 软件就装在 %AppData% 下时，第 2 步与第 3 步会命中同一个目录
        var dataRoot = Path.Combine(Path.GetTempPath(), "RcData_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dataRoot);
            var installRoot = Path.Combine(dataRoot, "测试软件");
            Directory.CreateDirectory(installRoot);

            var result = await Scanner(installRoot).ScanAsync(
                Snapshot("测试软件", installRoot, ("app.exe", false, 1024)),
                Options(extraRoots: [dataRoot]));

            Assert.Single(result.Directories);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, true);
            }
        }
    }

    [Fact]
    public async Task ScanAsync_NoInstallLocation_StillFindsNameMatchedDataDirectory()
    {
        // 真机反馈：很多按用户安装的软件不写 InstallLocation，
        // 以前这里直接 return，导致 %AppData% 下的残留永远扫不到（v0.9.6 修复）。
        var dataRoot = Path.Combine(Path.GetTempPath(), "RcData_" + Guid.NewGuid().ToString("N")[..8]);
        var target = Path.Combine(dataRoot, "测试软件");
        try
        {
            Directory.CreateDirectory(target);

            var result = await Scanner(dataRoot, target).ScanAsync(
                Snapshot("测试软件", installLocation: null),
                Options(extraRoots: [dataRoot]));

            var item = Assert.Single(result.Directories);
            Assert.Equal(target, item.DirectoryPath, ignoreCase: true);
            Assert.True(result.HasResidue);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, true);
            }
        }
    }

    [Fact]
    public async Task ScanAsync_NestedVendorDirectory_IsFound()
    {
        // %LocalAppData%\Programs\<App>、%AppData%\<Vendor>\<App> 这类两层形态
        var localAppDataLike = Path.Combine(
            Path.GetTempPath(), "RcData_" + Guid.NewGuid().ToString("N")[..8], "AppData", "Local");
        var vendor = Path.Combine(localAppDataLike, "SomeVendor");
        var target = Path.Combine(vendor, "测试软件");
        try
        {
            Directory.CreateDirectory(target);

            var result = await Scanner(localAppDataLike, vendor, target).ScanAsync(
                Snapshot("测试软件", installLocation: null),
                Options(extraRoots: [localAppDataLike]));

            var item = Assert.Single(result.Directories);
            Assert.Equal(target, item.DirectoryPath, ignoreCase: true);
            Assert.Contains(item.Evidence, e => e.Contains("厂商子目录"));
        }
        finally
        {
            var top = Path.GetDirectoryName(Path.GetDirectoryName(localAppDataLike));
            if (top is not null && Directory.Exists(top))
            {
                Directory.Delete(top, true);
            }
        }
    }

    [Fact]
    public async Task ScanAsync_MaxDepthOne_DoesNotLookIntoNestedDirectories()
    {
        var localAppDataLike = Path.Combine(
            Path.GetTempPath(), "RcData_" + Guid.NewGuid().ToString("N")[..8], "AppData", "Local");
        var target = Path.Combine(localAppDataLike, "SomeVendor", "测试软件");
        try
        {
            Directory.CreateDirectory(target);

            var options = new UninstallResidueOptions
            {
                ExtraRoots = [localAppDataLike],
                SettleDelaySeconds = 0,
                MaxDepth = 1,
            };

            var result = await Scanner(localAppDataLike).ScanAsync(
                Snapshot("测试软件", installLocation: null),
                options);

            Assert.Empty(result.Directories);
        }
        finally
        {
            var top = Path.GetDirectoryName(Path.GetDirectoryName(localAppDataLike));
            if (top is not null && Directory.Exists(top))
            {
                Directory.Delete(top, true);
            }
        }
    }

    [Fact]
    public async Task ScanAsync_MatchedOuterDirectory_DoesNotReportItsInnerDirectoryAgain()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "RcData_" + Guid.NewGuid().ToString("N")[..8]);
        var outer = Path.Combine(dataRoot, "测试软件");
        try
        {
            Directory.CreateDirectory(Path.Combine(outer, "测试软件"));

            var result = await Scanner(dataRoot, outer).ScanAsync(
                Snapshot("测试软件", installLocation: null),
                Options(extraRoots: [dataRoot]));

            // 外层命中就够了；再报一条 "测试软件\测试软件" 只会让用户多勾一个嵌套目录
            var item = Assert.Single(result.Directories);
            Assert.Equal(outer, item.DirectoryPath, ignoreCase: true);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, true);
            }
        }
    }

    [Fact]
    public async Task ScanAsync_DirectoryClaimedByInstalledApp_IsNotReported()
    {
        // 用户把软件又装回去了（或另一个产品共用这个目录）：不能报成残留。
        var dataRoot = Path.Combine(Path.GetTempPath(), "RcData_" + Guid.NewGuid().ToString("N")[..8]);
        var target = Path.Combine(dataRoot, "测试软件");
        try
        {
            Directory.CreateDirectory(target);

            var claimed = new InstalledApp
            {
                Id = "x",
                DisplayName = "测试软件",
                RegistryPath = @"HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\x",
                InstallLocation = target,
            };

            var result = await Scanner(dataRoot, target).ScanAsync(
                Snapshot("测试软件", installLocation: null),
                Options(extraRoots: [dataRoot], apps: [claimed]));

            Assert.Empty(result.Directories);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, true);
            }
        }
    }

    [Fact]
    public async Task ScanAsync_Cancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Scanner().ScanAsync(Snapshot(), Options(), null, cts.Token));
    }
}