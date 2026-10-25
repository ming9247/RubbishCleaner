// ============================================================================================
// 模式三：HKLM 32 位视图的真实往返（需要管理员）
//   用法：RubbishCleaner.RegE2E.exe e2e32
//
// 为什么单独做一个模式：v0.4.1 修了"32 位视图的 .reg 会还原到错误位置"这个真 bug，
// 但当时的验证只做到"备份段头写了 WOW6432Node、reg query 能查到"——
// 那是静态等价验证，不是真删真还原。真正的结论必须是：
//   在 32 位视图里造键 → 备份 → 删键 → reg import → 键真的回来了，且值一致。
// 这一步必须写 HKLM\SOFTWARE\WOW6432Node，所以要管理员。
//
// 安全边界（与 e2e 模式同一套纪律）：
//   * 只操作自己造的那一个 GUID 键；
//   * finally 一定删干净（无论成功失败）；
//   * 不用真实产品 GUID，用 DEADBEEF 段的自造 GUID。
// ============================================================================================
using Microsoft.Win32;
using RubbishCleaner.Core.RegistryCleanup;

internal static class E2E32
{
    private const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string TestGuid = "{DEADBEEF-2222-3333-4444-555566667777}";
    private const string TestName = "RC E2E 32位视图孤儿样本";

    public static async Task<int> RunAsync()
    {
        // 提权进程由 Start-Process -Verb RunAs 拉起时拿不到标准输出重定向
        // （-Verb 与 -RedirectStandardOutput 不能同时用），所以自己也写一份文件日志。
        var logPath = Path.Combine(Path.GetTempPath(), "rc-e2e32.log");
        try { if (File.Exists(logPath)) File.Delete(logPath); } catch { }

        void Emit(string line)
        {
            Console.WriteLine(line);
            try { File.AppendAllText(logPath, line + Environment.NewLine, System.Text.Encoding.UTF8); } catch { }
        }

        Emit($"日志文件：{logPath}");

        var failures = new List<string>();
        void Check(string name, bool ok, string? detail = null)
        {
            Emit((ok ? "  OK   " : "  FAIL ") + name + (detail is null ? "" : "  " + detail));
            if (!ok) failures.Add(name + (detail is null ? "" : " -> " + detail));
        }

        Emit("=== HKLM 32 位视图真实往返（备份 → 删键 → reg import 还原）===");

        var isElevated = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        Emit($"  提权状态：{isElevated}");
        if (!isElevated)
        {
            Emit("  跳过：需要管理员权限才能写 HKLM\\SOFTWARE\\WOW6432Node");
            Emit("  结论：未执行（不是失败，是没有权限）");
            return 3;
        }

        var backupDir = Path.Combine(Path.GetTempPath(), "rc-e2e32-" + Guid.NewGuid().ToString("N")[..8]);
        var subKeyPath = $@"{UninstallRoot}\{TestGuid}";
        // 注意：UninstallRoot 本身已经以 "SOFTWARE\" 开头，
        // 物理路径是把 "SOFTWARE\" 换成 "SOFTWARE\WOW6432Node\"，不是再套一层。
        var physicalPath = $@"SOFTWARE\WOW6432Node\{UninstallRoot["SOFTWARE\\".Length..]}\{TestGuid}";

        try
        {
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var key = baseKey.CreateSubKey(subKeyPath))
            {
                key.SetValue("DisplayName", TestName);
                key.SetValue("DisplayVersion", "0.0.1-e2e32");
                key.SetValue("Publisher", "RubbishCleaner E2E");
                key.SetValue("WindowsInstaller", 1, RegistryValueKind.DWord);
                key.SetValue("UninstallString", "MsiExec.exe /X" + TestGuid);
            }

            Emit($"  阶段1：已在 32 位视图造键 {subKeyPath}");

            using (var physical = Registry.LocalMachine.OpenSubKey(physicalPath))
            {
                Check("键物理落在 WOW6432Node 下", physical is not null, physicalPath);
                Check("WindowsInstaller 值可读", physical?.GetValue("WindowsInstaller") is int wi && wi == 1);
            }

            using (var b64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var k64 = b64.OpenSubKey(subKeyPath))
            {
                Check("64 位视图看不到这个键（证明两个视图是不同位置）", k64 is null);
            }

            var backup = RegistryKeyBackupWriter.Write(
                RegistryHive.LocalMachine, RegistryView.Registry32, subKeyPath, backupDir, out var backupError);

            if (backup is null)
            {
                Check("备份成功", false, backupError);
                throw new InvalidOperationException("备份失败，后续步骤无意义");
            }

            var regText = File.ReadAllText(backup);
            Emit($"  阶段2：备份 {Path.GetFileName(backup)}（{new FileInfo(backup).Length} 字节）");
            Check("备份含 WOW6432Node 物理路径",
                regText.Contains(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\", StringComparison.OrdinalIgnoreCase));

            var header = regText.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith('['))?.Trim();
            Emit($"    段头： {header}");

            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            {
                baseKey.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
            }

            Emit("  阶段3：已删键");
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var k = baseKey.OpenSubKey(subKeyPath))
            {
                Check("32 位视图里键确实没了", k is null);
            }

            var psi = new System.Diagnostics.ProcessStartInfo("reg.exe", $"import \"{backup}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var proc = System.Diagnostics.Process.Start(psi)!)
            {
                var stdout = proc.StandardOutput.ReadToEnd();
                var stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(20000);
                Emit($"  阶段4：reg import 退出码={proc.ExitCode} {stdout.Trim()} {stderr.Trim()}");
                Check("reg import 成功", proc.ExitCode == 0);
            }

            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var restored = baseKey.OpenSubKey(subKeyPath))
            {
                Check("32 位视图里键回来了", restored is not null);
                if (restored is not null)
                {
                    Check("DisplayName 一致", restored.GetValue("DisplayName") as string == TestName,
                        restored.GetValue("DisplayName") as string);
                    Check("DisplayVersion 一致", restored.GetValue("DisplayVersion") as string == "0.0.1-e2e32");
                    Check("WindowsInstaller 一致", restored.GetValue("WindowsInstaller") is int wi2 && wi2 == 1);
                    Check("UninstallString 一致",
                        restored.GetValue("UninstallString") as string == "MsiExec.exe /X" + TestGuid);
                }
            }

            using (var b64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var k64 = b64.OpenSubKey(subKeyPath))
            {
                Check("64 位视图没有被误写（旧实现会写到这里）", k64 is null);
            }

            Emit("");
            Emit("=== 扫描器能否识别这个 32 位视图条目 ===");
            var scanner = new OrphanUninstallScanner();
            var scan = await scanner.ScanAsync();
            var hit = scan.Entries.FirstOrDefault(e => e.ProductCode.Equals(TestGuid, StringComparison.OrdinalIgnoreCase));
            Emit($"  扫描 {scan.ScannedCount} 条，命中测试条目：{hit is not null}");
            if (hit is not null)
            {
                Emit($"    视图：{hit.View}，Hive：{hit.HiveName}");
                foreach (var line in hit.Evidence) Emit("    · " + line);
                Check("扫描结果标记为 32 位视图", hit.View == RegistryView.Registry32);
            }
            else
            {
                Check("造出来的 32 位视图孤儿被扫描器命中", false, "未命中");
            }

            // ---------- 阶段 5b：让真正的 OrphanCleaner 在 32 位视图上执行删键 ----------
            // 前面几步是我手工删键 + reg import，证明的是"备份格式对"。
            // 这里换成产品自己的清理器跑一遍，证明的是"清理器在 32 位视图上删的是对的那个键"——
            // 这是两个不同的结论，后者才是用户点按钮时真正走的路径。
            if (hit is not null)
            {
                Emit("");
                Emit("=== 用真实 OrphanCleaner 在 32 位视图上执行清理 ===");
                var cleaner = new OrphanCleaner(backupDir);
                var plan = cleaner.BuildPlan([hit]);
                Emit($"  计划 {plan.Count} 项，跳过 {plan.Skipped.Count} 项");
                Check("计划包含该条目", plan.Count == 1);

                var results = cleaner.Execute(plan);
                foreach (var r in results)
                {
                    Emit($"  → {(r.Success ? "成功" : "失败")}：{r.Message}");
                }

                Check("清理器执行成功", results.Count == 1 && results[0].Success);

                using (var b32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                using (var k32 = b32.OpenSubKey(subKeyPath))
                {
                    Check("32 位视图里键被清理器删掉了", k32 is null);
                }

                using (var b64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k64 = b64.OpenSubKey(subKeyPath))
                {
                    Check("清理器没有误删 64 位视图（那里本来也没有）", k64 is null);
                }

                // 清理器给的备份文件也要能还原（这是"可还原"的最终证明）
                var cleanerBackup = results[0].BackupPath;
                Check("清理器留下了备份文件", cleanerBackup is not null && File.Exists(cleanerBackup));
                if (cleanerBackup is not null)
                {
                    var psi2 = new System.Diagnostics.ProcessStartInfo("reg.exe", $"import \"{cleanerBackup}\"")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    };
                    using var proc2 = System.Diagnostics.Process.Start(psi2)!;
                    proc2.StandardOutput.ReadToEnd();
                    proc2.StandardError.ReadToEnd();
                    proc2.WaitForExit(20000);
                    Check("清理器备份能被 reg import 还原", proc2.ExitCode == 0);

                    using var b32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                    using var restored = b32.OpenSubKey(subKeyPath);
                    Check("还原后 32 位视图里键回来了", restored is not null);
                    Check("还原后 DisplayName 一致", restored?.GetValue("DisplayName") as string == TestName);
                }
            }
        }
        finally
        {
            try
            {
                using (var b32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                {
                    b32.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
                }

                using (var b64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    b64.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
                }

                Emit("  阶段6：现场已清理（两个视图的测试键都已删除）");
            }
            catch (Exception ex)
            {
                Emit("  阶段6：现场清理失败：" + ex.Message);
            }

            try { if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true); } catch { }
        }

        Emit("");
        Emit(failures.Count == 0 ? "结论：32 位视图真实往返全部通过" : $"结论：{failures.Count} 项失败");
        foreach (var f in failures) Emit("  · " + f);
        return failures.Count == 0 ? 0 : 1;
    }
}
