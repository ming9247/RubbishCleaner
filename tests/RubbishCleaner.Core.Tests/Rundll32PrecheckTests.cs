using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Uninstall;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// rundll32 卸载方式的安全判定测试。
///
/// <b>背景</b>：实测发现 NVIDIA 全家桶的官方卸载命令是
/// <c>RunDll32.EXE "…\NVI2.DLL",UninstallPackage Display.Driver</c>。
/// 第一版把它当成"系统工具滥用"直接拦了，属于误杀——
/// rundll32 本身不可疑，可疑的是"加载任意 DLL"。
///
/// 因此放行条件是：DLL 绝对路径 + 真实存在 + 不在系统目录。
/// </summary>
public class Rundll32PrecheckTests
{
    private static InstalledApp App(string command)
        => new()
        {
            Id = "x",
            DisplayName = "NVIDIA 驱动",
            UninstallCommand = command,
            RegistryPath = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NVIDIA",
        };

    [Fact]
    public void Rundll32WithExistingNonSystemDll_IsAllowed()
    {
        // 造一个临时 DLL 模拟合法的第三方卸载器
        var dir = Path.Combine(Path.GetTempPath(), "rc-rundll-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var dll = Path.Combine(dir, "NVI2.DLL");
        File.WriteAllText(dll, "stub");
        try
        {
            var command = $@"""C:\Windows\System32\rundll32.exe"" ""{dll}"",UninstallPackage Display.Driver";

            var result = UninstallPrechecker.Check(App(command));

            Assert.True(result.CanProceed, result.BlockReason);
            Assert.Contains(result.Warnings!, w => w.Contains("rundll32"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Rundll32WithMissingDll_IsBlocked()
    {
        // 实测场景：NVIDIA InstallerCore 目录已被删除，但注册表条目还在
        var command = @"""C:\Windows\SysWOW64\RunDll32.EXE"" "
                      + @"""C:\Program Files\NVIDIA Corporation\Installer2\InstallerCore\NVI2.DLL"","
                      + "UninstallPackage Display.Driver";

        var result = UninstallPrechecker.Check(App(command));

        Assert.False(result.CanProceed);
        Assert.Contains("系统工具", result.BlockReason);
    }

    [Fact]
    public void Rundll32PointingAtSystemDll_IsBlocked()
    {
        // 系统目录下的 DLL 不该被第三方软件当卸载器
        var systemDll = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll");

        if (!File.Exists(systemDll))
        {
            return;
        }

        var command = $@"""C:\Windows\System32\rundll32.exe"" ""{systemDll}"",SomeEntry";

        var result = UninstallPrechecker.Check(App(command));

        Assert.False(result.CanProceed);
        Assert.Contains("系统工具", result.BlockReason);
    }

    [Fact]
    public void Rundll32WithRelativeDll_IsBlocked()
    {
        var result = UninstallPrechecker.Check(
            App(@"""C:\Windows\System32\rundll32.exe"" evil.dll,Entry"));

        Assert.False(result.CanProceed);
    }

    [Theory]
    [InlineData(@"cmd.exe /c del something")]
    [InlineData(@"powershell.exe -c Remove-Item x")]
    [InlineData(@"reg.exe delete HKLM\X /f")]
    public void OtherSystemTools_RemainBlocked(string command)
    {
        // rundll32 开了例外，但其它系统工具仍严格禁止
        var result = UninstallPrechecker.Check(App(command));

        Assert.False(result.CanProceed);
        Assert.Contains("系统工具", result.BlockReason);
    }
}
