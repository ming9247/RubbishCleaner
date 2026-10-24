using Microsoft.Win32;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Uninstall;

/// <summary>软件列表枚举结果。</summary>
public sealed record InstalledAppsResult(
    IReadOnlyList<InstalledApp> Apps,
    int TotalRawEntries,
    int FilteredOut,
    string? Error = null)
{
    public bool HasError => Error is not null;
}

/// <summary>
/// 从 Uninstall 注册表键枚举已安装软件。
///
/// 数据源三处（覆盖绝大多数安装方式）：
/// <list type="bullet">
/// <item>HKLM 64 位</item>
/// <item>HKLM WOW6432Node（32 位程序）</item>
/// <item>HKCU（仅当前用户安装）</item>
/// </list>
///
/// <b>过滤规则（很重要）</b>：实测本机 492 个有名字的条目里，376 个是 <c>SystemComponent=1</c>。
/// 这些是系统补丁/组件/运行库，若全部列出来，真正的软件会被淹没在噪声里。
/// </summary>
public sealed class InstalledAppEnumerator
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly (RegistryHive Hive, RegistryView View, AppScope Scope, bool Is32Bit)[] Sources =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64, AppScope.Machine, false),
        (RegistryHive.LocalMachine, RegistryView.Registry32, AppScope.Machine, true),
        (RegistryHive.CurrentUser, RegistryView.Registry64, AppScope.User, false),
    ];

    /// <summary>枚举已安装软件；单个键读取失败只跳过，不影响整体。</summary>
    public InstalledAppsResult Enumerate()
    {
        var apps = new List<InstalledApp>();
        var total = 0;
        var filtered = 0;
        string? error = null;

        foreach (var (hive, view, scope, is32) in Sources)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(UninstallPath, writable: false);
                if (key is null)
                {
                    continue;
                }

                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    total++;
                    try
                    {
                        using var sub = key.OpenSubKey(subKeyName, writable: false);
                        if (sub is null)
                        {
                            filtered++;
                            continue;
                        }

                        var app = ReadEntry(sub, subKeyName, hive, view, scope, is32);
                        if (app is null)
                        {
                            filtered++;
                            continue;
                        }

                        apps.Add(app);
                    }
                    catch
                    {
                        // 单个条目损坏（实测存在）不应中断整轮枚举
                        filtered++;
                    }
                }
            }
            catch (Exception ex)
            {
                error ??= ex.Message;
            }
        }

        // 按名称排序，便于用户查找
        var ordered = apps
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new InstalledAppsResult(ordered, total, filtered, error);
    }

    /// <summary>读取单个条目；不满足"可展示软件"条件时返回 null。</summary>
    private static InstalledApp? ReadEntry(
        RegistryKey sub,
        string subKeyName,
        RegistryHive hive,
        RegistryView view,
        AppScope scope,
        bool is32Bit)
    {
        var displayName = RegistryTextSanitizer.Clean(sub.GetValue("DisplayName") as string);
        if (displayName is null)
        {
            return null;   // 无名称的不是可展示软件
        }

        // 系统组件/补丁：排除，否则列表会被几百条运行库淹没
        if (sub.GetValue("SystemComponent") is int sc && sc == 1)
        {
            return null;
        }

        // 父级条目（子组件已在主条目中体现）
        if (!string.IsNullOrWhiteSpace(sub.GetValue("ParentKeyName") as string))
        {
            return null;
        }

        // 明确标记为不可卸载的
        if (sub.GetValue("NoRemove") is int nr && nr == 1)
        {
            return null;
        }

        var uninstallRaw = sub.GetValue("UninstallString") as string;
        var uninstall = RegistryTextSanitizer.Clean(uninstallRaw);
        if (uninstall is null)
        {
            return null;   // 没有卸载命令的条目对"卸载工具"无意义
        }

        var quietRaw = sub.GetValue("QuietUninstallString") as string;
        var publisherRaw = sub.GetValue("Publisher") as string;
        var locationRaw = sub.GetValue("InstallLocation") as string;
        var versionRaw = sub.GetValue("DisplayVersion") as string;

        // 判断文本是否被污染：清洗后与原始值明显不同即认为脏
        var corrupted =
            IsCorrupted(uninstallRaw, uninstall) ||
            IsCorrupted(publisherRaw, RegistryTextSanitizer.Clean(publisherRaw)) ||
            IsCorrupted(locationRaw, RegistryTextSanitizer.Clean(locationRaw));

        var size = sub.GetValue("EstimatedSize") is int kb ? kb : 0;

        return new InstalledApp
        {
            Id = $"{hive}-{view}-{subKeyName}",
            DisplayName = displayName,
            DisplayVersion = RegistryTextSanitizer.Clean(versionRaw),
            Publisher = RegistryTextSanitizer.Clean(publisherRaw),
            InstallLocation = RegistryTextSanitizer.Clean(locationRaw),
            RegistryPath = $@"{HiveName(hive)}\{UninstallPath}\{subKeyName}",
            Scope = scope,
            Is32Bit = is32Bit,
            UninstallCommand = uninstall,
            QuietUninstallCommand = RegistryTextSanitizer.Clean(quietRaw),
            IsMsi = sub.GetValue("WindowsInstaller") is int wi && wi == 1,
            EstimatedSizeKb = size,
            HasCorruptedText = corrupted,
        };
    }

    private static bool IsCorrupted(string? raw, string? cleaned)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        // 原始值与清洗结果长度差异明显 -> 有垃圾被截掉
        return cleaned is null || cleaned.Length < raw.Length * 0.8;
    }

    private static string HiveName(RegistryHive hive) => hive switch
    {
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.LocalMachine => "HKLM",
        _ => hive.ToString(),
    };
}
