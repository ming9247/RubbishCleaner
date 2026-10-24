using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Leftover;

/// <summary>
/// 残留清理的<b>安全闸门</b>：决定一个目录"能不能被移入回收站"。
///
/// 这是整个 v0.3 里最需要谨慎的一段代码——它输出的是"允许删除"，
/// 一旦判错，用户丢掉的是真实文件。因此刻意做成<b>纯函数</b>：
/// 输入路径与上下文，输出"允许/拒绝 + 人类可读的原因"，可以穷举单测。
///
/// 七道闸门（按顺序执行，前一道拒绝就不再往下走）：
/// <list type="number">
/// <item>路径非空、可解析、是绝对路径（相对路径会被解析到不可预期的位置）</item>
/// <item>不是盘根（<c>D:\</c>）</item>
/// <item>层级足够（至少位于某个扫描根目录的下级，避免误删根目录的直接子目录）</item>
/// <item>不在系统保护路径之下（Windows / System32 / WinSxS / Common Files 等）</item>
/// <item>不包含本工具自身所在目录（否则可能删掉正在运行的自己）</item>
/// <item>不是重解析点（符号链接 / 目录联接指向别处，删它会牵连目标）</item>
/// <item>目录名不与系统保留名冲突（"Microsoft"、"Windows"、"Common Files" 等）</item>
/// </list>
/// </summary>
public static class LeftoverSafetyGuard
{
    /// <summary>绝对不允许删除的目录名（不区分大小写）。</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "System32", "SysWOW64", "WinSxS", "Common Files", "WindowsApps",
        "Microsoft", "Microsoft Office", "Microsoft.NET", "Microsoft Shared",
        "Program Files", "Program Files (x86)", "ProgramData", "Users", "AppData",
        "Local", "Roaming", "LocalLow", "Temp", "System", "System Volume Information",
        "$Recycle.Bin", "Recovery", "Boot", "EFI", "PerfLogs",
        "InstallShield Installation Information", "Package Cache", "Desktop", "Documents",
    };

    /// <summary>
    /// 位于这些路径片段之下的目录一律拒绝。
    ///
    /// 注意用<b>两侧带分隔符</b>的片段匹配，而不是简单的 StartsWith：
    /// 前者能正确处理大小写与中间层级，也不会让 "C:\MyWindows_bak" 误命中 "Windows"。
    /// </summary>
    private static readonly string[] ProtectedPathFragments =
    [
        @"\Windows\",
        @"\WinSxS\",
        @"\$Recycle.Bin\",
        @"\System Volume Information\",
        @"\WindowsApps\",
        @"\Common Files\",
        @"\InstallShield Installation Information\",
        @"\Package Cache\",
        @"\ModifiableWindowsApps\",
    ];

    /// <summary>盘根（如 "C:\"）——删除它等于清空系统盘。</summary>
    public static bool IsVolumeRoot(string path)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar);
            return !string.IsNullOrEmpty(root)
                   && string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 判定是否允许把 <paramref name="directoryPath"/> 移入回收站。
    /// </summary>
    /// <param name="directoryPath">待判定的绝对路径。</param>
    /// <param name="selfDirectory">本程序自身所在目录（可为 null），用于避免删掉自己。</param>
    /// <param name="minimumDepth">最小层级：盘根为 0，例如 <c>C:\Program Files\Foo</c> 为 2。</param>
    /// <returns>拒绝原因；返回 null 表示允许删除。</returns>
    public static string? Check(string? directoryPath, string? selfDirectory, int minimumDepth = 2)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return "路径为空";
        }

        var raw = directoryPath.Trim();

        // 必须先判原始输入是否绝对：Path.GetFullPath 会把相对路径<b>补全</b>成绝对路径，
        // 之后再也看不出它原本是相对的（实测踩过：@"AppDataLocalFoo" 被静默解析成
        // 当前工作目录下的路径，于是"相对路径一律拒绝"这道闸门形同虚设）。
        if (!Path.IsPathRooted(raw))
        {
            return "不是绝对路径";
        }

        string full;
        try
        {
            full = Path.GetFullPath(raw);
        }
        catch
        {
            return "路径不合法，无法解析";
        }

        if (IsVolumeRoot(full))
        {
            return "这是盘符根目录，绝不能删除";
        }

        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar);
        var segments = trimmed.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        // 盘根也算一级（C:\Foo 的 segments 是 ["C:","Foo"]）
        if (segments.Length <= minimumDepth)
        {
            return $"目录层级过浅（{segments.Length} 级），为避免误删上层目录而拒绝";
        }

        var leaf = Path.GetFileName(trimmed);
        if (ReservedNames.Contains(leaf))
        {
            return $"「{leaf}」是系统保留目录名，拒绝删除";
        }

        var normalized = trimmed.Replace('/', '\\') + "\\";
        foreach (var fragment in ProtectedPathFragments)
        {
            if (normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return $"位于系统保护路径（匹配 {fragment.Trim('\\')}），拒绝删除";
            }
        }

        if (!string.IsNullOrWhiteSpace(selfDirectory))
        {
            try
            {
                var self = Path.GetFullPath(selfDirectory.Trim()).TrimEnd(Path.DirectorySeparatorChar);
                if (string.Equals(self, trimmed, StringComparison.OrdinalIgnoreCase)
                    || self.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith(self + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return "与本程序所在目录重叠，拒绝删除（避免运行中被自己删掉）";
                }
            }
            catch
            {
                // 自身目录无法解析时跳过这道闸门，其余闸门仍然生效
            }
        }

        try
        {
            var info = new DirectoryInfo(trimmed);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return "这是符号链接或目录联接，删除会牵连真实目标，拒绝";
            }
        }
        catch
        {
            // 读不到属性时继续：回收站操作本身失败还有一层兜底
        }

        return null;
    }

    /// <summary>
    /// 批量判定：给每个条目补上 <see cref="LeftoverItem.BlockReason"/>。
    ///
    /// 之所以不把闸门写进扫描器，是为了让"扫描出什么"与"允许删什么"彻底解耦——
    /// 用户仍然能看到全部残留（包括不能删的），但清理按钮只对通过的项生效。
    /// </summary>
    public static IReadOnlyList<LeftoverItem> Apply(
        IReadOnlyList<LeftoverItem> items,
        string? selfDirectory,
        int minimumDepth = 2)
    {
        var result = new List<LeftoverItem>(items.Count);
        foreach (var item in items)
        {
            if (item.BlockReason is not null)
            {
                result.Add(item);
                continue;
            }

            var reason = Check(item.DirectoryPath, selfDirectory, minimumDepth);
            result.Add(reason is null ? item : item with { BlockReason = reason });
        }

        return result;
    }
}
