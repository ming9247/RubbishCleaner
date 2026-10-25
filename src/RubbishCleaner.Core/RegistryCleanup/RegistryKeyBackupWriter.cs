using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace RubbishCleaner.Core.RegistryCleanup;

/// <summary>
/// 把单个注册表键导出成标准 <c>.reg</c> 文件（v0.4）。
///
/// <b>为什么要标准 .reg 而不是自定义格式</b>：
/// 用户<b>不装本工具</b>也能双击还原——这是"可恢复性"的最强保证。
/// 与 v0.1.3 的启动项备份同一思路（ADR-017）。
///
/// <b>编码必须 UTF-16LE + BOM</b>：v0.1.3 实测过，
/// UTF-8 的 .reg 文件里中文会乱码，<c>reg import</c> 后条目名变成问号。
/// </summary>
public static class RegistryKeyBackupWriter
{
    /// <summary>备份文件扩展名。</summary>
    public const string Extension = ".reg";

    /// <summary>reg 文件头（REGEDIT4 是旧格式，5.00 支持 dword/hex 等现代类型）。</summary>
    private const string FileHeader = "Windows Registry Editor Version 5.00";

    /// <summary>
    /// 导出一个键（含其下所有值，不含子键）。
    ///
    /// 返回备份文件路径；失败返回 null 且 <paramref name="error"/> 说明原因。
    /// <b>调用方必须在拿到非 null 结果后才可以删键</b>。
    /// </summary>
    public static string? Write(
        RegistryHive hive,
        RegistryView view,
        string subKeyPath,
        string directory,
        out string? error,
        DateTimeOffset? timestamp = null)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(subKeyPath))
        {
            error = "子键路径为空";
            return null;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKeyPath, writable: false);
            if (key is null)
            {
                error = $"键不存在：{subKeyPath}";
                return null;
            }

            var content = BuildRegContent(hive, subKeyPath, key);
            if (content is null)
            {
                error = "读取键值时失败";
                return null;
            }

            Directory.CreateDirectory(directory);

            var stamp = (timestamp ?? DateTimeOffset.Now).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            var leaf = subKeyPath[(subKeyPath.LastIndexOf('\\') + 1)..];
            var safeLeaf = SanitizeFileName(leaf);
            var path = Path.Combine(directory, $"orphan-{safeLeaf}-{stamp}{Extension}");

            // UTF-16LE + BOM：reg.exe 只认这个编码的中文
            File.WriteAllText(path, content, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

            // 复核：文件确实写出来了且非空，否则不算备份成功
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0)
            {
                error = "备份文件写入后为空";
                return null;
            }

            return path;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>构造 .reg 文本。</summary>
    private static string? BuildRegContent(RegistryHive hive, string subKeyPath, RegistryKey key)
    {
        var builder = new StringBuilder();
        builder.AppendLine(FileHeader);
        builder.AppendLine();
        builder.AppendLine($"[{ToRegPath(hive, subKeyPath)}]");

        foreach (var valueName in key.GetValueNames())
        {
            RegistryValueKind kind;
            object? value;

            try
            {
                kind = key.GetValueKind(valueName);
                value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            }
            catch
            {
                continue;
            }

            var line = FormatValue(valueName, kind, value);
            if (line is not null)
            {
                builder.AppendLine(line);
            }
        }

        return builder.ToString();
    }

    /// <summary>把 hive + 子键路径拼成 .reg 里的完整路径（HKEY_LOCAL_MACHINE 而不是 HKLM）。</summary>
    private static string ToRegPath(RegistryHive hive, string subKeyPath) => hive switch
    {
        RegistryHive.LocalMachine => $@"HKEY_LOCAL_MACHINE\{subKeyPath}",
        RegistryHive.CurrentUser => $@"HKEY_CURRENT_USER\{subKeyPath}",
        RegistryHive.Users => $@"HKEY_USERS\{subKeyPath}",
        RegistryHive.ClassesRoot => $@"HKEY_CLASSES_ROOT\{subKeyPath}",
        _ => $@"{hive}\{subKeyPath}",
    };

    /// <summary>把一个值格式化成 .reg 语法。</summary>
    private static string? FormatValue(string valueName, RegistryValueKind kind, object? value)
    {
        // 默认值在 .reg 里写成 @=
        var name = valueName.Length == 0 ? "@" : $"\"{EscapeString(valueName)}\"";

        return kind switch
        {
            RegistryValueKind.String => $"{name}=\"{EscapeString(value as string)}\"",

            RegistryValueKind.ExpandString =>
                $"{name}=hex(2):{ToHex(Encoding.Unicode.GetBytes(((value as string) ?? string.Empty) + "\0"))}",

            RegistryValueKind.MultiString =>
                $"{name}=hex(7):{ToHex(Encoding.Unicode.GetBytes(JoinMultiString(value as string[])))}",

            RegistryValueKind.Binary =>
                $"{name}=hex:{ToHex(value as byte[] ?? [])}",

            RegistryValueKind.DWord =>
                $"{name}=dword:{(value is int i ? i : 0):x8}",

            RegistryValueKind.QWord =>
                $"{name}=hex(b):{ToHex(BitConverter.GetBytes(value is long l ? l : 0L))}",

            _ => null,
        };
    }

    /// <summary>多字符串在 .reg 里的形态是每个字符串 + \0，最后再补一个 \0。</summary>
    private static string JoinMultiString(string[]? values)
    {
        if (values is null || values.Length == 0)
        {
            return "\0";
        }

        var builder = new StringBuilder();
        foreach (var item in values)
        {
            builder.Append(item).Append('\0');
        }

        builder.Append('\0');
        return builder.ToString();
    }

    private static string ToHex(byte[] bytes)
        => string.Join(",", bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));

    /// <summary>转义 .reg 字符串里的反斜杠与引号。</summary>
    private static string EscapeString(string? text)
        => (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? "key" : result;
    }
}
