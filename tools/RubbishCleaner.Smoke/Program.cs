using RubbishCleaner.Common.Diagnostics;
using RubbishCleaner.Core.Detection;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Trust;

namespace RubbishCleaner.Smoke;

/// <summary>
/// 无界面冒烟验证工具：
/// 直接调用真实的检测引擎扫描本机，用于开发期与 CI 中验证"能跑通、有结果、不崩溃"。
/// 只读，不做任何修改。
/// 用法：dotnet run --project tools/RubbishCleaner.Smoke [-- --json] [-- --top N]
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var jsonMode = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        var top = ReadIntArg(args, "--top") ?? 20;

        var environment = EnvironmentProbe.Capture();
        if (!jsonMode)
        {
            Console.WriteLine(EnvironmentProbe.ToReport(environment));
            Console.WriteLine(new string('=', 78));
        }

        var trustTable = TrustTableLoader.Load();
        var scanner = StartupScanner.CreateDefault(trustTable);

        if (!jsonMode)
        {
            Console.WriteLine($"特征库：{trustTable.Source}（白名单 {trustTable.Whitelist.Count} 条 / 黑名单 {trustTable.Blacklist.Count} 条）");
            Console.WriteLine("开始扫描…");
        }

        var progress = new Progress<ScanProgress>(p =>
        {
            if (!jsonMode)
            {
                Console.Write($"\r  {p.Stage} {p.Completed}/{p.Total}   ");
            }
        });

        var result = await scanner.ScanAsync(progress);

        if (!jsonMode)
        {
            Console.WriteLine();
            Console.WriteLine(new string('-', 78));
        }

        if (jsonMode)
        {
            PrintJson(result);
        }
        else
        {
            PrintHuman(result, top);
        }

        return result.Profiles.Count > 0 ? 0 : 2;
    }

    private static void PrintHuman(ScanResult result, int top)
    {
        var orphanEntries = result.Profiles.Sum(p => p.OrphanEntries.Count);
        var orphanProfiles = result.Profiles.Count(p => p.OrphanEntries.Count > 0);

        Console.WriteLine($"扫描完成：{result.Profiles.Count} 个程序，{result.TotalStartupEntries} 个自启动点，用时 {result.Duration.TotalMilliseconds:F0} ms");
        Console.WriteLine($"高风险 {result.HighRiskCount} · 疑似 {result.SuspiciousCount} · 正常 {result.NormalCount}");
        Console.WriteLine($"残留启动项：{orphanEntries} 条（涉及 {orphanProfiles} 个程序，目标文件已不存在）");
        Console.WriteLine($"权限：{(result.IsElevated ? "管理员" : "标准用户")}");

        if (result.DegradedSources.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("降级的数据源：");
            foreach (var source in result.DegradedSources)
            {
                Console.WriteLine($"  - {source.Kind.ToDisplayName()}：{source.Error}");
            }
        }

        // 残留单独列出来：这是本工具最直接的"可操作项"。
        var orphans = result.Profiles.Where(p => p.OrphanEntries.Count > 0).ToList();
        if (orphans.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("失效启动项（可清理）：");
            foreach (var profile in orphans)
            {
                foreach (var entry in profile.OrphanEntries)
                {
                    Console.WriteLine($"  · [{entry.Source.ToDisplayName()}] {entry.Name} -> {entry.ExecutablePath ?? entry.RawCommand}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"风险最高的 {Math.Min(top, result.Profiles.Count)} 项：");
        Console.WriteLine($"{"分数",-5}{"等级",-6}{"名称",-28}{"启动方式",-24}发行商");
        Console.WriteLine(new string('-', 110));

        foreach (var profile in result.Profiles.Take(top))
        {
            Console.WriteLine(
                $"{profile.Score.Total,-5}" +
                $"{profile.Score.Level.ToDisplayName(),-6}" +
                $"{Truncate(profile.DisplayName, 26),-28}" +
                $"{Truncate(profile.StartupSummary, 22),-24}" +
                $"{Truncate(profile.Publisher ?? "（无签名）", 30)}");
        }
    }

    private static void PrintJson(ScanResult result)
    {
        // 手写 JSON 避免为冒烟工具引入序列化依赖；字段与 ScanResult 一一对应。
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append($"\"durationMs\":{result.Duration.TotalMilliseconds:F0},");
        sb.Append($"\"isElevated\":{result.IsElevated.ToString().ToLowerInvariant()},");
        sb.Append($"\"highRisk\":{result.HighRiskCount},");
        sb.Append($"\"suspicious\":{result.SuspiciousCount},");
        sb.Append($"\"normal\":{result.NormalCount},");
        sb.Append($"\"startupEntries\":{result.TotalStartupEntries},");
        sb.Append($"\"orphanEntries\":{result.Profiles.Sum(p => p.OrphanEntries.Count)},");
        sb.Append("\"profiles\":[");
        sb.Append(string.Join(',', result.Profiles.Select(p =>
            $"{{\"name\":\"{Escape(p.DisplayName)}\",\"score\":{p.Score.Total},\"level\":\"{p.Score.Level}\"," +
            $"\"trust\":\"{p.Trust}\",\"running\":{p.IsRunning.ToString().ToLowerInvariant()}," +
            $"\"orphans\":{p.OrphanEntries.Count}," +
            $"\"sources\":[{string.Join(',', p.StartupEntries.Select(e => $"\"{e.Source}\"").Distinct())}]}}")));
        sb.Append("]}");
        Console.WriteLine(sb.ToString());
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "…";

    private static int? ReadIntArg(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value) ? value : null;
    }
}
