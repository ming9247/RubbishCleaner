using RubbishCleaner.Common.Mft;
using RubbishCleaner.Core.Leftover;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Residue;

/// <summary>
/// 卸载后<b>定向</b>残留扫描（v0.3.1）。
///
/// 与 v0.3.0 的 <see cref="LeftoverScanner"/> 的本质区别：
/// <list type="bullet">
/// <item><b>通用扫描</b>不知道用户卸过什么，只能靠"孤儿卸载程序 / 失效启动项"这些硬痕迹去推断；</item>
/// <item><b>定向扫描</b>知道"刚刚卸的是 X，它装在 Y，卸载前有这些文件"，
/// 于是能给出确定性的残留清单。</item>
/// </list>
///
/// 三步走（每一步都可能有结果，互不依赖）：
/// <list type="number">
/// <item>安装目录里<b>哪些文件还在</b>——卸载器没删干净的具体文件；</item>
/// <item>安装目录<b>本身还在不在</b>——很多卸载器只删文件不删目录；</item>
/// <item>按软件名在<b>常见数据位置</b>找同名目录——%AppData%、%ProgramData% 等。</item>
/// </list>
/// </summary>
public sealed class UninstallResidueScanner
{
    private readonly IVolumeFileIndex _index;
    private readonly ILeftoverEnvironment _environment;

    public UninstallResidueScanner(IVolumeFileIndex? index = null, ILeftoverEnvironment? environment = null)
    {
        _index = index ?? new CompositeVolumeFileIndex(
            new UsnFileIndex(),
            new FallbackVolumeFileIndex());
        _environment = environment ?? new RealLeftoverEnvironment();
    }

    /// <summary>
    /// 对一份"卸载前快照"做定向扫描。
    ///
    /// <paramref name="snapshot"/> 由 <see cref="UninstallSnapshotStore.TakeSnapshot"/>
    /// 在卸载前采集，是整轮判定的<b>基准事实</b>。
    /// </summary>
    public async Task<UninstallResidueResult> ScanAsync(
        UninstallSnapshot snapshot,
        UninstallResidueOptions? options = null,
        IProgress<LeftoverScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= new UninstallResidueOptions();

        // 卸载器常常是异步的：主进程退出了，后台还在删文件、写日志。
        // 立即扫会把"正在被删除的文件"当成残留报出来，等一等更准。
        if (options.SettleDelaySeconds > 0)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.SettleDelaySeconds), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        var installLocation = snapshot.InstallLocation;

        // 没有安装位置（很多按用户安装的软件不在注册表写 InstallLocation）时，
        // 第 1、2 步无从做起，但**第 3 步「按软件名找数据目录」不需要安装位置**，
        // 必须照常执行——否则 %AppData% / %LocalAppData% 下的残留永远不会被扫到（v0.9.6 真机反馈）。
        var hasInstallLocation = !string.IsNullOrWhiteSpace(installLocation);
        var root = hasInstallLocation
            ? installLocation!.TrimEnd(Path.DirectorySeparatorChar)
            : null;

        if (!hasInstallLocation)
        {
            var nameOnly = new List<LeftoverItem>();
            progress?.Report(new LeftoverScanProgress("正在常见位置查找同名目录…"));
            await AppendNameMatchedDirectoriesAsync(
                    nameOnly, snapshot, options, installRoot: null, progress, cancellationToken)
                .ConfigureAwait(false);

            return new UninstallResidueResult
            {
                SoftwareName = snapshot.SoftwareName,
                RemainingFiles = [],
                Directories = nameOnly,
                RemovedCount = 0,
                SnapshotCount = snapshot.Entries.Count,
            };
        }

        // ---------- 第 1 步：安装目录里哪些文件还在 ----------
        progress?.Report(new LeftoverScanProgress("正在核对卸载前的文件…"));
        var remaining = new List<string>();
        var removed = 0;

        foreach (var entry in snapshot.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.IsDirectory)
            {
                continue;
            }

            var absolute = Path.Combine(root!, entry.RelativePath);

            bool exists;
            try
            {
                exists = _environment.DirectoryExists(Path.GetDirectoryName(absolute) ?? root!)
                         && File.Exists(absolute);
            }
            catch
            {
                exists = false;
            }

            if (exists)
            {
                remaining.Add(absolute);
            }
            else
            {
                removed++;
            }
        }

        // ---------- 第 2 步：安装目录本身还在不在 ----------
        progress?.Report(new LeftoverScanProgress("正在检查安装目录是否被删除…"));
        var directories = new List<LeftoverItem>();

        if (_environment.DirectoryExists(root!))
        {
            directories.Add(BuildInstallDirectoryItem(root!, snapshot));
        }

        // ---------- 第 3 步：按软件名扫常见数据位置 ----------
        progress?.Report(new LeftoverScanProgress("正在常见位置查找同名目录…"));
        await AppendNameMatchedDirectoriesAsync(
                directories, snapshot, options, root, progress, cancellationToken)
            .ConfigureAwait(false);

        // 去重：第 2 步与第 3 步可能命中同一个目录（例如软件就装在 %AppData% 下）。
        var unique = directories
            .GroupBy(d => d.DirectoryPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        return new UninstallResidueResult
        {
            SoftwareName = snapshot.SoftwareName,
            RemainingFiles = remaining,
            Directories = unique,
            RemovedCount = removed,
            SnapshotCount = snapshot.Entries.Count,
        };
    }

    /// <summary>
    /// 把"安装目录还在"包装成一条残留项。
    ///
    /// 这类残留的价值经常被低估：卸载器删掉文件却留下空目录（或只留个日志）很常见，
    /// 而用户看到的就是"卸完了，Program Files 里还有个文件夹"。
    /// </summary>
    private LeftoverItem BuildInstallDirectoryItem(string root, UninstallSnapshot snapshot)
    {
        var stats = SafeStatistics(root);
        var evidence = new List<string>
        {
            $"该目录是「{snapshot.SoftwareName}」的安装目录（{snapshot.InstallLocation}）",
            $"卸载前共有 {snapshot.FileCount} 个文件、{snapshot.DirectoryCount} 个子目录，"
            + $"卸载后仍有 {Math.Max(0, snapshot.FileCount - RemovedIn(snapshot, root))} 个文件未被删除",
        };

        var block = LeftoverSafetyGuard.Check(root, _environment.SelfDirectory);

        return new LeftoverItem
        {
            Id = root.ToLowerInvariant(),
            DirectoryPath = root,
            Kind = LeftoverKind.OrphanedUninstallerDirectory,
            Confidence = LeftoverConfidence.High,
            SoftwareName = snapshot.SoftwareName,
            TotalBytes = stats.TotalBytes,
            FileCount = stats.FileCount,
            SubDirectoryCount = stats.DirectoryCount,
            LastWriteTime = stats.LastWriteTime,
            Evidence = evidence,
            BlockReason = block,
        };
    }

    private static int RemovedIn(UninstallSnapshot snapshot, string root)
    {
        var count = 0;
        foreach (var entry in snapshot.Entries)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            var absolute = Path.Combine(root, entry.RelativePath);
            if (!File.Exists(absolute))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// 按软件名在常见数据位置找同名目录。
    ///
    /// <b>为什么这里可以用名字匹配</b>：通用扫描（v0.3.0）里名字匹配不可用，
    /// 是因为"不知道卸过什么"——306 个顶层目录里 285 个对不上任何已装软件，
    /// 按名字判残留误报率 93%。而这里软件名是<b>已知事实</b>，
    /// "刚刚卸了微信，%AppData% 下还有个 WeChat 目录"这条推理是成立的。
    ///
    /// 仍然保持保守：归一化后相等，或目录名包含软件名且软件名长度 ≥ 4（中文 2 字也算）。
    ///
    /// <para><b>v0.9.6 增加两层</b>：① 没有安装位置时也执行本步（见调用处）；
    /// ② AppData / ProgramData 这类"厂商再加一层"的根，往下多找一层——
    /// <c>%LocalAppData%\Programs\&lt;App&gt;</c>、<c>%AppData%\&lt;Vendor&gt;\&lt;App&gt;</c>
    /// 这种形态很常见，只看第一层会整片漏掉（真机反馈）。</para>
    /// </summary>
    private async Task AppendNameMatchedDirectoriesAsync(
        List<LeftoverItem> directories,
        UninstallSnapshot snapshot,
        UninstallResidueOptions options,
        string? installRoot,
        IProgress<LeftoverScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var variants = SoftwareNameNormalizer.Variants(snapshot.SoftwareName)
            .Where(v => v.Length >= 2)
            .ToList();

        if (variants.Count == 0)
        {
            return;
        }

        var roots = options.ExtraRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 预算：一次扫描最多看这么多个目录，防止在 %LocalAppData% 这种大目录上失控。
        var budget = Math.Max(200, options.MaxDirectoriesPerRoot);

        foreach (var candidateRoot in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_environment.DirectoryExists(candidateRoot))
            {
                continue;
            }

            var maxDepth = options.MaxDepth > 1 && IsDeepRoot(candidateRoot) ? 2 : 1;
            var visited = 0;

            foreach (var (child, depth) in SafeEnumerateDirectories(candidateRoot, maxDepth, budget))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++visited > budget)
                {
                    break;
                }

                var name = Path.GetFileName(child.TrimEnd(Path.DirectorySeparatorChar));
                var normalized = SoftwareNameNormalizer.Normalize(name);

                if (normalized.Length < 2)
                {
                    continue;
                }

                // 短名字（< 4 字符）只允许全等：否则"微信"会匹配到"微信小程序"、
                // "QQ"会匹配到"QQ音乐旧版"这类不相关目录，误报代价由用户承担。
                var matched = variants.Any(v =>
                    string.Equals(normalized, v, StringComparison.OrdinalIgnoreCase)
                    || (v.Length >= 4 && normalized.Contains(v, StringComparison.OrdinalIgnoreCase)));

                if (!matched)
                {
                    continue;
                }

                // 安装目录本身已在第 2 步报过，不重复。
                if (installRoot is not null
                    && string.Equals(child.TrimEnd(Path.DirectorySeparatorChar), installRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 外层目录已经命中时，不再把它下面的子目录也当成独立残留（同一份残留报两次没意义）。
                if (directories.Any(d => child.StartsWith(
                        d.DirectoryPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // 已被现存的已安装软件认领 → 不是残留（用户可能又装回去了，或另一产品共用该目录）。
                // 以前 Evidence 里写着"没有任何条目认领"，但代码其实没查——现在真的查了（v0.9.6）。
                if (IsClaimedByInstalledApp(child, options.InstalledApps))
                {
                    continue;
                }
                
                var stats = SafeStatistics(child);
                var block = LeftoverSafetyGuard.Check(child, _environment.SelfDirectory);

                directories.Add(new LeftoverItem
                {
                    Id = child.ToLowerInvariant(),
                    DirectoryPath = child,
                    Kind = LeftoverKind.OrphanedUninstallerDirectory,
                    Confidence = LeftoverConfidence.High,
                    SoftwareName = snapshot.SoftwareName,
                    TotalBytes = stats.TotalBytes,
                    FileCount = stats.FileCount,
                    SubDirectoryCount = stats.DirectoryCount,
                    LastWriteTime = stats.LastWriteTime,
                    Evidence =
                    [
                        $"目录名与刚卸载的「{snapshot.SoftwareName}」匹配（{name}）",
                        $"位于常见残留位置：{candidateRoot}" + (depth > 1 ? "（厂商子目录下）" : string.Empty),
                        "已安装软件列表中没有任何条目认领该目录",
                    ],
                    BlockReason = block,
                });
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// 该目录是否被某个<b>当前还装着</b>的软件认领（按其 <c>InstallLocation</c> 比较）。
    ///
    /// <para>只做"相等或位于其下"的判断：宁可多认领（少报一条），也不要把在用的目录报成残留。</para>
    /// </summary>
    private static bool IsClaimedByInstalledApp(
        string candidate, IReadOnlyList<Models.InstalledApp> installedApps)
    {
        if (installedApps.Count == 0)
        {
            return false;
        }

        var normalized = candidate.TrimEnd(Path.DirectorySeparatorChar);

        foreach (var app in installedApps)
        {
            var location = app.InstallLocation?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(location))
            {
                continue;
            }

            var claimed = location!.TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(normalized, claimed, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(claimed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 这些根目录下常见「厂商/产品」两层结构，需要多看一层。
    /// 其余根（Program Files、用户主目录）只看第一层：再深一层会把 Documents/Desktop 之类翻个底朝天，
    /// 又慢又没收益。
    /// </summary>
    private static bool IsDeepRoot(string root)
    {
        var normalized = root.TrimEnd(Path.DirectorySeparatorChar);

        if (normalized.EndsWith(@"\AppData\Local", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(@"\AppData\Roaming", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(@"\AppData\LocalLow", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            .TrimEnd(Path.DirectorySeparatorChar);
        return programData.Length > 0
            && string.Equals(normalized, programData, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 按层枚举子目录（默认只第一层）。
    ///
    /// 不进入重解析点（目录联接/符号链接）：那可能指向别的盘甚至网络位置。
    /// 命中预算后停下——宁可少报，也不能让扫描卡住。
    /// </summary>
    private List<(string Path, int Depth)> SafeEnumerateDirectories(
        string directoryPath, int maxDepth, int budget)
    {
        var result = new List<(string Path, int Depth)>();
        try
        {
            if (!Directory.Exists(directoryPath))
            {
                return result;
            }

            var level = new List<string> { directoryPath };
            for (var depth = 1; depth <= maxDepth; depth++)
            {
                var next = new List<string>();
                foreach (var parent in level)
                {
                    foreach (var child in Directory.EnumerateDirectories(parent))
                    {
                        if (result.Count >= budget)
                        {
                            return result;
                        }

                        var info = new DirectoryInfo(child);
                        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            continue;
                        }

                        result.Add((child, depth));

                        // 只对马上要看的那一层做展开；Temp/Packages 这类巨目录不进。
                        if (depth + 1 <= maxDepth)
                        {
                            var leaf = Path.GetFileName(child);
                            if (!IsTraversalBlocked(leaf))
                            {
                                next.Add(child);
                            }
                        }
                    }
                }
                level = next;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // 单个目录不可访问：已收集到的照常使用。
        }

        return result;
    }

    /// <summary>深挖时不进去的目录：全是系统/缓存大杂烩，按名字匹配没有意义。</summary>
    private static bool IsTraversalBlocked(string directoryName)
        => directoryName.Equals("Temp", StringComparison.OrdinalIgnoreCase)
           || directoryName.Equals("Packages", StringComparison.OrdinalIgnoreCase)
           || directoryName.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
           || directoryName.Equals("Windows", StringComparison.OrdinalIgnoreCase);

    private DirectoryStatistics SafeStatistics(string directoryPath)
    {
        try
        {
            return _index.GetStatistics(directoryPath);
        }
        catch
        {
            return DirectoryStatistics.Empty;
        }
    }

    private List<string> SafeEnumerateDirectories(string directoryPath)
    {
        try
        {
            return Directory.Exists(directoryPath)
                ? Directory.EnumerateDirectories(directoryPath).ToList()
                : [];
        }
        catch
        {
            return [];
        }
    }
}
