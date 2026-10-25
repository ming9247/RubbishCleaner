
// ============================================================================================
// 模式二：32 位视图 / 多上下文实测（只读，不需要管理员）
//   用法：RubbishCleaner.RegE2E.exe views
//
// 为什么单独做成一个模式：v0.4.1 修了三个"只在特定环境下才暴露"的缺口
// （32 位视图还原路径 / 子键递归 / 三上下文枚举），它们的正确性不能只靠单测断言，
// 必须对着真机注册表说一遍"到底有多少条目、两个视图差多少"。
// 本模式<b>全程只读</b>：不建键、不删键，只在 HKCU 下建一个探针键验证重定向后立即删除。
// ============================================================================================
using Microsoft.Win32;
using RubbishCleaner.Core.RegistryCleanup;

internal static class ViewProbe
{
    private const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly System.Text.RegularExpressions.Regex GuidPattern = new(
        @"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static async Task<int> RunAsync()
    {
        var failures = new List<string>();
        void Check(string name, bool ok, string? detail = null)
        {
            Console.WriteLine((ok ? "  OK   " : "  FAIL ") + name + (detail is null ? "" : "  " + detail));
            if (!ok) failures.Add(name + (detail is null ? "" : " -> " + detail));
        }

        Console.WriteLine("=== 1. 各位置的 MSI 条目统计（与扫描器同一套判据：WindowsInstaller=1 + 严格 GUID）===");

        var locations = new (RegistryHive Hive, RegistryView View, string Tag)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"HKLM\64"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"HKLM\32"),
            (RegistryHive.CurrentUser, RegistryView.Registry64, @"HKCU\64"),
        };

        var perLocation = new Dictionary<string, (int All, int Msi, int MsiGuid)>();
        var guidSets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view, tag) in locations)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallRoot);
            if (uninstall is null)
            {
                Console.WriteLine($"  {tag}: 键不存在，跳过");
                continue;
            }

            var all = 0;
            var msi = 0;
            var msiGuid = 0;
            var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in uninstall.GetSubKeyNames())
            {
                all++;
                using var sub = uninstall.OpenSubKey(name);
                if (sub?.GetValue("WindowsInstaller") is not int v || v != 1)
                {
                    continue;
                }

                msi++;
                if (GuidPattern.IsMatch(name))
                {
                    msiGuid++;
                    guids.Add(name);
                }
            }

            perLocation[tag] = (all, msi, msiGuid);
            guidSets[tag] = guids;
            Console.WriteLine($"  {tag,-7} 子键 {all,4}，MSI 条目 {msi,4}，其中 GUID 形态 {msiGuid,4}");
        }

        var msiTotal = perLocation.Values.Sum(x => x.Msi);
        Console.WriteLine($"  合计 MSI 条目 = {msiTotal}");

        // 关键断言：只扫 64 位视图会漏掉多少
        if (guidSets.TryGetValue(@"HKLM\32", out var hklm32) && guidSets.TryGetValue(@"HKLM\64", out var hklm64))
        {
            var only32 = hklm32.Except(hklm64, StringComparer.OrdinalIgnoreCase).ToList();
            var only64 = hklm64.Except(hklm32, StringComparer.OrdinalIgnoreCase).ToList();
            Console.WriteLine($"  仅存在于 32 位视图的 GUID：{only32.Count} 个");
            Console.WriteLine($"  仅存在于 64 位视图的 GUID：{only64.Count} 个");
            Check("两个视图确实是不同的条目集合（必须都扫）", only32.Count > 0 || only64.Count > 0,
                $"32 位专属 {only32.Count}、64 位专属 {only64.Count}");
        }

        // HKCU 两个视图是否指向同一份数据
        using (var b32 = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32))
        using (var b64 = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
        using (var k32 = b32.OpenSubKey(UninstallRoot))
        using (var k64 = b64.OpenSubKey(UninstallRoot))
        {
            var n32 = k32?.GetSubKeyNames() ?? [];
            var n64 = k64?.GetSubKeyNames() ?? [];
            var same = n32.Length == n64.Length && n32.All(n => n64.Contains(n, StringComparer.OrdinalIgnoreCase));
            Console.WriteLine($"  HKCU 32 位视图 {n32.Length} 条 / 64 位视图 {n64.Length} 条 → {(same ? "同一份数据" : "不同")}");
            Check("HKCU 两个视图读到同一份数据（所以只扫一次即可，ADR-049 依据）", same);
        }

        Console.WriteLine();
        Console.WriteLine("=== 2. 扫描器自报的 ScannedCount 是否与手工统计一致 ===");
        var scanner = new OrphanUninstallScanner();
        var scan = await scanner.ScanAsync();
        Console.WriteLine($"  ScannedCount = {scan.ScannedCount}，孤儿 {scan.Entries.Count} 个，用时 {scan.Duration.TotalMilliseconds:F0}ms");
        Check("ScannedCount 等于各位置 MSI 条目之和", scan.ScannedCount == msiTotal,
            $"扫描器 {scan.ScannedCount} vs 手工 {msiTotal}");
        Check("没有把 MSI 已安装的产品报成孤儿",
            scan.Entries.All(e => e.MsiState is 1 or 2 or 3 or 4294967295u or 4294967294u));

        Console.WriteLine();
        Console.WriteLine("=== 3. HKCU 是否做 WOW64 重定向（决定备份该不该写 WOW6432Node）===");
        var probeName = "RcRedirectProbe_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\" + probeName))
            {
                k.SetValue("Probe", "物理路径探针");
            }

            var appearsUnderWow = false;
            using (var wow = Registry.CurrentUser.OpenSubKey(@"Software\WOW6432Node\" + probeName))
            {
                appearsUnderWow = wow is not null;
            }

            string? via32;
            string? via64;
            using (var b32 = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32))
            using (var k = b32.OpenSubKey(@"Software\" + probeName))
            {
                via32 = k?.GetValue("Probe") as string;
            }

            using (var b64 = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            using (var k = b64.OpenSubKey(@"Software\" + probeName))
            {
                via64 = k?.GetValue("Probe") as string;
            }

            Console.WriteLine($@"  HKCU\Software\{probeName}：");
            Console.WriteLine($"    物理出现在 WOW6432Node 下？ {appearsUnderWow}");
            Console.WriteLine($"    32 位视图读到： {via32 ?? "(null)"}");
            Console.WriteLine($"    64 位视图读到： {via64 ?? "(null)"}");
            Check("HKCU 不做 WOW64 重定向（32 位视图能读到同一份数据）", !appearsUnderWow && via32 is not null);
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\" + probeName, false); } catch { }
            Console.WriteLine("  探针键已清理");
        }

        Console.WriteLine();
        Console.WriteLine("=== 4. 32 位视图导出的 .reg 路径是否正确（ADR-049 核心断言）===");
        var testDir = Path.Combine(Path.GetTempPath(), "rc-32probe-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            string? firstGuid = null;
            using (var b32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var uninstall = b32.OpenSubKey(UninstallRoot))
            {
                firstGuid = uninstall?.GetSubKeyNames().FirstOrDefault(n => GuidPattern.IsMatch(n));
            }

            if (firstGuid is null)
            {
                Console.WriteLine("  本机 32 位视图没有 GUID 形态的条目，跳过");
            }
            else
            {
                var subPath = $@"{UninstallRoot}\{firstGuid}";
                var reg = RegistryKeyBackupWriter.Write(
                    RegistryHive.LocalMachine, RegistryView.Registry32, subPath, testDir, out var err);

                if (reg is null)
                {
                    Console.WriteLine($"  导出失败：{err}");
                    Check("32 位视图备份导出成功", false, err);
                }
                else
                {
                    var text = File.ReadAllText(reg);
                    var header = text.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith('['));
                    Console.WriteLine($"  导出段头： {header?.Trim()}");
                    Check("备份段头含 WOW6432Node 物理路径", text.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase));
                    Check(@"备份不含 64 位视图路径（HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows）",
                        !text.Contains(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + firstGuid));

                    // 用 reg query 真去查段头里的物理路径
                    var physical = @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" + firstGuid;
                    var rcPhysical = RunReg("query \"" + physical + "\"");
                    Console.WriteLine($"  reg query 物理路径 → 退出码 {rcPhysical}（0 = 真实存在）");
                    Check("段头物理路径能被 reg query 查到", rcPhysical == 0);

                    // 反向：旧实现写的路径（不带 WOW6432Node）在 64 位视图下是另一个键
                    var wrong = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + firstGuid;
                    var rcWrong = RunReg("query \"" + wrong + "\"");
                    Console.WriteLine($"  reg query 旧实现路径 → 退出码 {rcWrong}（非 0 = 旧实现会还原到别处）");
                    Check("旧实现路径确实不是同一个键（证明修复是必要的）", rcWrong != 0,
                        $"退出码 {rcWrong}，若为 0 说明该 GUID 两个视图都有，换一个样本");
                }
            }
        }
        finally
        {
            try { if (Directory.Exists(testDir)) Directory.Delete(testDir, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("=== 5. 三上下文枚举（ADR-050）===");

        // MsiInterop 是 Core 的 internal（P/Invoke 细节不该越过程序集边界），
        // 探针用反射读一次。刻意不给它开 InternalsVisibleTo：
        // 那是给测试程序集的授权，验证工具不应该扩大生产程序集的可见面。
        var enumerate = typeof(OrphanUninstallScanner).Assembly
            .GetType("RubbishCleaner.Core.RegistryCleanup.MsiInterop", throwOnError: true)!
            .GetMethod("EnumerateProducts", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;

        var enumerated = (HashSet<string>)enumerate.Invoke(null, null)!;
        Console.WriteLine($"  三上下文并集 = {enumerated.Count} 个产品");
        Check("枚举结果非空", enumerated.Count > 0);
        Check("枚举结果全部是严格 GUID 形态", enumerated.All(g => GuidPattern.IsMatch(g)));
        Check("枚举结果无重复（大小写不敏感集合）", enumerated.Count == enumerated.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Console.WriteLine();
        Console.WriteLine(failures.Count == 0 ? "结论：视图实测全部通过" : $"结论：{failures.Count} 项失败");
        foreach (var f in failures) Console.WriteLine("  · " + f);
        return failures.Count == 0 ? 0 : 1;
    }

    private static int RunReg(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("reg.exe", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return p.ExitCode;
    }
}
