using Microsoft.Win32;

namespace RubbishCleaner.Core.Models;

/// <summary>
/// 注册表定位信息：能唯一定位到一个"值"（而不是整个键）。
///
/// 刻意使用强类型而不是字符串拼接，是为了让"删除"这类不可逆操作
/// 在编译期就能被检查到，并且可以被单元测试逐字段断言。
/// </summary>
public sealed record RegistryAnchor(
    RegistryHive Hive,
    RegistryView View,
    string SubKeyPath,
    string ValueName)
{
    /// <summary>人类可读的完整路径，例如 <c>HKCU\Software\...\Run\QQNT</c>。</summary>
    public string FullPath => $@"{HiveName}\{SubKeyPath}\{ValueName}";

    public string HiveName => Hive switch
    {
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.Users => "HKU",
        RegistryHive.ClassesRoot => "HKCR",
        _ => Hive.ToString(),
    };

    public override string ToString() => FullPath;
}
