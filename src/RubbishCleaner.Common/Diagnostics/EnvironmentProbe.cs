using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace RubbishCleaner.Common.Diagnostics;

/// <summary>本机运行环境快照，用于"关于"面板与问题反馈。</summary>
public sealed record EnvironmentInfo(
    string OsCaption,
    string OsVersion,
    string Architecture,
    string ProcessArchitecture,
    string DotNetVersion,
    string RuntimeDescription,
    bool IsElevated,
    string UserName,
    string MachineName,
    int ProcessorCount,
    long TotalPhysicalMemoryMb,
    string SystemDrive,
    IReadOnlyList<string> Notes);

/// <summary>环境探测：只读，不修改任何系统状态。</summary>
public static class EnvironmentProbe
{
    public static EnvironmentInfo Capture()
    {
        var notes = new List<string>();
        var isElevated = Win32.PrivilegeHelper.IsElevated;
        if (!isElevated)
        {
            notes.Add("当前以标准用户权限运行，计划任务与部分 HKLM 数据可能不完整。");
        }

        return new EnvironmentInfo(
            OsCaption: RuntimeInformation.OSDescription,
            OsVersion: Environment.OSVersion.Version.ToString(),
            Architecture: RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
            DotNetVersion: Environment.Version.ToString(),
            RuntimeDescription: RuntimeInformation.FrameworkDescription,
            IsElevated: isElevated,
            UserName: Environment.UserName,
            MachineName: Environment.MachineName,
            ProcessorCount: Environment.ProcessorCount,
            TotalPhysicalMemoryMb: GetTotalMemoryMb(),
            SystemDrive: Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
            Notes: notes);
    }

    /// <summary>生成适合贴进 Issue 的多行文本。</summary>
    public static string ToReport(EnvironmentInfo info)
    {
        var builder = new StringBuilder();
        builder.AppendLine("RubbishCleaner 环境信息");
        builder.AppendLine($"  操作系统    : {info.OsCaption}");
        builder.AppendLine($"  版本号      : {info.OsVersion}");
        builder.AppendLine($"  系统架构    : {info.Architecture} / 进程 {info.ProcessArchitecture}");
        builder.AppendLine($"  .NET        : {info.RuntimeDescription} ({info.DotNetVersion})");
        builder.AppendLine($"  管理员权限  : {(info.IsElevated ? "是" : "否")}");
        builder.AppendLine($"  用户 / 机器 : {info.UserName} @ {info.MachineName}");
        builder.AppendLine($"  CPU / 内存  : {info.ProcessorCount} 核 / {info.TotalPhysicalMemoryMb} MB");
        builder.AppendLine($"  系统盘      : {info.SystemDrive}");
        foreach (var note in info.Notes)
        {
            builder.AppendLine($"  提示        : {note}");
        }

        return builder.ToString();
    }

    private static long GetTotalMemoryMb()
    {
        try
        {
            return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        }
        catch
        {
            return 0;
        }
    }
}
