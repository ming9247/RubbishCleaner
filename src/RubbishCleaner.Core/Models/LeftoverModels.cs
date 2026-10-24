namespace RubbishCleaner.Core.Models;

/// <summary>残留项的来源类型。决定了它能被"多大程度地相信"。</summary>
public enum LeftoverKind
{
    /// <summary>
    /// 目录里还留着卸载程序（unins000.exe / uninst.exe / uninstall.exe 等），
    /// 但"已安装软件"列表里没有任何条目认领这个目录 → 该软件已被卸载（或在别处卸载），文件留下了。
    /// <b>这是本工具置信度最高的信号</b>：卸载程序的存在本身就证明这里曾经安装过一个软件。
    /// </summary>
    OrphanedUninstallerDirectory,

    /// <summary>
    /// 失效启动项（目标文件已不存在）指向的<b>目录仍然存在</b>。
    /// 启动项是"这里装过东西"的铁证，而目标文件没了说明软件已被删除 → 该目录是残留。
    /// </summary>
    StartupTrailDirectory,

    /// <summary>
    /// 目录名与某个已卸载软件的名称匹配，但既没有卸载程序也没有启动项佐证。
    /// 置信度最低，仅作提示，<b>默认不进入自动清理计划</b>。
    /// </summary>
    NameMatchedDirectory,
}

/// <summary>残留判定的置信度。UI 按此分组与配色，清理计划只接受 High。</summary>
public enum LeftoverConfidence
{
    /// <summary>有卸载程序或注册表痕迹作为直接证据。</summary>
    High,

    /// <summary>有间接痕迹（例如目录名 + 时间），需用户确认。</summary>
    Medium,

    /// <summary>仅有名字相似，属于提示性质。</summary>
    Low,
}

/// <summary>
/// 一条残留记录：磁盘上一个"看起来属于已卸载软件"的目录。
///
/// 刻意做成<b>目录粒度</b>而不是文件粒度：用户要清理的是"某个软件的残留目录"，
/// 而不是一堆零散文件；目录也是回收站删除的最小可还原单位。
/// </summary>
public sealed record LeftoverItem
{
    /// <summary>稳定标识（就是归一化后的目录路径），便于 UI 去重与日志定位。</summary>
    public required string Id { get; init; }

    /// <summary>残留目录的绝对路径。</summary>
    public required string DirectoryPath { get; init; }

    public required LeftoverKind Kind { get; init; }

    public LeftoverConfidence Confidence { get; init; } = LeftoverConfidence.High;

    /// <summary>推测归属的软件名称（可能来自目录名、版本信息或启动项名称）。</summary>
    public string? SoftwareName { get; init; }

    /// <summary>目录内发现的卸载程序文件名（相对目录），用于向用户证明"这里确实装过东西"。</summary>
    public IReadOnlyList<string> UninstallerFiles { get; init; } = [];

    /// <summary>子树总计占用字节数。</summary>
    public long TotalBytes { get; init; }

    public int FileCount { get; init; }

    public int SubDirectoryCount { get; init; }

    /// <summary>子树内最新的写入时间；用于判断"是不是很久没动过了"。</summary>
    public DateTimeOffset? LastWriteTime { get; init; }

    /// <summary>判定依据（逐条可读文字），直接展示给用户，回答"凭什么说它是残留"。</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>通过安全闸门后才有值：不通过时为 null 且在 <see cref="BlockReason"/> 说明原因。</summary>
    public bool CanDelete => BlockReason is null;

    /// <summary>不能删除的原因（例如路径是系统目录、目录名受保护）。</summary>
    public string? BlockReason { get; init; }

    /// <summary>显示用的名称：优先推测的软件名，否则用目录名。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(SoftwareName)
        ? Path.GetFileName(DirectoryPath.TrimEnd(Path.DirectorySeparatorChar))
        : SoftwareName!;

    public override string ToString() => $"[{Kind}/{Confidence}] {DirectoryPath}";
}

/// <summary>一次残留扫描的进度。</summary>
public sealed record LeftoverScanProgress(string Stage, int Completed = 0, int Total = 0, string? Detail = null);

/// <summary>一次残留扫描的完整结果。</summary>
public sealed class LeftoverScanResult
{
    public IReadOnlyList<LeftoverItem> Items { get; init; } = [];

    /// <summary>实际使用的索引方式（"USN 索引"或"目录遍历回退"），必须如实告知用户。</summary>
    public string IndexDescription { get; init; } = "未知";

    /// <summary>降级说明：非空表示结果可能不完整（例如权限不足、某个卷枚举失败）。</summary>
    public string? DegradedReason { get; init; }

    public bool IsDegraded => !string.IsNullOrWhiteSpace(DegradedReason);

    public bool IsElevated { get; init; }

    public TimeSpan Duration { get; init; }

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>扫描的根目录，便于用户理解"扫了哪些地方"。</summary>
    public IReadOnlyList<string> ScannedRoots { get; init; } = [];

    public long TotalBytes => Items.Sum(i => i.TotalBytes);

    public int HighConfidenceCount => Items.Count(i => i.Confidence == LeftoverConfidence.High);

    /// <summary>可进入自动清理计划的条数（高置信度且通过安全闸门）。</summary>
    public int CleanableCount => Items.Count(i => i.Confidence == LeftoverConfidence.High && i.CanDelete);
}
