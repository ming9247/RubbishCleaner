using System.Diagnostics;

namespace RubbishCleaner.Common.Mft;

/// <summary>文件索引的可用状态。</summary>
public enum FileIndexAvailability
{
    /// <summary>索引可用。</summary>
    Available,

    /// <summary>需要管理员权限才能直读卷（USN/MFT）。</summary>
    NeedsElevation,

    /// <summary>切卷或查询失败。</summary>
    Failed,
}

/// <summary>索引中的一个文件。</summary>
public sealed record IndexedFile(string FullPath, long SizeBytes, DateTimeOffset LastWriteTime);

/// <summary>一个目录子树的大小统计。</summary>
public sealed record DirectoryStatistics(
    long TotalBytes,
    int FileCount,
    int DirectoryCount,
    DateTimeOffset? LastWriteTime)
{
    public static readonly DirectoryStatistics Empty = new(0, 0, 0, null);
}

/// <summary>
/// 卷级文件索引：全盘按文件名查找 + 目录子树统计。
///
/// <b>为什么需要这个抽象</b>：残留扫描要在几秒内回答"哪些目录里有卸载程序"，而
/// <c>Directory.GetFiles()</c> 递归遍历在整机上要几十秒。USN 枚举能把全盘 MFT 记录
/// 在 1.5 秒内读完（本机实测 C: 82.6 万条 / 1.5 秒，D: 53.1 万条 / 2.2 秒），
/// 但需要管理员权限，且实现较复杂。
///
/// 因此定义两套实现，Core 只依赖本接口：
/// <list type="bullet">
/// <item><see cref="UsnFileIndex"/>：管理员下直读卷（快，实测整机 &lt; 3 秒）</item>
/// <item><see cref="FallbackVolumeFileIndex"/>：目录遍历回退（标准用户可用，慢但结果一致）</item>
/// </list>
///
/// 两套实现必须给出<b>相同的结果</b>，否则安全判定会随权限漂移。
/// </summary>
public interface IVolumeFileIndex
{
    /// <summary>当前可用状态。</summary>
    FileIndexAvailability Availability { get; }

    /// <summary>不可用的原因（用于向用户解释，例如"需要管理员权限"）。</summary>
    string? UnavailableReason { get; }

    /// <summary>面向用户的索引描述，例如"USN 索引（C:）"或"目录遍历回退"。</summary>
    string Description { get; }

    /// <summary>
    /// 建立索引。返回 false 表示不可用（调用方应改用回退实现）。
    /// </summary>
    /// <param name="rootPaths">扫描根目录列表，实现据此推断需要索引哪些卷。</param>
    Task<bool> PrepareAsync(
        IReadOnlyCollection<string> rootPaths,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 在 <paramref name="directoryPath"/> 的<b>整棵子树</b>中查找文件名属于
    /// <paramref name="fileNames"/> 的文件（大小写不敏感）。
    ///
    /// 只返回目录内的文件，不返回目录自身；目录不存在或无权访问时返回空集合而不是抛异常。
    /// </summary>
    IReadOnlyList<IndexedFile> FindFiles(string directoryPath, IReadOnlySet<string> fileNames);

    /// <summary>统计目录子树的大小与文件数；失败时返回 <see cref="DirectoryStatistics.Empty"/>。</summary>
    DirectoryStatistics GetStatistics(string directoryPath);

    /// <summary>释放索引占用的内存（整机索引可达上百 MB）。</summary>
    void Release();
}

/// <summary>
/// 组合索引：优先用 <paramref name="primary"/>（通常是 USN），
/// 不可用时自动降级到 <paramref name="fallback"/>（目录遍历）。
///
/// 这样上层不需要关心"有没有管理员权限"，只需要关心结果，
/// 而"当前用的是哪种索引"通过 <see cref="Description"/> 如实呈现给用户——
/// 不能让用户以为标准用户下也做了全盘扫描。
/// </summary>
public sealed class CompositeVolumeFileIndex : IVolumeFileIndex
{
    private readonly IVolumeFileIndex _primary;
    private readonly IVolumeFileIndex _fallback;
    private IVolumeFileIndex _active;

    public CompositeVolumeFileIndex(IVolumeFileIndex primary, IVolumeFileIndex fallback)
    {
        _primary = primary;
        _fallback = fallback;
        _active = fallback;
    }

    public FileIndexAvailability Availability => _active.Availability;

    public string? UnavailableReason => _active.UnavailableReason;

    public string Description => _active.Description;

    /// <summary>实际生效的实现（用于测试与诊断）。</summary>
    public IVolumeFileIndex Active => _active;

    /// <summary>是否已降级到回退实现。</summary>
    public bool IsDegraded => !ReferenceEquals(_active, _primary);

    public async Task<bool> PrepareAsync(
        IReadOnlyCollection<string> rootPaths,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (await _primary.PrepareAsync(rootPaths, progress, cancellationToken).ConfigureAwait(false))
            {
                _active = _primary;
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 主索引失败不应让扫描失败：降级继续。
        }

        _active = _fallback;
        return await _fallback.PrepareAsync(rootPaths, progress, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<IndexedFile> FindFiles(string directoryPath, IReadOnlySet<string> fileNames)
        => _active.FindFiles(directoryPath, fileNames);

    public DirectoryStatistics GetStatistics(string directoryPath) => _active.GetStatistics(directoryPath);

    public void Release()
    {
        try
        {
            _primary.Release();
        }
        catch
        {
            // 忽略释放失败
        }

        try
        {
            _fallback.Release();
        }
        catch
        {
            // 忽略释放失败
        }
    }
}
