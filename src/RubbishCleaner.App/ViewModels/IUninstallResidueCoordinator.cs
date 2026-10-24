using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Residue;

namespace RubbishCleaner.App.ViewModels;

/// <summary>
/// "卸载 ↔ 残留扫描"的协调者（v0.3.1）。
///
/// <b>为什么抽一层接口</b>：卸载流程（<see cref="AppsViewModel"/>）是 v0.2 的老代码，
/// 残留扫描是 v0.3 的新代码。把两者的协作点收窄到这一个接口里，
/// 卸载流程只需要知道"卸载前问一声、卸载后通知一声"，不需要知道快照怎么存、残留怎么扫。
/// 这样 v0.2 的行为不会因为 v0.3.1 的改动而回归。
/// </summary>
public interface IUninstallResidueCoordinator
{
    /// <summary>
    /// 卸载<b>前</b>调用：对即将卸载的软件做快照。
    ///
    /// 返回 false 表示"没做成快照"（例如没有 InstallLocation、目录不存在），
    /// 调用方应静默继续——快照失败不该阻止用户卸载软件。
    /// </summary>
    bool CaptureBefore(InstalledApp app);

    /// <summary>
    /// 卸载<b>后</b>调用：拿 <see cref="CaptureBefore"/> 存的快照做定向扫描。
    ///
    /// 返回 null 表示没有可对比的快照（用户可能没经过本工具卸载，或快照已被清理）。
    /// </summary>
    Task<UninstallResidueResult?> ScanAfterAsync(
        InstalledApp app,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>删除某个软件的过期快照，避免无限增长。</summary>
    void PruneSnapshots(string softwareName);
}
