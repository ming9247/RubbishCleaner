using System.IO;
using RubbishCleaner.App.ViewModels;
using RubbishCleaner.Core.Cleanup;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.App.Tests;

/// <summary>
/// 「需手动处理」的一键引导（v0.9.4）。
///
/// 背景：以前只给一条注册表/任务计划的地址和命令，新手不知道去哪、点哪里。
/// 现在是「打开对应管理界面 + 把名字放进剪贴板 + 分步提示」，最后那一下右键留给用户。
///
/// 这里不启动任何真实程序、不碰剪贴板：打开目标与复制文本都可注入。
/// </summary>
public class ManualFixCommandTests
{
    private static ManualFixGuide Guide(
        string name, StartupSourceKind source, ManualFixTargetKind target, string? targetPath)
        => new(name, source, @"D:\Gone\x.exe", "标题", "说明", null, null, target, targetPath);

    private static (MainViewModel Vm, List<string> Opened, List<string?> Args, List<string> Copied) Create()
    {
        var vm = new MainViewModel();
        var opened = new List<string>();
        var args = new List<string?>();
        var copied = new List<string>();
        vm.OpenTargetHandler = (target, argument) =>
        {
            opened.Add(target);
            args.Add(argument);
            return true;
        };
        vm.CopyTextHandler = text => copied.Add(text);
        return (vm, opened, args, copied);
    }

    [Fact]
    public void Service_OpensServicesMscAndCopiesServiceName()
    {
        var (vm, opened, _, copied) = Create();
        var item = new ManualFixItem(Guide("WxService", StartupSourceKind.WindowsService,
            ManualFixTargetKind.WindowsService, "WxService"));

        vm.OpenManualFixTargetCommand.Execute(item);

        Assert.Equal(["services.msc"], opened);
        Assert.Equal(["WxService"], copied);
        Assert.Contains("服务", vm.StatusText);
    }

    [Fact]
    public void Task_OpensTaskSchedulerAndCopiesNormalizedName()
    {
        var (vm, opened, _, copied) = Create();
        var item = new ManualFixItem(Guide(@"\Microsoft\Windows\MyTask", StartupSourceKind.ScheduledTask,
            ManualFixTargetKind.ScheduledTask, @"\Microsoft\Windows\MyTask"));

        vm.OpenManualFixTargetCommand.Execute(item);

        Assert.Equal(["taskschd.msc"], opened);
        Assert.Equal([@"\Microsoft\Windows\MyTask"], copied);
        Assert.Contains("任务计划程序", vm.StatusText);
    }

    [Fact]
    public void Shortcut_ExistingFile_SelectsItInExplorer()
    {
        var (vm, opened, args, _) = Create();
        var link = Path.Combine(Path.GetTempPath(), $"rc-manualfix-{Guid.NewGuid():N}.lnk");
        File.WriteAllText(link, "fake");
        try
        {
            var item = new ManualFixItem(Guide("SomeApp.lnk", StartupSourceKind.StartupFolderUser,
                ManualFixTargetKind.StartupFolder, link));

            vm.OpenManualFixTargetCommand.Execute(item);

            Assert.Equal(["explorer.exe"], opened);
            Assert.Contains("/select", args[0]);
            Assert.Contains(link, args[0]);
            Assert.Contains("Delete", vm.StatusText);
        }
        finally
        {
            File.Delete(link);
        }
    }

    [Fact]
    public void Shortcut_MissingFile_OpensItsFolder()
    {
        var (vm, opened, args, _) = Create();
        var folder = Path.Combine(Path.GetTempPath(), "rc-manualfix-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var item = new ManualFixItem(Guide("Gone.lnk", StartupSourceKind.StartupFolderUser,
                ManualFixTargetKind.StartupFolder, Path.Combine(folder, "Gone.lnk")));

            vm.OpenManualFixTargetCommand.Execute(item);

            Assert.Equal(["explorer.exe"], opened);
            Assert.Contains(folder, args[0]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TaskDefinitionFile_SelectsTheFileAndSaysAdminNeeded()
    {
        var (vm, opened, args, _) = Create();
        var file = Path.Combine(Path.GetTempPath(), $"rc-taskdef-{Guid.NewGuid():N}");
        File.WriteAllText(file, "<Task />");
        try
        {
            var item = new ManualFixItem(Guide("SomeTask", StartupSourceKind.ScheduledTask,
                ManualFixTargetKind.TaskDefinitionFile, file));

            Assert.Contains("定义文件", item.OpenTargetText);

            vm.OpenManualFixTargetCommand.Execute(item);

            Assert.Equal(["explorer.exe"], opened);
            Assert.Contains("/select", args[0]);
            Assert.Contains("管理员权限", vm.StatusText);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void CopyCommand_PutsTextOnClipboardAndReportsIt()
    {
        var (vm, _, _, copied) = Create();

        vm.CopyManualFixTextCommand.Execute("schtasks.exe /Delete /TN MyTask /F");

        Assert.Single(copied);
        Assert.Contains("schtasks", copied[0]);
        Assert.Contains("已复制", vm.StatusText);
    }

    [Fact]
    public void OpenFailure_IsReportedWithoutThrowing()
    {
        var (vm, _, _, _) = Create();
        vm.OpenTargetHandler = (_, _) => throw new InvalidOperationException("没有默认程序");
        var item = new ManualFixItem(Guide("WxService", StartupSourceKind.WindowsService,
            ManualFixTargetKind.WindowsService, "WxService"));

        var ex = Record.Exception(() => vm.OpenManualFixTargetCommand.Execute(item));

        Assert.Null(ex);
        Assert.Contains("无法打开管理界面", vm.StatusText);
    }
}
