using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Residue;

namespace RubbishCleaner.Core.Residue;

/// <summary>
/// 卸载前快照的采集与持久化（v0.3.1）。
///
/// <b>为什么需要快照</b>：通用残留扫描（v0.3.0）是<b>启发式</b>的——它靠卸载程序、
/// 启动项这些硬痕迹去猜"这个目录曾经装过软件"。而"用户刚刚用本工具卸载了 X"
/// 是一个<b>已知事实</b>：软件叫什么、装在哪里、卸载前有哪些文件，全都知道。
/// 拿卸载后的磁盘状态和这个已知事实对比，得到的就是确定性残留。
///
/// <b>为什么只记路径/大小/文件数，不记内容哈希</b>：对比的目的只是回答
/// "哪些条目消失了、哪些留下了"。记 SHA256 会让快照大几十倍、慢几十倍，
/// 而没有任何判定会因此变得更准。
/// </summary>
public static class UninstallSnapshotStore
{
    private const string SnapshotFolderName = "uninstall-snapshots";

    /// <summary>JSON 序列化选项：可空字段显式写出，缩进便于人工排查。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>快照默认存放目录：<c>%LocalAppData%\RubbishCleaner\uninstall-snapshots\</c>。</summary>
    public static string DefaultDirectory
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = Path.GetTempPath();
            }

            return Path.Combine(localAppData, "RubbishCleaner", SnapshotFolderName);
        }
    }

    /// <summary>
    /// 对一个即将卸载的软件做快照。
    ///
    /// 返回 null 的三种情况（都不是错误，只是"没什么可记的"）：
    /// <list type="bullet">
    /// <item>注册表没写 <c>InstallLocation</c>——无从知道装在哪；</item>
    /// <item><c>InstallLocation</c> 指向的目录不存在——可能已被手工删过；</item>
    /// <item>目录里什么都没有——没有对比基准。</item>
    /// </list>
    /// </summary>
    public static UninstallSnapshot? TakeSnapshot(
        InstalledApp app,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        var location = app.InstallLocation?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(location) || !Path.IsPathRooted(location))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return null;
        }

        if (!Directory.Exists(full))
        {
            return null;
        }

        var entries = new List<SnapshotEntry>(256);
        Collect(full, full, entries, progress, cancellationToken);

        if (entries.Count == 0)
        {
            return null;
        }

        return new UninstallSnapshot(
            SoftwareName: app.DisplayName,
            InstallLocation: full,
            TakenAt: DateTimeOffset.Now,
            Entries: entries,
            UninstallCommand: app.UninstallCommand,
            Publisher: app.Publisher);
    }

    /// <summary>
    /// 递归采集目录内容，记录<b>相对于安装目录</b>的路径。
    ///
    /// 用相对路径而不是绝对路径，是为了让快照在"软件被装到另一个盘/另一台机器"时
    /// 仍可读；同时也让 JSON 更小（几百个条目时不明显，几千个时差别很大）。
    /// </summary>
    private static void Collect(
        string root,
        string current,
        List<SnapshotEntry> entries,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var info = new DirectoryInfo(directory);

                // 跳过符号链接/目录联接：跟着它走可能走到别的盘，把整棵树采进来。
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                entries.Add(new SnapshotEntry(Relative(root, directory), true, 0));
                Collect(root, directory, entries, progress, cancellationToken);
            }

            foreach (var file in Directory.EnumerateFiles(current))
            {
                cancellationToken.ThrowIfCancellationRequested();

                long size = 0;
                try
                {
                    size = new FileInfo(file).Length;
                }
                catch
                {
                    // 文件正被占用/无权限：记 0 字节，条目本身仍然要记
                }

                entries.Add(new SnapshotEntry(Relative(root, file), false, size));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // 单个子树不可访问：跳过它，其余部分仍然有效。
            // 快照的价值在于"知道大致有什么"，不要求 100% 完整。
            progress?.Report($"跳过不可访问的子目录：{current}");
        }
    }

    private static string Relative(string root, string path)
        => path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? path[(root.Length + 1)..]
            : Path.GetFileName(path);

    /// <summary>把快照写成 JSON 文件，返回文件路径。</summary>
    public static string Save(UninstallSnapshot snapshot, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var folder = directory ?? DefaultDirectory;
        Directory.CreateDirectory(folder);

        var name = SanitizeFileName(snapshot.SoftwareName);
        var path = Path.Combine(folder, $"{name}-{snapshot.TakenAt:yyyyMMddHHmmss}.json");

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return path;
    }

    /// <summary>读取快照；文件不存在或内容损坏时返回 null（绝不抛）。</summary>
    public static UninstallSnapshot? Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UninstallSnapshot>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            // 损坏的快照不该让卸载流程失败：返回 null，上层按"没有快照"处理。
            return null;
        }
    }

    /// <summary>取某个软件最近一次的快照。</summary>
    public static UninstallSnapshot? LoadLatest(string directory, string softwareName)
    {
        var prefix = SanitizeFileName(softwareName) + "-";
        UninstallSnapshot? latest = null;

        foreach (var file in SafeEnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var snapshot = Load(file);
            if (snapshot is null)
            {
                continue;
            }

            if (latest is null || snapshot.TakenAt > latest.TakenAt)
            {
                latest = snapshot;
            }
        }

        return latest;
    }

    /// <summary>删除一个快照文件。</summary>
    public static void Delete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 删不掉只是留个垃圾文件，不影响功能
        }
    }

    /// <summary>
    /// 清理旧快照：每个软件只保留最近 <paramref name="keepPerSoftware"/> 份。
    ///
    /// 不清理的话，每次卸载都留一份，半年后就是几百个小 JSON——
    /// 虽然单个只有几 KB，但这是"流氓工具"最典型的毛病：用完留下一地垃圾。
    /// </summary>
    public static int Prune(string? directory = null, int keepPerSoftware = 3)
    {
        var folder = directory ?? DefaultDirectory;
        var removed = 0;

        try
        {
            // 排序键用"文件名里的时间戳"，而不是文件写入时间：
            // 连续 Save 多个快照时，写入时间可能落在同一毫秒（实测踩过），
            // 按写入时间排序会随机留下旧快照、删掉新快照。
            // 文件名自带 yyyyMMddHHmmss，字典序即时间序，稳定且无需读文件。
            var groups = SafeEnumerateFiles(folder)
                .Select(f => new { Path = f, Name = Path.GetFileName(f) })
                .Where(f => f.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Select(f => new { f.Path, f.Name, Key = SortKey(f.Name) })
                .GroupBy(f =>
                {
                    var dash = f.Name.LastIndexOf('-');
                    return dash > 0 ? f.Name[..dash] : f.Name;
                });

            foreach (var group in groups)
            {
                foreach (var stale in group.OrderByDescending(f => f.Key).Skip(keepPerSoftware))
                {
                    Delete(stale.Path);
                    removed++;
                }
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }

        return removed;
    }

    /// <summary>
    /// 从快照文件名取排序键：<c>{软件名}-{yyyyMMddHHmmss}.json</c> 里的时间部分。
    /// 取不到就退回文件名本身（字典序仍然稳定）。
    /// </summary>
    private static string SortKey(string fileName)
    {
        var dash = fileName.LastIndexOf('-');
        var stem = dash > 0 && dash < fileName.Length - 1 ? fileName[(dash + 1)..] : fileName;

        // 去掉 .json 后缀
        if (stem.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^5];
        }

        return stem;
    }

    private static IEnumerable<string> SafeEnumerateFiles(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.EnumerateFiles(directory) : [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>把软件名变成安全的文件名（去掉路径分隔符与保留字符）。</summary>
    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unknown";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (var ch in name)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? "unknown" : result;
    }
}
