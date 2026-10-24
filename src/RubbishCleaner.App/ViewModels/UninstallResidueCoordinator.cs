using System.IO;
using RubbishCleaner.App.ViewModels;
using RubbishCleaner.Common.Logging;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Residue;

namespace RubbishCleaner.App.ViewModels;

/// <summary>
/// "卸载 ↔ 残留扫描"协调者的默认实现（v0.3.1）。
///
/// 职责只有两件事，刻意保持薄：
/// <list type="number">
/// <item><b>卸载前</b>：把软件快照存到磁盘；</item>
/// <item><b>卸载后</b>：把快照读回来做定向扫描。</item>
/// </list>
///
/// 为什么不把判定逻辑放这里：判定属于 Core（<see cref="UninstallResidueScanner"/>），
/// 这里只负责"什么时候调、拿什么调"。这样 Core 可以在没有 UI 的情况下被完整测试。
/// </summary>
public sealed class UninstallResidueCoordinator : IUninstallResidueCoordinator
{
    private readonly UninstallResidueScanner _scanner;
    private readonly string _snapshotDirectory;
    private readonly Func<IReadOnlyList<InstalledApp>> _installedAppsProvider;

    /// <param name="scanner">定向扫描器；默认用真实索引（USN/回退）。</param>
    /// <param name="snapshotDirectory">快照存放目录；默认 %LocalAppData%\RubbishCleaner\uninstall-snapshots。</param>
    /// <param name="installedAppsProvider">扫描时提供"当前已安装软件"用于认领判定。</param>
    public UninstallResidueCoordinator(
        UninstallResidueScanner? scanner = null,
        string? snapshotDirectory = null,
        Func<IReadOnlyList<InstalledApp>>? installedAppsProvider = null)
    {
        _scanner = scanner ?? new UninstallResidueScanner();
        _snapshotDirectory = snapshotDirectory ?? UninstallSnapshotStore.DefaultDirectory;
        _installedAppsProvider = installedAppsProvider ?? DefaultInstalledApps;
    }

    /// <summary>卸载前做快照。失败返回 false，绝不抛异常。</summary>
    public bool CaptureBefore(InstalledApp app)
    {
        try
        {
            var snapshot = UninstallSnapshotStore.TakeSnapshot(app);
            if (snapshot is null)
            {
                return false;
            }

            var path = UninstallSnapshotStore.Save(snapshot, _snapshotDirectory);

            // 顺手清理旧快照：每个软件只留最近 3 份。
            // 不清理的话，用户每卸一个软件就多一个 JSON——这正是"流氓工具"的典型毛病。
            UninstallSnapshotStore.Prune(_snapshotDirectory);

            Log.Info($"已记录卸载前快照：{app.DisplayName}（{snapshot.FileCount} 个文件）-> {path}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"卸载前快照失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>卸载后做定向扫描。没有快照时返回 null。</summary>
    public async Task<UninstallResidueResult?> ScanAfterAsync(
        InstalledApp app,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = UninstallSnapshotStore.LoadLatest(_snapshotDirectory, app.DisplayName);
            if (snapshot is null)
            {
                return null;
            }

            Log.Info($"开始卸载后定向扫描：{app.DisplayName}（快照于 {snapshot.TakenAt:HH:mm:ss}，{snapshot.FileCount} 个文件）");

            var result = await _scanner
                .ScanAsync(snapshot, BuildOptions(), null, cancellationToken)
                .ConfigureAwait(false);

            // 扫描完成就删掉快照：它的使命已经完成，留着只会让下次误判
            // （用户下次再装同一个软件时，旧快照会被当成"这次卸载前的状态"）。
            UninstallSnapshotStore.Prune(_snapshotDirectory, keepPerSoftware: 1);
            var latest = UninstallSnapshotStore.LoadLatest(_snapshotDirectory, app.DisplayName);
            if (latest is not null && latest.TakenAt == snapshot.TakenAt)
            {
                UninstallSnapshotStore.Delete(Path.Combine(
                    _snapshotDirectory,
                    Sanitize(snapshot.SoftwareName) + "-" + snapshot.TakenAt.ToString("yyyyMMddHHmmss") + ".json"));
            }

            Log.Info($"定向扫描完成：残留 {result.RemainingFiles.Count} 个文件、{result.Directories.Count} 个目录"
                     + $"（卸载器已清理 {result.RemovedCount}/{result.SnapshotCount}）");

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"卸载后定向扫描失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>清理某个软件的过期快照。</summary>
    public void PruneSnapshots(string softwareName)
    {
        try
        {
            UninstallSnapshotStore.Prune(_snapshotDirectory);
        }
        catch (Exception ex)
        {
            Log.Warn($"清理快照失败：{ex.Message}");
        }
    }

    private UninstallResidueOptions BuildOptions() => new()
    {
        InstalledApps = _installedAppsProvider(),

        // 等 2 秒让卸载器的后台收尾跑完，否则会把"正在被删的文件"报成残留。
        SettleDelaySeconds = 2,
    };

    private static IReadOnlyList<InstalledApp> DefaultInstalledApps()
    {
        try
        {
            return new Core.Uninstall.InstalledAppEnumerator().Enumerate().Apps;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取已安装软件失败：{ex.Message}");
            return [];
        }
    }

    private static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unknown";
        }

        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim() is { Length: > 0 } s
            ? s
            : "unknown";
    }
}
