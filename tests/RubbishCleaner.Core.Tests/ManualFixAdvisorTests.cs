using RubbishCleaner.Core.Cleanup;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// 手动处理指引测试。
///
/// 核心关注点：<b>给出的命令必须是对的</b>——
/// 用户会真的复制粘贴执行，命令错了后果可能很严重
/// （例如服务名猜错，可能删掉别的东西）。
/// </summary>
public class ManualFixAdvisorTests
{
    private static StartupEntry Entry(
        string name,
        StartupSourceKind kind,
        string? path = @"D:\Gone\x.exe",
        string location = @"C:\Windows\System32\Tasks")
        => new()
        {
            Id = name,
            Source = kind,
            Name = name,
            Location = location,
            ExecutablePath = path,
            RawCommand = path,
            TargetExistsOverride = false,
        };

    [Fact]
    public void Service_ProducesScDeleteCommandWithQuotedName()
    {
        var entry = Entry("Winstep Xtreme Service", StartupSourceKind.WindowsService,
            @"D:\nexus\Winstep\WsxService");

        var guide = ManualFixAdvisor.For(entry);

        Assert.NotNull(guide);
        Assert.Equal("残留的 Windows 服务", guide!.Title);
        // 服务名含空格，必须加引号，否则 sc 会把它当两个参数
        Assert.Equal("sc.exe delete \"Winstep Xtreme Service\"", guide.Command);
    }

    [Fact]
    public void Service_WarnsWhenServiceNameDiffersFromFileName()
    {
        // 服务名与可执行文件名不一致时，用户可能删错，必须提醒
        var entry = Entry("Winstep Xtreme Service", StartupSourceKind.WindowsService,
            @"D:\nexus\Winstep\WsxService");

        var guide = ManualFixAdvisor.For(entry);

        Assert.NotNull(guide!.CommandHint);
        Assert.Contains("Winstep Xtreme Service", guide.CommandHint);
        Assert.Contains("WsxService", guide.CommandHint);
    }

    [Fact]
    public void Service_NoHintWhenNameMatchesFile()
    {
        var entry = Entry("MyService", StartupSourceKind.WindowsService, @"C:\Apps\MyService.exe");

        var guide = ManualFixAdvisor.For(entry);

        Assert.Null(guide!.CommandHint);
    }

    [Fact]
    public void ScheduledTask_ProducesSchtasksDeleteWithLeadingBackslash()
    {
        // schtasks 要求任务名以 \ 开头，否则找不到任务
        var entry = Entry("MyTask", StartupSourceKind.ScheduledTask);

        // 注册探测在生产里走真实注册表；这里固定为"已注册"，专测命令拼装。
        var guide = ManualFixAdvisor.For(entry, isTaskRegistered: _ => true);

        Assert.NotNull(guide);
        Assert.Equal(@"schtasks.exe /Delete /TN ""\MyTask"" /F", guide!.Command);
    }

    [Fact]
    public void ScheduledTask_KeepsExistingPathStructure()
    {
        var entry = Entry(@"\Microsoft\Windows\MyTask", StartupSourceKind.ScheduledTask);

        var guide = ManualFixAdvisor.For(entry, isTaskRegistered: _ => true);

        // 已有前导反斜杠时不能重复添加
        Assert.Equal(@"schtasks.exe /Delete /TN ""\Microsoft\Windows\MyTask"" /F", guide!.Command);
        Assert.Null(guide.CommandHint);
    }

    [Theory]
    [InlineData(StartupSourceKind.StartupFolderUser)]
    [InlineData(StartupSourceKind.StartupFolderMachine)]
    public void StartupFolder_GivesLocationInsteadOfCommand(StartupSourceKind kind)
    {
        var entry = Entry("SomeApp", kind, @"D:\Gone\app.exe",
            location: @"C:\Users\x\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup");

        var guide = ManualFixAdvisor.For(entry);

        Assert.NotNull(guide);
        // 删快捷方式不需要命令，给位置就够了
        Assert.Null(guide!.Command);
        Assert.Contains("位置：", guide.CommandHint);
    }

    [Theory]
    [InlineData(StartupSourceKind.RegistryRunHkcu)]
    [InlineData(StartupSourceKind.RegistryRunHklm)]
    [InlineData(StartupSourceKind.RegistryRunWow64)]
    public void RunEntries_HaveNoManualGuide(StartupSourceKind kind)
    {
        // Run 类由"清理残留启动项"自动处理，不需要手动指引
        var entry = Entry("App", kind);

        Assert.Null(ManualFixAdvisor.For(entry));
    }

    [Fact]
    public void Service_TargetsServicesMscWithServiceName()
    {
        var entry = Entry("Winstep Xtreme Service", StartupSourceKind.WindowsService,
            @"D:\nexus\Winstep\WsxService");

        var guide = ManualFixAdvisor.For(entry);

        Assert.Equal(ManualFixTargetKind.WindowsService, guide!.Target);
        Assert.Equal("Winstep Xtreme Service", guide.TargetPath);
    }

    [Fact]
    public void ScheduledTask_TargetsTaskSchedulerWithNormalizedName()
    {
        var guide = ManualFixAdvisor.For(Entry("MyTask", StartupSourceKind.ScheduledTask), isTaskRegistered: _ => true);

        Assert.Equal(ManualFixTargetKind.ScheduledTask, guide!.Target);
        // 任务计划程序里显示的完整名以 \ 开头，复制给用户时必须是这个形态
        Assert.Equal(@"\MyTask", guide.TargetPath);
    }

    [Fact]
    public void StartupFolder_TargetsTheShortcutItself()
    {
        var location = @"C:\Users\x\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup";
        var guide = ManualFixAdvisor.For(Entry("SomeApp.lnk", StartupSourceKind.StartupFolderUser,
            @"D:\Gone\app.exe", location: location));

        Assert.Equal(ManualFixTargetKind.StartupFolder, guide!.Target);
        Assert.Equal(Path.Combine(location, "SomeApp.lnk"), guide.TargetPath);
    }

    [Fact]
    public void TaskRegistered_KeepsSchtasksCommand()
    {
        var entry = Entry("MyTask", StartupSourceKind.ScheduledTask);

        var guide = ManualFixAdvisor.For(entry, isTaskRegistered: _ => true);

        Assert.Equal(ManualFixTargetKind.ScheduledTask, guide!.Target);
        Assert.Contains("schtasks.exe /Delete", guide.Command);
        Assert.DoesNotContain("del /f", guide.Command);
    }

    [Fact]
    public void TaskNotRegistered_PointsAtTheDefinitionFileInstead()
    {
        // 真机场景：OneDrive 升级后留下定义文件但没有注册项——
        // 这时 schtasks 必然报「找不到」，必须改成"删那份文件"。
        var entry = Entry("SomeTask-500", StartupSourceKind.ScheduledTask,
            location: @"C:\Windows\System32\Tasks");

        var guide = ManualFixAdvisor.For(entry, isTaskRegistered: _ => false);

        Assert.Equal(ManualFixTargetKind.TaskDefinitionFile, guide!.Target);
        Assert.Equal(@"C:\Windows\System32\Tasks\SomeTask-500", guide.TargetPath);
        Assert.Contains("已不存在", guide.Title);
        Assert.StartsWith("del /f /q", guide.Command);
        Assert.Contains("SomeTask-500", guide.Command);
    }

    [Fact]
    public void TaskRegistrationUnknown_KeepsSchtasksButMentionsFileFallback()
    {
        // 非管理员读不到 TaskCache 时不能瞎断言，命令保留，但要把"只剩定义文件"的出路写清楚
        var entry = Entry("SomeTask", StartupSourceKind.ScheduledTask,
            location: @"C:\Windows\System32\Tasks");

        var guide = ManualFixAdvisor.For(entry, isTaskRegistered: _ => null);

        Assert.Equal(ManualFixTargetKind.ScheduledTask, guide!.Target);
        Assert.Contains("schtasks.exe /Delete", guide.Command);
        Assert.Contains("只剩定义文件", guide.CommandHint);
        Assert.Contains(@"C:\Windows\System32\Tasks\SomeTask", guide.CommandHint);
    }

    [Fact]
    public void NullEntry_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ManualFixAdvisor.For(null!));
    }
}

/// <summary>验证"残留总数"与"可清理数"的区分逻辑（UI 文案依据）。</summary>
public class OrphanCountSemanticsTests
{
    private static StartupEntry Entry(string name, StartupSourceKind kind, bool orphan)
        => new()
        {
            Id = name,
            Source = kind,
            Name = name,
            Location = "loc",
            ExecutablePath = @"D:\Gone\x.exe",
            TargetExistsOverride = !orphan,
            RegistryAnchor = kind is StartupSourceKind.RegistryRunHkcu
                or StartupSourceKind.RegistryRunHklm
                or StartupSourceKind.RegistryRunWow64
                ? new RegistryAnchor(Microsoft.Win32.RegistryHive.CurrentUser,
                    Microsoft.Win32.RegistryView.Default,
                    @"Software\Microsoft\Windows\CurrentVersion\Run", name)
                : null,
        };

    [Fact]
    public void BuildPlan_CountMatchesCleanableOrphansOnly()
    {
        var profile = new SoftwareProfile
        {
            Key = "x",
            DisplayName = "x",
            StartupEntries =
            [
                Entry("RunOrphan", StartupSourceKind.RegistryRunHkcu, orphan: true),
                Entry("SvcOrphan", StartupSourceKind.WindowsService, orphan: true),
                Entry("Live", StartupSourceKind.RegistryRunHkcu, orphan: false),
            ],
            Score = new RiskScore(30, RiskLevel.Normal, []),
        };

        var plan = new StartupCleaner().BuildPlan([profile]);

        // 总残留 2 条，但只有 1 条可自动清理
        Assert.Equal(2, profile.OrphanEntries.Count);
        Assert.Equal(1, plan.Count);
        Assert.Single(plan.Skipped);
    }
}
