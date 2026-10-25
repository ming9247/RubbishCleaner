using RubbishCleaner.Core.Uninstall;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// 卸载命令行拆分测试。
///
/// 起因是一个真实缺陷：v0.3.1 真机卸载 CC Switch（MSI）时报
/// "启动卸载程序失败：找不到 MsiExec.exe /X{...}"——
/// 因为 TryExtractExecutable 对 MsiExec 返回 null，fallback 把整条命令行当成了文件名。
/// <b>影响本机 27 个 MSI 软件的全部卸载</b>，v0.2 起就存在，只因当时没做真机卸载验证而潜伏至今。
/// </summary>
public class UninstallCommandSplitTests
{
    [Fact]
    public void SplitCommand_MsiExec_IsSplitIntoFileAndArgs()
    {
        // 这就是真机上炸掉的那一条
        var (file, args) = UninstallExecutor.SplitCommand(
            "MsiExec.exe /X{09CC399B-909F-47A2-BC3E-0009F440BFB9}");

        Assert.Equal("MsiExec.exe", file);
        Assert.Equal("/X{09CC399B-909F-47A2-BC3E-0009F440BFB9}", args);
    }

    [Fact]
    public void SplitCommand_QuotedPathWithArgs()
    {
        var (file, args) = UninstallExecutor.SplitCommand(
            "\"C:\\Program Files\\App\\uninst.exe\" /S");

        Assert.Equal(@"C:\Program Files\App\uninst.exe", file);
        Assert.Equal("/S", args);
    }

    [Fact]
    public void SplitCommand_UnquotedPathWithArgs()
    {
        var (file, args) = UninstallExecutor.SplitCommand(
            @"C:\App\uninst.exe /allusers");

        Assert.Equal(@"C:\App\uninst.exe", file);
        Assert.Equal("/allusers", args);
    }

    [Fact]
    public void SplitCommand_BareExecutable_NoArgs()
    {
        var (file, args) = UninstallExecutor.SplitCommand(@"C:\App\uninst.exe");

        Assert.Equal(@"C:\App\uninst.exe", file);
        Assert.Equal(string.Empty, args);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SplitCommand_Empty_ReturnsEmptyFile(string? command)
    {
        var (file, args) = UninstallExecutor.SplitCommand(command!);

        Assert.Equal(string.Empty, file);
        Assert.Equal(string.Empty, args);
    }

    [Fact]
    public void SplitCommand_ExeWithSpacesInsideQuotes()
    {
        // 实测存在："Uninstall 极狐游戏加速器.exe" 带空格
        var (file, args) = UninstallExecutor.SplitCommand(
            "\"D:\\极狐加速器\\Uninstall 极狐游戏加速器.exe\" /allusers");

        Assert.Equal(@"D:\极狐加速器\Uninstall 极狐游戏加速器.exe", file);
        Assert.Equal("/allusers", args);
    }

    [Fact]
    public void SplitCommand_Rundll32_KeepsDllArgument()
    {
        // NVIDIA 的卸载命令形态
        var (file, args) = UninstallExecutor.SplitCommand(
            "RunDll32.EXE \"C:\\NVI2.DLL\",UninstallPackage Display.Driver");

        Assert.Equal("RunDll32.EXE", file);
        Assert.Contains("NVI2.DLL", args);
        Assert.Contains("UninstallPackage", args);
    }
}