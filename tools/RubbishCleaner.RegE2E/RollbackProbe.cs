using Microsoft.Win32;
using RubbishCleaner.Core.BackupHistory;
using RubbishCleaner.Core.RegistryCleanup;

internal static class RollbackProbe
{
    public static async Task<int> RunAsync()
    {

    Console.OutputEncoding = System.Text.Encoding.UTF8;

    const string TestGuid = "{DEADBEEF-4444-5555-6666-777788889999}";
    const string Root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    var subKey = $@"{Root}\{TestGuid}";
    var failures = new List<string>();
    void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine((ok ? "  OK   " : "  FAIL ") + name + (detail is null ? "" : "  " + detail));
    if (!ok) failures.Add(name);
}

    try
{
    // 1. 造孤儿
    using (var b = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
    using (var k = b.CreateSubKey(subKey))
    {
        k.SetValue("DisplayName", "RC v0.6 回滚验证");
        k.SetValue("WindowsInstaller", 1, RegistryValueKind.DWord);
    }
    Console.WriteLine("阶段1：已造 HKCU 孤儿条目");

    // 2. 清理（产生真实备份）
    var scanner = new OrphanUninstallScanner();
    var scan = await scanner.ScanAsync();
    var hit = scan.Entries.FirstOrDefault(e => e.ProductCode == TestGuid);
    Check("清理前命中", hit is not null);
    var cleaner = new OrphanCleaner();
    var plan = cleaner.BuildPlan([hit!]);
    var results = cleaner.Execute(plan);
    Check("清理成功（备份已生成）", results.Count == 1 && results[0].Success, results.FirstOrDefault()?.Message);
    var backupFile = results[0].BackupPath!;

    // 3. 备份历史扫描
    var store = new BackupHistoryStore();
    var history = await store.ListAsync();
    var entry = history.Entries.FirstOrDefault(e => e.FilePath == backupFile);
    Check("备份历史能扫到刚生成的备份", entry is not null, $"{history.Entries.Count} 个备份");
    if (entry is null) throw new InvalidOperationException("无备份可回滚");
    Console.WriteLine($"  条目：{entry.DisplayName} @ {entry.KeyPath}");
    Console.WriteLine($"  可回滚：{entry.CanRollback}（{entry.RollbackBlockReason ?? "无阻碍"}）");

    // 4. 键被删 → 回滚
    using (var b = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
    using (var k = b.OpenSubKey(subKey)) Check("清理后键已不存在（回滚前提）", k is null);

    var rollback = await store.RollbackAsync(entry);
    Console.WriteLine($"阶段4：回滚 → {(rollback.Success ? "成功" : "失败")}：{rollback.Message}");
    Check("回滚成功", rollback.Success);

    // 5. 复核
    using (var b = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
    using (var k = b.OpenSubKey(subKey))
    {
        Check("回滚后键回来了", k is not null);
        Check("DisplayName 与删除前一致", k?.GetValue("DisplayName") as string == "RC v0.6 回滚验证");
    }

    // 6. 再次回滚应被拒绝（键已存在 → 防覆盖）
    var again = await store.RollbackAsync(entry);
    Check("重复回滚被防线拒绝（目标键已存在）", !again.Success && again.Message.Contains("已存在"));
}
    finally
{
    using (var b = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
    {
        b.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
    }
    Console.WriteLine("现场已清理（测试键已删除；备份 .reg 保留在备份目录供人工检查）");
}

    Console.WriteLine();
    Console.WriteLine(failures.Count == 0 ? "结论：v0.6 真机回滚验证全部通过" : $"结论：{failures.Count} 项失败");

        return failures.Count == 0 ? 0 : 1;
    }
}
