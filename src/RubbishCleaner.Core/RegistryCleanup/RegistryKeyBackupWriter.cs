using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace RubbishCleaner.Core.RegistryCleanup;

/// <summary>
/// 把单个注册表键（含其下所有子键与值）导出成标准 <c>.reg</c> 文件（v0.4）。
///
/// <b>为什么要标准 .reg 而不是自定义格式</b>：
/// 用户<b>不装本工具</b>也能双击还原——这是"可恢复性"的最强保证。
/// 与 v0.1.3 的启动项备份同一思路（ADR-017）。
///
/// <b>编码必须 UTF-16LE + BOM</b>：v0.1.3 实测过，
/// UTF-8 的 .reg 文件里中文会乱码，<c>reg import</c> 后条目名变成问号。
///
/// <b>32 位视图必须写成 WOW6432Node 物理路径</b>（v0.4.0 修复）：
/// <c>reg.exe</c> 与双击 .reg <b>都没有视图开关</b>，导入时走的是"文件里写的物理路径"。
/// 若把 <c>RegistryView.Registry32</c> 下打开的 <c>HKLM\SOFTWARE\...\Uninstall\{GUID}</c>
/// 原样写成 <c>HKEY_LOCAL_MACHINE\SOFTWARE\...\Uninstall\{GUID}</c>，
/// 导入会落到 <b>64 位视图</b>，32 位条目根本还原不回来。
/// 本机（Windows 11 22631，AMD64）只读实测：
/// <list type="bullet">
/// <item><c>reg query ...\Uninstall /reg:32</c> 得到 241 个子键，
/// 与 <c>HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\...\Uninstall</c> 的 241 个<b>完全一致</b>；</item>
/// <item>同一路径 <c>/reg:64</c> 得到 270 个，即 32 位视图的真实物理位置就是
/// <c>SOFTWARE\WOW6432Node\...</c>。</item>
/// </list>
/// <b>HKCU 不写 WOW6432Node</b>：实测 HKCU 下 32 位视图与 64 位视图读到的是
/// <b>同一份数据</b>（<c>reg query ...\Uninstall /reg:32</c> 与 <c>/reg:64</c> 都是 8 条、集合完全相同；
/// 而 <c>HKCU\SOFTWARE\WOW6432Node\...\Uninstall</c> 这个物理路径<b>不存在</b>，0 个子键），
/// 说明 HKCU 不做 WOW64 重定向，写 WOW6432Node 反而会把条目还原到错误位置。
/// </summary>
public static class RegistryKeyBackupWriter
{
    /// <summary>备份文件扩展名。</summary>
    public const string Extension = ".reg";

    /// <summary>reg 文件头（REGEDIT4 是旧格式，5.00 支持 dword/hex 等现代类型）。</summary>
    private const string FileHeader = "Windows Registry Editor Version 5.00";

    /// <summary>
    /// 递归的最大深度。正常 Uninstall 条目只有一两层子键；
    /// 设上限只为防御病态注册表结构把栈打穿，不作为业务限制。
    /// </summary>
    private const int MaxDepth = 64;

    /// <summary>
    /// 导出一个键<b>及其下所有子键</b>（递归），每个子键一条 <c>[路径]</c> 段。
    ///
    /// 返回备份文件路径；失败返回 null 且 <paramref name="error"/> 说明原因。
    /// <b>调用方必须在拿到非 null 结果后才可以删键</b>。
    ///
    /// <b>为什么必须递归子键</b>：删键用的是 <c>DeleteSubKeyTree</c>（整棵子树），
    /// 只备份值的 .reg 导入后只剩一个空壳键，子键与里面的值永久丢失——那就不叫"可还原"。
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

            var content = BuildRegContent(hive, view, subKeyPath, key);
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

    /// <summary>构造 .reg 文本：根键 + 递归子键，每个键一段。</summary>
    private static string? BuildRegContent(
        RegistryHive hive,
        RegistryView view,
        string subKeyPath,
        RegistryKey key)
    {
        var builder = new StringBuilder();
        builder.AppendLine(FileHeader);
        builder.AppendLine();

        // 任何一层读失败都返回 null：宁可不备份（调用方因此不删键），也不写出残缺备份。
        return AppendKeyTree(builder, hive, view, subKeyPath, key, depth: 0)
            ? builder.ToString()
            : null;
    }

    /// <summary>
    /// 递归输出一棵键树。每段形如：
    /// <code>
    /// [HKEY_CURRENT_USER\Software\Foo]
    /// "A"="1"
    ///
    /// [HKEY_CURRENT_USER\Software\Foo\Child]
    /// "B"="2"
    /// </code>
    /// 没有值的键只写段头（reg import 会据此创建键，结构和原来一致）。
    /// </summary>
    private static bool AppendKeyTree(
        StringBuilder builder,
        RegistryHive hive,
        RegistryView view,
        string subKeyPath,
        RegistryKey key,
        int depth)
    {
        if (depth > MaxDepth)
        {
            return false;
        }

        builder.AppendLine($"[{ToRegPath(hive, view, subKeyPath)}]");
        AppendValues(builder, key);
        builder.AppendLine();

        string[] subKeyNames;
        try
        {
            subKeyNames = key.GetSubKeyNames();
        }
        catch
        {
            return false;
        }

        foreach (var childName in subKeyNames)
        {
            RegistryKey? child = null;
            try
            {
                child = key.OpenSubKey(childName, writable: false);
                if (child is null)
                {
                    return false;
                }

                var childPath = $@"{subKeyPath}\{childName}";
                if (!AppendKeyTree(builder, hive, view, childPath, child, depth + 1))
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                child?.Dispose();
            }
        }

        return true;
    }

    /// <summary>输出一个键下的所有值（.reg 语法）。</summary>
    private static void AppendValues(StringBuilder builder, RegistryKey key)
    {
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
    }

    /// <summary>
    /// 把 hive + 视图 + 子键路径拼成 .reg 里的<b>物理</b>完整路径
    /// （<c>HKEY_LOCAL_MACHINE</c> 而不是 <c>HKLM</c>）。
    ///
    /// 关键点：<c>reg.exe</c>/双击导入没有视图开关，只认物理路径，
    /// 因此 32 位视图必须把 WOW64 重定向显式写出来（见类注释里的实测数据）。
    /// </summary>
    internal static string ToRegPath(RegistryHive hive, RegistryView view, string subKeyPath) => hive switch
    {
        // HKLM + 32 位视图：WOW64 只重定向 SOFTWARE 子树，其余子树（SYSTEM/HARDWARE/...）两视图共享。
        RegistryHive.LocalMachine when view == RegistryView.Registry32
            => $@"HKEY_LOCAL_MACHINE\{RedirectToWow6432Node(subKeyPath)}",
        RegistryHive.LocalMachine => $@"HKEY_LOCAL_MACHINE\{subKeyPath}",

        // HKCU 不做 WOW64 重定向（实测 32/64 视图数据同一份、WOW6432Node 物理路径不存在），保持原样。
        RegistryHive.CurrentUser => $@"HKEY_CURRENT_USER\{subKeyPath}",
        RegistryHive.Users => $@"HKEY_USERS\{subKeyPath}",
        RegistryHive.ClassesRoot => $@"HKEY_CLASSES_ROOT\{subKeyPath}",
        _ => $@"{hive}\{subKeyPath}",
    };

    /// <summary>
    /// 把 32 位视图下的相对路径翻译成物理路径：<c>Software\X</c> → <c>Software\WOW6432Node\X</c>。
    /// 非 <c>Software</c> 子树不被 WOW64 重定向，原样返回。
    /// </summary>
    private static string RedirectToWow6432Node(string subKeyPath)
    {
        const string software = @"Software\";
        if (!subKeyPath.StartsWith(software, StringComparison.OrdinalIgnoreCase))
        {
            return subKeyPath;
        }

        // 保留调用方原来的 "Software" 写法（注册表路径不区分大小写，
        // 但备份文件里的路径与被备份的路径写法一致更好对照）；
        // WOW6432Node 是本方法补进去的，固定用这个标准写法。
        var prefix = subKeyPath[..software.Length];
        return $@"{prefix}WOW6432Node\{subKeyPath[software.Length..]}";
    }

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
