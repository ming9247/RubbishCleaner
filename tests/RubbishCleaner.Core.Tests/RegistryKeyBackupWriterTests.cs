using System.Text;
using Microsoft.Win32;
using RubbishCleaner.Core.RegistryCleanup;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// 注册表备份文件测试（v0.4）。
///
/// 真实的注册表写入放在 HKCU 下自建键，用完立即删除；
/// <b>不触碰任何系统键</b>。
/// </summary>
public class RegistryKeyBackupWriterTests
{
    private const string TestRoot = @"Software\RubbishCleanerTests";

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

            // 用 reg import 还原（这与用户双击 .reg 是同一路径）
            var psi = new System.Diagnostics.ProcessStartInfo("reg.exe", $"import \"{path}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = System.Diagnostics.Process.Start(psi)!;
            process.WaitForExit(15000);

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
}
