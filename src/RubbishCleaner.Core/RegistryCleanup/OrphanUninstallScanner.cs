using System.Text.RegularExpressions;
using Microsoft.Win32;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.RegistryCleanup;

/// <summary>
/// 孤儿 Uninstall 条目扫描器（v0.4）。
///
/// <b>唯一判据</b>：Windows Installer 自己说这个产品没装。
/// 两条同时成立才算孤儿：
/// <list type="number">
/// <item><c>MsiQueryProductState</c> 返回 ABSENT（1/2/3）；</item>
/// <item><c>MsiEnumProducts</c> 的已安装列表里<b>没有</b>它。</item>
/// </list>
///
/// <b>刻意不看的东西</b>（每一条都是真机调研换来的）：
/// <list type="bullet">
/// <item>不看 <c>Classes\Installer\Products\{GUID}</c> 是否存在——
/// 实测 CC Switch 产品完好但该键不存在，按它判会误删；</item>
/// <item>不看 <c>InstallLocation</c> 目录是否存在——
/// 用户手动删目录很常见，但软件其实是"装着"的（本机 CC Switch 现在正是这样）；</item>
/// <item>不看 <c>LocalPackage</c> 是否存在——用户可能清过 <c>C:\Windows\Installer</c> 缓存。</item>
/// </list>
/// </summary>
public sealed class OrphanUninstallScanner : IOrphanUninstallScanner
{
    /// <summary>产品 GUID 的严格形态：{8-4-4-4-12}。</summary>
    private static readonly Regex GuidPattern = new(
        @"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>扫描的位置：机器级 64/32 位 + 当前用户。</summary>
    private static readonly (RegistryHive Hive, RegistryView View, string Tag)[] Locations =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM\\64"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM\\32"),
        (RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU\\64"),
    ];

    public Task<OrphanScanResult> ScanAsync(
        IProgress<OrphanScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(progress, cancellationToken), cancellationToken);

    private static OrphanScanResult Scan(
        IProgress<OrphanScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var degraded = new List<string>();

        progress?.Report(new OrphanScanProgress("正在读取 Windows Installer 的产品列表…"));

        // 先拿一次已安装产品列表：整个扫描期间只取一次（MSI 数据库读取不便宜）
        var installedProducts = MsiInterop.EnumerateProducts();
        if (installedProducts.Count == 0)
        {
            degraded.Add("无法枚举 Windows Installer 的已安装产品（msi.dll 不可用或权限不足），本次没有扫描");
            return new OrphanScanResult
            {
                Entries = [],
                ScannedCount = 0,
                DegradedReason = string.Join("；", degraded),
                Duration = watch.Elapsed,
            };
        }

        var entries = new List<OrphanUninstallEntry>();
        var scanned = 0;

        foreach (var (hive, view, tag) in Locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new OrphanScanProgress($"正在检查 {tag}…", scanned, 0, tag));

            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(UninstallRoot);
                if (uninstall is null)
                {
                    continue;
                }

                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // 只看 MSI 条目：只有它们才有"产品是否还存在"的可靠判据。
                    // 非 MSI 条目的卸载命令可能指向任何东西，无法判断。
                    if (!LooksLikeWindowsInstallerEntry(uninstall, subKeyName))
                    {
                        continue;
                    }

                    scanned++;

                    // 闸门 R1：键名必须是严格 GUID。
                    // 非 GUID 的 MSI 条目结构未知，不碰（实测本机 0 个，但规则要有）。
                    if (!GuidPattern.IsMatch(subKeyName))
                    {
                        continue;
                    }

                    var entry = Evaluate(hive, view, uninstall, subKeyName, installedProducts);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }
                }
            }
            catch (System.Security.SecurityException ex)
            {
                degraded.Add($"{tag} 权限不足：{ex.Message}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                degraded.Add($"{tag} 读取失败：{ex.Message}");
            }
        }

        watch.Stop();
        progress?.Report(new OrphanScanProgress("完成", scanned, scanned));

        return new OrphanScanResult
        {
            Entries = entries
                .OrderByDescending(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ScannedCount = scanned,
            DegradedReason = degraded.Count > 0 ? string.Join("；", degraded) : null,
            Duration = watch.Elapsed,
        };
    }

    /// <summary>该子键是不是 MSI 条目（<c>WindowsInstaller = 1</c>）。</summary>
    private static bool LooksLikeWindowsInstallerEntry(RegistryKey uninstall, string subKeyName)
    {
        try
        {
            using var sub = uninstall.OpenSubKey(subKeyName);
            return sub?.GetValue("WindowsInstaller") is int value && value == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 判定单个条目。返回 null 表示"不是孤儿"（包括无法判定的情况——不确定就不报）。
    /// </summary>
    private static OrphanUninstallEntry? Evaluate(
        RegistryHive hive,
        RegistryView view,
        RegistryKey uninstall,
        string productCode,
        HashSet<string> installedProducts)
    {
        string? displayName;
        string? displayVersion;
        string? publisher;
        string? installLocation;

        try
        {
            using var sub = uninstall.OpenSubKey(productCode);
            if (sub is null)
            {
                return null;
            }

            displayName = RegistryTextSanitizer.Clean(sub.GetValue("DisplayName") as string);
            displayVersion = RegistryTextSanitizer.Clean(sub.GetValue("DisplayVersion") as string);
            publisher = RegistryTextSanitizer.Clean(sub.GetValue("Publisher") as string);
            installLocation = RegistryTextSanitizer.Clean(sub.GetValue("InstallLocation") as string);
        }
        catch
        {
            return null;
        }

        // ---------- 判据 1：MSI 报告的状态 ----------
        var state = MsiInterop.QueryProductState(productCode);

        // 状态存在（已安装/公告）→ 不是孤儿，直接返回。
        // 这是本机 CC Switch 的形态：文件被清掉了，但产品仍然有效。
        if (MsiInterop.IsPresent(state))
        {
            return null;
        }

        // 不确定（UNKNOWN / API 不可用）→ 不报，保守。
        if (!MsiInterop.IsAbsent(state))
        {
            return null;
        }

        // ---------- 判据 2：不在已安装产品枚举里 ----------
        var inEnumeration = installedProducts.Contains(productCode);
        if (inEnumeration)
        {
            // 状态说 ABSENT 但枚举里有：两个 API 打架，不确定 → 不报。
            return null;
        }

        var evidence = new List<string>
        {
            $"Windows Installer 报告该产品未安装：{MsiInterop.DescribeState(state)}",
            "该产品不在已安装产品列表中（MsiEnumProducts 交叉验证）",
            $"应用和功能列表里仍保留此条目：{productCode}",
        };

        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            evidence.Add($"记录中的安装位置：{installLocation}"
                         + (Directory.Exists(installLocation.Trim().TrimEnd('\\'))
                             ? "（目录仍存在）"
                             : "（目录已不存在）"));
        }

        return new OrphanUninstallEntry
        {
            ProductCode = productCode,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? productCode : displayName,
            DisplayVersion = displayVersion,
            Publisher = publisher,
            InstallLocation = installLocation,
            Hive = hive,
            View = view,
            UninstallRootPath = UninstallRoot,
            MsiState = state,
            Evidence = evidence,
        };
    }
}
