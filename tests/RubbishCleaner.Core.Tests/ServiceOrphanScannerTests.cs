using RubbishCleaner.Core.Integrity;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// 服务 ImagePath 孤儿扫描器的判据测试（v0.5）。
///
/// <b>全部走注入的假数据，绝不碰真实服务表</b>（真实扫描只由一条标注的只读冒烟测试做一次）。
/// 核心断言是"该跳的必跳、该报的才报"——服务表里混着驱动与系统组件，误报比漏报危险。
/// </summary>
public class ServiceOrphanScannerTests
{
    private const string TestRoot = @"SYSTEM\ControlSet001\Services";

    /// <summary>造一个全部可注入的扫描器：文件系统是内存里的集合，SCM 查询返回"已停止"。</summary>
    private static ServiceOrphanScanner MakeScanner(
        IReadOnlyList<ServiceRecord> records,
        HashSet<string>? existingFiles = null,
        string? stateText = "已停止",
        string windowsDirectory = "C:\\Windows",
        Func<string, string>? environmentExpander = null)
    {
        var files = existingFiles ?? [];
        return new ServiceOrphanScanner(
            new FakeServiceRecordSource(records),
            TestRoot,
            stateText is null
                ? null
                : new ServiceStateQuery(_ => stateText),
            fileExists: path => files.Contains(Normalize(path)),
            environmentExpander: environmentExpander,
            windowsDirectory: windowsDirectory);
    }

    /// <summary>统一大小写与分隔符，让断言对路径写法不敏感（只关心判据本身）。</summary>
    private static string Normalize(string path)
        => path.Replace('/', '\\').ToUpperInvariant();

    private static ServiceRecord Record(
        string name,
        string? imagePath,
        int? start = 2,
        int? type = 0x10,
        string? displayName = null)
        => new(name, displayName, imagePath, start, type);

    // ---------- 跳过规则：每条分支一个用例 ----------

    [Fact]
    public void Evaluate_EmptyImagePath_Skips()
    {
        var scanner = MakeScanner([]);
        var verdict = scanner.Evaluate(Record("NullDriver", null));
        var verdict2 = scanner.Evaluate(Record("EmptyDriver", "   "));

        Assert.False(verdict.IsOrphan);
        Assert.False(verdict2.IsOrphan);
        Assert.Equal("ImagePath 缺失", verdict.SkipReason);
        Assert.Equal("ImagePath 缺失", verdict2.SkipReason);
    }

    [Theory]
    [InlineData(@"\SystemRoot\System32\drivers\acpi.sys")]
    [InlineData(@"\??\C:\Windows\system32\drivers\x.sys")]
    [InlineData(@"\Device\HarddiskVolume1\Windows\a.sys")]
    [InlineData("system32\\drivers\\x.sys")]
    [InlineData("System32\\x.sys")]
    [InlineData("SysWOW64\\x.dll")]
    public void Evaluate_KernelFormPaths_Skip(string imagePath)
    {
        // 内核形态路径的语义是 NT 对象管理器的，不是 Win32 的，File.Exists 必然失真。
        var scanner = MakeScanner([]);
        var verdict = scanner.Evaluate(Record("kdrv", imagePath));

        Assert.False(verdict.IsOrphan);
        Assert.Equal("内核形态路径", verdict.SkipReason);
    }

    [Theory]
    [InlineData("svchost.exe -k netsvcs -p -s BITS")]
    [InlineData(@"C:\Windows\system32\svchost.exe -k LocalService")]
    [InlineData("\"C:\\Custom\\svchost.exe\" -k test")]
    public void Evaluate_SvchostHost_Skips(string imagePath)
    {
        // 真实 DLL 在 ServiceDll 参数里，本版不深挖；宿主在不在系统目录都要先按 svchost 跳过。
        var scanner = MakeScanner([]);
        var verdict = scanner.Evaluate(Record("shared", imagePath));

        Assert.False(verdict.IsOrphan);
        Assert.Equal("svchost 宿主", verdict.SkipReason);
    }

    [Theory]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData(@"C:\Windows\System32\svc.exe")]
    [InlineData(@"C:\Windows\SysWOW64\svc.exe")]
    [InlineData(@"C:\Windows\System32\drivers\etc\x.exe")]
    [InlineData("%SystemRoot%\\System32\\svc.exe")]
    public void Evaluate_SystemDirectoryPaths_Skip(string imagePath)
    {
        // 调研文档的教训：系统组件的文件可能由 Windows Update 回装，存在性检查在这里没有判定力。
        var scanner = MakeScanner([]);
        var verdict = scanner.Evaluate(Record("sysc", imagePath));

        Assert.False(verdict.IsOrphan);
        Assert.Equal("系统目录", verdict.SkipReason);
    }

    [Fact]
    public void Evaluate_RelativePath_Skips()
    {
        // 解析不出绝对 Win32 路径，保守不报（裸文件名按 DLL 搜索路径解析，存在性检查失真）。
        var scanner = MakeScanner([]);
        var verdict = scanner.Evaluate(Record("bare", "someapp.exe -k"));

        Assert.False(verdict.IsOrphan);
        Assert.Equal("相对路径", verdict.SkipReason);
    }

    [Fact]
    public void Evaluate_BadEnvironmentVariable_SkipsWithReason()
    {
        var scanner = MakeScanner(
            [],
            environmentExpander: _ => throw new ArgumentException("环境变量引用畸形"));
        var verdict = scanner.Evaluate(Record("badenv", "%NoCloseBrace\\a.exe"));

        Assert.False(verdict.IsOrphan);
        Assert.StartsWith("无法解析", verdict.SkipReason);
    }

    // ---------- 路径解析：引号 / 环境变量 / 参数 ----------

    [Fact]
    public void Evaluate_QuotedPathWithArguments_ExtractsExecutable()
    {
        // ""C:\Program Files\\A B\\svc.exe\" -a -b" 形态：取引号内、去参数。
        var scanner = MakeScanner([]);
        var verdict = scanner.Evaluate(Record("q", @"""C:\Program Files\A B\svc.exe"" -a -b"));

        Assert.True(verdict.IsOrphan);
        Assert.Equal(@"C:\Program Files\A B\svc.exe", verdict.ResolvedPath);
    }

    [Fact]
    public void Evaluate_EnvironmentVariablePath_IsExpanded()
    {
        // %ProgramFiles% 要真的展开成绝对路径再判定（展开失败与不展开都不可判）。
        const string raw = "%ProgramFiles%\\GhostApp\\ghost.exe -s";
        var scanner = MakeScanner(
            [],
            environmentExpander: _ => raw.Replace("%ProgramFiles%", "C:\\Program Files"));
        var verdict = scanner.Evaluate(Record("env", raw));

        Assert.True(verdict.IsOrphan);
        Assert.Equal(@"C:\Program Files\GhostApp\ghost.exe", verdict.ResolvedPath);
    }

    [Fact]
    public void Evaluate_UnquotedPathWithSpacesAndArguments_TakesExeOnly()
    {
        // 未加引号 + 含空格 + 带参数：服务表 ImagePath 的实际形态，切到 .exe 即可。
        var scanner = MakeScanner([]);
        var verdict = scanner.Evaluate(Record("unq", @"C:\Program Files\A B\svc.exe -k run"));

        Assert.True(verdict.IsOrphan);
        Assert.Equal(@"C:\Program Files\A B\svc.exe", verdict.ResolvedPath);
    }

    // ---------- Start / Type 翻译表 ----------

    [Theory]
    [InlineData(0, "引导加载(0)")]
    [InlineData(1, "系统启动(1)")]
    [InlineData(2, "自动(2)")]
    [InlineData(3, "手动(3)")]
    [InlineData(4, "禁用(4)")]
    [InlineData(9, "未知(9)")]
    public void DescribeStart_TranslatesWin32Constants(int start, string expected)
    {
        Assert.Equal(expected, ServiceOrphanScanner.DescribeStart(start));
    }

    [Fact]
    public void DescribeStart_NullMeansNotSet()
    {
        Assert.Equal("未设置", ServiceOrphanScanner.DescribeStart(null));
    }

    [Theory]
    [InlineData(1, "内核驱动(1)")]
    [InlineData(2, "文件系统驱动(2)")]
    [InlineData(0x10, "Win32 服务")]
    [InlineData(0x20, "Win32 服务")]
    [InlineData(0x110, "Win32 服务(可交互)")]
    [InlineData(0x120, "Win32 服务(可交互)")]
    [InlineData(0x8, "其他(8)")]
    public void DescribeType_TranslatesWin32Constants(int type, string expected)
    {
        Assert.Equal(expected, ServiceOrphanScanner.DescribeType(type));
    }

    [Fact]
    public void DescribeType_NullMeansNotSet()
    {
        Assert.Equal("未设置", ServiceOrphanScanner.DescribeType(null));
    }

    // ---------- 扫描器整体行为（假数据） ----------

    [Fact]
    public async Task ScanAsync_AllFilesExist_ReportsZeroOrphans()
    {
        var records = new[]
        {
            Record("healthy", @"C:\Apps\good.exe"),
            Record("healthy2", @"C:\Apps\good2.exe --flag"),
        };
        var scanner = MakeScanner(records, existingFiles: [Normalize(@"C:\Apps\good.exe"), Normalize(@"C:\Apps\good2.exe")]);

        var result = await scanner.ScanAsync();

        Assert.Empty(result.Entries);
        Assert.False(result.IsDegraded);
        Assert.Equal(2, result.ScannedCount);
        Assert.Equal(0, result.SkippedCount);
    }

    [Fact]
    public async Task ScanAsync_MissingFile_ReportedWithEvidence()
    {
        var scanner = MakeScanner([Record("ghost", @"C:\Apps\gone.exe")]);

        var result = await scanner.ScanAsync();

        var entry = Assert.Single(result.Entries);
        Assert.Equal("ghost", entry.ServiceName);
        Assert.Equal(@"C:\Apps\gone.exe", entry.ResolvedPath);
        Assert.Equal(@"C:\Apps\gone.exe", entry.ImagePathRaw);
        Assert.Equal("自动(2)", entry.StartModeText);
        Assert.Equal("已停止", entry.StateText);
        Assert.Equal("Win32 服务", ServiceOrphanScanner.DescribeType(0x10));
        Assert.NotEmpty(entry.Evidence);
        Assert.Contains(entry.Evidence, e => e.Contains("已不存在"));
        Assert.Contains("sc delete ghost", entry.ManualCommand);
    }

    [Fact]
    public async Task ScanAsync_MixedData_CountsSkipsAndOrphans()
    {
        var records = new[]
        {
            Record("drv", null, start: 0, type: 1),                        // ImagePath 缺失
            Record("svchost", "svchost.exe -k x", start: 2, type: 0x20),   // svchost
            Record("kernel", @"\SystemRoot\a.sys", start: 1, type: 1), // 内核形态
            Record("sys", @"C:\Windows\System32\s.exe"),            // 系统目录
            Record("healthy", @"C:\Apps\in.exe"),                      // 文件在
            Record("orphan", @"C:\Apps\gone.exe"),                     // 文件不在
        };
        var scanner = MakeScanner(records, existingFiles: [Normalize(@"C:\Apps\in.exe")], stateText: "运行中");

        var result = await scanner.ScanAsync();

        var entry = Assert.Single(result.Entries);
        Assert.Equal("orphan", entry.ServiceName);
        Assert.Equal(6, result.ScannedCount);
        Assert.Equal(4, result.SkippedCount);
        Assert.Equal("运行中", entry.StateText);
    }

    [Fact]
    public async Task ScanAsync_EmptyServiceTable_IsDegradedNotSilent()
    {
        // 一条都读不到时不能装作"扫过了、0 孤儿"——必须降级说明。
        var scanner = MakeScanner([]);

        var result = await scanner.ScanAsync();

        Assert.Empty(result.Entries);
        Assert.Equal(0, result.ScannedCount);
        Assert.True(result.IsDegraded);
        Assert.NotNull(result.DegradedReason);
    }

    [Fact]
    public async Task ScanAsync_SourceError_IsDegraded()
    {
        var scanner = new ServiceOrphanScanner(
            new ErrorServiceRecordSource("读取 HKLM 失败：权限不足"),
            TestRoot,
            null, null, null, null);

        var result = await scanner.ScanAsync();

        Assert.True(result.IsDegraded);
        Assert.Contains("权限不足", result.DegradedReason);
    }

    [Fact]
    public async Task ScanAsync_StateQueryFailure_IsDegradedButEntryStillReported()
    {
        // 受限环境下 SCM 查询会失败：判定不因此作废，状态标"未知"，整体降级说明。
        var scanner = new ServiceOrphanScanner(
            new FakeServiceRecordSource([Record("ghost", @"C:\Apps\gone.exe")]),
            TestRoot,
            _ => throw new InvalidOperationException("SCM 打不开"),
            fileExists: _ => false,
            environmentExpander: null,
            windowsDirectory: "C:\\Windows");

        var result = await scanner.ScanAsync();

        var entry = Assert.Single(result.Entries);
        Assert.Contains("未知", entry.StateText);
        Assert.True(result.IsDegraded);
        Assert.Contains("运行状态查询失败", result.DegradedReason);
    }

    [Fact]
    public async Task ScanAsync_Cancellation_Throws()
    {
        var scanner = MakeScanner([Record("a", @"C:\Apps\a.exe")]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scanner.ScanAsync(cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ScanAsync_ReportsProgress()
    {
        var records = Enumerable.Range(0, 60)
            .Select(i => Record($"svc{i}", $"C:\\Apps\\app{i}.exe"))
            .ToList();
        var scanner = MakeScanner(records);
        var messages = new List<string>();
        var progress = new Progress<string>(messages.Add);

        await scanner.ScanAsync(progress);

        Assert.Contains(messages, m => m.Contains("正在读取服务表"));
        Assert.Contains(messages, m => m.Contains("扫描完成"));
        Assert.Contains(messages, m => m.Contains("60"));
    }

    // ---------- 契约锁死：永不自动清理 ----------

    [Fact]
    public void CanAutoClean_IsAlwaysFalse_ByContract()
    {
        // v0.5 铁律在类型上锁死：这条断言失败说明有人动了契约，必须找 Lead。
        var entry = new ServiceOrphanEntry
        {
            ServiceName = "x",
            ImagePathRaw = @"C:\a.exe",
            ResolvedPath = @"C:\a.exe",
            StartModeText = "手动(3)",
            StateText = "已停止",
        };

        Assert.False(entry.CanAutoClean);
    }

    [Fact]
    public async Task ScanAsync_EntriesNeverClaimAutoClean()
    {
        var scanner = MakeScanner([Record("ghost", @"C:\Apps\gone.exe")]);

        var result = await scanner.ScanAsync();

        Assert.All(result.Entries, e => Assert.False(e.CanAutoClean));
    }

    // ---------- 只读冒烟：真实机器上跑一次（全程只读注册表 + 只读 SCM + File.Exists） ----------

    [Fact]
    public async Task ScanAsync_RealMachine_ReadOnlySmoke()
    {
        // 只读冒烟：不写任何注册表值、不调用任何会改变服务状态的 API。
        // 断言只锁"结果形状"，不锁本机孤儿数量（那是环境数据，会漂移）。
        var scanner = new ServiceOrphanScanner();

        var result = await scanner.ScanAsync();

        Assert.True(result.ScannedCount > 100, $"本机服务数不应这么少：{result.ScannedCount}");
        Assert.True(result.SkippedCount > 0, "本机必有驱动/svchost 形态被跳过");
        Assert.All(result.Entries, e =>
        {
            Assert.NotEmpty(e.Evidence);
            Assert.False(e.CanAutoClean);
            Assert.True(Path.IsPathRooted(e.ResolvedPath), $"孤儿路径必须是绝对 Win32 路径：{e.ResolvedPath}");
        });
    }

    /// <summary>假数据源：返回固定记录，绝不碰注册表。</summary>
    private sealed class FakeServiceRecordSource(IReadOnlyList<ServiceRecord> records) : IServiceRecordSource
    {
        public (IReadOnlyList<ServiceRecord>, string?) Read(string rootPath) => (records, null);
    }

    /// <summary>故障注入源：模拟注册表读不出来。</summary>
    private sealed class ErrorServiceRecordSource(string error) : IServiceRecordSource
    {
        public (IReadOnlyList<ServiceRecord>, string?) Read(string rootPath) => ([], error);
    }
}
