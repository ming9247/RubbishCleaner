using Microsoft.Win32;
using RubbishCleaner.Common.Registry;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Cleanup;

/// <summary>
/// 清理残留启动项。
///
/// <b>安全原则（本类的全部价值都在这里）</b>：
///
/// 1. <b>只删"失效"的项</b>——目标文件必须确实不存在。
///    删除前<b>重新校验一次</b>，不信任扫描时的旧结果：
///    从扫描到用户点确认之间，程序可能已被重装，此时绝不能删。
/// 2. <b>只删注册表 Run / RunOnce 类的值</b>。
///    服务、计划任务涉及系统机制，删除影响面大，明确不做（留给后续版本单独设计）。
/// 3. <b>只删值，不删键</b>——键下可能还有别的启动项。
/// 4. <b>先备份再删</b>，备份是可双击还原的 .reg 文件。
/// 5. 任何一项失败都不影响其余项，逐项报告结果。
/// </summary>
public sealed class StartupCleaner
{
    private static readonly StartupSourceKind[] RemovableKinds =
    [
        StartupSourceKind.RegistryRunHkcu,
        StartupSourceKind.RegistryRunHklm,
        StartupSourceKind.RegistryRunWow64,
    ];

    /// <summary>由软件画像生成清理计划；不合规的项会进入 <see cref="CleanupPlan.Skipped"/>。</summary>
    public CleanupPlan BuildPlan(IEnumerable<SoftwareProfile> profiles)
    {
        var targets = new List<CleanupTarget>();
        var skipped = new List<(string, string)>();

        foreach (var profile in profiles)
        {
            foreach (var entry in profile.StartupEntries)
            {
                if (!entry.IsOrphan)
                {
                    // 目标文件还在 —— 这不是残留，不碰。
                    continue;
                }

                if (!RemovableKinds.Contains(entry.Source))
                {
                    skipped.Add((entry.Name,
                        $"{entry.Source.ToDisplayName()} 暂不支持自动清理（仅支持注册表 Run/RunOnce）"));
                    continue;
                }

                if (entry.RegistryAnchor is null)
                {
                    skipped.Add((entry.Name, "缺少注册表定位信息"));
                    continue;
                }

                targets.Add(new CleanupTarget(
                    profile.DisplayName,
                    entry.Name,
                    entry.Source,
                    entry.RegistryAnchor,
                    entry.ExecutablePath,
                    entry.RawCommand));
            }
        }

        return new CleanupPlan { Targets = targets, Skipped = skipped };
    }

    /// <summary>
    /// 生成可双击还原的 .reg 备份文件，返回路径。
    /// 备份失败时调用方<b>必须中止</b>删除，否则不可逆。
    /// </summary>
    public string WriteBackup(CleanupPlan plan, string directory, DateTime timestamp)
    {
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"RubbishCleaner-启动项备份-{timestamp:yyyyMMdd-HHmmss}.reg");
        var lines = new List<string>
        {
            "Windows Registry Editor Version 5.00",
            string.Empty,
            "; RubbishCleaner 自动生成 —— 双击本文件可将以下启动项还原到注册表",
            $"; 生成时间：{timestamp:yyyy-MM-dd HH:mm:ss}",
            $"; 条目数：{plan.Count}",
            string.Empty,
        };

        foreach (var group in plan.Targets.GroupBy(t => (t.Anchor.Hive, t.Anchor.View, t.Anchor.SubKeyPath)))
        {
            var (hive, view, subKey) = group.Key;
            lines.Add($"[{RegPath(hive, view, subKey)}]");

            foreach (var target in group)
            {
                var value = target.RawCommand ?? target.Anchor.ValueName;
                lines.Add($"\"{Escape(target.Anchor.ValueName)}\"=\"{Escape(value)}\"");
            }

            lines.Add(string.Empty);
        }

        // .reg 文件必须是 UTF-16 LE 带 BOM，否则中文路径会乱码、无法导入。
        File.WriteAllLines(path, lines, new System.Text.UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        return path;
    }

    /// <summary>执行清理；逐项返回结果。</summary>
    public IReadOnlyList<CleanupResult> Execute(CleanupPlan plan)
    {
        var results = new List<CleanupResult>(plan.Count);

        foreach (var target in plan.Targets)
        {
            var anchor = target.Anchor;

            // 关键：重新校验目标文件是否仍然不存在。
            // 扫描到点击确认之间可能已重装，此时删除会破坏用户的正常启动项。
            if (FileStillExists(target.ExecutablePath))
            {
                results.Add(new CleanupResult(target, false, "目标文件已恢复存在，已跳过（不删除）"));
                continue;
            }

            var outcome = RegistryWriter.DeleteValue(anchor.Hive, anchor.View, anchor.SubKeyPath, anchor.ValueName);
            results.Add(new CleanupResult(target, outcome.Success, outcome.Message));
        }

        return results;
    }

    /// <summary>单条清理结果。</summary>
    public sealed record CleanupResult(CleanupTarget Target, bool Success, string Message);

    private static bool FileStillExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;   // 没有路径信息时沿用"扫描时的失效判定"
        }

        if (!Path.IsPathRooted(path))
        {
            return false;   // 相对路径无法判定，沿用扫描结果
        }

        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static string RegPath(RegistryHive hive, RegistryView view, string subKey)
    {
        var prefix = hive switch
        {
            RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
            RegistryHive.LocalMachine => view == RegistryView.Registry32
                ? @"HKEY_LOCAL_MACHINE\Software\WOW6432Node"
                : "HKEY_LOCAL_MACHINE",
            _ => hive.ToString(),
        };

        // Registry32 视图下这里给的是 WOW6432Node 前缀，子键里不再包含它
        var suffix = view == RegistryView.Registry32 && hive == RegistryHive.LocalMachine
                     && subKey.StartsWith(@"Software\", StringComparison.OrdinalIgnoreCase)
            ? subKey[@"Software\".Length..]
            : subKey;

        return $@"{prefix}\{suffix}";
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
