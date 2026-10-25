using RubbishCleaner.Core.Integrity;
        using RubbishCleaner.Core.Uninstall;

internal static class IntegrityProbe
{
    public static async Task<int> RunAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine("=== v0.5 真机验证（只读）===");

        Console.WriteLine();
        Console.WriteLine("--- 1. 服务 ImagePath 孤儿 ---");
        var svc = await new ServiceOrphanScanner().ScanAsync();
        Console.WriteLine($"检查服务 {svc.ScannedCount} 个，跳过 {svc.SkippedCount} 个，报告 {svc.Entries.Count} 条，用时 {svc.Duration.TotalMilliseconds:F0}ms");
        Console.WriteLine($"降级: {svc.DegradedReason ?? "无"}");
        foreach (var e in svc.Entries)
{
    Console.WriteLine($"  [{e.StartModeText}/{e.StateText}] {e.ServiceName}（{e.DisplayName}）");
    Console.WriteLine($"    缺失: {e.ResolvedPath}");
    Console.WriteLine($"    原始: {e.ImagePathRaw}");
    foreach (var line in e.Evidence) Console.WriteLine($"    · {line}");
    Console.WriteLine($"    手动: {e.ManualCommand}   CanAutoClean={e.CanAutoClean}");
}

        Console.WriteLine();
        Console.WriteLine("--- 2. 文件与注册表状态不一致 ---");
        var app = await new AppStateScanner(() => new InstalledAppEnumerator().Enumerate()).ScanAsync();
        Console.WriteLine($"检查软件 {app.CheckedCount} 个，报告 {app.Entries.Count} 条，用时 {app.Duration.TotalMilliseconds:F0}ms");
        Console.WriteLine($"降级: {app.DegradedReason ?? "无"}");
        foreach (var g in app.Entries.GroupBy(e => e.Kind))
{
    Console.WriteLine($"  [{g.Key}] {g.Count()} 条");
    foreach (var e in g.Take(6))
    {
        Console.WriteLine($"    {e.DisplayName}" + (e.ProductCode is null ? "" : $" ({e.ProductCode})"));
        foreach (var line in e.Evidence.Take(2)) Console.WriteLine($"      · {line}");
    }
    if (g.Count() > 6) Console.WriteLine($"    … 另有 {g.Count() - 6} 条");
}

        Console.WriteLine();
        Console.WriteLine("结论：真机验证完成（全程只读）");
        return 0;
    }
}
