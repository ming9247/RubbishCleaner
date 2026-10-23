using RubbishCleaner.Common.Win32;
using RubbishCleaner.Core.Abstractions;

namespace RubbishCleaner.Core.Detection;

/// <summary>
/// 桌面 / 开始菜单快捷方式索引。
/// 做法：扫描一次快捷方式目录，把每个 .lnk 解析出的目标路径与文件名收进哈希集合，
/// 之后对每个软件做 O(1) 查询，而不是每个软件都去遍历目录。
/// </summary>
public sealed class ShortcutIndex : IShortcutIndex
{
    private readonly Lazy<IndexData> _desktop;
    private readonly Lazy<IndexData> _startMenu;

    public ShortcutIndex()
    {
        _desktop = new Lazy<IndexData>(() => Build(DesktopFolders()), isThreadSafe: true);
        _startMenu = new Lazy<IndexData>(() => Build(StartMenuFolders()), isThreadSafe: true);
    }

    public bool HasDesktopShortcut(string? executablePath, string displayName)
        => Matches(_desktop.Value, executablePath, displayName);

    public bool HasStartMenuEntry(string? executablePath, string displayName)
        => Matches(_startMenu.Value, executablePath, displayName);

    private static bool Matches(IndexData data, string? executablePath, string displayName)
    {
        if (!string.IsNullOrWhiteSpace(executablePath) && data.Targets.Contains(executablePath))
        {
            return true;
        }

        // 退回按名称模糊匹配：很多快捷方式指向 launcher，名称才是用户可见的线索。
        var normalized = NormalizeName(displayName);
        return normalized.Length > 0 && data.Names.Any(n => n.Contains(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        // 去掉常见后缀，便于 "腾讯会议" 匹配 "腾讯会议.lnk"
        foreach (var suffix in new[] { ".exe", ".lnk", " Launcher", " Helper" })
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^suffix.Length];
            }
        }

        return trimmed;
    }

    private static IndexData Build(IEnumerable<string> folders)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();

        foreach (var folder in folders.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                names.Add(Path.GetFileNameWithoutExtension(file));

                var link = ShellLinkReader.Read(file);
                if (!string.IsNullOrWhiteSpace(link.TargetPath))
                {
                    targets.Add(link.TargetPath);
                }
            }
        }

        return new IndexData(targets, names);
    }

    private static IEnumerable<string> DesktopFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    }

    private static IEnumerable<string> StartMenuFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
    }

    private sealed record IndexData(HashSet<string> Targets, List<string> Names);
}
