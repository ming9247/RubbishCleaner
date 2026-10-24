using Microsoft.Win32;

namespace RubbishCleaner.Common.Registry;

/// <summary>删除一条注册表值的结果。</summary>
public sealed record RegistryDeleteResult(bool Success, string Message)
{
    public static RegistryDeleteResult Ok(string message = "已删除") => new(true, message);

    public static RegistryDeleteResult Fail(string message) => new(false, message);
}

/// <summary>
/// 注册表<b>值</b>的删除能力。
///
/// 安全约束（这是不可逆操作，必须严格遵守）：
/// 1. <b>只删值，绝不删键</b>。调用方传进来的子键路径只用来定位，不会被删除，
///    即使这个键下只剩一个值。
/// 2. 删除前必须确认目标值确实存在，避免"值已被别的程序删掉"时误报成功。
/// 3. 任何异常都转成 <see cref="RegistryDeleteResult"/>，绝不向上抛。
/// </summary>
public static class RegistryWriter
{
    /// <summary>删除指定注册表值。</summary>
    public static RegistryDeleteResult DeleteValue(RegistryHive hive, RegistryView view, string subKeyPath, string valueName)
    {
        if (string.IsNullOrWhiteSpace(subKeyPath))
        {
            return RegistryDeleteResult.Fail("子键路径为空，为安全起见中止操作");
        }

        if (string.IsNullOrWhiteSpace(valueName))
        {
            // 空值名代表"默认值"，误删影响面大，直接拒绝。
            return RegistryDeleteResult.Fail("值名为空（默认值），拒绝删除");
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKeyPath, writable: true);
            if (key is null)
            {
                return RegistryDeleteResult.Fail("注册表键不存在（可能已被删除）");
            }

            var existingNames = key.GetValueNames();
            if (!existingNames.Any(n => string.Equals(n, valueName, StringComparison.OrdinalIgnoreCase)))
            {
                return RegistryDeleteResult.Fail("注册表值不存在（可能已被删除）");
            }

            key.DeleteValue(valueName, throwOnMissingValue: false);

            // 复核：只有确认真的删掉了才报成功，避免静默失败。
            var remaining = key.GetValueNames();
            return remaining.Any(n => string.Equals(n, valueName, StringComparison.OrdinalIgnoreCase))
                ? RegistryDeleteResult.Fail("删除后该值仍然存在，可能权限不足")
                : RegistryDeleteResult.Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return RegistryDeleteResult.Fail("权限不足，请以管理员身份运行");
        }
        catch (System.Security.SecurityException)
        {
            return RegistryDeleteResult.Fail("权限不足，请以管理员身份运行");
        }
        catch (Exception ex)
        {
            return RegistryDeleteResult.Fail($"删除失败：{ex.Message}");
        }
    }

    /// <summary>读取单个字符串值（用于删除前备份）。</summary>
    public static string? ReadValue(RegistryHive hive, RegistryView view, string subKeyPath, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKeyPath, writable: false);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }
}
