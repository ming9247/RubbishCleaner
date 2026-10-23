using Microsoft.Win32;

namespace RubbishCleaner.Common.Registry;

/// <summary>单个注册表值（已展开环境变量）。</summary>
public sealed record RegistryValueInfo(string Name, string? Value, RegistryValueKind Kind);

/// <summary>
/// 注册表安全读取封装。
/// 约定：读取失败一律转成"空结果 + 错误信息"，绝不向上抛异常，
/// 因为单个键不可读不应中断整轮扫描。
/// </summary>
public static class RegistryReader
{
    /// <summary>读取某个键下的所有字符串值，并展开其中的 <c>%VAR%</c>。</summary>
    public static IReadOnlyList<RegistryValueInfo> ReadStringValues(
        RegistryHive hive,
        string subKeyPath,
        RegistryView view,
        out string? error)
    {
        error = null;
        var values = new List<RegistryValueInfo>();

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKeyPath, writable: false);
            if (key is null)
            {
                // 键不存在是正常情况（例如某些系统没有 WOW6432Node），不算错误。
                return values;
            }

            foreach (var name in key.GetValueNames())
            {
                try
                {
                    var kind = key.GetValueKind(name);
                    if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
                    {
                        continue;
                    }

                    var raw = key.GetValue(name) as string;
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    values.Add(new RegistryValueInfo(name, Expand(raw), kind));
                }
                catch (Exception ex)
                {
                    error ??= ex.Message;
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return values;
    }

    /// <summary>枚举子键名；失败返回空集合并给出错误信息。</summary>
    public static IReadOnlyList<string> EnumerateSubKeyNames(
        RegistryHive hive,
        string subKeyPath,
        RegistryView view,
        out string? error)
    {
        error = null;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKeyPath, writable: false);
            return key is null ? [] : key.GetSubKeyNames();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return [];
        }
    }

    /// <summary>读取单个键下的指定值；不存在返回 null。</summary>
    public static object? ReadValue(RegistryHive hive, string subKeyPath, string valueName, RegistryView view)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKeyPath, writable: false);
            return key?.GetValue(valueName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>展开环境变量，并去除首尾空白与包裹引号。</summary>
    public static string Expand(string raw)
    {
        var expanded = Environment.ExpandEnvironmentVariables(raw).Trim();
        return expanded.Length > 1 && expanded[0] == '"' && expanded[^1] == '"'
            ? expanded[1..^1].Trim()
            : expanded;
    }
}
