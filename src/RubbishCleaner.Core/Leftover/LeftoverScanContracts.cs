using RubbishCleaner.Common.Mft;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Uninstall;

namespace RubbishCleaner.Core.Leftover;

/// <summary>残留扫描的输入参数。</summary>
public sealed class LeftoverScanOptions
{
    /// <summary>
    /// 扫描根目录。默认是五个"软件会往这里装东西"的位置：
    /// Program Files / Program Files (x86) / ProgramData / %LocalAppData% / %AppData%。
    /// </summary>
    public IReadOnlyList<string> Roots { get; init; } = DefaultRoots();

    /// <summary>
    /// 已安装软件列表（<b>判定"被谁认领"的唯一依据</b>）。
    ///
    /// 由调用方注入而不是让扫描器自己去读注册表，是为了：
    /// 1. 单测可以塞假数据；
    /// 2. UI 上"已安装软件"页与"残留扫描"用同一份快照，避免两次读取之间软件被卸掉导致结论自相矛盾。
    /// </summary>
    public IReadOnlyList<InstalledApp> InstalledApps { get; init; } = [];

    /// <summary>
    /// 额外的"已认领路径"证据，通常来自 <c>InstalledAppsResult.ClaimPaths</c>。
    ///
    /// 为什么要单独传：<see cref="InstalledApps"/> 只包含<b>可展示</b>的软件，
    /// 而 <c>SystemComponent=1</c> 的补丁、没有 DisplayName 的组件条目
    /// 虽然不展示，它们在磁盘上占的目录同样是<b>有主的</b>。
    /// 不把这类路径算进认领，就会把在用的目录误报成残留——
    /// 实测本机 Adobe CoreSync 的注册表条目就没有 DisplayName。
    /// </summary>
    public IReadOnlyList<string> AdditionalClaimPaths { get; init; } = [];

    /// <summary>
    /// 上一次启动项扫描的结果（可选）。提供了才能做"失效启动项 → 残留目录"的交叉验证。
    /// </summary>
    public IReadOnlyList<SoftwareProfile> StartupProfiles { get; init; } = [];

    /// <summary>
    /// 绝对不扫的目录。
    ///
    /// 默认排除本工具自己的数据目录（<c>%LocalAppData%\RubbishCleaner</c>）——
    /// 那里放的是清理备份与日志，一旦被当成残留删掉，用户就失去了还原能力。
    /// </summary>
    public IReadOnlyList<string> ExcludedPaths { get; init; } = DefaultExcludedPaths();

    /// <summary>默认排除项。</summary>
    public static IReadOnlyList<string> DefaultExcludedPaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? []
            : [Path.Combine(localAppData, "RubbishCleaner")];
    }

    /// <summary>是否统计目录大小（需要遍历子树，大目录会明显变慢）。</summary>
    public bool MeasureSize { get; init; } = true;

    /// <summary>默认扫描根目录（去重后按存在性过滤）。</summary>
    public static IReadOnlyList<string> DefaultRoots()
    {
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        };

        return candidates
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>
/// 残留扫描器。
///
/// <b>判定逻辑的核心思想</b>：不猜"这个目录像不像某个软件"，而是找<b>硬痕迹</b>——
/// 目录里还留着卸载程序（<c>unins000.exe</c> / <c>uninst.exe</c> / <c>uninstall.exe</c>…），
/// 却没有任何已安装软件认领它。卸载程序的存在本身就证明这里曾经装过一个软件，
/// 这是比"名字长得像"强得多的证据。
/// </summary>
public interface ILeftoverScanner
{
    /// <summary>执行一次残留扫描；不修改任何文件。</summary>
    Task<LeftoverScanResult> ScanAsync(
        LeftoverScanOptions options,
        IProgress<LeftoverScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}