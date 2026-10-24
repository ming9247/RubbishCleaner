using RubbishCleaner.Core.Abstractions;
using RubbishCleaner.Core.Detection;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Scoring;
using RubbishCleaner.Core.Trust;

namespace RubbishCleaner.Core.Tests;

/// <summary>用假实现替代真实系统访问，使扫描编排逻辑可在无管理员权限下测试。</summary>
internal sealed class FakeStartupSource : IStartupSource
{
    private readonly Func<SourceResult> _factory;

    public FakeStartupSource(StartupSourceKind kind, Func<SourceResult> factory)
    {
        Kind = kind;
        _factory = factory;
    }

    public StartupSourceKind Kind { get; }

    public bool RequiresElevation => false;

    public Task<SourceResult> CollectAsync(ScanContext context) => Task.FromResult(_factory());
}

internal sealed class FakeProcessFactsProvider : IProcessFactsProvider
{
    private readonly Dictionary<string, ProcessFacts> _facts = new(StringComparer.OrdinalIgnoreCase);

    public bool IsElevated { get; init; }

    public FakeProcessFactsProvider Add(string path, bool running, bool visible, bool signed)
    {
        _facts[path] = new ProcessFacts(
            ProcessId: running ? 42 : 0,
            ProcessName: Path.GetFileNameWithoutExtension(path),
            ExecutablePath: path,
            IsRunning: running,
            HasVisibleWindow: visible,
            HasTaskbarButton: visible,
            SignatureValid: signed,
            IsMicrosoftSigned: false,
            SignerSubject: signed ? "CN=Test, O=Test Vendor" : null,
            Publisher: signed ? "Test Vendor" : null,
            FileDescription: null);
        return this;
    }

    public ProcessFacts GetFacts(string? executablePath, string fallbackName)
    {
        if (executablePath is not null && _facts.TryGetValue(executablePath, out var facts))
        {
            return facts;
        }

        return ProcessFacts.NotRunning(fallbackName, executablePath);
    }
}

internal sealed class FakeShortcutIndex : IShortcutIndex
{
    public bool DesktopValue { get; init; }

    public bool StartMenuValue { get; init; }

    public bool HasDesktopShortcut(string? executablePath, string displayName) => DesktopValue;

    public bool HasStartMenuEntry(string? executablePath, string displayName) => StartMenuValue;
}

internal sealed class FakeSignatureProvider : IFileSignatureProvider
{
    public SignatureFacts? Verify(string? filePath) => null;

    public string? ComputeSha256(string filePath) => null;
}

public class StartupScannerTests
{
    private static StartupEntry Entry(
        string name,
        string? path,
        StartupSourceKind kind = StartupSourceKind.RegistryRunHkcu)
        => new()
        {
            Id = EntryId.Create(kind, @"HKCU\Run", name),
            Source = kind,
            Name = name,
            Location = @"HKCU\Run",
            RawCommand = path,
            ExecutablePath = path,
            TargetExistsOverride = true,
        };

    private static StartupScanner CreateScanner(
        IReadOnlyList<IStartupSource> sources,
        IProcessFactsProvider facts,
        IShortcutIndex shortcuts,
        TrustTable? trust = null)
        => new(
            sources,
            facts,
            shortcuts,
            new TrustMatcher(trust ?? TrustTable.Empty),
            new RiskScorer(),
            new FakeSignatureProvider());

    [Fact]
    public async Task ScanAsync_DeduplicatesIdenticalEntries()
    {
        var entry = Entry("App", @"C:\Apps\app.exe");
        var source = new FakeStartupSource(
            StartupSourceKind.RegistryRunHkcu,
            () => new SourceResult(StartupSourceKind.RegistryRunHkcu, [entry, entry]));

        var scanner = CreateScanner(
            [source],
            new FakeProcessFactsProvider().Add(@"C:\Apps\app.exe", running: true, visible: true, signed: true),
            new FakeShortcutIndex { DesktopValue = true, StartMenuValue = true });

        var result = await scanner.ScanAsync();

        Assert.Single(result.Profiles);
        Assert.Single(result.Profiles[0].StartupEntries);
    }

    [Fact]
    public async Task ScanAsync_AggregatesMultipleStartupPointsIntoOneProfile()
    {
        // 同一个 exe 同时通过 Run 键和服务自启动，应聚合成一个软件
        var run = Entry("App", @"C:\Apps\app.exe");
        var service = Entry("AppSvc", @"C:\Apps\app.exe", StartupSourceKind.WindowsService);

        var sources = new IStartupSource[]
        {
            new FakeStartupSource(StartupSourceKind.RegistryRunHkcu, () => new SourceResult(StartupSourceKind.RegistryRunHkcu, [run])),
            new FakeStartupSource(StartupSourceKind.WindowsService, () => new SourceResult(StartupSourceKind.WindowsService, [service])),
        };

        var scanner = CreateScanner(
            sources,
            new FakeProcessFactsProvider().Add(@"C:\Apps\app.exe", running: true, visible: false, signed: false),
            new FakeShortcutIndex());

        var result = await scanner.ScanAsync();

        Assert.Single(result.Profiles);
        Assert.Equal(2, result.Profiles[0].StartupEntries.Count);
        Assert.Equal(2, result.TotalStartupEntries);
    }

    [Fact]
    public async Task ScanAsync_FailingSource_IsMarkedDegradedAndOthersContinue()
    {
        var sources = new IStartupSource[]
        {
            new FakeStartupSource(StartupSourceKind.RegistryRunHkcu, () => throw new InvalidOperationException("注册表读取失败")),
            new FakeStartupSource(StartupSourceKind.StartupFolderUser, () => new SourceResult(
                StartupSourceKind.StartupFolderUser, [Entry("Good", @"C:\Good\good.exe", StartupSourceKind.StartupFolderUser)])),
        };

        var scanner = CreateScanner(
            sources,
            new FakeProcessFactsProvider().Add(@"C:\Good\good.exe", running: true, visible: true, signed: true),
            new FakeShortcutIndex { DesktopValue = true, StartMenuValue = true });

        var result = await scanner.ScanAsync();

        Assert.Single(result.DegradedSources);
        Assert.Contains("注册表读取失败", result.DegradedSources[0].Error);
        Assert.Single(result.Profiles); // 其余数据源仍正常产出
    }

    [Fact]
    public async Task ScanAsync_SortsByScoreDescending()
    {
        var safe = Entry("Safe", @"C:\Safe\safe.exe");
        var risky = Entry("Risky", @"C:\Risky\risky.exe");

        var scanner = CreateScanner(
            [new FakeStartupSource(StartupSourceKind.RegistryRunHkcu, () => new SourceResult(
                StartupSourceKind.RegistryRunHkcu, [safe, risky]))],
            new FakeProcessFactsProvider()
                .Add(@"C:\Safe\safe.exe", running: true, visible: true, signed: true)
                .Add(@"C:\Risky\risky.exe", running: true, visible: false, signed: false),
            new FakeShortcutIndex { DesktopValue = true, StartMenuValue = true });

        var result = await scanner.ScanAsync();

        Assert.Equal("risky", result.Profiles[0].DisplayName);
        Assert.True(result.Profiles[0].Score.Total > result.Profiles[1].Score.Total);
    }

    [Fact]
    public async Task ScanAsync_CancelledToken_Throws()
    {
        var scanner = CreateScanner(
            [new FakeStartupSource(StartupSourceKind.RegistryRunHkcu, () => SourceResult.Empty(StartupSourceKind.RegistryRunHkcu))],
            new FakeProcessFactsProvider(),
            new FakeShortcutIndex());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ScanAsync(null, cts.Token));
    }

    [Fact]
    public async Task ScanAsync_TrustTableSafeSuppressesLevel()
    {
        var entry = Entry("SysApp", @"C:\Apps\sysapp.exe");
        var trust = new TrustTable
        {
            Whitelist = [new TrustRule(TrustRuleType.Path, @"C:\Apps\", "白名单目录")],
        };

        var scanner = CreateScanner(
            [new FakeStartupSource(StartupSourceKind.RegistryRunHkcu, () => new SourceResult(StartupSourceKind.RegistryRunHkcu, [entry]))],
            new FakeProcessFactsProvider().Add(@"C:\Apps\sysapp.exe", running: true, visible: false, signed: false),
            new FakeShortcutIndex(),
            trust);

        var result = await scanner.ScanAsync();
        var profile = result.Profiles[0];

        Assert.Equal(TrustVerdict.Safe, profile.Trust);
        Assert.Equal(RiskLevel.Normal, profile.Score.Level);
        Assert.True(profile.Score.Suppressed);
    }

    [Fact]
    public async Task ScanAsync_ProducesEvidenceChain()
    {
        var entry = Entry("App", @"C:\Apps\app.exe");

        var scanner = CreateScanner(
            [new FakeStartupSource(StartupSourceKind.RegistryRunHkcu, () => new SourceResult(StartupSourceKind.RegistryRunHkcu, [entry]))],
            new FakeProcessFactsProvider().Add(@"C:\Apps\app.exe", running: true, visible: false, signed: false),
            new FakeShortcutIndex());

        var result = await scanner.ScanAsync();
        var codes = result.Profiles[0].Evidences.Select(e => e.Code).ToList();

        Assert.Contains("PROCESS_RUNNING", codes);
        Assert.Contains("SIGNATURE_MISSING", codes);
        Assert.Contains("TRAY_UNKNOWN", codes);
    }

    [Fact]
    public async Task ScanAsync_ReportsProgressUpToCompletion()
    {
        var reports = new List<ScanProgress>();
        var progress = new Progress<ScanProgress>(reports.Add);

        var scanner = CreateScanner(
            [new FakeStartupSource(StartupSourceKind.RegistryRunHkcu, () => new SourceResult(
                StartupSourceKind.RegistryRunHkcu, [Entry("A", @"C:\A\a.exe")]))],
            new FakeProcessFactsProvider().Add(@"C:\A\a.exe", running: true, visible: true, signed: true),
            new FakeShortcutIndex());

        _ = await scanner.ScanAsync(progress);
        await Task.Delay(100); // Progress<T> 通过同步上下文异步投递

        Assert.NotEmpty(reports);
        Assert.Contains(reports, r => r.Stage == "完成");
    }
}
