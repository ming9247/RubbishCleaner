using RubbishCleaner.Common.Mft;
using RubbishCleaner.Core.Leftover;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Uninstall;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// 残留扫描器测试。
///
/// 用假索引 + 假环境驱动，<b>不触碰真实磁盘</b>：这样才敢构造"系统目录"、"被认领的目录"
/// 这类真实机器上无法安全制造的场景。
/// </summary>
public class LeftoverScannerTests
{
    private sealed class FakeIndex : IVolumeFileIndex
    {
        private readonly List<IndexedFile> _files;

        public FakeIndex(params IndexedFile[] files) => _files = [.. files];

        public FileIndexAvailability Availability => FileIndexAvailability.Available;

        public string? UnavailableReason => null;

        public string Description => "测试索引";

        public bool Prepared { get; private set; }

        public bool Released { get; private set; }

        public Task<bool> PrepareAsync(
            IReadOnlyCollection<string> rootPaths,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Prepared = true;
            return Task.FromResult(true);
        }

        public IReadOnlyList<IndexedFile> FindFiles(string directoryPath, IReadOnlySet<string> fileNames)
            => _files
                .Where(f => f.FullPath.StartsWith(directoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .Where(f => fileNames.Contains(Path.GetFileName(f.FullPath)))
                .ToList();

        public DirectoryStatistics GetStatistics(string directoryPath)
            => new(4096, 4, 1, new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

        public void Release() => Released = true;
    }

    private sealed class FakeEnvironment(params string[] existingDirectories) : ILeftoverEnvironment
    {
        public bool DirectoryExists(string path)
            => existingDirectories.Any(d => string.Equals(d, path, StringComparison.OrdinalIgnoreCase));

        public string? SelfDirectory => @"D:\Tools\RubbishCleaner";
    }

    private static readonly string Root = @"C:\Program Files";

    private static InstalledApp App(
        string name,
        string? installLocation = null,
        string? uninstallCommand = null,
        string? publisher = null)
        => new()
        {
            Id = name,
            DisplayName = name,
            Publisher = publisher,
            RegistryPath = @"HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\" + name,
            InstallLocation = installLocation,
            UninstallCommand = uninstallCommand ?? @"C:\Program Files\Live\unins000.exe",
        };

    private static LeftoverScanOptions Options(
        IReadOnlyList<InstalledApp>? apps = null,
        IReadOnlyList<SoftwareProfile>? profiles = null)
        => new()
        {
            Roots = [Root],
            InstalledApps = apps ?? [],
            StartupProfiles = profiles ?? [],
            MeasureSize = true,
        };

    [Fact]
    public async Task Scan_OrphanUninstaller_IsReportedAsHighConfidence()
    {
        var index = new FakeIndex(new IndexedFile(
            @"C:\Program Files\GoneApp\unins000.exe", 1024, DateTimeOffset.Now));
        var scanner = new LeftoverScanner(index, new FakeEnvironment(Root));

        var result = await scanner.ScanAsync(Options());

        Assert.Single(result.Items);
        Assert.Equal(LeftoverKind.OrphanedUninstallerDirectory, result.Items[0].Kind);
        Assert.Equal(LeftoverConfidence.High, result.Items[0].Confidence);
        Assert.Equal(@"C:\Program Files\GoneApp", result.Items[0].DirectoryPath);
        Assert.Contains("unins000.exe", result.Items[0].UninstallerFiles);
    }

    [Fact]
    public async Task Scan_ClaimedDirectory_IsNotReported()
    {
        // 目录里有卸载程序，但已安装软件列表认领了它 → 不是残留
        var index = new FakeIndex(new IndexedFile(
            @"C:\Program Files\LiveApp\unins000.exe", 1024, DateTimeOffset.Now));
        var scanner = new LeftoverScanner(index, new FakeEnvironment(Root));

        var result = await scanner.ScanAsync(Options([App("LiveApp", @"C:\Program Files\LiveApp")]));

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Scan_ClaimedByParentInstallLocation_IsNotReported()
    {
        // 注册表只写了 C:\Program Files\Vendor，子目录 C:\Program Files\Vendor\Product 也算有主
        var index = new FakeIndex(new IndexedFile(
            @"C:\Program Files\Vendor\Product\unins000.exe", 1024, DateTimeOffset.Now));
        var scanner = new LeftoverScanner(index, new FakeEnvironment(Root));

        var result = await scanner.ScanAsync(Options([App("Product", @"C:\Program Files\Vendor")]));

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Scan_DirectoryThatContainsClaimedDirectory_IsNotReported()
    {
        // 反向：检测到的是父目录，而子目录里有在用的软件 → 删父目录会连带删掉它，必须跳过。
        // 实测本机 C:\Program Files\OEM 就是这种情况（里面有"机械革命电竞控制台"）。
        var index = new FakeIndex(new IndexedFile(
            @"C:\Program Files\OEM\unins000.exe", 1024, DateTimeOffset.Now));
        var scanner = new LeftoverScanner(index, new FakeEnvironment(Root));

        var result = await scanner.ScanAsync(Options([App("控制台", @"C:\Program Files\OEM\机械革命电竞控制台")]));

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Scan_ClaimedByUninstallerDirectory_IsNotReported()
    {
        // 注册表没写 InstallLocation，但卸载命令指向子目录 → 一样算认领
        var index = new FakeIndex(new IndexedFile(
            @"C:\Program Files\Installer\unins000.exe", 1024, DateTimeOffset.Now));
        var scanner = new LeftoverScanner(index, new FakeEnvironment(Root));

        var result = await scanner.ScanAsync(
            Options([App("Installer", uninstallCommand: @"C:\Program Files\Installer\uninstall.exe")]));

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Scan_NestedLeftovers_KeepOnlyOutermost()
    {
        // 父目录与子目录都有孤儿卸载程序：只报父目录，避免重复统计与用户困惑
        var index = new FakeIndex(
            new IndexedFile(@"C:\Program Files\Outer\unins000.exe", 1024, DateTimeOffset.Now),
            new IndexedFile(@"C:\Program Files\Outer\Inner\uninst.exe", 512, DateTimeOffset.Now));
        var scanner = new LeftoverScanner(index, new FakeEnvironment(Root));

        var result = await scanner.ScanAsync(Options());

        Assert.Single(result.Items);
        Assert.Equal(@"C:\Program Files\Outer", result.Items[0].DirectoryPath);
    }

    [Fact]
    public async Task Scan_ExcludedPath_IsNotReported()
    {
        // 本工具自己的数据目录（备份/日志）绝不能被当成残留
        var index = new FakeIndex(new IndexedFile(
            @"C:\Program Files\RubbishCleaner\unins000.exe", 1024, DateTimeOffset.Now));
        var scanner = new LeftoverScanner(index, new FakeEnvironment(Root));

        var options = new LeftoverScanOptions
        {
            Roots = [Root],
            InstalledApps = [],
            ExcludedPaths = [@"C:\Program Files\RubbishCleaner"],
        };

        var result = await scanner.ScanAsync(options);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Scan_OrphanStartupTrail_IsReportedWhenDirectoryStillExists()
    {
        const string directory = @"C:\Program Files\GhostApp";

        var entry = new StartupEntry
        {
            Id = "ghost",
            Source = StartupSourceKind.RegistryRunHklm,
            Name = "GhostApp",
            Location = @"HKLM\Software\Microsoft\Windows\CurrentVersion\Run",
            RawCommand = @"C:\Program Files\GhostApp\ghost.exe",
            ExecutablePath = @"C:\Program Files\GhostApp\ghost.exe",
            TargetExistsOverride = false,
        };

        var profile = new SoftwareProfile
        {
            Key = "ghost",
            DisplayName = "GhostApp",
            ExecutablePath = entry.ExecutablePath,
            StartupEntries = [entry],
        };

        var scanner = new LeftoverScanner(new FakeIndex(), new FakeEnvironment(Root, directory));

        var result = await scanner.ScanAsync(Options(profiles: [profile]));

        Assert.Single(result.Items);
        Assert.Equal(LeftoverKind.StartupTrailDirectory, result.Items[0].Kind);
        Assert.Equal(directory, result.Items[0].DirectoryPath);
        Assert.Contains(result.Items[0].Evidence, e => e.Contains("GhostApp"));
    }

    [Fact]
    public async Task Scan_OrphanStartupTrail_IsIgnoredWhenDirectoryGone()
    {
        var entry = new StartupEntry
        {
            Id = "ghost",
            Source = StartupSourceKind.RegistryRunHklm,
            Name = "GhostApp",
            Location = @"HKLM\Run",
            ExecutablePath = @"C:\Program Files\GhostApp\ghost.exe",
            TargetExistsOverride = false,
        };

        var profile = new SoftwareProfile
        {
            Key = "ghost",
            DisplayName = "GhostApp",
            StartupEntries = [entry],
        };

        var scanner = new LeftoverScanner(new FakeIndex(), new FakeEnvironment(Root));

        var result = await scanner.ScanAsync(Options(profiles: [profile]));

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Scan_ContainerDirectoriesAreNotAddedAsClaims()
    {
        // 回归防护（真实 E2E 里踩到的严重漏报）：
        // 本机有多个软件的卸载程序直接躺在 C:\Program Files\<Vendor> 下，
        // "卸载器所在目录的上一级"因此把 C:\Program Files 加成了认领路径，
        // 于是整棵树都变成"有主"，Program Files 下的残留一条都报不出来。
        var claims = LeftoverScanner.BuildClaims(
        [
            App("VendorApp", uninstallCommand: @"C:\Program Files\Vendor\uninst.exe"),
        ]);

        Assert.DoesNotContain(@"C:\Program Files", claims);
        Assert.Contains(@"C:\Program Files\Vendor", claims);
    }

    [Fact]
    public async Task Scan_AdditionalClaimPaths_PreventFalsePositive()
    {
        // 实测踩到的误报：Adobe CoreSync 的注册表条目没有 DisplayName，
        // 被"可展示软件"过滤掉，于是它目录里的 uninstall.exe 被判成孤儿。
        // 修法是把<b>所有</b>条目的路径证据都传进来。
        var index = new FakeIndex(new IndexedFile(
            @"C:\Program Files (x86)\Adobe\Adobe Sync\customhook\uninstall.exe", 1024, DateTimeOffset.Now));

        const string x86Root = @"C:\Program Files (x86)";
        var scanner = new LeftoverScanner(index, new FakeEnvironment(x86Root));
        var withEvidence = await scanner.ScanAsync(new LeftoverScanOptions
        {
            Roots = [x86Root],
            InstalledApps = [],
            AdditionalClaimPaths = [@"C:\Program Files (x86)\Adobe\Adobe Sync"],
            MeasureSize = true,
        });

        var withoutEvidence = await scanner.ScanAsync(new LeftoverScanOptions
        {
            Roots = [x86Root],
            InstalledApps = [],
            MeasureSize = true,
        });

        Assert.Empty(withEvidence.Items);
        Assert.Single(withoutEvidence.Items);
    }

    [Fact]
    public async Task Scan_VendorSharedContainer_IsDemotedToMedium()
    {
        // C:\Program Files (x86)\Common Files\Autodesk Shared\AdskLicensing 有 6MB 卸载程序、
        // 注册表里没有对应条目，但它属于 Autodesk 全家桶的共享组件。
        // 这类位置降到 Medium：只提示、不自动清理。
        var index = new FakeIndex(new IndexedFile(
            @"C:\Program Files (x86)\Common Files\Autodesk Shared\AdskLicensing\uninstall.exe",
            1024,
            DateTimeOffset.Now));

        const string x86Root = @"C:\Program Files (x86)";
        var scanner = new LeftoverScanner(index, new FakeEnvironment(x86Root));
        var result = await scanner.ScanAsync(new LeftoverScanOptions
        {
            Roots = [x86Root],
            InstalledApps = [App("AutoCAD 2024", publisher: "Autodesk, Inc.")],
            MeasureSize = false,
        });

        Assert.Single(result.Items);
        Assert.Equal(LeftoverConfidence.Medium, result.Items[0].Confidence);
        Assert.Equal(0, result.CleanableCount);
    }

    [Fact]
    public async Task Scan_VendorNamedDirectoryOutsideSharedContainer_StaysHigh()
    {
        // 反向：不在共享容器里的厂商目录不降级，否则 C:\Program Files\Tencent\QQOld
        // 这类真残留会被降成"只提示"，清理能力被削掉。
        var index = new FakeIndex(new IndexedFile(
            @"C:\Program Files\Tencent\QQOld\unins000.exe", 1024, DateTimeOffset.Now));

        var scanner = new LeftoverScanner(index, new FakeEnvironment(Root));
        var result = await scanner.ScanAsync(new LeftoverScanOptions
        {
            Roots = [Root],
            InstalledApps = [App("QQ", publisher: "Tencent Technology")],
            MeasureSize = false,
        });

        Assert.Single(result.Items);
        Assert.Equal(LeftoverConfidence.High, result.Items[0].Confidence);
        Assert.Equal(1, result.CleanableCount);
    }

    [Fact]
    public async Task Scan_ReportsIndexDescriptionAndReleasesIndex()
    {
        var index = new FakeIndex();
        var scanner = new LeftoverScanner(index, new FakeEnvironment(Root));

        var result = await scanner.ScanAsync(Options());

        Assert.True(index.Prepared);
        Assert.True(index.Released);
        Assert.Equal("测试索引", result.IndexDescription);
    }

    [Fact]
    public async Task Scan_NoExistingRoot_DegradesInsteadOfThrowing()
    {
        var scanner = new LeftoverScanner(new FakeIndex(), new FakeEnvironment());

        var result = await scanner.ScanAsync(Options());

        Assert.Empty(result.Items);
        Assert.True(result.IsDegraded);
        Assert.Contains("没有任何可扫描的目录", result.DegradedReason!);
    }

    [Fact]
    public async Task Scan_EmptyInstalledApps_MakesEverythingUnclaimed()
    {
        // 极端情况：读不到已安装软件列表时，孤儿卸载程序目录会被大量报出。
        // 这是<b>预期行为</b>（有硬证据 + 无人认领），但 UI 必须提示"已安装软件列表不完整"。
        var index = new FakeIndex(
            new IndexedFile(@"C:\Program Files\A\unins000.exe", 1, DateTimeOffset.Now),
            new IndexedFile(@"C:\Program Files\B\unins000.exe", 1, DateTimeOffset.Now));
        var scanner = new LeftoverScanner(index, new FakeEnvironment(Root));

        var result = await scanner.ScanAsync(Options());

        Assert.Equal(2, result.HighConfidenceCount);
        Assert.Equal(2, result.CleanableCount);
    }
}