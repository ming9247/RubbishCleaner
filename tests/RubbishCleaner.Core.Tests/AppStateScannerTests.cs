using RubbishCleaner.Core.Integrity;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.RegistryCleanup;
using RubbishCleaner.Core.Uninstall;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// 文件与注册表状态一致性扫描的判据测试（v0.5）。
///
/// <para>用注入的假数据把判定逻辑从 msi.dll 和真实注册表里剥离出来：
/// MSI 状态、LocalPackage、条目列表全部由测试控制，可以精确构造
/// "已安装但目录没了"、"已安装但缓存包没了"、"未安装"等真机上不易稳定复现的形态。</para>
///
/// <para>最关键的边界是「MSI 状态为 ABSENT 时不报」——那是 v0.4 孤儿清理的地盘，
/// 两个版本对同一条目的判定不能打架。</para>
/// </summary>
public class AppStateScannerTests : IDisposable
{
    /// <summary>产品码严格形态 {8-4-4-4-12}，扫描器按 RegistryPath 最后一段取它。</summary>
    private const string ProductCode = "{11111111-2222-3333-4444-555555555555}";

    /// <summary>RegistryPath 的固定前缀（与 InstalledAppEnumerator 的产出形态一致）。</summary>
    private const string RegistryPathPrefix =
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";

    /// <summary>MSI INSTALLED 状态值。</summary>
    private const uint MsiInstalled = 5;

    /// <summary>MSI ABSENT 状态值。</summary>
    private const uint MsiAbsent = 1;

    private readonly List<string> _tempPaths = [];

    public void Dispose()
    {
        foreach (var path in _tempPaths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // 测试临时目录删不掉不影响判定
            }
        }
    }

    // ---------- 判定用的小工厂：默认参数代表"一切正常"的条目 ----------

    private static InstalledApp MakeApp(
        string displayName = "测试软件",
        bool isMsi = false,
        string? installLocation = null,
        string subKeyName = "NonMsiEntry") => new()
    {
        Id = $"LocalMachine-Registry64-{subKeyName}",
        DisplayName = displayName,
        RegistryPath = RegistryPathPrefix + subKeyName,
        UninstallCommand = "MsiExec.exe /X" + subKeyName,
        IsMsi = isMsi,
        InstallLocation = installLocation,
    };

    /// <summary>构造一个不存在的绝对路径（每次唯一，避免并行测试互相污染）。</summary>
    private string MissingDirPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "rc-appstate-test-" + Guid.NewGuid().ToString("N"));
        _tempPaths.Add(path);
        return path;
    }

    /// <summary>构造一个真实存在的目录（用于"目录还在就不报"的反向用例）。</summary>
    private string ExistingDirPath()
    {
        var path = MissingDirPath();
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>构造一个不存在的 .msi 缓存包路径。</summary>
    private string MissingPackagePath() => Path.Combine(MissingDirPath(), "cached.msi");

    /// <summary>
    /// 构造被测扫描器。委托按条目名决定 MSI 状态与 LocalPackage，
    /// 让单个测试只声明它关心的那部分行为。
    /// </summary>
    private static AppStateScanner MakeScanner(
        IReadOnlyList<InstalledApp> apps,
        Func<string, uint> state,
        Func<string, string?>? localPackage = null,
        string? enumerateError = null)
        => new(
            () => new InstalledAppsResult(apps, apps.Count, 0, enumerateError),
            state,
            localPackage ?? (_ => "C:\\Windows\\Installer\\" + Guid.NewGuid().ToString("N") + ".msi"));

    // ---------- InstallLocationMissing ----------

    [Fact]
    public async Task InstallLocationMissing_NonMsiEntry_DirectoryGone_Reported()
    {
        // 非 MSI 条目没有产品码可言，但 InstallLocation 判定照样成立：
        // 条目自己记录的目录没了，与 MSI 无关。
        var missing = MissingDirPath();
        var scanner = MakeScanner(
            [MakeApp(installLocation: missing)],
            _ => throw new InvalidOperationException("非 MSI 条目不应查询 MSI 状态"));

        var result = await scanner.ScanAsync();

        var issue = Assert.Single(result.Entries);
        Assert.Equal(AppStateIssueKind.InstallLocationMissing, issue.Kind);
        Assert.Equal("测试软件", issue.DisplayName);
        Assert.Null(issue.ProductCode);
        Assert.Contains(issue.Evidence, e => e.Contains(missing));
    }

    [Fact]
    public async Task InstallLocationMissing_DirectoryStillExists_NotReported()
    {
        var existing = ExistingDirPath();
        var scanner = MakeScanner(
            [MakeApp(installLocation: existing)],
            _ => throw new InvalidOperationException("不应查询 MSI 状态"));

        var result = await scanner.ScanAsync();

        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task InstallLocationMissing_EmptyLocation_Skipped()
    {
        // v0.4 的教训：InstallLocation 常为空，为空的一律跳过不计问题
        var scanner = MakeScanner(
            [MakeApp(installLocation: null), MakeApp(installLocation: "")],
            _ => throw new InvalidOperationException("不应查询 MSI 状态"));

        var result = await scanner.ScanAsync();

        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task InstallLocationMissing_RelativePath_Skipped()
    {
        // 相对路径没有判定意义（卸载器常按工作目录写相对值），跳过
        var scanner = MakeScanner(
            [MakeApp(installLocation: @"bin\setup"), MakeApp(installLocation: "C:relative")],
            _ => throw new InvalidOperationException("不应查询 MSI 状态"));

        var result = await scanner.ScanAsync();

        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task InstallLocationMissing_QuotedAndTrailingSeparator_AreNormalized()
    {
        // 注册表里的值可能带引号、带结尾分隔符——规范化之后才是同一个路径
        var missing = MissingDirPath();
        var scanner = MakeScanner(
            [MakeApp(installLocation: "\"" + missing + "\"" + Path.DirectorySeparatorChar)],
            _ => throw new InvalidOperationException("不应查询 MSI 状态"));

        var result = await scanner.ScanAsync();

        var issue = Assert.Single(result.Entries);
        Assert.Contains(issue.Evidence, e => e.Contains(missing));
    }

    // ---------- CachedPackageMissing ----------

    [Fact]
    public async Task CachedPackageMissing_MsiInstalled_PackageGone_Reported()
    {
        var missingPackage = MissingPackagePath();
        var scanner = MakeScanner(
            [MakeApp(isMsi: true, subKeyName: ProductCode)],
            _ => MsiInstalled,
            _ => missingPackage);

        var result = await scanner.ScanAsync();

        var issue = Assert.Single(result.Entries);
        Assert.Equal(AppStateIssueKind.CachedPackageMissing, issue.Kind);
        Assert.Equal(ProductCode, issue.ProductCode);
        Assert.Contains(issue.Evidence, e => e.Contains(missingPackage));
    }

    [Fact]
    public async Task CachedPackageMissing_PackageStillExists_NotReported()
    {
        // LocalPackage 指向的文件还在 → 无问题
        var existingDir = ExistingDirPath();
        var existingPackage = Path.Combine(existingDir, "cached.msi");
        await File.WriteAllTextAsync(existingPackage, "fake msi");

        var scanner = MakeScanner(
            [MakeApp(isMsi: true, subKeyName: ProductCode)],
            _ => MsiInstalled,
            _ => existingPackage);

        var result = await scanner.ScanAsync();

        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task CachedPackageMissing_QueryReturnsNull_Skipped()
    {
        // 拿不到 LocalPackage（属性查询失败）→ 无法判定就不报
        var scanner = MakeScanner(
            [MakeApp(isMsi: true, subKeyName: ProductCode)],
            _ => MsiInstalled,
            _ => null);

        var result = await scanner.ScanAsync();

        Assert.Empty(result.Entries);
    }

    // ---------- MSI 状态闸门（与 v0.4 的边界） ----------

    [Fact]
    public async Task MsiStateAbsent_NothingReported_EvenWhenEverythingIsBroken()
    {
        // 关键边界：MSI 状态 ABSENT 的条目是 v0.4 孤儿清理的地盘。
        // 即使目录和缓存包都没了，v0.5 也绝不报——两个版本不能对同一条目同时出手。
        var scanner = MakeScanner(
            [MakeApp(isMsi: true, installLocation: MissingDirPath(), subKeyName: ProductCode)],
            _ => MsiAbsent,
            _ => MissingPackagePath());

        var result = await scanner.ScanAsync();

        Assert.Empty(result.Entries);
        Assert.Equal(1, result.CheckedCount);
    }

    [Fact]
    public async Task MsiStateUnknown_Skipped_Conservative()
    {
        // 0 = UNKNOWN（msi.dll 不可用等）：不确定就不报
        var scanner = MakeScanner(
            [MakeApp(isMsi: true, installLocation: MissingDirPath(), subKeyName: ProductCode)],
            _ => 0,
            _ => MissingPackagePath());

        var result = await scanner.ScanAsync();

        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task MsiEntry_NonGuidSubKeyName_Skipped_WithoutQueryingMsi()
    {
        // IsMsi 但子键名不是严格 GUID：无法可靠查询 MSI 数据库，保守跳过
        var queried = false;
        var scanner = MakeScanner(
            [MakeApp(isMsi: true, installLocation: MissingDirPath(), subKeyName: "NotAGuid")],
            _ => { queried = true; return MsiInstalled; },
            _ => { queried = true; return MissingPackagePath(); });

        var result = await scanner.ScanAsync();

        Assert.Empty(result.Entries);
        Assert.False(queried, "非 GUID 条目不应触碰 MSI 查询");
    }

    [Fact]
    public async Task NonMsiEntry_NeverChecksLocalPackage()
    {
        // 非 MSI 条目没有产品码，绝不能拿着任意字符串去查 MSI 数据库
        var queried = false;
        var scanner = MakeScanner(
            [MakeApp(isMsi: false, installLocation: MissingDirPath())],
            _ => { queried = true; return MsiInstalled; },
            _ => { queried = true; return MissingPackagePath(); });

        var result = await scanner.ScanAsync();

        Assert.Single(result.Entries);
        Assert.False(queried, "非 MSI 条目不应查询 LocalPackage");
    }

    [Fact]
    public async Task MsiEntry_BothProblemsAtOnce_TwoIssuesReported()
    {
        // 一个条目可以同时贡献两类问题：目录没了 + 缓存包没了
        var scanner = MakeScanner(
            [MakeApp(isMsi: true, installLocation: MissingDirPath(), subKeyName: ProductCode)],
            _ => MsiInstalled,
            _ => MissingPackagePath());

        var result = await scanner.ScanAsync();

        Assert.Equal(2, result.Entries.Count);
        Assert.Contains(result.Entries, i => i.Kind == AppStateIssueKind.InstallLocationMissing);
        Assert.Contains(result.Entries, i => i.Kind == AppStateIssueKind.CachedPackageMissing);
    }

    // ---------- Suggestions 文案分层 ----------

    [Fact]
    public async Task InstallLocationMissing_Suggestion_TellsUserToReinstallOrUninstall()
    {
        var scanner = MakeScanner(
            [MakeApp(installLocation: MissingDirPath())],
            _ => throw new InvalidOperationException("不应查询 MSI 状态"));

        var result = await scanner.ScanAsync();

        var issue = Assert.Single(result.Entries);
        Assert.Contains(issue.Suggestions, s => s.Contains("重新安装"));
        Assert.Contains(issue.Suggestions, s => s.Contains("卸载"));
    }

    [Fact]
    public async Task CachedPackageMissing_Suggestion_SaysUsuallyHarmless()
    {
        var scanner = MakeScanner(
            [MakeApp(isMsi: true, subKeyName: ProductCode)],
            _ => MsiInstalled,
            _ => MissingPackagePath());

        var result = await scanner.ScanAsync();

        var issue = Assert.Single(result.Entries);
        // 文案必须明确"通常无害"，避免把用户吓进"清理缓存"的坑里
        Assert.Contains(issue.Suggestions, s => s.Contains("通常无害"));
    }

    // ---------- 只读铁律 ----------

    [Fact]
    public async Task ReportedIssues_NeverAutoFixable()
    {
        // v0.5 铁律：只读报告，永不自动清理。CanAutoFix 必须是 false。
        var scanner = MakeScanner(
            [
                MakeApp(installLocation: MissingDirPath()),
                MakeApp(isMsi: true, subKeyName: ProductCode),
            ],
            _ => MsiInstalled,
            _ => MissingPackagePath());

        var result = await scanner.ScanAsync();

        Assert.NotEmpty(result.Entries);
        Assert.All(result.Entries, issue => Assert.False(issue.CanAutoFix));
    }

    // ---------- 整体扫描行为 ----------

    [Fact]
    public async Task Scan_HealthyApps_ZeroIssues()
    {
        var existing = ExistingDirPath();
        var scanner = MakeScanner(
            [
                MakeApp(displayName: "A", installLocation: existing),
                MakeApp(displayName: "B"),
            ],
            _ => MsiInstalled,
            _ => Path.Combine(existing, "cached.msi"));   // 上面的文件没创建，但目录存在——文件判定按 File.Exists

        var result = await scanner.ScanAsync();

        Assert.Empty(result.Entries);
        Assert.Equal(2, result.CheckedCount);
        Assert.False(result.IsDegraded);
    }

    [Fact]
    public async Task Scan_MixedApps_ReportsOnlyBrokenOnes()
    {
        var missing = MissingDirPath();
        var existing = ExistingDirPath();
        var scanner = MakeScanner(
            [
                MakeApp(displayName: "完好的", installLocation: existing),
                MakeApp(displayName: "目录丢了", installLocation: missing),
                MakeApp(displayName: "缓存丢了", isMsi: true, subKeyName: ProductCode),
            ],
            code => code == ProductCode ? MsiInstalled : throw new InvalidOperationException(),
            code => code == ProductCode ? MissingPackagePath() : throw new InvalidOperationException());

        var result = await scanner.ScanAsync();

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(3, result.CheckedCount);
        Assert.All(result.Entries, i => Assert.NotEqual("完好的", i.DisplayName));
    }

    [Fact]
    public async Task Scan_AppsProviderThrows_DegradedNotThrown()
    {
        var scanner = new AppStateScanner(
            () => throw new InvalidOperationException("注册表读不了"),
            _ => MsiInstalled,
            _ => null);

        var result = await scanner.ScanAsync();

        Assert.Empty(result.Entries);
        Assert.True(result.IsDegraded);
        Assert.Contains("获取失败", result.DegradedReason);
    }

    [Fact]
    public async Task Scan_EnumerationHadError_DegradedButStillScans()
    {
        // 枚举器部分失败（InstalledAppsResult.Error）：结果可能不完整，要降级说明，
        // 但拿到的条目照样要扫完
        var missing = MissingDirPath();
        var scanner = MakeScanner(
            [MakeApp(installLocation: missing)],
            _ => throw new InvalidOperationException("不应查询 MSI 状态"),
            enumerateError: "HKLM\\64 权限不足：拒绝访问");

        var result = await scanner.ScanAsync();

        Assert.Single(result.Entries);
        Assert.True(result.IsDegraded);
        Assert.Contains("权限不足", result.DegradedReason);
    }

    [Fact]
    public async Task Scan_ProgressReports_AndCompletes()
    {
        var scanner = MakeScanner(
            [MakeApp(), MakeApp(displayName: "第二个")],
            _ => throw new InvalidOperationException("不应查询 MSI 状态"));
        var messages = new List<string>();

        var result = await scanner.ScanAsync(new Progress<string>(messages.Add));

        Assert.True(result.Duration >= TimeSpan.Zero);
        Assert.Contains(messages, m => m.Contains("测试软件"));
        Assert.Contains(messages, m => m.Contains("完成"));
    }

    [Fact]
    public async Task Scan_CancellationRequested_ThrowsOperationCanceled()
    {
        var scanner = MakeScanner(
            [MakeApp(), MakeApp(), MakeApp()],
            _ => throw new InvalidOperationException("不应查询 MSI 状态"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scanner.ScanAsync(cancellationToken: cts.Token));
    }

    // ---------- 真机回归（只读） ----------

    [Fact]
    public async Task ScanAsync_RealMachine_CompletesAndNeverReportsAutoFixable()
    {
        // 与 v0.4 的 OrphanUninstallScannerTests 同一思路：拿真机当回归数据集。
        // 本机有几百个在装产品，任何一条被报出 CanAutoFix=true 都说明铁律被破坏。
        var scanner = new AppStateScanner(() => new InstalledAppEnumerator().Enumerate());

        var result = await scanner.ScanAsync();

        Assert.True(result.CheckedCount > 0, "本机应有可检查的软件条目");
        Assert.All(result.Entries, issue =>
        {
            Assert.False(issue.CanAutoFix);
            Assert.NotEmpty(issue.Evidence);
            Assert.NotEmpty(issue.Suggestions);
            if (issue.Kind == AppStateIssueKind.CachedPackageMissing)
            {
                Assert.Contains(issue.Suggestions, s => s.Contains("通常无害"));
            }
        });
    }
}
