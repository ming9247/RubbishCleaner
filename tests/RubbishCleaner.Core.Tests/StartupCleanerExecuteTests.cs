using Microsoft.Win32;
using RubbishCleaner.Core.Cleanup;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// 执行阶段的安全护栏测试。
///
/// 这些用例专门验证<b>"不该删时一个都不删"</b>：
/// 从扫描到用户点确认之间，程序可能已被重装，此时删除会破坏正常的启动项。
/// </summary>
public class StartupCleanerExecuteTests
{
    [Fact]
    public void Execute_SkipsTargetWhoseFileReappeared()
    {
        // 构造一条"扫描时失效、执行时已存在"的项：
        // 用真实存在的 notepad.exe，模拟"用户中途把软件装回来了"
        var systemExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

        if (!File.Exists(systemExe))
        {
            return;   // 环境异常时跳过，避免误报
        }

        var target = new CleanupTarget(
            "Notepad", "Notepad",
            StartupSourceKind.RegistryRunHkcu,
            new RegistryAnchor(RegistryHive.CurrentUser, RegistryView.Default,
                @"Software\RubbishCleaner.Tests\NeverExists", "Notepad"),
            systemExe, systemExe);

        var plan = new CleanupPlan { Targets = [target] };
        var results = new StartupCleaner().Execute(plan);

        Assert.Single(results);
        Assert.False(results[0].Success);
        Assert.Contains("目标文件已恢复存在", results[0].Message);
    }

    [Fact]
    public void Execute_OnNonExistentKey_ReportsFailureWithoutThrowing()
    {
        // 键不存在时应返回失败而不是抛异常，且不影响其它项
        var target = new CleanupTarget(
            "Ghost", "Ghost",
            StartupSourceKind.RegistryRunHkcu,
            new RegistryAnchor(RegistryHive.CurrentUser, RegistryView.Default,
                @"Software\RubbishCleaner.Tests\GuaranteedMissingKey", "Ghost"),
            @"D:\Gone\ghost.exe", @"D:\Gone\ghost.exe");

        var results = new StartupCleaner().Execute(new CleanupPlan { Targets = [target] });

        Assert.Single(results);
        Assert.False(results[0].Success);
        Assert.Contains("不存在", results[0].Message);
    }

    [Fact]
    public void Execute_EmptyPlan_ReturnsEmpty()
    {
        Assert.Empty(new StartupCleaner().Execute(new CleanupPlan()));
    }
}

/// <summary>注册表删除底层能力的安全测试（不触碰真实业务键）。</summary>
public class RegistryWriterTests
{
    private const string SandboxKey = @"Software\RubbishCleaner.Tests\Sandbox";

    private static void EnsureSandboxValue(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SandboxKey, writable: true)!;
        key.SetValue(name, value, RegistryValueKind.String);
    }

    private static void DeleteSandbox()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(SandboxKey, throwOnMissingSubKey: false);
        }
        catch
        {
            // 清理失败不影响断言
        }
    }

    [Fact]
    public void DeleteValue_RemovesExistingValueAndKeepsKey()
    {
        EnsureSandboxValue("Victim", "x");
        EnsureSandboxValue("Keeper", "y");
        try
        {
            var result = RubbishCleaner.Common.Registry.RegistryWriter.DeleteValue(
                RegistryHive.CurrentUser, RegistryView.Default, SandboxKey, "Victim");

            Assert.True(result.Success);

            using var key = Registry.CurrentUser.OpenSubKey(SandboxKey, writable: false)!;
            var names = key.GetValueNames();

            Assert.DoesNotContain("Victim", names);
            Assert.Contains("Keeper", names);   // 只删值，不删键，也不动别的值
        }
        finally
        {
            DeleteSandbox();
        }
    }

    [Fact]
    public void DeleteValue_MissingValue_ReturnsFailureNotSuccess()
    {
        EnsureSandboxValue("Keeper", "y");
        try
        {
            var result = RubbishCleaner.Common.Registry.RegistryWriter.DeleteValue(
                RegistryHive.CurrentUser, RegistryView.Default, SandboxKey, "NotThere");

            Assert.False(result.Success);
            Assert.Contains("不存在", result.Message);
        }
        finally
        {
            DeleteSandbox();
        }
    }

    [Fact]
    public void DeleteValue_EmptyValueName_IsRefused()
    {
        // 空值名是默认值，误删影响面大，必须拒绝
        var result = RubbishCleaner.Common.Registry.RegistryWriter.DeleteValue(
            RegistryHive.CurrentUser, RegistryView.Default, SandboxKey, "");

        Assert.False(result.Success);
        Assert.Contains("拒绝", result.Message);
    }

    [Fact]
    public void DeleteValue_EmptySubKey_IsRefused()
    {
        var result = RubbishCleaner.Common.Registry.RegistryWriter.DeleteValue(
            RegistryHive.CurrentUser, RegistryView.Default, "", "X");

        Assert.False(result.Success);
        Assert.Contains("中止", result.Message);
    }
}
