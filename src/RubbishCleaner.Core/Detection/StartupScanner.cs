using System.Collections.Concurrent;
using System.Diagnostics;
using RubbishCleaner.Core.Abstractions;
using RubbishCleaner.Core.Cleanup;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Scoring;
using RubbishCleaner.Core.Trust;

namespace RubbishCleaner.Core.Detection;

/// <summary>
/// 扫描编排器：采集 → 去重 → 聚合 → 事实采集 → 确认表 → 评分 → 排序。
/// 设计原则见技术文档第 4.1 节；任一数据源失败只降级，不影响整体。
/// </summary>
public sealed class StartupScanner
{
    private readonly IReadOnlyList<IStartupSource> _sources;
    private readonly IProcessFactsProvider _processFacts;
    private readonly IShortcutIndex _shortcuts;
    private readonly TrustMatcher _trustMatcher;
    private readonly RiskScorer _scorer;
    private readonly IFileSignatureProvider _signatures;

    public StartupScanner(
        IReadOnlyList<IStartupSource> sources,
        IProcessFactsProvider processFacts,
        IShortcutIndex shortcuts,
        TrustMatcher trustMatcher,
        RiskScorer? scorer = null,
        IFileSignatureProvider? signatures = null)
    {
        _sources = sources;
        _processFacts = processFacts;
        _shortcuts = shortcuts;
        _trustMatcher = trustMatcher;
        _scorer = scorer ?? new RiskScorer();
        _signatures = signatures ?? new FileSignatureProvider();
    }

    /// <summary>创建使用真实系统能力的默认扫描器。</summary>
    public static StartupScanner CreateDefault(TrustTable? trustTable = null)
    {
        var signatures = new FileSignatureProvider();
        var table = trustTable ?? TrustTableLoader.Load();

        IStartupSource[] sources =
        [
            new Sources.RegistryRunSource(StartupSourceKind.RegistryRunHkcu),
            new Sources.RegistryRunSource(StartupSourceKind.RegistryRunHklm),
            new Sources.RegistryRunSource(StartupSourceKind.RegistryRunWow64),
            new Sources.StartupFolderSource(StartupSourceKind.StartupFolderUser),
            new Sources.StartupFolderSource(StartupSourceKind.StartupFolderMachine),
            new Sources.WindowsServiceSource(),
            new Sources.ScheduledTaskSource(),
        ];

        return new StartupScanner(
            sources,
            new ProcessFactsProvider(),
            new ShortcutIndex(),
            new TrustMatcher(table),
            new RiskScorer(),
            signatures);
    }

    /// <summary>当前是否以管理员权限运行（决定计划任务等数据源是否完整）。</summary>
    public bool IsElevated => _processFacts.IsElevated;

    public async Task<ScanResult> ScanAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var isElevated = _processFacts.IsElevated;

        var context = new ScanContext
        {
            IsElevated = isElevated,
            Progress = progress,
            CancellationToken = cancellationToken,
            Signatures = _signatures,
        };

        // 阶段 1：并行采集所有数据源
        progress?.Report(new ScanProgress("正在采集自启动项…", 0, _sources.Count));
        var results = await CollectAllAsync(context, progress).ConfigureAwait(false);

        var degraded = results.Where(r => r.Degraded).ToList();
        var entries = results
            .SelectMany(r => r.Entries)
            .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // 阶段 2：按可执行文件聚合
        progress?.Report(new ScanProgress("正在聚合程序…", 0, entries.Count));
        var groups = entries
            .GroupBy(e => e.GroupKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 阶段 3：并发采集进程事实 + 确认表 + 评分
        var profiles = new ConcurrentBag<SoftwareProfile>();
        var completed = 0;
        var total = groups.Count;

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
        };

        await Parallel.ForEachAsync(groups, parallelOptions, (group, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var profile = BuildProfile(group.ToList());
            profiles.Add(profile);

            var done = Interlocked.Increment(ref completed);
            if (done % 10 == 0 || done == total)
            {
                progress?.Report(new ScanProgress("正在分析程序…", done, total, profile.DisplayName));
            }

            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        var ordered = profiles
            .OrderByDescending(p => p.Score.Total)
            .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        stopwatch.Stop();
        progress?.Report(new ScanProgress("完成", total, total));

        return new ScanResult
        {
            Profiles = ordered,
            DegradedSources = degraded,
            Duration = stopwatch.Elapsed,
            StartedAt = startedAt,
            IsElevated = isElevated,
        };
    }

    private async Task<IReadOnlyList<SourceResult>> CollectAllAsync(ScanContext context, IProgress<ScanProgress>? progress)
    {
        var results = new List<SourceResult>(_sources.Count);
        var completed = 0;

        foreach (var source in _sources)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await source.CollectAsync(context).ConfigureAwait(false);
                results.Add(result);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 数据源级隔离：记录降级而不是让整次扫描失败。
                results.Add(SourceResult.Failed(source.Kind, ex.Message));
            }

            var done = Interlocked.Increment(ref completed);
            progress?.Report(new ScanProgress("正在采集自启动项…", done, _sources.Count, source.Kind.ToDisplayName()));
        }

        return results;
    }

    private SoftwareProfile BuildProfile(List<StartupEntry> groupEntries)
    {
        var first = groupEntries[0];
        var executablePath = groupEntries
            .Select(e => e.ExecutablePath)
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));

        var displayName = ResolveDisplayName(first, executablePath);
        var processName = Path.GetFileNameWithoutExtension(executablePath ?? displayName);

        var facts = _processFacts.GetFacts(executablePath, processName);

        var hasDesktop = _shortcuts.HasDesktopShortcut(executablePath, displayName);
        var hasStartMenu = _shortcuts.HasStartMenuEntry(executablePath, displayName);

        var trust = _trustMatcher.Match(
            executablePath,
            processName,
            facts.SignerSubject,
            () => executablePath is null ? null : _signatures.ComputeSha256(executablePath));

        var profile = new SoftwareProfile
        {
            Key = first.GroupKey,
            DisplayName = displayName,
            ExecutablePath = executablePath,
            StartupEntries = groupEntries,
            LiveProcess = facts,
            HasDesktopShortcut = hasDesktop,
            HasStartMenuEntry = hasStartMenu,
            Trust = trust.Verdict,
            TrustReason = trust.Reason,
        };

        profile.Score = _scorer.Score(profile);
        profile.Evidences = BuildEvidences(profile);
        return profile;
    }

    private static string ResolveDisplayName(StartupEntry entry, string? executablePath)
    {
        // 优先用文件名（用户最熟悉的标识），没有路径时退回启动项名称。
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var fileName = Path.GetFileNameWithoutExtension(executablePath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        return entry.Name;
    }

    private static IReadOnlyList<Evidence> BuildEvidences(SoftwareProfile profile)
    {
        var list = new List<Evidence>();

        foreach (var entry in profile.StartupEntries)
        {
            list.Add(Evidence.Info(
                $"AUTOSTART_{entry.Source}",
                $"{entry.Source.ToDisplayName()}：{entry.Name}" +
                (string.IsNullOrWhiteSpace(entry.RawCommand) ? string.Empty : $" → {entry.RawCommand}")));

            if (!entry.IsReadable)
            {
                list.Add(Evidence.Warning("ENTRY_UNREADABLE", $"启动项「{entry.Name}」无法解析：{entry.ReadError}"));
            }
        }

        if (profile.Trust != TrustVerdict.Unknown && profile.TrustReason is not null)
        {
            list.Add(profile.Trust == TrustVerdict.Safe
                ? Evidence.Info("TRUST_WHITELIST", profile.TrustReason)
                : Evidence.Critical("TRUST_BLACKLIST", profile.TrustReason));
        }

        // 失效启动项：把具体路径与注册表位置告诉用户，让他能直接去清理。
        foreach (var orphan in profile.OrphanEntries)
        {
            list.Add(Evidence.Warning(
                "ORPHAN_STARTUP",
                $"启动项「{orphan.Name}」指向的文件已不存在：{orphan.ExecutablePath}（位于 {orphan.Location}）"));

            // 不可自动清理的类型（服务/计划任务/快捷方式）给出可复制的手动处理命令。
            // 刻意只给指引、不代劳：删服务可能影响依赖它的其他服务，风险由用户判断。
            var guide = ManualFixAdvisor.For(orphan);
            if (guide is not null && !string.IsNullOrWhiteSpace(guide.Command))
            {
                list.Add(Evidence.Info(
                    "ORPHAN_MANUAL_FIX",
                    $"需手动处理（{guide.Title}）：{guide.Command}"));
            }
        }

        if (profile.LiveProcess is { IsRunning: true } facts)
        {
            list.Add(Evidence.Info(
                "PROCESS_RUNNING",
                $"进程运行中（PID {facts.ProcessId}），可见窗口：{(facts.HasVisibleWindow ? "有" : "无")}，任务栏按钮：{(facts.HasTaskbarButton ? "有" : "无")}"));
        }
        else if (!profile.IsOrphaned)
        {
            // 失效启动项谈不上"未运行"，否则会与上面的残留提示重复且误导。
            list.Add(Evidence.Info("PROCESS_NOT_RUNNING", "进程当前未运行"));
        }

        if (profile.LiveProcess is { SignatureValid: false } && !profile.IsOrphaned)
        {
            list.Add(Evidence.Warning("SIGNATURE_MISSING", "未找到有效的数字签名"));
        }

        if (profile.Score.Suppressed)
        {
            list.Add(Evidence.Info("TRUST_SUPPRESSED", "命中白名单，已压制风险等级（保留原始分数供参考）"));
        }

        // 托盘图标无法判定，显式告知用户，避免"为什么没有这一项"的困惑。
        list.Add(Evidence.Info("TRAY_UNKNOWN", "托盘图标无法可靠枚举，v0.1 不参与评分"));

        return list;
    }
}
