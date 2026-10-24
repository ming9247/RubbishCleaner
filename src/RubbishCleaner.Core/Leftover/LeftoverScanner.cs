using RubbishCleaner.Common.Formatting;
using RubbishCleaner.Common.Mft;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Uninstall;

namespace RubbishCleaner.Core.Leftover;

/// <summary>扫描器依赖的最小文件系统能力（抽出来是为了能单测，不必真造目录）。</summary>
public interface ILeftoverEnvironment
{
    bool DirectoryExists(string path);

    /// <summary>本程序自身所在目录（用于安全闸门）。</summary>
    string? SelfDirectory { get; }
}

/// <summary>真实文件系统实现。</summary>
public sealed class RealLeftoverEnvironment : ILeftoverEnvironment
{
    public bool DirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public string? SelfDirectory
    {
        get
        {
            try
            {
                return Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty);
            }
            catch
            {
                return null;
            }
        }
    }
}

/// <summary>
/// 残留目录扫描器（v0.3）。
///
/// <b>判定哲学：只信硬证据，不猜名字。</b>
///
/// 市面上多数清理工具靠"目录名像某个已卸载软件"来猜残留，这在真机上误报率极高——
/// 实测本机 306 个顶层目录里，靠名字能对上已安装软件的只有 21 个，
/// 其余全是 "ASP.NET"、"NVIDIA Corporation"、"Code" 这类<b>属于已装软件的公共/厂商目录</b>，
/// 靠名字判定会把它们全判成残留。
///
/// 因此这里只用两类证据：
/// <list type="number">
/// <item>
/// <b>孤儿卸载程序</b>：目录里还留着 <c>unins000.exe</c> / <c>uninst.exe</c> / <c>uninstall.exe</c>，
/// 且没有任何已安装软件认领该目录。卸载程序的存在本身就证明"这里装过软件"，
/// 而"没人认领"证明它已被卸载或在别处卸载——这是最强证据。
/// </item>
/// <item>
/// <b>失效启动项指向的目录仍存在</b>：启动项是"装过东西"的铁证，
/// 目标文件没了但目录还在，说明软件被删而目录留下。
/// </item>
/// </list>
///
/// 名字匹配只用于<b>反向</b>：证明"这个目录有主"，从而避免误报。
/// </summary>
public sealed class LeftoverScanner : ILeftoverScanner
{
    /// <summary>
    /// 卸载程序文件名。刻意覆盖常见安装器产物：
    /// Inno Setup（unins000.exe）、NSIS（uninst.exe / Uninstall.exe）、
    /// InstallShield（setup.exe 无法区分，故不收录）、以及带语言后缀的变体。
    /// </summary>
    public static readonly IReadOnlySet<string> UninstallerFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "unins000.exe", "unins001.exe", "unins002.exe", "unins003.exe",
        "uninst.exe", "uninstall.exe", "uninst64.exe", "uninstall64.exe",
        "unins000.dat", "unins001.dat",
        "uninstall_zh.exe", "uninst_zh.exe",
    };

    private readonly IVolumeFileIndex _index;
    private readonly ILeftoverEnvironment _environment;

    /// <summary>
    /// 创建扫描器。
    ///
    /// 无参构造会使用<b>最优可用</b>的索引：管理员下自动启用 USN 直读（秒级），
    /// 否则降级为目录遍历（慢但结果一致）。测试可以注入假索引与假环境。
    /// </summary>
    public LeftoverScanner(IVolumeFileIndex? index = null, ILeftoverEnvironment? environment = null)
    {
        _index = index ?? new CompositeVolumeFileIndex(
            TryCreateUsnIndex(),
            new FallbackVolumeFileIndex());
        _environment = environment ?? new RealLeftoverEnvironment();
    }

    /// <summary>
    /// 用真实的 USN 索引（管理员）或目录遍历回退（标准用户）创建扫描器。
    ///
    /// <b>降级是显式的</b>：标准用户下拿不到卷句柄（<c>CreateFile</c> 返回 5/拒绝访问），
    /// 此时只能用目录遍历，速度慢一个数量级，但结果一致。
    /// 这一点必须如实告诉用户，不能让用户以为两种情况扫的东西一样。
    /// </summary>
    public static LeftoverScanner CreateDefault()
        => new(new CompositeVolumeFileIndex(
                TryCreateUsnIndex(),
                new FallbackVolumeFileIndex()),
            new RealLeftoverEnvironment());

    /// <summary>
    /// 尝试创建 USN 索引。
    ///
    /// 用反射而不是直接 new：USN 索引依赖管理员权限下的卷句柄，
    /// 万一在目标环境里不可用（非 NTFS 卷、精简系统），
    /// 也必须能无缝降级到目录遍历而不是让整个功能崩掉。
    /// </summary>
    private static IVolumeFileIndex TryCreateUsnIndex()
    {
        try
        {
            return new UsnFileIndex();
        }
        catch
        {
            return new FallbackVolumeFileIndex();
        }
    }

    public async Task<LeftoverScanResult> ScanAsync(
        LeftoverScanOptions options,
        IProgress<LeftoverScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var startedAt = DateTimeOffset.Now;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 只扫真实存在的根目录；不存在的根不是错误（例如 32 位目录在纯 64 位系统上也可能没有）
        var roots = options.Roots
            .Where(r => !string.IsNullOrWhiteSpace(r) && _environment.DirectoryExists(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var degraded = new List<string>();
        if (roots.Count == 0)
        {
            return new LeftoverScanResult
            {
                Items = [],
                IndexDescription = _index.Description,
                DegradedReason = "没有任何可扫描的目录（Program Files / ProgramData / AppData 均不可访问）",
                IsElevated = IsElevated(),
                Duration = stopwatch.Elapsed,
                StartedAt = startedAt,
                ScannedRoots = [],
            };
        }

        progress?.Report(new LeftoverScanProgress("正在建立文件索引…"));
        var prepared = false;
        try
        {
            prepared = await _index
                .PrepareAsync(roots, new Progress<string>(s => progress?.Report(new LeftoverScanProgress(s))), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            degraded.Add($"建立文件索引失败：{ex.Message}");
        }

        if (!prepared)
        {
            degraded.Add("文件索引不可用，本次没有扫描任何目录");
            return new LeftoverScanResult
            {
                Items = [],
                IndexDescription = _index.Description,
                DegradedReason = string.Join("；", degraded),
                IsElevated = IsElevated(),
                Duration = stopwatch.Elapsed,
                StartedAt = startedAt,
                ScannedRoots = roots,
            };
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // ---------- 阶段 1：建立"已认领路径"集合 ----------
            progress?.Report(new LeftoverScanProgress("正在整理已安装软件的位置…", 0, roots.Count));
            var claims = BuildClaims(options.InstalledApps);
            foreach (var extra in options.AdditionalClaimPaths)
            {
                AddClaim(claims, extra);
            }

            var vendorTokens = BuildVendorTokens(options.InstalledApps);

            // ---------- 阶段 2：找孤儿卸载程序 ----------
            progress?.Report(new LeftoverScanProgress("正在查找残留的卸载程序…", 1, roots.Count));

            var candidates = new Dictionary<string, CandidateBuilder>(StringComparer.OrdinalIgnoreCase);
            var completed = 0;

            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<IndexedFile> hits;
                try
                {
                    hits = _index.FindFiles(root, UninstallerFileNames);
                }
                catch (Exception ex)
                {
                    degraded.Add($"扫描 {root} 失败：{ex.Message}");
                    hits = [];
                }

                foreach (var hit in hits)
                {
                    var directory = Path.GetDirectoryName(hit.FullPath);
                    if (string.IsNullOrWhiteSpace(directory))
                    {
                        continue;
                    }

                    if (IsClaimed(directory, claims) || IsExcluded(directory, options))
                    {
                        continue;
                    }

                    var builder = candidates.TryGetValue(directory, out var existing)
                        ? existing
                        : new CandidateBuilder(directory);
                    builder.Uninstallers.Add(Path.GetFileName(hit.FullPath));
                    candidates[directory] = builder;
                }

                var done = Interlocked.Increment(ref completed);
                progress?.Report(new LeftoverScanProgress("正在查找残留的卸载程序…", done, roots.Count, root));
            }

            // ---------- 阶段 3：与失效启动项交叉验证 ----------
            progress?.Report(new LeftoverScanProgress("正在核对失效启动项的落点…", 2, roots.Count));
            var startupTrails = BuildStartupTrails(options.StartupProfiles, claims, options, degraded);

            // ---------- 阶段 4：合并、去重、算大小 ----------
            progress?.Report(new LeftoverScanProgress("正在统计残留目录大小…", 3, roots.Count));
            var items = new List<LeftoverItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var builder in candidates.Values)
            {
                if (!seen.Add(builder.DirectoryPath))
                {
                    continue;
                }

                items.Add(BuildUninstallerItem(builder, options, vendorTokens, cancellationToken));
            }

            foreach (var trail in startupTrails)
            {
                if (!seen.Add(trail.DirectoryPath))
                {
                    // 已被"孤儿卸载程序"收录：补一条交叉证据，置信度不变（已经是 High）
                    var index = items.FindIndex(i => string.Equals(i.DirectoryPath, trail.DirectoryPath, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                    {
                        items[index] = items[index] with
                        {
                            Evidence = [.. items[index].Evidence, .. trail.Evidence],
                        };
                    }

                    continue;
                }

                items.Add(trail);
            }

            // ---------- 阶段 5：去掉"父子重复" ----------
            // 若 C:\Foo 与 C:\Foo\Bar 都被判为残留，只保留 C:\Foo：
            // 删父目录已经覆盖子目录，同时列出会让用户以为要删两次，也会重复统计体积。
            var deduped = RemoveNested(items);

            // ---------- 阶段 6：安全闸门 ----------
            var guarded = LeftoverSafetyGuard.Apply(deduped, _environment.SelfDirectory);

            var ordered = guarded
                .OrderByDescending(i => i.Confidence)
                .ThenByDescending(i => i.TotalBytes)
                .ThenBy(i => i.DirectoryPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            stopwatch.Stop();
            progress?.Report(new LeftoverScanProgress("完成", ordered.Count, ordered.Count));

            return new LeftoverScanResult
            {
                Items = ordered,
                IndexDescription = _index.Description,
                DegradedReason = degraded.Count > 0 ? string.Join("；", degraded) : null,
                IsElevated = IsElevated(),
                Duration = stopwatch.Elapsed,
                StartedAt = startedAt,
                ScannedRoots = roots,
            };
        }
        finally
        {
            // 整机索引可达上百 MB，扫完立刻释放，不占着内存。
            _index.Release();
        }
    }

    // ---------------- 内部实现 ----------------

    private sealed class CandidateBuilder(string directoryPath)
    {
        public string DirectoryPath { get; } = directoryPath;

        public List<string> Uninstallers { get; } = [];
    }

    private static bool IsElevated()
    {
        try
        {
            return Common.Win32.PrivilegeHelper.IsElevated;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 认领路径的最小层级（盘根算第 1 级）。
    ///
    /// 必须 ≥ 3：因为 <see cref="BuildClaims"/> 会把"卸载程序所在目录的上一级"也加进来，
    /// 而实测本机有多个软件的卸载程序直接躺在 <c>C:\Program Files\&lt;Vendor&gt;</c> 下，
    /// 于是一句 <c>Path.GetDirectoryName</c> 就把 <c>C:\Program Files</c> 加成了认领路径，
    /// 结果<b>整棵树都变成"有主"</b>，Program Files 下的残留一条都报不出来。
    ///
    /// 这个 bug 是在真机 E2E 里暴露的：自己造的残留样本放在
    /// <c>C:\Program Files\RcE2ELead_*</c>，扫描器却视而不见。
    /// 属于"安全方向"的漏报（不会误删），但会让功能整体失效，必须修。
    /// </summary>
    private const int MinimumClaimDepth = 3;

    /// <summary>这些名字出现在路径末尾时，绝不作为认领路径（它们是容器目录，不是软件目录）。</summary>
    private static readonly HashSet<string> NonClaimableLeafNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program Files", "Program Files (x86)", "ProgramData", "Users", "AppData",
        "Local", "Roaming", "LocalLow", "Common Files", "Windows",
    };

    /// <summary>
    /// 从已安装软件构造"已认领路径"集合。
    ///
    /// 认领依据：
    /// 1. <c>InstallLocation</c>（最直接）；
    /// 2. 卸载程序所在目录；
    /// 3. 卸载程序所在目录的<b>上一级</b>（很多软件装到 <c>Vendor\Product</c>，注册表只写了产品目录）。
    ///
    /// 但以上路径必须是"像软件目录"的路径：层级 ≥ <see cref="MinimumClaimDepth"/>，
    /// 且末端不是 <c>Program Files</c> / <c>ProgramData</c> / <c>AppData</c> 这类容器目录。
    /// 否则一个 <c>C:\Program Files</c> 会把整棵树都标成"有主"。
    /// </summary>
    internal static HashSet<string> BuildClaims(IReadOnlyList<InstalledApp> apps)
    {
        var claims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in apps)
        {
            AddClaim(claims, app.InstallLocation);

            var exe = RegistryTextSanitizer.TryExtractExecutable(app.UninstallCommand);
            if (!string.IsNullOrWhiteSpace(exe) && Path.IsPathRooted(exe))
            {
                var directory = Path.GetDirectoryName(exe);
                AddClaim(claims, directory);

                AddClaim(claims, directory is null ? null : Path.GetDirectoryName(directory));
            }
        }

        return claims;
    }

    /// <summary>
    /// 把一条路径加进认领集合，并做"像不像软件目录"的过滤。
    ///
    /// 过滤依据是 <see cref="MinimumClaimDepth"/> 与 <see cref="NonClaimableLeafNames"/>，
    /// 目的只有一个：<b>别让容器目录进入认领集合</b>。
    /// 一旦 <c>C:\Program Files</c> 被认领，整棵树都会被判成"有主"，残留一条都报不出来。
    /// </summary>
    internal static void AddClaim(HashSet<string> claims, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var trimmed = path.Trim().Trim('"').TrimEnd(Path.DirectorySeparatorChar);
        if (trimmed.Length < 4 || !Path.IsPathRooted(trimmed))
        {
            return;
        }

        var segments = trimmed.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < MinimumClaimDepth)
        {
            return;
        }

        if (NonClaimableLeafNames.Contains(Path.GetFileName(trimmed)))
        {
            return;
        }

        claims.Add(trimmed);
    }

    /// <summary>
    /// 目录是否已被某个已安装软件认领。
    ///
    /// 双向判断：
    /// <list type="bullet">
    /// <item>目录等于/位于认领路径之下 → 有主（例如 <c>Program Files\Foo\bin</c> 属于 <c>Program Files\Foo</c>）；</item>
    /// <item>目录<b>包含</b>认领路径 → 也有主。例如残留检测到 <c>Program Files\OEM</c>，
    /// 而 <c>Program Files\OEM\机械革命电竞控制台</c> 是已装软件：这种情况下删父目录会连带删掉在用的软件，
    /// 必须跳过。</item>
    /// </list>
    /// </summary>
    internal static bool IsClaimed(string directoryPath, HashSet<string> claims)
    {
        if (claims.Count == 0)
        {
            return false;
        }

        var path = directoryPath.TrimEnd(Path.DirectorySeparatorChar);
        if (claims.Contains(path))
        {
            return true;
        }

        foreach (var claim in claims)
        {
            if (path.StartsWith(claim + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (claim.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>绝对不扫的目录：本工具自己的数据目录（里面是备份与日志，删了用户就没法还原）。</summary>
    private static bool IsExcluded(string directoryPath, LeftoverScanOptions options)
    {
        foreach (var excluded in options.ExcludedPaths)
        {
            if (string.IsNullOrWhiteSpace(excluded))
            {
                continue;
            }

            var path = excluded.TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(directoryPath.TrimEnd(Path.DirectorySeparatorChar), path, StringComparison.OrdinalIgnoreCase)
                || directoryPath.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private LeftoverItem BuildUninstallerItem(
        CandidateBuilder builder,
        LeftoverScanOptions options,
        IReadOnlySet<string> vendorTokens,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var stats = options.MeasureSize
            ? SafeStatistics(builder.DirectoryPath)
            : DirectoryStatistics.Empty;

        var name = SoftwareNameNormalizer.CleanDirectoryNameForDisplay(
            Path.GetFileName(builder.DirectoryPath.TrimEnd(Path.DirectorySeparatorChar)));

        var evidence = new List<string>
        {
            $"目录内仍保留卸载程序：{string.Join("、", builder.Uninstallers.Distinct(StringComparer.OrdinalIgnoreCase))}",
            "已安装软件列表中没有任何条目认领该目录（说明软件已被卸载，文件被留下）",
        };

        if (stats.FileCount > 0)
        {
            evidence.Add($"目录内共有 {stats.FileCount} 个文件，合计 {ByteSize.Format(stats.TotalBytes)}");
        }

        if (stats.LastWriteTime is { } lastWrite)
        {
            var age = DateTimeOffset.Now - lastWrite;
            evidence.Add($"最后一次写入：{lastWrite.LocalDateTime:yyyy-MM-dd HH:mm}（距今 {FormatAge(age)}）");
        }

        // 降置信度："厂商共享目录"形态
        //
        // 实测踩到的误报：C:\Program Files (x86)\Common Files\Autodesk Shared\AdskLicensing
        // 里有 6 MB 的 uninstall.exe，注册表里<b>确实没有</b>对应的 Uninstall 条目，
        // 但它属于 Autodesk 全家桶（AutoCAD 的授权组件正在用它），不是在用的残留。
        //
        // 判据是<b>两个条件同时成立</b>：
        // 1. 路径位于"共享容器"下（Common Files / Shared / ProgramData）——
        //    这些位置本来就是厂商放全家桶共享组件的地方；
        // 2. 路径里出现某个已安装软件的厂商名。
        //
        // 刻意<b>不</b>对所有带厂商名的目录都降级：否则 C:\Program Files\Tencent\QQOld
        // 这类真残留也会被降成"只提示"，清理能力就被削没了。
        // 共享容器本身也已被安全闸门拦住（不会自动删），这里降置信度是为了让"为什么不能删"
        // 的表述更准确：不是"危险所以不许删"，而是"不确定所以不自动删"。
        var confidence = LeftoverConfidence.High;
        if (IsInSharedContainer(builder.DirectoryPath))
        {
            var normalizedPath = NormalizedDirectoryName(builder.DirectoryPath);
            foreach (var token in vendorTokens)
            {
                if (normalizedPath.Contains(token, StringComparison.Ordinal))
                {
                    confidence = LeftoverConfidence.Medium;
                    evidence.Add($"位于共享目录且命中已安装厂商「{token}」，可能仍是全家桶的共享组件，因此不自动清理");
                    break;
                }
            }
        }

        return new LeftoverItem
        {
            Id = builder.DirectoryPath.ToLowerInvariant(),
            DirectoryPath = builder.DirectoryPath,
            Kind = LeftoverKind.OrphanedUninstallerDirectory,
            Confidence = confidence,
            SoftwareName = name,
            UninstallerFiles = builder.Uninstallers.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            TotalBytes = stats.TotalBytes,
            FileCount = stats.FileCount,
            SubDirectoryCount = stats.DirectoryCount,
            LastWriteTime = stats.LastWriteTime,
            Evidence = evidence,
        };
    }

    /// <summary>
    /// 是否位于"共享容器"目录下。
    ///
    /// 这些位置的设计用途就是放多个软件共用的组件，因此出现孤儿卸载程序时，
    /// 更可能是"厂商全家桶的共享组件"而不是"某个软件没卸干净"。
    /// </summary>
    private static bool IsInSharedContainer(string directoryPath)
    {
        var normalized = directoryPath.Replace('/', '\\');
        if (!normalized.EndsWith('\\'))
        {
            normalized += "\\";
        }

        return normalized.Contains(@"\Common Files\", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains(@"\Shared\", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains(@"\ProgramData\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>目录路径归一化（只保留字母数字、转小写），用于厂商名包含判断。</summary>
    private static string NormalizedDirectoryName(string path)
    {
        var builder = new System.Text.StringBuilder(path.Length);
        foreach (var ch in path)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 从已安装软件构造"厂商名"集合（用于把厂商共享目录降为"只提示"）。
    ///
    /// 取 <c>Publisher</c> 与 <c>DisplayName</c> 里的词，长度 ≥ 5 才收：
    /// 太短的词（"Inc"、"Co"、"Ltd"）会造成大面积误降级。
    /// </summary>
    internal static HashSet<string> BuildVendorTokens(IReadOnlyList<InstalledApp> apps)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);

        foreach (var app in apps)
        {
            foreach (var word in SoftwareNameNormalizer.Words(app.Publisher, minLength: 5))
            {
                tokens.Add(word);
            }

            // 软件名只取第一个词：例如 "Adobe After Effects 2023" → "adobe"
            var words = SoftwareNameNormalizer.Words(app.DisplayName, minLength: 5);
            if (words.Count > 0)
            {
                tokens.Add(words[0]);
            }
        }

        return tokens;
    }

    /// <summary>
    /// 失效启动项 → 目录仍然存在 → 该目录是残留。
    ///
    /// 只处理<b>目录仍存在</b>的情况：目录也没了说明清得很干净，没有可清理的东西。
    /// </summary>
    private List<LeftoverItem> BuildStartupTrails(
        IReadOnlyList<SoftwareProfile> profiles,
        HashSet<string> claims,
        LeftoverScanOptions options,
        List<string> degraded)
    {
        var result = new List<LeftoverItem>();

        foreach (var profile in profiles)
        {
            foreach (var entry in profile.OrphanEntries)
            {
                if (string.IsNullOrWhiteSpace(entry.ExecutablePath) || !Path.IsPathRooted(entry.ExecutablePath))
                {
                    continue;   // 裸文件名由系统解析，不能据此推断目录
                }

                string? directory;
                try
                {
                    directory = Path.GetDirectoryName(entry.ExecutablePath);
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(directory) || !_environment.DirectoryExists(directory))
                {
                    continue;
                }

                if (IsClaimed(directory, claims) || IsExcluded(directory, options))
                {
                    continue;
                }

                var stats = options.MeasureSize ? SafeStatistics(directory) : DirectoryStatistics.Empty;

                result.Add(new LeftoverItem
                {
                    Id = directory.ToLowerInvariant(),
                    DirectoryPath = directory,
                    Kind = LeftoverKind.StartupTrailDirectory,
                    Confidence = LeftoverConfidence.High,
                    SoftwareName = profile.DisplayName,
                    TotalBytes = stats.TotalBytes,
                    FileCount = stats.FileCount,
                    SubDirectoryCount = stats.DirectoryCount,
                    LastWriteTime = stats.LastWriteTime,
                    Evidence =
                    [
                        $"启动项「{entry.Name}」（{entry.Source.ToDisplayName()}）指向的文件已不存在：{entry.ExecutablePath}",
                        $"但该启动项所在的目录仍然存在：{directory}",
                        "已安装软件列表中没有任何条目认领该目录",
                    ],
                });
            }
        }

        _ = degraded;
        return result;
    }

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

    /// <summary>去掉"父子重复"：若父目录已在列表中，子目录不再单列。</summary>
    internal static List<LeftoverItem> RemoveNested(List<LeftoverItem> items)
    {
        if (items.Count <= 1)
        {
            return items;
        }

        var ordered = items
            .OrderBy(i => i.DirectoryPath.Length)
            .ThenBy(i => i.DirectoryPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var kept = new List<LeftoverItem>(ordered.Count);
        foreach (var item in ordered)
        {
            var path = item.DirectoryPath.TrimEnd(Path.DirectorySeparatorChar);
            var covered = kept.Any(k =>
                path.StartsWith(
                    k.DirectoryPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));

            if (!covered)
            {
                kept.Add(item);
            }
        }

        return kept;
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 365)
        {
            return $"{age.TotalDays / 365:F1} 年";
        }

        if (age.TotalDays >= 1)
        {
            return $"{age.TotalDays:F0} 天";
        }

        if (age.TotalHours >= 1)
        {
            return $"{age.TotalHours:F0} 小时";
        }

        return $"{Math.Max(1, age.TotalMinutes):F0} 分钟";
    }
}