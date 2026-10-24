namespace RubbishCleaner.Common.Formatting;

/// <summary>
/// 字节数的人类可读格式。抽成独立纯函数是为了能被单测覆盖：
/// 列表里"1.2 GB / 934 MB"这类显示在残留清理场景中直接影响用户判断该不该删。
/// </summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// 格式化为 "512 B" / "12.3 KB" / "4.5 MB" / "1.02 GB"。
    /// 小于 1 KB 不显示小数；其余保留 1 位小数（GB 以上保留 2 位）。
    /// </summary>
    public static string Format(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        if (unit == 0)
        {
            return $"{bytes} B";
        }

        var digits = unit >= 3 ? 2 : 1;
        return $"{value.ToString($"F{digits}", System.Globalization.CultureInfo.InvariantCulture)} {Units[unit]}";
    }

    /// <summary>带总和的描述，例如 "1.2 GB（3 个目录）"。</summary>
    public static string FormatTotal(long bytes) => Format(bytes);
}
