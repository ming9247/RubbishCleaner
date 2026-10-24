using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace RubbishCleaner.Common.Mft;

/// <summary>
/// 管理员权限下的卷级文件索引：用 <c>FSCTL_ENUM_USN_DATA</c> 直接枚举卷上的 MFT 记录，
/// 建立 FRN（文件引用号）→（父 FRN，文件名）的映射，从而把"整机按名字找文件"
/// 从几十秒降到几秒。
///
/// <b>为什么要直读卷</b>：残留扫描要对整机每个候选目录回答"子树里有没有 unins000.exe"，
/// 用 <c>Directory.GetFiles()</c> 递归要几十秒。USN 枚举一趟就能把全卷目录项读进内存
/// （本机实测 C: 82.6 万条 / 1.5 秒，D: 53.1 万条 / 2.2 秒），之后再查只是内存扫描。
///
/// <b>权限边界</b>：打开卷句柄需要 GENERIC_READ，标准用户下会被拒绝（错误码 5）。
/// 这种情况<b>不抛异常</b>，而是把 <see cref="Availability"/> 置为
/// <see cref="FileIndexAvailability.NeedsElevation"/>，由
/// <see cref="CompositeVolumeFileIndex"/> 自动降级到 <see cref="FallbackVolumeFileIndex"/>。
/// 扫描结果不能因为权限不同而消失，只能变慢——所以这里所有失败路径都必须返回状态而不是异常。
///
/// <b>内存策略</b>：USN_RECORD 有 83 万条，如果每条都建对象，光对象头就要几十 MB。
/// 因此只用四条与记录数等长的紧凑数组（名字引用、自身 FRN、父 FRN、是否目录），
/// 外加一个<b>只覆盖目录</b>的 FRN→下标字典：路径回溯链上只有目录节点，
/// 所以不需要全量字典。整卷折合约 40–70 MB，<see cref="Release"/> 一次性丢弃。
///
/// <b>为什么不存文件大小与时间</b>：USN_RECORD 里根本没有这两个字段。
/// <see cref="FindFiles"/> 的命中数通常只有几十个，逐个 <see cref="FileInfo"/> 补齐即可；
/// <see cref="GetStatistics"/> 则退化成目录遍历——调用方只对少量候选目录调用，
/// 用准确的时间和大小换掉"索引里造假数据"。
/// </summary>
public sealed class UsnFileIndex : IVolumeFileIndex
{
    /// <summary>
    /// FSCTL_ENUM_USN_DATA：按 FRN 顺序枚举卷上仍然存在的 MFT 记录。
    /// 选它而不是 FSCTL_QUERY_USN_JOURNAL 的原因：只要枚举结果，不依赖 USN 日志是否开启。
    /// </summary>
    private const uint FsctlEnumUsnData = 0x000900B3;

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;

    /// <summary>打开目录/卷句柄必须带此标志，否则 CreateFileW 对目录直接失败。</summary>
    private const uint FileFlagBackupSemantics = 0x02000000;

    private const int ErrorAccessDenied = 5;
    private const int ErrorNotSupported = 50;
    /// <summary>枚举结束的正常信号：没有更多记录（不是错误）。</summary>
    private const int ErrorHandleEof = 38;

    private const uint FileAttributeDirectory = 0x10;

    /// <summary>
    /// 卷根目录在 NTFS 上的 MFT 记录号固定为 5，回溯到它就说明已经到顶。
    ///
    /// 注意：USN_RECORD 里的 FileReferenceNumber 是 64 位"序号 + 记录号"打包值
    /// （高 16 位序号、低 48 位记录号），根目录实测是 0x0005000000000005 而不是 5。
    /// 直接拿 0x0005_0000_0000_0005 和 5 比较会永远不相等——父链永远到不了顶，
    /// 结果就是"每条记录都被当成断链丢弃"或者（更糟）拼出错误的卷根路径。必须掩码后比较。
    /// </summary>
    private const ulong RootFileReferenceNumber = 5;

    /// <summary>FRN 的低 48 位才是 MFT 记录号，高 16 位是会被复用的序号。</summary>
    private const ulong FileReferenceNumberIndexMask = 0x0000_FFFF_FFFF_FFFF;

    /// <summary>USN_RECORD 头固定 60 字节（V2），其后才是文件名。</summary>
    private const int RecordHeaderSize = 60;

    /// <summary>
    /// 1 MB 输出缓冲：实测比 64 KB 快约 30%，又不会像"一次性全量分配"那样把内存打爆。
    /// 复用同一块缓冲，逐块解析，避免整盘数组的峰值。
    /// </summary>
    private const int EnumBufferSize = 1024 * 1024;

    /// <summary>MFT_ENUM_DATA_V0 的长度：StartFileReferenceNumber + LowUsn + HighUsn。</summary>
    private const int EnumInputSize = 24;

    /// <summary>路径回溯的深度上限，防止索引损坏（父子成环）时死循环。</summary>
    private const int MaxPathDepth = 256;

    /// <summary>单个文件允许的硬链接名上限；正常文件 1 条，WinSxS 里最多几条。</summary>
    private const int MaxHardLinkCount = 64;

    private static readonly IntPtr InvalidHandleValue = new(-1);

    private volatile Dictionary<char, VolumeIndex>? _index;
    private volatile string _description = "USN 索引（未建立）";

    public FileIndexAvailability Availability { get; private set; } = FileIndexAvailability.Failed;

    /// <summary>不可用原因（面向用户，例如"需要管理员权限"）。</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>面向用户的索引描述，例如"USN 索引（C:、D:）"。</summary>
    public string Description => _description;

    public Task<bool> PrepareAsync(
        IReadOnlyCollection<string> rootPaths,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 枚举是纯同步的阻塞 IO，包一层 Task.Run 让 UI 线程不被卡住；
        // 顺带把 CancellationToken 传进去，取消时在块边界退出。
        return Task.Run(() => Prepare(rootPaths, progress, cancellationToken), cancellationToken);
    }

    public IReadOnlyList<IndexedFile> FindFiles(string directoryPath, IReadOnlySet<string> fileNames)
    {
        var results = new List<IndexedFile>();
        if (string.IsNullOrWhiteSpace(directoryPath) || fileNames is null || fileNames.Count == 0)
        {
            return results;
        }

        var index = _index;
        if (index is null || index.Count == 0)
        {
            // 已 Release 或从未 Prepare：按契约返回空集合，不抛异常。
            return results;
        }

        var normalized = NormalizeDirectoryPath(directoryPath);
        if (normalized is null)
        {
            return results;
        }

        var volume = FindVolume(index, normalized);
        if (volume is null)
        {
            return results;
        }

        // 调用方给的大小写约定不可靠（可能用 Ordinal 建的集合），这里统一成大小写不敏感，
        // 因为 Windows 文件名本来就大小写不敏感。
        var wanted = new HashSet<string>(fileNames, StringComparer.OrdinalIgnoreCase);
        var directoryPrefix = normalized.EndsWith(Path.DirectorySeparatorChar) ? normalized : normalized + Path.DirectorySeparatorChar;

        // 同一批命中往往集中在少数几个目录，缓存父目录的绝对路径，省掉重复回溯。
        // 值为 null 表示"这条父链拼不全"，也缓存起来，避免同名断链反复回溯。
        var directoryCache = new Dictionary<ulong, string?>(64);

        var names = volume.Names;
        var parentFrns = volume.ParentFrns;
        var isDirectory = volume.IsDirectory;

        for (var i = 0; i < volume.Count; i++)
        {
            if (isDirectory[i] || !wanted.Contains(names[i]))
            {
                continue;
            }

            string? directoryFullPath;
            try
            {
                directoryFullPath = ResolveDirectoryPath(volume, parentFrns[i], directoryCache);
            }
            catch
            {
                // 单条记录异常（理论上不会发生）只丢这一条，绝不打断整轮扫描。
                continue;
            }

            if (directoryFullPath is null)
            {
                continue;
            }

            var fullPath = directoryFullPath.EndsWith(Path.DirectorySeparatorChar)
                ? directoryFullPath + names[i]
                : directoryFullPath + Path.DirectorySeparatorChar + names[i];

            // USN 枚举对每个 MFT 记录只回一个名字，而 NTFS 硬链接是"一个记录、多个目录项"。
            // 实测 C:\Windows\System32\notepad.exe 与 C:\Windows\WinSxS\...\notepad.exe 是同一条记录，
            // 索引里只保留了 WinSxS 那个名字，于是扫描 System32 会漏报——回退实现却能看到它，
            // 两套结果就随权限漂移了。
            //
            // 注意这里不能先按"命中项自己是否在目标子树内"过滤再补全：
            // 指向目标子树的那个链接名，恰恰不在索引里。必须先把硬链接名全部展开，再对每个名字判前缀。
            foreach (var candidate in EnumerateHardLinkPaths(volume.VolumeRoot, fullPath))
            {
                var candidateDirectory = Path.GetDirectoryName(candidate);
                if (string.IsNullOrEmpty(candidateDirectory) || !IsSameOrUnder(candidateDirectory, normalized, directoryPrefix))
                {
                    continue;
                }

                // 索引里可能有刚被删除但记录还没回收的项。回退实现遍历的是活文件系统，
                // 为了让两套实现结果一致，这里也只返回真实存在的文件。
                try
                {
                    var info = new FileInfo(candidate);
                    if (!info.Exists)
                    {
                        continue;
                    }

                    results.Add(new IndexedFile(
                        candidate,
                        info.Length,
                        new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
                }
                catch
                {
                    // 无权限读取单个文件元数据：跳过，避免给出 0 字节的假信息。
                }
            }
        }

        return results;
    }

    public DirectoryStatistics GetStatistics(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return DirectoryStatistics.Empty;
        }

        // USN_RECORD 不含大小与时间戳，因此统计退化成目录遍历。
        // 性能可以接受：调用方只对少数候选目录调用，不是整机全扫。
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
                // 跳过符号链接/目录联接，否则统计会沿链接跑到别的目录甚至死循环。
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false,
            };

            // 用 EnumerateFileSystemInfos 而不是 EnumerateFileSystemEntries + new FileInfo：
            // 前者由 FindFirstFile/FindNextFile 一次性带回大小与时间戳，后者每一条都要额外查一次元数据。
            // Program Files（14.9 万条）上实测从 8.8 秒降到约 1 秒量级。
            foreach (var info in new DirectoryInfo(directoryPath).EnumerateFileSystemInfos("*", options))
            {
                try
                {
                    if (info.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        dirs++;
                        continue;
                    }

                    files++;
                    var length = info is FileInfo file ? file.Length : 0;
                    if (length > 0)
                    {
                        bytes += length;
                    }

                    var write = info.LastWriteTimeUtc;
                    if (lastWrite is null || write > lastWrite.Value.UtcDateTime)
                    {
                        lastWrite = new DateTimeOffset(write, TimeSpan.Zero);
                    }
                }
                catch
                {
                    // 单个条目读不到元数据：跳过，返回已统计的部分。
                }
            }
        }
        catch
        {
            // 整棵子树不可访问：返回已统计的部分。
        }

        return new DirectoryStatistics(bytes, files, dirs, lastWrite);
    }

    public void Release()
    {
        // 只丢引用，让 GC 一次性回收几条大数组；比逐条清理快几个数量级。
        // 注意：这里只清数据、不"钉死"状态——残留扫描每轮结束都会 Release，
        // 下一轮会重新 Prepare。若在此把状态钉成不可用，上层就再也拿不回快路径了。
        _index = null;
        Availability = FileIndexAvailability.Failed;
        UnavailableReason = "USN 索引尚未建立（已释放），下一轮扫描会重新建立";
        _description = "USN 索引（未建立）";
    }

    private bool Prepare(
        IReadOnlyCollection<string>? rootPaths,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            Release();

            var volumes = CollectVolumeLetters(rootPaths);
            if (volumes.Count == 0)
            {
                Availability = FileIndexAvailability.Failed;
                UnavailableReason = "没有可用于 USN 枚举的本地磁盘根目录";
                return false;
            }

            var index = new Dictionary<char, VolumeIndex>(volumes.Count);
            var unavailableReasons = new List<string>();
            var elevated = 0;
            var totalMs = 0.0;

            foreach (var letter in volumes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"正在枚举 {letter}: 的 MFT 记录…");

                var watch = Stopwatch.StartNew();
                var outcome = TryScanVolume(letter, out var volume, out var reason);
                watch.Stop();

                if (outcome == FileIndexAvailability.Available && volume is not null)
                {
                    index[letter] = volume;
                    totalMs += watch.Elapsed.TotalMilliseconds;
                    progress?.Report(string.Format(
                        CultureInfo.InvariantCulture,
                        "USN 索引：{0}: 共 {1} 条记录，耗时 {2:F1} 秒",
                        letter,
                        volume.Count,
                        watch.Elapsed.TotalSeconds));
                    continue;
                }

                if (outcome == FileIndexAvailability.NeedsElevation)
                {
                    elevated++;
                }

                if (!string.IsNullOrEmpty(reason))
                {
                    unavailableReasons.Add(string.Format(CultureInfo.InvariantCulture, "{0}: {1}", letter, reason));
                }
            }

            if (index.Count > 0)
            {
                // 多卷时把已索引的盘符与耗时都写进描述：上层会把它直接展示给用户，
                // 用来解释"这次扫得快是因为直读卷索引"，而不是让用户猜。
                var letters = string.Join("、", index.Keys.OrderBy(c => c).Select(c => c + ":"));
                _description = string.Format(
                    CultureInfo.InvariantCulture,
                    "USN 索引（{0}，约 {1:F1} 秒）",
                    letters,
                    totalMs / 1000.0);
                _index = index;
                Availability = FileIndexAvailability.Available;
                UnavailableReason = unavailableReasons.Count > 0 ? string.Join("；", unavailableReasons) : null;
                return true;
            }

            _index = null;
            if (elevated > 0)
            {
                Availability = FileIndexAvailability.NeedsElevation;
                UnavailableReason = "读取卷需要管理员权限（USN 索引），已改用目录遍历";
            }
            else
            {
                Availability = FileIndexAvailability.Failed;
                UnavailableReason = unavailableReasons.Count > 0
                    ? string.Join("；", unavailableReasons)
                    : "USN 枚举失败";
            }

            _description = elevated > 0 ? "USN 索引（需要管理员权限）" : "USN 索引（不可用）";
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 任何未预料的失败都降级，绝不让索引问题把整轮扫描打掉。
            _index = null;
            Availability = FileIndexAvailability.Failed;
            UnavailableReason = $"USN 索引建立失败：{ex.Message}";
            _description = "USN 索引（建立失败）";
            return false;
        }
    }

    /// <summary>
    /// 打开卷句柄并完整枚举一遍。失败通过返回值 + <paramref name="reason"/> 表达，不抛异常。
    /// </summary>
    private static FileIndexAvailability TryScanVolume(char driveLetter, out VolumeIndex? index, out string? reason)
    {
        index = null;
        reason = null;

        var devicePath = $"\\\\.\\{driveLetter}:";
        var handle = CreateFileW(
            devicePath,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle == InvalidHandleValue)
        {
            var error = Marshal.GetLastWin32Error();

            // 实测标准用户在这里拿到 5；必须如实上报"需要提权"，而不是伪装成枚举失败。
            if (error == ErrorAccessDenied)
            {
                reason = "读取卷需要管理员权限";
                return FileIndexAvailability.NeedsElevation;
            }

            reason = error == ErrorNotSupported
                ? $"卷 {driveLetter}: 不是 NTFS，无法使用 USN 枚举"
                : $"CreateFileW({devicePath}) 失败，Win32 错误码 {error}";
            return FileIndexAvailability.Failed;
        }

        try
        {
            var records = new VolumeRecordBuffer();
            if (!EnumerateRecords(handle, records, out var errorCode))
            {
                reason = $"DeviceIoControl(FSCTL_ENUM_USN_DATA) 失败，Win32 错误码 {errorCode}";
                return FileIndexAvailability.Failed;
            }

            // "\\.\C:" 对应的索引根是 "C:\"，后面拼接路径统一以它开头。
            index = records.Build(driveLetter + ":\\");
            return FileIndexAvailability.Available;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return FileIndexAvailability.Failed;
        }
        finally
        {
            // 句柄必须无条件关闭，否则枚举失败时每卷泄漏一个句柄。
            _ = CloseHandle(handle);
        }
    }

    /// <summary>
    /// 逐块调用 DeviceIoControl 直到 ERROR_HANDLE_EOF（没有更多记录）。
    /// 返回 false 表示真的失败，<paramref name="errorCode"/> 为 Win32 错误码。
    /// </summary>
    private static bool EnumerateRecords(IntPtr handle, VolumeRecordBuffer records, out int errorCode)
    {
        errorCode = 0;

        var input = new byte[EnumInputSize];
        var output = new byte[EnumBufferSize];
        var nextStart = 0UL;
        var span = input.AsSpan();

        while (true)
        {
            // MFT_ENUM_DATA_V0：offset0=起始 FRN、offset8=LowUsn、offset16=HighUsn。
            // HighUsn 必须是 long.MaxValue；用 ulong.MaxValue 会立刻拿到 err=38 且 0 条记录（实测）。
            span.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(span, nextStart);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(8), 0);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(16), long.MaxValue);

            if (!DeviceIoControl(
                    handle,
                    FsctlEnumUsnData,
                    input,
                    (uint)input.Length,
                    output,
                    (uint)output.Length,
                    out var returned,
                    IntPtr.Zero))
            {
                var code = Marshal.GetLastWin32Error();
                if (code == ErrorHandleEof)
                {
                    return true;
                }

                errorCode = code;
                return false;
            }

            var returnedBytes = (int)returned;
            if (returnedBytes < 8)
            {
                // 只有"下一次起点"，说明本轮没有任何记录。
                return true;
            }

            // 输出缓冲区前 8 字节是下一轮要用的起始 FRN。
            nextStart = BinaryPrimitives.ReadUInt64LittleEndian(output);
            ParseRecords(output, returnedBytes, records);
        }
    }

    /// <summary>
    /// 解析一块输出缓冲里的 USN_RECORD 链。字段偏移见任务实测：RecordLength@0、
    /// FileReferenceNumber@8、ParentFileReferenceNumber@16、FileAttributes@52、
    /// FileNameLength@56、FileNameOffset@58，名字为 UTF-16LE。
    /// </summary>
    private static void ParseRecords(byte[] buffer, int returnedBytes, VolumeRecordBuffer records)
    {
        var offset = 8; // 跳过"下一次起始 FRN"
        while (offset + RecordHeaderSize <= returnedBytes)
        {
            var recordLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
            if (recordLength < RecordHeaderSize || offset + recordLength > returnedBytes)
            {
                // 数据损坏（理论上不可能）：停止解析这一块，不要把垃圾当文件名。
                return;
            }

            var fileReference = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset + 8));

            // 跳过卷根与 NTFS 元数据（$MFT、$Extend 等以 $ 开头）。它们既不是用户文件，
            // 参与路径回溯还会把 "$Extend\$ObjId" 之类的内部目录混进结果。
            if ((fileReference & FileReferenceNumberIndexMask) != RootFileReferenceNumber)
            {
                var attributes = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 52));
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 56));
                var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 58));

                if (nameLength > 0 && nameOffset + nameLength <= recordLength)
                {
                    var name = Encoding.Unicode.GetString(buffer, offset + nameOffset, nameLength);
                    if (name.Length > 0 && name[0] != '$')
                    {
                        var parentReference = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset + 16));
                        records.Add(name, fileReference, parentReference, (attributes & FileAttributeDirectory) != 0);
                    }
                }
            }

            offset += recordLength;
        }
    }

    /// <summary>
    /// 回溯目录的绝对路径。只对"目录 FRN"查字典，因为父链上只可能走到目录。
    /// 找不到父节点时停止回溯（说明父目录记录已不存在或超出枚举范围），
    /// 宁可给出一条拼不全的路径让上层前缀匹配失败，也不要瞎猜。
    /// </summary>
    private static string? ResolveDirectoryPath(VolumeIndex volume, ulong directoryFr, Dictionary<ulong, string?> cache)
    {
        if ((directoryFr & FileReferenceNumberIndexMask) == RootFileReferenceNumber)
        {
            return volume.VolumeRoot;
        }

        if (cache.TryGetValue(directoryFr, out var cached))
        {
            return cached;
        }

        List<string>? segments = null;
        var current = directoryFr;
        var reachedRoot = false;
        for (var depth = 0; depth < MaxPathDepth; depth++)
        {
            if (!volume.DirectoryLookup.TryGetValue(current, out var recordIndex))
            {
                // 父目录不在索引里（父链断裂或成环）。
                break;
            }

            (segments ??= new List<string>(8)).Add(volume.Names[recordIndex]);
            current = volume.ParentFrns[recordIndex];
            if ((current & FileReferenceNumberIndexMask) == RootFileReferenceNumber)
            {
                reachedRoot = true;
                break;
            }
        }

        if (!reachedRoot)
        {
            // 关键：拼不完整的路径必须整条丢弃，绝不能拼成"卷根下的 XXX"。
            // 否则扫描 "C:\" 时这些孤儿记录会变成假阳性——残留扫描报错文件比漏报更伤用户信任。
            cache[directoryFr] = null;
            return null;
        }

        var builder = new StringBuilder(volume.VolumeRoot, volume.VolumeRoot.Length + 64);
        if (segments is not null)
        {
            for (var i = segments.Count - 1; i >= 0; i--)
            {
                builder.Append(segments[i]);
                if (i > 0)
                {
                    builder.Append(Path.DirectorySeparatorChar);
                }
            }
        }

        var path = builder.ToString();
        cache[directoryFr] = path;
        return path;
    }

    /// <summary>
    /// 枚举一个文件的全部硬链接路径（含它自己），并统一成"卷根 + 相对路径"的绝对形式。
    ///
    /// <b>为什么需要</b>：<c>FSCTL_ENUM_USN_DATA</c> 是按 MFT 记录枚举的，一条记录只带一个
    /// 文件名；而硬链接是"一条记录、多个目录项"。实测 <c>C:\Windows\System32\notepad.exe</c>、
    /// <c>C:\Windows\notepad.exe</c> 与 WinSxS 里的那份是同一个记录，索引里只能看到 WinSxS 那条，
    /// 于是 System32 目录会漏报。回退实现看不到这个差别，两套结果就会随权限漂移。
    ///
    /// <b>为什么代价可以接受</b>：只有文件名命中集合的文件才会走到这里，通常几十个；
    /// 每个文件一次 <c>FindFirstFileNameW</c> 调用，毫秒级。
    ///
    /// 失败时退回只返回文件自身，绝不因为补全失败而丢掉已经正确的命中。
    /// </summary>
    private static List<string> EnumerateHardLinkPaths(string volumeRoot, string fullPath)
    {
        var paths = new List<string>(4);
        var rootWithoutSeparator = volumeRoot.TrimEnd(Path.DirectorySeparatorChar);

        IntPtr handle = IntPtr.Zero;
        try
        {
            // 注意：FindFirstFileNameW 会直接把第一个链接名写进缓冲区（实测行为），
            // 它不是"只探测长度"——当成仅探测长度会静默丢掉一条链接名。
            var buffer = new char[1024];
            var firstSize = (uint)buffer.Length;
            handle = FindFirstFileNameW(fullPath, 0, ref firstSize, buffer);
            if (handle != InvalidHandleValue && handle != IntPtr.Zero)
            {
                AddLinkPath(paths, buffer, rootWithoutSeparator);

                for (var guard = 0; guard < MaxHardLinkCount; guard++)
                {
                    var size = (uint)buffer.Length;
                    if (!FindNextFileNameW(handle, ref size, buffer))
                    {
                        break;
                    }

                    AddLinkPath(paths, buffer, rootWithoutSeparator);
                }
            }
        }
        catch
        {
            // 查询硬链接名失败不影响主流程，走下面的兜底。
        }
        finally
        {
            if (handle != IntPtr.Zero && handle != InvalidHandleValue)
            {
                _ = FindClose(handle);
            }
        }

        // 查询失败（无权限、卷不支持）时至少返回索引里已经拼出来的那条路径，
        // 绝不能因为补全失败而丢掉本来就正确的命中。
        if (paths.Count == 0)
        {
            paths.Add(fullPath);
        }

        return paths;
    }

    /// <summary>
    /// 把 <c>FindFirstFileNameW</c> / <c>FindNextFileNameW</c> 写进缓冲区的相对路径拼成带盘符的绝对路径。
    ///
    /// 返回值形如 <c>\Windows\System32\notepad.exe</c>：没有盘符、以反斜杠开头，
    /// 因此要把卷根末尾的分隔符去掉再拼，否则会得到 <c>C:\\Windows</c> 这种非法路径。
    /// </summary>
    private static void AddLinkPath(List<string> paths, char[] buffer, string rootWithoutSeparator)
    {
        var length = Array.IndexOf(buffer, '\0');
        if (length <= 0)
        {
            return;
        }

        var relative = new string(buffer, 0, length);
        if (relative[0] != Path.DirectorySeparatorChar)
        {
            return;
        }

        var absolute = rootWithoutSeparator + relative;
        if (!paths.Contains(absolute, StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(absolute);
        }
    }

    /// <summary>判断绝对目录路径是否等于目标目录或位于其下（大小写不敏感）。</summary>
    private static bool IsSameOrUnder(string directoryFullPath, string normalizedTarget, string targetPrefix)
    {
        if (directoryFullPath.Length == normalizedTarget.Length)
        {
            return string.Equals(directoryFullPath, normalizedTarget, StringComparison.OrdinalIgnoreCase);
        }

        return directoryFullPath.Length > normalizedTarget.Length
            && directoryFullPath.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeDirectoryPath(string directoryPath)
    {
        try
        {
            var full = Path.GetFullPath(directoryPath);
            // 去掉尾部反斜杠便于前缀比较；卷根（"C:\"）保留原样。
            if (full.Length > 3)
            {
                full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return full;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>在已索引的卷里找 <paramref name="fullPath"/> 所属的那一个。</summary>
    private static VolumeIndex? FindVolume(Dictionary<char, VolumeIndex> index, string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
        {
            return null;
        }

        return index.TryGetValue(char.ToUpperInvariant(root[0]), out var volume) ? volume : null;
    }

    /// <summary>从扫描根目录推断需要索引哪些卷（去重、只保留本地盘）。</summary>
    private static List<char> CollectVolumeLetters(IReadOnlyCollection<string>? rootPaths)
    {
        var letters = new List<char>();
        if (rootPaths is null)
        {
            return letters;
        }

        foreach (var rootPath in rootPaths)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                continue;
            }

            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(rootPath));
                // UNC 路径（\\server\share）长度会大于 3，没有可直读的卷句柄，直接跳过。
                if (string.IsNullOrEmpty(root) || root.Length > 3 || root[1] != ':')
                {
                    continue;
                }

                var letter = char.ToUpperInvariant(root[0]);
                if (!letters.Contains(letter))
                {
                    letters.Add(letter);
                }
            }
            catch
            {
                // 非法路径：跳过，不影响其它卷。
            }
        }

        letters.Sort();
        return letters;
    }

    /// <summary>
    /// 一次枚举的原始记录缓冲：四条等长数组按需扩容，避免 List&lt;T&gt; 的额外容量浪费。
    /// </summary>
    private sealed class VolumeRecordBuffer
    {
        private const int InitialCapacity = 1 << 16;

        private string[] _names = new string[InitialCapacity];
        private ulong[] _fileReferenceNumbers = new ulong[InitialCapacity];
        private ulong[] _parentFileReferenceNumbers = new ulong[InitialCapacity];
        private bool[] _isDirectory = new bool[InitialCapacity];

        public int Count { get; private set; }

        public void Add(string name, ulong fileReference, ulong parentReference, bool isDirectory)
        {
            if (Count == _names.Length)
            {
                var capacity = _names.Length * 2;
                Array.Resize(ref _names, capacity);
                Array.Resize(ref _fileReferenceNumbers, capacity);
                Array.Resize(ref _parentFileReferenceNumbers, capacity);
                Array.Resize(ref _isDirectory, capacity);
            }

            _names[Count] = name;
            _fileReferenceNumbers[Count] = fileReference;
            _parentFileReferenceNumbers[Count] = parentReference;
            _isDirectory[Count] = isDirectory;
            Count++;
        }

        /// <summary>裁剪到精确长度并建立"仅目录"的 FRN→下标字典，然后交给 <see cref="VolumeIndex"/>。</summary>
        public VolumeIndex Build(string volumeRoot)
        {
            var count = Count;
            Array.Resize(ref _names, count);
            Array.Resize(ref _parentFileReferenceNumbers, count);
            Array.Resize(ref _isDirectory, count);

            var directoryLookup = new Dictionary<ulong, int>(Math.Max(16, count / 8));
            for (var i = 0; i < count; i++)
            {
                if (_isDirectory[i])
                {
                    // TryAdd：极少数目录硬链接会出现重复 FRN，保留第一条即可。
                    _ = directoryLookup.TryAdd(_fileReferenceNumbers[i], i);
                }
            }

            // 自身 FRN 只在建字典时用得到，这里主动释放，省下每卷约 6.6 MB。
            _fileReferenceNumbers = [];

            return new VolumeIndex(volumeRoot, _names, _parentFileReferenceNumbers, _isDirectory, directoryLookup, count);
        }
    }

    /// <summary>单个卷的只读索引（建好后不再修改，因此并发 FindFiles 是安全的）。</summary>
    private sealed class VolumeIndex(
        string volumeRoot,
        string[] names,
        ulong[] parentFrns,
        bool[] isDirectory,
        Dictionary<ulong, int> directoryLookup,
        int count)
    {
        public string VolumeRoot { get; } = volumeRoot;

        public string[] Names { get; } = names;

        public ulong[] ParentFrns { get; } = parentFrns;

        public bool[] IsDirectory { get; } = isDirectory;

        public Dictionary<ulong, int> DirectoryLookup { get; } = directoryLookup;

        public int Count { get; } = count;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        uint nInBufferSize,
        byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// 开始枚举硬链接名；第一个链接名会直接写进 <paramref name="linkName"/>，
    /// 返回的句柄随后用 <see cref="FindNextFileNameW"/> 继续枚举。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindFirstFileNameW(
        string lpFileName,
        uint dwFlags,
        ref uint stringLength,
        [Out] char[] linkName);

    /// <summary>取下一个硬链接名；没有更多链接时返回 false。</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileNameW(IntPtr hFindStream, ref uint stringLength, [Out] char[] linkName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr hFindFile);
}
