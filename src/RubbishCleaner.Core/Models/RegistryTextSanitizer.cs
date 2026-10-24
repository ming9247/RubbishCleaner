using System.Text;

namespace RubbishCleaner.Core.Models;

/// <summary>
/// 注册表字符串清洗。
///
/// <b>为什么需要</b>：不是所有软件都规范写注册表。实测本机存在被第三方安装器写坏的条目——
/// <c>DisplayName</c>、<c>Publisher</c>、<c>UninstallString</c> 里混入了二进制垃圾
/// （形如 <c>明日之后 羮翶 琢 ...</c>），直接显示会让列表出现一大片乱码。
///
/// 处理策略是<b>截断到第一个可疑字符</b>，而不是"过滤掉非法字符"：
/// 因为垃圾数据通常是从某个位置开始整段污染，后面的内容没有保留价值，
/// 而前面那段（如"明日之后"）恰恰是有效信息。
/// </summary>
public static class RegistryTextSanitizer
{
    /// <summary>单条显示文本的最大长度，超出截断（防止极端脏数据撑爆界面）。</summary>
    private const int MaxLength = 200;

    /// <summary>
    /// 清洗注册表文本：截断到第一个控制字符或异常字符，并去除首尾空白。
    /// 返回 null 表示清洗后为空（调用方应视作"无有效值"）。
    /// </summary>
    public static string? Clean(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(raw.Length, MaxLength));
        foreach (var ch in raw)
        {
            if (!IsSuspicious(ch))
            {
                builder.Append(ch);
                continue;
            }

            // 遇到污染起点就停：后面的内容通常是连续的二进制垃圾
            break;
        }

        var cleaned = builder.ToString().Trim();

        // 有效文本至少要有 2 个字符，且不应全是符号
        if (cleaned.Length < 2 || !cleaned.Any(char.IsLetterOrDigit))
        {
            return null;
        }

        return cleaned;
    }

    /// <summary>判断字符是否属于"污染"信号。</summary>
    private static bool IsSuspicious(char ch)
    {
        // 控制字符（含 \0）：注册表字符串里出现基本就是脏数据
        if (char.IsControl(ch))
        {
            return true;
        }

        // 私用区（U+E000–U+F8FF）：常出现在被截断的多字节序列里
        if (ch is >= '\uE000' and <= '\uF8FF')
        {
            return true;
        }

        // 代理项（孤立的高/低代理）：说明原始字符串已损坏
        if (char.IsSurrogate(ch))
        {
            return true;
        }

        // 大量重复的空白代表二进制填充
        return ch == '\u200B';   // 零宽空格
    }

    /// <summary>
    /// 解析卸载命令，取出可执行文件路径（用于校验文件是否存在）。
    /// 支持 <c>"C:\a b\x.exe" /arg</c> 与裸路径两种形态；
    /// <c>MsiExec.exe /X{...}</c> 这类返回 null（没有独立 exe 可校验）。
    /// </summary>
    public static string? TryExtractExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var text = command.Trim();

        // MSI 卸载：没有可校验的 exe
        if (text.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (text[0] == '"')
        {
            var closing = text.IndexOf('"', 1);
            return closing > 1 ? text[1..closing] : null;
        }

        // 无引号：取到第一个 .exe 为止
        var exeIndex = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex > 0)
        {
            return text[..(exeIndex + 4)];
        }

        var space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }
}
