using Microsoft.Win32;
using RubbishCleaner.Core.Cleanup;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// 清理计划的安全性测试。
///
/// 这个功能会真正写注册表，所以测试的重点不是"能删"，而是<b>"不该删的绝不删"</b>。
/// 所有用例都只构造内存对象，不触碰真实注册表。
/// </summary>
public class StartupCleanerPlanTests
{
    private static readonly RegistryAnchor RunAnchor = new(
        RegistryHive.CurrentUser,
        RegistryView.Default,
        @"Software\Microsoft\Windows\CurrentVersion\Run",
        "OldApp");

    private static StartupEntry Entry(
        string name,
        string? path,
        bool targetExists,
        StartupSourceKind kind = StartupSourceKind.RegistryRunHkcu,
        RegistryAnchor? anchor = null,
        bool omitAnchor = false)
        => new()
        {
            Id = name,
            Source = kind,
            Name = name,
            Location = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
            RawCommand = path,
            ExecutablePath = path,
            TargetExistsOverride = targetExists,
            RegistryAnchor = omitAnchor
                ? null
                : anchor ?? (kind is StartupSourceKind.RegistryRunHkcu
                    or StartupSourceKind.RegistryRunHklm
                    or StartupSourceKind.RegistryRunWow64
                    ? RunAnchor with { ValueName = name }
                    : null),
        };

    private static SoftwareProfile Profile(string name, params StartupEntry[] entries)
        => new()
        {
            Key = name,
            DisplayName = name,
            StartupEntries = entries,
            Score = new RiskScore(30, RiskLevel.Normal, []),
        };

    [Fact]
    public void BuildPlan_IncludesOrphanedRunEntries()
    {
        var profile = Profile("OldApp", Entry("OldApp", @"D:\Gone\old.exe", targetExists: false));

        var plan = new StartupCleaner().BuildPlan([profile]);

        Assert.Equal(1, plan.Count);
        Assert.Equal("OldApp", plan.Targets[0].EntryName);
        Assert.Equal(RegistryHive.CurrentUser, plan.Targets[0].Anchor.Hive);
    }

    [Fact]
    public void BuildPlan_ExcludesEntriesWhoseTargetStillExists()
    {
        // 最关键的安全用例：目标文件还在，绝不能进清理名单
        var profile = Profile("LiveApp", Entry("LiveApp", @"C:\Apps\live.exe", targetExists: true));

        var plan = new StartupCleaner().BuildPlan([profile]);

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Skipped);   // 不是"跳过"，而是根本不该考虑
    }

    [Fact]
    public void BuildPlan_OnlyTakesOrphanedEntriesFromMixedProfile()
    {
        // 一个软件两个启动点：一个失效、一个有效 —— 只能删失效那个
        var profile = Profile("Mixed",
            Entry("MixedOld", @"D:\Old\mixed.exe", targetExists: false),
            Entry("MixedNew", @"C:\Apps\mixed.exe", targetExists: true));

        var plan = new StartupCleaner().BuildPlan([profile]);

        Assert.Equal(1, plan.Count);
        Assert.Equal("MixedOld", plan.Targets[0].EntryName);
    }

    [Theory]
    [InlineData(StartupSourceKind.WindowsService)]
    [InlineData(StartupSourceKind.ScheduledTask)]
    [InlineData(StartupSourceKind.StartupFolderUser)]
    [InlineData(StartupSourceKind.StartupFolderMachine)]
    public void BuildPlan_RejectsNonRunKindsAndExplainsWhy(StartupSourceKind kind)
    {
        var profile = Profile("Other", Entry("Other", @"D:\Gone\x.exe", targetExists: false, kind: kind));

        var plan = new StartupCleaner().BuildPlan([profile]);

        Assert.True(plan.IsEmpty);
        Assert.Single(plan.Skipped);
        Assert.Contains("暂不支持自动清理", plan.Skipped[0].Reason);
    }

    [Fact]
    public void BuildPlan_RejectsEntryWithoutRegistryAnchor()
    {
        var profile = Profile("NoAnchor",
            Entry("NoAnchor", @"D:\Gone\x.exe", targetExists: false, omitAnchor: true));

        var plan = new StartupCleaner().BuildPlan([profile]);

        Assert.True(plan.IsEmpty);
        Assert.Contains("缺少注册表定位信息", plan.Skipped[0].Reason);
    }

    [Fact]
    public void BuildPlan_EmptyProfiles_ProducesEmptyPlan()
    {
        var plan = new StartupCleaner().BuildPlan([]);

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void BuildPlan_HandlesMultipleProfilesAndEntries()
    {
        var a = Profile("A", Entry("A1", @"D:\Gone\a1.exe", false), Entry("A2", @"D:\Gone\a2.exe", false));
        var b = Profile("B", Entry("B1", @"C:\Live\b1.exe", true));

        var plan = new StartupCleaner().BuildPlan([a, b]);

        Assert.Equal(2, plan.Count);
        Assert.DoesNotContain(plan.Targets, t => t.EntryName == "B1");
    }
}

/// <summary>备份文件生成测试（不写真实注册表，只验文件内容）。</summary>
public class StartupCleanerBackupTests
{
    private static CleanupPlan PlanWith(params (RegistryHive Hive, string SubKey, string Value)[] items)
    {
        var targets = items.Select(i => new CleanupTarget(
            "App",
            i.Value,
            StartupSourceKind.RegistryRunHkcu,
            new RegistryAnchor(i.Hive, RegistryView.Default, i.SubKey, i.Value),
            $@"D:\Gone\{i.Value}.exe",
            $@"D:\Gone\{i.Value}.exe")).ToList();

        return new CleanupPlan { Targets = targets };
    }

    [Fact]
    public void WriteBackup_CreatesRegFileWithHeader()
    {
        var dir = TempDir();
        try
        {
            var plan = PlanWith((RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "OldApp"));

            var path = new StartupCleaner().WriteBackup(plan, dir, DateTime.Now);

            Assert.True(File.Exists(path));
            Assert.EndsWith(".reg", path);

            // .reg 必须以 UTF-16 LE BOM 开头，否则中文路径导入会乱码
            var bytes = File.ReadAllBytes(path);
            Assert.Equal([0xFF, 0xFE], bytes[..2]);

            var text = File.ReadAllText(path);
            Assert.Contains("Windows Registry Editor Version 5.00", text);
            Assert.Contains(@"[HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run]", text);
            Assert.Contains("\"OldApp\"=", text);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void WriteBackup_PreservesOriginalCommandSoRestoreWorks()
    {
        var dir = TempDir();
        try
        {
            var plan = new CleanupPlan
            {
                Targets =
                [
                    new CleanupTarget(
                        "App", "OldApp", StartupSourceKind.RegistryRunHkcu,
                        new RegistryAnchor(RegistryHive.CurrentUser, RegistryView.Default,
                            @"Software\Microsoft\Windows\CurrentVersion\Run", "OldApp"),
                        @"D:\Gone\old.exe",
                        "\"D:\\Gone\\old.exe\" --flag"),
                ],
            };

            var path = new StartupCleaner().WriteBackup(plan, dir, DateTime.Now);
            var text = File.ReadAllText(path);

            // 原始命令行必须被保存，否则还原出来的是错的值；
            // .reg 里内层引号要转义成 \"，所以最终文本形如 \"D:\\Gone\\old.exe\" --flag
            Assert.Contains("--flag", text);
            Assert.Contains("old.exe", text);

            // 还原回来的值应当与原始命令行一致（去掉 .reg 的转义后）
            var restored = text.Replace("\\\"", "\"").Replace("\\\\", "\\");
            Assert.Contains(@"""D:\Gone\old.exe"" --flag", restored);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void WriteBackup_GroupsByKeySoRegFileIsValid()
    {
        var dir = TempDir();
        try
        {
            var runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            var plan = PlanWith(
                (RegistryHive.CurrentUser, runKey, "A"),
                (RegistryHive.CurrentUser, runKey, "B"));

            var path = new StartupCleaner().WriteBackup(plan, dir, DateTime.Now);
            var occurrences = File.ReadAllLines(path).Count(l => l.StartsWith('['));

            // 同一个键只能有一个 [section]，否则 .reg 结构不合法
            Assert.Equal(1, occurrences);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void WriteBackup_LocalMachineDotNet32_MapsToWow6432Node()
    {
        var dir = TempDir();
        try
        {
            var plan = new CleanupPlan
            {
                Targets =
                [
                    new CleanupTarget(
                        "App", "X", StartupSourceKind.RegistryRunWow64,
                        new RegistryAnchor(RegistryHive.LocalMachine, RegistryView.Registry32,
                            @"Software\Microsoft\Windows\CurrentVersion\Run", "X"),
                        @"D:\Gone\x.exe", @"D:\Gone\x.exe"),
                ],
            };

            var path = new StartupCleaner().WriteBackup(plan, dir, DateTime.Now);

            Assert.Contains(@"HKEY_LOCAL_MACHINE\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
                File.ReadAllText(path));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void WriteBackup_MultipleBackups_ProduceDistinctFiles()
    {
        var dir = TempDir();
        try
        {
            var plan = PlanWith((RegistryHive.CurrentUser, @"Software\Run", "A"));
            var cleaner = new StartupCleaner();

            var first = cleaner.WriteBackup(plan, dir, new DateTime(2026, 1, 1, 10, 0, 0));
            var second = cleaner.WriteBackup(plan, dir, new DateTime(2026, 1, 1, 10, 0, 1));

            Assert.NotEqual(first, second);
            Assert.Equal(2, Directory.GetFiles(dir, "*.reg").Length);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rc-backup-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响断言结果
        }
    }
}
