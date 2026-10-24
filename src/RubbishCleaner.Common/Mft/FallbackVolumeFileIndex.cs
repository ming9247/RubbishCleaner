namespace RubbishCleaner.Common.Mft;

/// <summary>
/// 目录遍历实现的文件索引：<b>不需要管理员权限</b>，结果是真值的兜底来源。
///
/// 速度远不如 USN 索引（整机约 10–40 秒），因此只在下列情况使用：
/// 1. 标准用户权限下（卷句柄 GENERIC_READ 会被拒绝，实测错误码 5）；
/// 2. USN 枚举失败（非 NTFS 卷等）。
///
/// 实现要点：
/// <list type="bullet">
/// <item>跳过重解析点（符号链接 / 目录联接），否则可能死循环；</item>
/// <item><c>IgnoreInaccessible</c> 打开，避免个别目录无权限导致整棵树失败；</item>
/// <item>任何异常都转成"空结果"，绝不向上抛——残留扫描的完整性由上层用降级标记表达。</item>
/// </list>
/// </summary>
public sealed class FallbackVolumeFileIndex : IVolumeFileIndex
{
    private static readonly EnumerationOptions SearchOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
    };

    public FileIndexAvailability Availability { get; private set; } = FileIndexAvailability.Available;

    public string? UnavailableReason { get; private set; }

    public string Description => "目录遍历回退（标准权限，速度较慢）";

    public Task<bool> PrepareAsync(
        IReadOnlyCollection<string> rootPaths,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Availability = FileIndexAvailability.Available;
        UnavailableReason = null;
        progress?.Report("使用目录遍历回退（无需管理员权限，速度较慢）");
        return Task.FromResult(true);
    }

    public IReadOnlyList<IndexedFile> FindFiles(string directoryPath, IReadOnlySet<string> fileNames)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || fileNames.Count == 0)
        {
            return [];
        }

        var results = new List<IndexedFile>();
        try
        {
            if (!Directory.Exists(directoryPath))
            {
                return results;
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false,
            };

            foreach (var file in Directory.EnumerateFiles(directoryPath, "*", options))
            {
                var name = Path.GetFileName(file);
                if (!fileNames.Contains(name))
                {
                    continue;
                }

                results.Add(ToIndexedFile(file));
            }
        }
        catch
        {
            // 目录不可访问：返回已找到的部分，不中断整轮扫描。
        }

        return results;
    }

    public DirectoryStatistics GetStatistics(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return DirectoryStatistics.Empty;
        }

        long bytes = 0;
        var files = 0;
        var dirs = 0;
        DateTimeOffset? lastWrite = null;

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false,
            };

            foreach (var path in Directory.EnumerateFileSystemEntries(directoryPath, "*", options))
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        dirs++;
                        continue;
                    }

                    files++;
                    if (info.Length > 0)
                    {
                        bytes += info.Length;
                    }

                    var write = info.LastWriteTimeUtc;
                    if (lastWrite is null || write > lastWrite.Value.UtcDateTime)
                    {
                        lastWrite = new DateTimeOffset(write, TimeSpan.Zero);
                    }
                }
                catch
                {
                    // 单个条目失败不影响统计
                }
            }
        }
        catch
        {
            // 忽略：返回已统计的部分
        }

        return new DirectoryStatistics(bytes, files, dirs, lastWrite);
    }

    public void Release()
    {
        // 无缓存可释放
    }

    private static IndexedFile ToIndexedFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return new IndexedFile(
                info.FullName,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        }
        catch
        {
            return new IndexedFile(path, 0, DateTimeOffset.MinValue);
        }
    }
}
