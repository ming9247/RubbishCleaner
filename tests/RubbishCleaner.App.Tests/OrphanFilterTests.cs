using RubbishCleaner.App.ViewModels;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.App.Tests;

/// <summary>
/// 针对「残留启动项」筛选与展示的测试。
///
/// 这些用例直接驱动 <see cref="MainViewModel"/>，而不是点 UI——
/// 主程序以管理员权限运行，普通权限的合成鼠标输入会被 UIPI 丢弃，
/// UI 自动化在跨完整性级别时不可靠（实测踩过）。
/// </summary>
public class OrphanFilterTests
{
    /// <summary>构造一个"启动项指向不存在文件"的画像。</summary>
    private static SoftwareProfile OrphanProfile(string name, string missingPath)
        => new()
        {
            Key = missingPath.ToLowerInvariant(),
            DisplayName = name,
            ExecutablePath = missingPath,
            StartupEntries =
            [
                new StartupEntry
                {
                    Id = name,
                    Source = StartupSourceKind.RegistryRunHkcu,
                    Name = name,
                    Location = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
                    ExecutablePath = missingPath,
                    TargetExistsOverride = false,
                },
            ],
            LiveProcess = ProcessFacts.NotRunning(name, missingPath),
            Score = new RiskScore(30, RiskLevel.Normal, []),
        };

    private static SoftwareProfile HealthyProfile(string name)
        => new()
        {
            Key = $@"c:\apps\{name}.exe",
            DisplayName = name,
            ExecutablePath = $@"C:\Apps\{name}.exe",
            StartupEntries =
            [
                new StartupEntry
                {
                    Id = name,
                    Source = StartupSourceKind.RegistryRunHkcu,
                    Name = name,
                    Location = @"HKCU\Run",
                    ExecutablePath = $@"C:\Apps\{name}.exe",
                    TargetExistsOverride = true,
                },
            ],
            LiveProcess = ProcessFacts.NotRunning(name, $@"C:\Apps\{name}.exe"),
            Score = new RiskScore(30, RiskLevel.Normal, []),
        };

    [Fact]
    public void SoftwareItem_OrphanEntry_ReportsResidueState()
    {
        var vm = new SoftwareItemViewModel(OrphanProfile("Ghost", @"D:\Old\Ghost\ghost.exe"));

        Assert.True(vm.IsOrphaned);
        Assert.Equal(1, vm.OrphanCount);
        Assert.Equal("启动项已失效", vm.StateText);
        Assert.Single(vm.OrphanPaths);
        Assert.Contains(@"D:\Old\Ghost\ghost.exe", vm.OrphanPaths[0]);
    }

    [Fact]
    public void SoftwareItem_HealthyEntry_KeepsRunningText()
    {
        var vm = new SoftwareItemViewModel(HealthyProfile("Good"));

        Assert.False(vm.IsOrphaned);
        Assert.Equal(0, vm.OrphanCount);
        Assert.Equal("未运行", vm.StateText);
    }

    [Fact]
    public void SoftwareItem_PartiallyOrphaned_ReportsCount()
    {
        var profile = new SoftwareProfile
        {
            Key = "mixed",
            DisplayName = "Mixed",
            ExecutablePath = @"C:\Apps\mixed.exe",
            StartupEntries =
            [
                new StartupEntry
                {
                    Id = "old", Source = StartupSourceKind.RegistryRunHkcu, Name = "Mixed(old)",
                    Location = @"HKCU\Run", ExecutablePath = @"D:\Old\mixed.exe",
                    TargetExistsOverride = false,
                },
                new StartupEntry
                {
                    Id = "new", Source = StartupSourceKind.RegistryRunHkcu, Name = "Mixed",
                    Location = @"HKCU\Run", ExecutablePath = @"C:\Apps\mixed.exe",
                    TargetExistsOverride = true,
                },
            ],
            Score = new RiskScore(30, RiskLevel.Normal, []),
        };

        var vm = new SoftwareItemViewModel(profile);

        Assert.False(vm.IsOrphaned);           // 还有有效启动点，不算整体失效
        Assert.Equal(1, vm.OrphanCount);       // 但要报出 1 条失效
        Assert.Equal("1 条启动项失效", vm.StateText);
    }
}
