// v0.4 注册表清理的真机探针（不依赖 WPF，不碰任何真实软件键）
//
// 两个模式：
//   e2e   —— 全链路：造孤儿条目 → 扫描 → 计划 → 备份 → 删键 → reg import 还原 → 现场清理
//   views —— 32 位视图 / 三上下文实测（只读）：条目统计、两视图差集、HKCU 重定向、备份路径
//
// 用法：
//   dotnet run --project tools/RubbishCleaner.RegE2E -- e2e
//   dotnet run --project tools/RubbishCleaner.RegE2E -- views
using System.Diagnostics;
using Microsoft.Win32;
using RubbishCleaner.Core.RegistryCleanup;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 模式分发：默认跑 e2e，保持既有脚本与习惯不变
var mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "e2e";
if (mode is "views" or "view" or "32")
{
    return await ViewProbe.RunAsync();
}

if (mode is "e2e32" or "32bit")
{
    return await E2E32.RunAsync();
}

if (mode is not ("e2e" or "all"))
{
    Console.WriteLine($"未知模式：{mode}（可用：e2e / e2e32 / views）");
    return 2;
}

const string TestGuid = "{DEADBEEF-1111-2222-3333-444455556666}";
const string TestName = "RC E2E 孤儿样本";
const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

var view = RegistryView.Registry64;
var targetPath = $@"{UninstallRoot}\{TestGuid}";

// 机器级写不了就退回 HKCU（标准用户跑时；判据与删除逻辑完全一致，只是 hive 不同）
RegistryHive hive = RegistryHive.LocalMachine;
try
{
    using var probe = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view)
        .CreateSubKey(@"SOFTWARE\RubbishCleanerE2EProbe");
    probe.Dispose();
    RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view)
        .DeleteSubKeyTree(@"SOFTWARE\RubbishCleanerE2EProbe", throwOnMissingSubKey: false);
    hive = RegistryHive.LocalMachine;
    Console.WriteLine("hive: HKLM（有写权限）");
}
catch (Exception ex)
{
    hive = RegistryHive.CurrentUser;
    Console.WriteLine($"hive: HKCU（HKLM 不可写：{ex.GetType().Name} {ex.Message}）");
}

var backupDir = Path.Combine(Path.GetTempPath(), "rc-e2e-" + Guid.NewGuid().ToString("N")[..8]);
var failures = new List<string>();
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine((ok ? "  OK   " : "  FAIL ") + name + (detail is null ? "" : "  " + detail));
    if (!ok) failures.Add(name + (detail is null ? "" : " -> " + detail));
}

try
{
    // ---------- 阶段 1：造一个真实的孤儿条目 ----------
    using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
    using (var key = baseKey.CreateSubKey(targetPath))
    {
        key.SetValue("DisplayName", TestName);
        key.SetValue("DisplayVersion", "0.0.1-e2e");
        key.SetValue("Publisher", "RubbishCleaner E2E");
        key.SetValue("WindowsInstaller", 1, RegistryValueKind.DWord);
        key.SetValue("UninstallString", "MsiExec.exe /X" + TestGuid);
        key.SetValue("InstallLocation", @"D:\NoSuchDir\RcE2E");
    }

    Console.WriteLine($"阶段1：已造测试条目 {targetPath}");
    // 探针不引用 Core 的 internal MsiInterop（那层是 P/Invoke 细节），
    // 只用扫描结果里的公开字段复述判定，保证探针本身不"帮"被测代码
    static bool IsAbsentState(uint s) => s is 1 or 2 or 3 or 4294967295u or 4294967294u;

    // ---------- 阶段 2：扫描 ----------
    var scanner = new OrphanUninstallScanner();
    var scan = await scanner.ScanAsync();
    Console.WriteLine($"阶段2：扫描 {scan.ScannedCount} 个 MSI 条目，报出 {scan.Entries.Count} 个孤儿，用时 {scan.Duration.TotalMilliseconds:F0}ms");
    var hit = scan.Entries.FirstOrDefault(e => e.ProductCode.Equals(TestGuid, StringComparison.OrdinalIgnoreCase));

    if (hive == RegistryHive.CurrentUser)
    {
        // 扫描器的 HKCU 位置是 Registry64；若本机 HKCU 视图差异导致取不到，明确说明而不是谎报通过
        Check("造出来的孤儿条目被命中", hit is not null, hit is null ? "（HKCU 形态未命中，需确认扫描位置）" : null);
    }
    else
    {
        Check("造出来的孤儿条目被命中", hit is not null);
    }

    if (hit is not null)
    {
        Console.WriteLine($"  判据状态：{hit.MsiState}（{hit.MsiStateText}）");
        Console.WriteLine("  证据链：");
        foreach (var line in hit.Evidence) Console.WriteLine("    · " + line);
        Check("证据链含 MSI 判定", hit.Evidence.Any(e => e.Contains("Windows Installer")));
        Check("证据链含枚举交叉验证", hit.Evidence.Any(e => e.Contains("MsiEnumProducts") || e.Contains("已安装产品列表")));
    }

    // 反向：本机所有条目里不应该出现"MSI 说已安装"的
    Check("没有把 MSI 已安装的产品报成孤儿",
        scan.Entries.All(e => IsAbsentState(e.MsiState)));

    if (hit is null)
    {
        throw new InvalidOperationException("孤儿条目没被扫到，后续步骤没有意义");
    }

    // ---------- 阶段 3：计划 + 执行（真删，但删的是自己造的键） ----------
    var cleaner = new OrphanCleaner(backupDir);
    var plan = cleaner.BuildPlan([hit]);
    Console.WriteLine($"阶段3：计划 {plan.Count} 项，跳过 {plan.Skipped.Count} 项");
    Check("计划包含该条目", plan.Count == 1);

    var results = cleaner.Execute(plan);
    foreach (var r in results)
    {
        Console.WriteLine($"  → {(r.Success ? "成功" : "失败")}：{r.Message}");
    }

    Check("删除成功", results.All(r => r.Success));

    using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
    using (var key = baseKey.OpenSubKey(targetPath))
    {
        Check("删除后键确实不存在", key is null);
    }

    // ---------- 阶段 4：备份可还原（reg import 实质验证） ----------
    var backup = results.FirstOrDefault()?.BackupPath;
    Check("有备份文件", backup is not null && File.Exists(backup));
    if (backup is not null)
    {
        Console.WriteLine($"阶段4：备份 {Path.GetFileName(backup)}（{new FileInfo(backup).Length} 字节）");
        var bytes = File.ReadAllBytes(backup);
        Check("备份编码为 UTF-16LE + BOM", bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xFE);

        var psi = new ProcessStartInfo("reg.exe", $"import \"{backup}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(15000);
        Console.WriteLine($"  reg import 退出码={proc.ExitCode} {stdout.Trim()} {stderr.Trim()}");
        Check("reg import 成功", proc.ExitCode == 0);

        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var restored = baseKey.OpenSubKey(targetPath);
        Check("还原后键回来了", restored is not null);
        if (restored is not null)
        {
            Check("DisplayName 一致", restored.GetValue("DisplayName") as string == TestName,
                restored.GetValue("DisplayName") as string);
            Check("WindowsInstaller 一致", restored.GetValue("WindowsInstaller") is int i && i == 1);
            Check("UninstallString 一致",
                restored.GetValue("UninstallString") as string == "MsiExec.exe /X" + TestGuid);
        }
    }
}
finally
{
    // ---------- 现场清理：只删自己造的键 ----------
    try
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        baseKey.DeleteSubKeyTree(targetPath, throwOnMissingSubKey: false);
        Console.WriteLine("阶段5：现场已清理（测试键已删除）");
    }
    catch (Exception ex)
    {
        Console.WriteLine("阶段5：现场清理失败：" + ex.Message);
    }

    try { if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true); } catch { }
}

Console.WriteLine();
Console.WriteLine(failures.Count == 0 ? "结论：E2E 全部通过" : $"结论：{failures.Count} 项失败");
foreach (var f in failures) Console.WriteLine("  · " + f);
return failures.Count == 0 ? 0 : 1;