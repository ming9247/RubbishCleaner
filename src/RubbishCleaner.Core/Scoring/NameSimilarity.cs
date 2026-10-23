namespace RubbishCleaner.Core.Scoring;

/// <summary>
/// 名称相似度工具：用于识别"伪装系统进程"（如 svch0st、scvhost）。
/// </summary>
public static class NameSimilarity
{
    /// <summary>内置系统进程名（不含扩展名）。</summary>
    public static readonly IReadOnlyList<string> SystemProcessNames =
    [
        "svchost", "lsass", "csrss", "winlogon", "services", "explorer", "smss",
        "wininit", "spoolsv", "taskhostw", "dwm", "runtimebroker", "sihost",
        "ctfmon", "conhost", "searchindexer", "audiodg", "fontdrvhost",
    ];

    /// <summary>
    /// Damerau-Levenshtein（受限最优字符串对齐，OSA）编辑距离，大小写不敏感。
    /// 相比纯 Levenshtein 多识别"相邻字符换位"这一类替换：<c>scvhost</c> → <c>svchost</c> 距离为 1，
    /// 而换位正是最常见的仿冒手法之一。
    /// </summary>
    public static int Levenshtein(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a))
        {
            return b?.Length ?? 0;
        }

        if (string.IsNullOrEmpty(b))
        {
            return a.Length;
        }

        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();

        var rows = a.Length + 1;
        var cols = b.Length + 1;
        var d = new int[rows, cols];

        for (var i = 0; i < rows; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j < cols; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i < rows; i++)
        {
            for (var j = 1; j < cols; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;

                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);

                // 换位：ab -> ba
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + cost);
                }
            }
        }

        return d[rows - 1, cols - 1];
    }

    /// <summary>
    /// 判断进程名是否疑似伪装系统进程：与某个系统进程名编辑距离 ≤ 1，且不是该系统名本身。
    /// </summary>
    public static bool LooksMasquerading(string? processName, out string? matchedSystemName)
    {
        matchedSystemName = null;
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(processName).Trim().ToLowerInvariant();
        if (name.Length < 4)
        {
            return false;
        }

        foreach (var systemName in SystemProcessNames)
        {
            if (string.Equals(name, systemName, StringComparison.Ordinal))
            {
                return false; // 名字完全一致，是正常系统进程
            }

            if (Levenshtein(name, systemName) <= 1)
            {
                matchedSystemName = systemName;
                return true;
            }
        }

        return false;
    }
}
