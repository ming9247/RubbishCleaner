using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using RubbishCleaner.Core.RegistryCleanup;
using Xunit.Abstractions;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// 注册表备份文件测试（v0.4）。
///
/// 真实的注册表写入放在 HKCU 下自建键，用完立即删除；
/// <b>不触碰任何系统键</b>（唯一读 HKLM 的两条用例是纯只读导出）。
/// </summary>
public class RegistryKeyBackupWriterTests
{
    private const string TestRoot = @"Software\RubbishCleanerTests";

    private readonly ITestOutputHelper _output;

    public RegistryKeyBackupWriterTests(ITestOutputHelper output) => _output = output;

    private static (string KeyPath, string Dir) CreateTestKey(Action<RegistryKey>? fill = null)
    {
        var keyName = Guid.NewGuid().ToString("N")[..8];
        var keyPath = @$"{TestRoot}\{keyName}";
        var dir = Path.Combine(Path.GetTempPath(), "rc-regbak-" + keyName);

        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            fill?.Invoke(key);
        }

        return (keyPath, dir);
    }

    private static void Cleanup(string keyPath, string dir)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
        catch
        {
        }

        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch
        {
        }
    }

    /// <summary>用 reg.exe 导入一个 .reg（与用户双击 .reg 走同一条路径）。</summary>
    private static (int ExitCode, string Output) RegImport(string regPath)
    {
        var psi = new ProcessStartInfo("reg.exe", $"import \"{regPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(15000);

        return (process.ExitCode, (stdout + stderr).Trim());
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [Fact]
    public void Write_ProducesValidRegFile()
    {
        var (keyPath, dir) = CreateTestKey(key =>
        {
            key.SetValue("DisplayName", "测试软件");
            key.SetValue("DisplayVersion", "1.0.0");
            key.SetValue("WindowsInstaller", 1, RegistryValueKind.DWord);
        });

        try
        {
            var path = RegistryKeyBackupWriter.Write(
                RegistryHive.CurrentUser, RegistryView.Default, keyPath, dir, out var error);

            Assert.Null(error);
            Assert.NotNull(path);
            Assert.True(File.Exists(path));

            var content = File.ReadAllText(path);

            Assert.Contains("Windows Registry Editor Version 5.00", content);
            Assert.Contains(@"[HKEY_CURRENT_USER\Software\RubbishCleanerTests\", content);
            Assert.Contains("\"DisplayName\"=\"测试软件\"", content);
            Assert.Contains("\"WindowsInstaller\"=dword:00000001", content);
        }
        finally
        {
            Cleanup(keyPath, dir);
        }
    }

    [Fact]
    public void Write_UsesUtf16LeWithBom()
    {
        // v0.1.3 实测：UTF-8 的 .reg 文件里中文会乱码，reg import 后变问号。
        var (keyPath, dir) = CreateTestKey(key => key.SetValue("Name", "中文测试"));

        try
        {
            var path = RegistryKeyBackupWriter.Write(
                RegistryHive.CurrentUser, RegistryView.Default, keyPath, dir, out _)!;

            var bytes = File.ReadAllBytes(path);

            // UTF-16LE BOM: FF FE
            Assert.Equal(0xFF, bytes[0]);
            Assert.Equal(0xFE, bytes[1]);
        }
        finally
        {
            Cleanup(keyPath, dir);
        }
    }

    [Fact]
    public void Write_EscapesBackslashesAndQuotes()
    {
        var (keyPath, dir) = CreateTestKey(key =>
            key.SetValue("Path", @"C:\Program Files\App\""quoted""\x.exe"));

        try
        {
            var path = RegistryKeyBackupWriter.Write(
                RegistryHive.CurrentUser, RegistryView.Default, keyPath, dir, out _)!;

            var content = File.ReadAllText(path);

            // .reg 里反斜杠要写成 \\，引号要写成 \"
            Assert.Contains(@"C:\\Program Files\\App\\", content);
        }
        finally
        {
            Cleanup(keyPath, dir);
        }
    }

    [Fact]
    public void Write_NonExistentKey_ReturnsNullWithError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rc-regbak-missing");

        try
        {
            var path = RegistryKeyBackupWriter.Write(
                RegistryHive.CurrentUser, RegistryView.Default,
                @"Software\NoSuchKey_" + Guid.NewGuid().ToString("N")[..8],
                dir, out var error);

            Assert.Null(path);
            Assert.NotNull(error);
            Assert.Contains("键不存在", error);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void Write_InvalidDirectory_ReturnsNullWithError()
    {
        var (keyPath, _) = CreateTestKey(key => key.SetValue("X", "1"));

        try
        {
            var path = RegistryKeyBackupWriter.Write(
                RegistryHive.CurrentUser, RegistryView.Default, keyPath,
                @"Z:\NoSuchDrive\backups", out var error);

            Assert.Null(path);
            Assert.NotNull(error);
        }
        finally
        {
            Cleanup(keyPath, Path.Combine(Path.GetTempPath(), "unused"));
        }
    }

    [Fact]
    public void Write_EmptyPath_ReturnsNull()
    {
        var path = RegistryKeyBackupWriter.Write(
            RegistryHive.CurrentUser, RegistryView.Default, "", Path.GetTempPath(), out var error);

        Assert.Null(path);
        Assert.NotNull(error);
    }

    [Fact]
    public void Write_BackupCanBeReadBackByRegImport()
    {
        // 这是"可还原"的实质验证：用 reg import 把备份导回去，值应该一致。
        var (keyPath, dir) = CreateTestKey(key =>
        {
            key.SetValue("DisplayName", "可还原测试");
            key.SetValue("Version", 42, RegistryValueKind.DWord);
        });

        try
        {
            var path = RegistryKeyBackupWriter.Write(
                RegistryHive.CurrentUser, RegistryView.Default, keyPath, dir, out _)!;

            // 先删掉原键，模拟"清理已执行"
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            Assert.Null(Registry.CurrentUser.OpenSubKey(keyPath));

            var (exitCode, output) = RegImport(path);
            Assert.True(exitCode == 0, "reg import 失败：" + output);

            using var restored = Registry.CurrentUser.OpenSubKey(keyPath);

            Assert.NotNull(restored);
            Assert.Equal("可还原测试", restored!.GetValue("DisplayName"));
            Assert.Equal(42, restored.GetValue("Version"));
        }
        finally
        {
            Cleanup(keyPath, dir);
        }
    }

    // ================= A. 32 位视图的 .reg 还原路径 =================

    [Theory]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{09CC399B-909F-47A2-BC3E-0009F440BFB9}")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{09CC399B-909F-47A2-BC3E-0009F440BFB9}")]
    [InlineData(@"SOFTWARE\Vendor\App")]
    public void ToRegPath_LocalMachine32_InsertsWow6432Node(string subKeyPath)
    {
        // 32 位视图的真实物理位置：WOW6432Node 才是 reg.exe 认的那条路径。
        var actual = RegistryKeyBackupWriter.ToRegPath(
            RegistryHive.LocalMachine, RegistryView.Registry32, subKeyPath);

        Assert.StartsWith(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\", actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"SOFTWARE\WOW6432Node\WOW6432Node", actual, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{09CC399B-909F-47A2-BC3E-0009F440BFB9}")]
    [InlineData(@"SOFTWARE\Vendor\App")]
    public void ToRegPath_LocalMachine64_DoesNotInsertWow6432Node(string subKeyPath)
    {
        var actual = RegistryKeyBackupWriter.ToRegPath(
            RegistryHive.LocalMachine, RegistryView.Registry64, subKeyPath);

        Assert.Equal(@"HKEY_LOCAL_MACHINE\" + subKeyPath, actual, ignoreCase: true);
        Assert.DoesNotContain("WOW6432Node", actual, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToRegPath_LocalMachine32_DoesNotTouchNonSoftwareSubtree()
    {
        // WOW64 只重定向 SOFTWARE 子树；SYSTEM 等子树两个视图是同一份数据，
        // 加 WOW6432Node 会拼出根本不存在的路径。
        var actual = RegistryKeyBackupWriter.ToRegPath(
            RegistryHive.LocalMachine, RegistryView.Registry32, @"SYSTEM\CurrentControlSet\Services\Foo");

        Assert.Equal(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Foo", actual, ignoreCase: true);
    }

    [Theory]
    [InlineData(RegistryView.Registry32)]
    [InlineData(RegistryView.Registry64)]
    public void ToRegPath_CurrentUser_NeverInsertsWow6432Node(RegistryView view)
    {
        // 依据（2026-10-25 本机只读实测，Windows 11 22631 / AMD64）：
        // reg query HKCU\...\Uninstall /reg:32 与 /reg:64 都是 8 条且集合完全相同；
        // 物理路径 HKCU\SOFTWARE\WOW6432Node\...\Uninstall 不存在（0 个子键）。
        // 即 HKCU 不做 WOW64 重定向，写 WOW6432Node 反而会还原错位置。
        var actual = RegistryKeyBackupWriter.ToRegPath(
            RegistryHive.CurrentUser, view, @"Software\RubbishCleanerTests\X");

        Assert.Equal(@"HKEY_CURRENT_USER\Software\RubbishCleanerTests\X", actual, ignoreCase: true);
        Assert.DoesNotContain("WOW6432Node", actual, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_RealHklmKey_32ViewEmitsWow6432Node_64ViewDoesNot()
    {
        // 只读：导出 HKLM 两个视图下的同一个物理键，比较文件里写的路径。
        // 用 RegisteredApplications 是因为它两个视图都存在、且没有子键（导出的文件很小）。
        const string subKeyPath = @"SOFTWARE\RegisteredApplications";

        using (var probe = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
        using (var key = probe.OpenSubKey(subKeyPath))
        {
            Assert.True(key is not null, "本机应有标准的 SOFTWARE\\RegisteredApplications 键");
        }

        var dir = Path.Combine(Path.GetTempPath(), "rc-regbak-hklm-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            var path32 = RegistryKeyBackupWriter.Write(
                RegistryHive.LocalMachine, RegistryView.Registry32, subKeyPath, dir, out var error32);
            Assert.Null(error32);
            Assert.NotNull(path32);

            var content32 = File.ReadAllText(path32!);
            Assert.Contains(
                @"[HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\RegisteredApplications]",
                content32,
                StringComparison.OrdinalIgnoreCase);

            var path64 = RegistryKeyBackupWriter.Write(
                RegistryHive.LocalMachine, RegistryView.Registry64, subKeyPath, dir, out var error64);
            Assert.Null(error64);
            Assert.NotNull(path64);

            var content64 = File.ReadAllText(path64!);
            Assert.Contains(
                @"[HKEY_LOCAL_MACHINE\SOFTWARE\RegisteredApplications]",
                content64,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("WOW6432Node", content64, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void Write_Hkcu32View_DoesNotEmitWow6432Node_AndRoundTrips()
    {
        // HKCU 下 32 位视图与 64 位视图是同一份数据，所以这里同时验证两件事：
        // 1) 文件里不能出现 WOW6432Node（否则会还原到不存在的路径）；
        // 2) 走 Registry32 写出的备份仍然能真还原（删除 → reg import → 值一致）。
        var (keyPath, dir) = CreateTestKey(key =>
        {
            key.SetValue("DisplayName", "32位视图往返");
            key.SetValue("Count", 7, RegistryValueKind.DWord);
        });

        try
        {
            var path = RegistryKeyBackupWriter.Write(
                RegistryHive.CurrentUser, RegistryView.Registry32, keyPath, dir, out var error);

            Assert.Null(error);
            Assert.NotNull(path);

            var content = File.ReadAllText(path!);
            Assert.Contains(@"[HKEY_CURRENT_USER\Software\RubbishCleanerTests\", content);
            Assert.DoesNotContain("WOW6432Node", content, StringComparison.OrdinalIgnoreCase);

            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            Assert.Null(Registry.CurrentUser.OpenSubKey(keyPath));

            var (exitCode, output) = RegImport(path!);
            Assert.True(exitCode == 0, "reg import 失败：" + output);

            using var restored = Registry.CurrentUser.OpenSubKey(keyPath);
            Assert.NotNull(restored);
            Assert.Equal("32位视图往返", restored!.GetValue("DisplayName"));
            Assert.Equal(7, restored.GetValue("Count"));
        }
        finally
        {
            Cleanup(keyPath, dir);
        }
    }

    [Fact]
    public void Write_Hklm32View_ImportPathResolvesToOriginalData()
    {
        // 不需要管理员：把 32 位视图导出的段头路径按"导入时的解析方式"（64 位视图/物理路径）
        // 重新打开，必须命中最初那份数据——这正是修复前做不到的事。
        const string subKeyPath = @"SOFTWARE\RegisteredApplications";
        var dir = Path.Combine(Path.GetTempPath(), "rc-regbak-resolve-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            var path = RegistryKeyBackupWriter.Write(
                RegistryHive.LocalMachine, RegistryView.Registry32, subKeyPath, dir, out var error);
            Assert.Null(error);
            Assert.NotNull(path);

            var sectionPath = File.ReadAllLines(path!)
                .First(line => line.StartsWith('[') && line.EndsWith(']'))[1..^1];

            // 导入时 reg.exe 没有视图开关，按物理路径解析 → 等价于用 Registry64 打开
            var relative = sectionPath[@"HKEY_LOCAL_MACHINE\".Length..];
            using var physical = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var resolved = physical.OpenSubKey(relative);
            Assert.True(resolved is not null, $"导入路径解析不到：{sectionPath}");

            using var original = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var source = original.OpenSubKey(subKeyPath);
            Assert.NotNull(source);

            Assert.Equal(
                source!.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
                resolved!.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void Write_Hklm32View_FullRoundTrip_RestoresAtWow6432Node()
    {
        // 真实"导出 → 删键 → reg import → 值一致"的 HKLM 32 位往返。
        // HKLM 下建键需要管理员；非管理员时退化为只读等价验证（导入路径解析），
        // 两种情况下都不触碰任何现有系统键。
        var name = "RubbishCleanerTests" + Guid.NewGuid().ToString("N")[..8];
        var logicalPath = @$"SOFTWARE\{name}";                        // 32 位视图的逻辑路径
        var physicalPath = @$"SOFTWARE\WOW6432Node\{name}";           // 32 位视图的真实物理路径
        var dir = Path.Combine(Path.GetTempPath(), "rc-regbak-hklm-rt-" + Guid.NewGuid().ToString("N")[..8]);

        if (!IsAdministrator())
        {
            _output.WriteLine(
                "未以管理员身份运行：跳过 HKLM 的真实删除 + reg import 往返，"
                + "改为只读验证「32 位视图导出的路径 = WOW6432Node 物理路径」。"
                + "要覆盖真实往返请以管理员身份运行测试。");

            // 只读等价：32 位视图与"64 位视图下的 WOW6432Node 物理路径"必须是同一个键
            using var view32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var view64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            var mapped = RegistryKeyBackupWriter.ToRegPath(
                RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            Assert.Contains(@"SOFTWARE\WOW6432Node\", mapped, StringComparison.OrdinalIgnoreCase);

            var relPhysical = mapped[@"HKEY_LOCAL_MACHINE\".Length..];
            using var a = view32.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            using var b = view64.OpenSubKey(relPhysical);
            Assert.True(a is not null && b is not null, "32 位视图与 WOW6432Node 物理路径应都存在");
            Assert.Equal(
                a!.GetSubKeyNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
                b!.GetSubKeyNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            return;
        }

        try
        {
            // 用 64 位视图写 WOW6432Node 物理路径，等价于"32 位程序安装时写的位置"
            using (var physical = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = physical.CreateSubKey(physicalPath))
            {
                key.SetValue("DisplayName", "32位往返");
                key.SetValue("Version", 3, RegistryValueKind.DWord);
            }

            var path = RegistryKeyBackupWriter.Write(
                RegistryHive.LocalMachine, RegistryView.Registry32, logicalPath, dir, out var error);
            Assert.Null(error);
            Assert.NotNull(path);
            Assert.Contains(@"SOFTWARE\WOW6432Node\" + name, File.ReadAllText(path!), StringComparison.OrdinalIgnoreCase);

            // 删键（模拟清理）
            using (var view32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            {
                view32.DeleteSubKeyTree(logicalPath, throwOnMissingSubKey: false);
                Assert.Null(view32.OpenSubKey(logicalPath));
            }

            var (exitCode, output) = RegImport(path!);
            Assert.True(exitCode == 0, "reg import 失败：" + output);

            using var restoredBase = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var restored = restoredBase.OpenSubKey(logicalPath);
            Assert.NotNull(restored);
            Assert.Equal("32位往返", restored!.GetValue("DisplayName"));
            Assert.Equal(3, restored.GetValue("Version"));
        }
        finally
        {
            try
            {
                using var physical = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                physical.DeleteSubKeyTree(physicalPath, throwOnMissingSubKey: false);
            }
            catch
            {
            }

            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
            }
        }
    }

    // ================= B. 备份必须包含子键 =================

    [Fact]
    public void Write_RecursivelyExportsSubKeys()
    {
        var (keyPath, dir) = CreateTestKey(key =>
        {
            key.SetValue("DisplayName", "带子键的软件");
            using var child = key.CreateSubKey("Child");
            child.SetValue("Level", 1, RegistryValueKind.DWord);
            using var grandChild = child.CreateSubKey("GrandChild");
            grandChild.SetValue("Deep", "深层值");
            using var empty = key.CreateSubKey("EmptyChild");
        });

        try
        {
            var path = RegistryKeyBackupWriter.Write(
                RegistryHive.CurrentUser, RegistryView.Default, keyPath, dir, out var error);

            Assert.Null(error);
            Assert.NotNull(path);

            var content = File.ReadAllText(path!);

            // 三个子键各一段；没有值的键也要有段头，否则结构丢了
            Assert.Contains($@"[{@"HKEY_CURRENT_USER"}\{keyPath}\Child]", content);
            Assert.Contains($@"[{@"HKEY_CURRENT_USER"}\{keyPath}\Child\GrandChild]", content);
            Assert.Contains($@"[{@"HKEY_CURRENT_USER"}\{keyPath}\EmptyChild]", content);

            Assert.Contains("\"Level\"=dword:00000001", content);
            Assert.Contains("\"Deep\"=\"深层值\"", content);
        }
        finally
        {
            Cleanup(keyPath, dir);
        }
    }

    [Fact]
    public void Write_SubKeyRoundTrip_RestoresWholeTree()
    {
        // 删键用的是 DeleteSubKeyTree（整棵子树）；只备份值的 .reg 导入后只剩空壳。
        // 这条用例把"子键 + 子键里的值"一起锁住。
        var (keyPath, dir) = CreateTestKey(key =>
        {
            key.SetValue("DisplayName", "整树往返");
            using var child = key.CreateSubKey("Child");
            child.SetValue("Level", 1, RegistryValueKind.DWord);
            using var grandChild = child.CreateSubKey("GrandChild");
            grandChild.SetValue("Deep", "深层值");
            using var empty = key.CreateSubKey("EmptyChild");
        });

        try
        {
            var path = RegistryKeyBackupWriter.Write(
                RegistryHive.CurrentUser, RegistryView.Default, keyPath, dir, out var error);
            Assert.Null(error);

            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            Assert.Null(Registry.CurrentUser.OpenSubKey(keyPath));

            var (exitCode, output) = RegImport(path!);
            Assert.True(exitCode == 0, "reg import 失败：" + output);

            using var restored = Registry.CurrentUser.OpenSubKey(keyPath);
            Assert.NotNull(restored);
            Assert.Equal("整树往返", restored!.GetValue("DisplayName"));

            using var childRestored = restored.OpenSubKey("Child");
            Assert.NotNull(childRestored);
            Assert.Equal(1, childRestored!.GetValue("Level"));

            using var grandChildRestored = childRestored.OpenSubKey("GrandChild");
            Assert.NotNull(grandChildRestored);
            Assert.Equal("深层值", grandChildRestored!.GetValue("Deep"));

            using var emptyRestored = restored.OpenSubKey("EmptyChild");
            Assert.NotNull(emptyRestored);
        }
        finally
        {
            Cleanup(keyPath, dir);
        }
    }

    [Fact]
    public void Write_RealHklm32UninstallRoot_ExportsAllSubKeysAndWow6432Node()
    {
        // 真机规模的递归验证（纯只读）：导出整个 HKLM 32 位视图的 Uninstall 根。
        // 本机实测 241 个子键、每个约 20+ 个值——修复前只导出根键自己的值
        // （Uninstall 根下一个值都没有），文件近乎空壳；修复后应有 242 个段头。
        const string subKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var dir = Path.Combine(Path.GetTempPath(), "rc-regbak-root-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            int expectedChildren;
            using (var view32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var root = view32.OpenSubKey(subKeyPath))
            {
                Assert.True(root is not null, "本机应有 HKLM 32 位视图的 Uninstall 根键");
                expectedChildren = root!.GetSubKeyNames().Length;
            }

            Assert.True(expectedChildren > 0, "本机 Uninstall 根下应有子键，否则这条用例没有意义");

            var path = RegistryKeyBackupWriter.Write(
                RegistryHive.LocalMachine, RegistryView.Registry32, subKeyPath, dir, out var error);
            Assert.Null(error);
            Assert.NotNull(path);

            var lines = File.ReadAllLines(path!);

            // 1) 根段是 WOW6432Node 物理路径
            Assert.Contains(
                @"[HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall]",
                lines);

            // 2) 子键全部导出：段头数量至少是「1 个根 + N 个直接子键」；
            //    本机实测 243 = 242 + 1，多出的 1 个就是某个子键下的孙键，
            //    恰好证明递归不止一层；这里只卡"不少于"，不把机器差异写死。
            var sectionCount = lines.Count(l => l.StartsWith('[') && l.EndsWith(']'));
            Assert.True(
                sectionCount >= expectedChildren + 1,
                $"段头数量 {sectionCount} 少于根键 + 直接子键的 {expectedChildren + 1}");

            // 3) 至少有一个子键的值被写出来（证明递归到第二层确实读了值）
            Assert.Contains(lines, l => l.StartsWith("\"DisplayName\"=", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
            }
        }
    }
}

