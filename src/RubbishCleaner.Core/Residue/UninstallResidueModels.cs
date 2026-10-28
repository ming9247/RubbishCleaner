using RubbishCleaner.Core.Leftover;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Residue;

/// <summary>卸载前对某个软件的安装目录做的一次快照。</summary>
/// <remarks>
/// <b>为什么要有快照</b>：通用残留扫描是"猜"——靠卸载程序、启动项这些硬痕迹推断
/// 哪些目录曾经装过软件。而"用户刚刚用本工具卸载了 X"是一个<b>已知事实</b>：
/// 软件的名字、装在哪里、卸载前有哪些文件，全都知道。
/// 拿卸载后的磁盘状态和这个已知事实对比，得到的是<b>确定性残留</b>而不是启发式猜测。
///
/// 只记"路径 + 大小 + 文件数"，不记文件内容哈希：
/// 记内容会让快照变大几十倍，而对比的目的只是回答"哪些条目消失了/哪些留下了"。
/// </remarks>
public sealed record UninstallSnapshot(
    string SoftwareName,
    string? InstallLocation,
    DateTimeOffset TakenAt,
    IReadOnlyList<SnapshotEntry> Entries,
    string? UninstallCommand = null,
    string? Publisher = null)
{
    /// <summary>稳定标识：软件名 + 时间戳，便于日志定位与 UI 去重。</summary>
    public string Id => $"{SoftwareName}@{TakenAt:yyyyMMddHHmmss}";

    public int FileCount => Entries.Count(e => !e.IsDirectory);

    public int DirectoryCount => Entries.Count(e => e.IsDirectory);

    public long TotalBytes => Entries.Where(e => !e.IsDirectory).Sum(e => e.SizeBytes);
}

/// <summary>快照里的一个条目（目录或文件）。</summary>
public sealed record SnapshotEntry(
    string RelativePath,
    bool IsDirectory,
    long SizeBytes);

/// <summary>
/// 定向扫描的结果：软件 X 刚刚被卸载，磁盘上还留着什么。
/// </summary>
public sealed record UninstallResidueResult
{
    /// <summary>确认残留：卸载前存在、卸载后仍然存在的文件（卸载器没删干净的部分）。</summary>
    public required IReadOnlyList<string> RemainingFiles { get; init; }

    /// <summary>卸载后新出现/仍然存在的疑似残留目录（含子目录与常见数据位置）。</summary>
    public required IReadOnlyList<LeftoverItem> Directories { get; init; }

    /// <summary>卸载前存在、卸载后已消失的条目数（用于告诉用户"卸载器清理了大部分"）。</summary>
    public int RemovedCount { get; init; }

    /// <summary>卸载前快照的总条目数。</summary>
    public int SnapshotCount { get; init; }

    public required string SoftwareName { get; init; }

    public bool HasResidue => RemainingFiles.Count > 0 || Directories.Count > 0;

    public long TotalBytes => RemainingFiles.Sum(f => new System.IO.FileInfo(f).Length)
        + Directories.Sum(d => d.TotalBytes);
}

/// <summary>定向扫描的输入参数。</summary>
public sealed record UninstallResidueOptions
{
    /// <summary>
    /// 除了"已卸载软件的安装目录"之外，还要额外扫哪些根目录。
    ///
    /// 默认扫 <see cref="LeftoverScanOptions.DefaultRoots"/>：流氓软件的残留极少只留在
    /// 安装目录里，更多在 <c>%AppData%</c> / <c>%ProgramData%</c> / <c>%LocalAppData%</c>。
    /// </summary>
    public IReadOnlyList<string> ExtraRoots { get; init; } = LeftoverScanOptions.DefaultRoots().ToList();

    /// <summary>用于"认领"判定的已安装软件列表（扫描时重新读注册表，通常就是刚卸载后的状态）。</summary>
    public IReadOnlyList<Models.InstalledApp> InstalledApps { get; init; } = [];

    /// <summary>
    /// 卸载后最多允许等待多久（秒）。
    ///
    /// 卸载器常常是异步的：主进程退出了，后台还在删文件、写日志。
    /// 立即扫会把"正在被删除的文件"当成残留报出来，所以需要等一下。
    /// </summary>
    public int SettleDelaySeconds { get; init; } = 2;

    /// <summary>
    /// 按名字找数据目录时最多往下几层（v0.9.6）。
    ///
    /// <para>1 = 只看根目录的直接子目录（v0.9.5 及更早的行为）。
    /// 2 = 在 <c>%LocalAppData%</c> / <c>%AppData%</c> / <c>%LocalAppData%\LocalLow</c> / <c>%ProgramData%</c>
    /// 这类"厂商 + 产品"两层的根上多看一层，才能命中 <c>%LocalAppData%\Programs\&lt;App&gt;</c>、
    /// <c>%AppData%\&lt;Vendor&gt;\&lt;App&gt;</c> 这些很常见的形态。</para>
    /// </summary>
    public int MaxDepth { get; init; } = 2;

    /// <summary>
    /// 每个根目录最多枚举多少个子目录（v0.9.6）。
    ///
    /// 防止在 <c>%LocalAppData%</c> 这种几百上千个子目录的根上失控——宁可少报，不能让卸载后扫描卡住。
    /// </summary>
    public int MaxDirectoriesPerRoot { get; init; } = 4000;
}
