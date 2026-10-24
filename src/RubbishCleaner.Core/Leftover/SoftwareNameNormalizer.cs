using System.Text;

namespace RubbishCleaner.Core.Leftover;

/// <summary>
/// 软件名归一化，用于判断"磁盘上这个目录是否已经由一个已安装软件认领"。
///
/// <b>为什么需要归一化</b>：注册表里的软件名是给人看的（"Adobe Photoshop 2023"、
/// "微信开发者工具 2.02.2608060"），磁盘目录名是安装器起的（"Adobe Photoshop 2023"、
/// "微信开发者工具"）。直接比较会把同一个软件判成两个，于是把正在使用的目录误报成残留。
///
/// <b>设计取向：宁可多认领，不可多误报</b>。名字匹配只用于"证明有主"这一侧：
/// 匹配上就认为目录不该被清理；匹配不上也不直接判残留，还要找到卸载程序这类硬痕迹。
/// 因此本类允许较宽的匹配（前缀、去版本号）。
/// </summary>
public static class SoftwareNameNormalizer
{
    /// <summary>
    /// 归一化：只保留字母与数字并转小写。
    ///
    /// 例："Visual Studio Code (User)" → "visualstudiocodeuser"
    /// 例："腾讯科技(深圳)有限公司" → "腾讯科技深圳有限公司"
    /// 例："PDFgear 2.1.15" → "pdfgear2115"
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 生成可比较变体：完整归一化形式，以及去掉末尾"数字+点"版本段后的形式。
    ///
    /// 例："Adobe After Effects 2023" → [「adobeaftereffects2023」, 「adobeaftereffects」]
    /// 例："微信开发者工具 2.02.2608060" → [「微信开发者工具2022608060」, 「微信开发者工具」]
    ///
    /// 只去<b>末尾</b>的数字串，因此 "7-Zip" → "7zip" 不会被破坏成 "zip"。
    /// </summary>
    public static IReadOnlyList<string> Variants(string? text)
    {
        var full = Normalize(text);

        // 长度 ≥ 2 就收：中文软件名大量是两个字（微信、QQ、抖音、钉钉），
        // 通用扫描里它们因为"太短会误报"被排除，但定向扫描（v0.3.1）里
        // 软件名是<b>已知事实</b>，"刚卸载了微信，%AppData% 下还有微信目录"这条推理成立。
        // 防误报的责任交给调用方：短名字只允许<b>全等</b>匹配，不允许包含匹配。
        if (full.Length < 2)
        {
            return [];
        }

        var result = new List<string>(2) { full };

        var end = full.Length;
        while (end > 0 && char.IsDigit(full[end - 1]))
        {
            end--;
        }

        // 至少要留下 3 个字符，否则 "2023" 这类名字会被削成空串
        if (end >= 3 && end < full.Length)
        {
            var trimmed = full[..end];
            if (!string.Equals(trimmed, full, StringComparison.Ordinal))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    /// <summary>
    /// 把一段文本拆成"词"（按非字母数字切分，各自归一化），用于发行商这类多词字段。
    ///
    /// 例："Autodesk, Inc." → ["autodesk", "inc"]
    /// 过滤掉长度小于 4 的词，避免 "Inc"、"Co" 这类通用词造成大面积误认领。
    /// </summary>
    public static IReadOnlyList<string> Words(string? text, int minLength = 4)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var words = new List<string>();
        var builder = new StringBuilder();

        void Flush()
        {
            if (builder.Length >= minLength)
            {
                words.Add(builder.ToString());
            }

            builder.Clear();
        }

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return words;
    }

    /// <summary>
    /// 从目录名推测展示用的软件名：去掉常见后缀噪声，只影响 UI 展示，不参与判定。
    ///
    /// 例："NVIDIA Corporation" → 原样；"Foo (x86)" → "Foo"；"Bar v2.1" → "Bar"。
    /// </summary>
    public static string CleanDirectoryNameForDisplay(string directoryName)
    {
        var name = directoryName.Trim();
        if (name.Length == 0)
        {
            return name;
        }

        foreach (var marker in new[] { " (x86)", " (x64)", " v1", " v2", " v3", " V1", " V2", " V3" })
        {
            var index = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index > 2)
            {
                return name[..index].Trim();
            }
        }

        return name;
    }
}
