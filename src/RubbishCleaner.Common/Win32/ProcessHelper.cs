using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RubbishCleaner.Common.Win32;

/// <summary>进程基本信息（不包含窗口事实，窗口由 <see cref="WindowInspector"/> 统一采集）。</summary>
public sealed record ProcessInfo(int Id, string Name, string? ExecutablePath);

/// <summary>进程枚举与路径解析。</summary>
public static class ProcessHelper
{
    /// <summary>枚举全部可访问进程；访问被拒绝的进程会被跳过而不是抛异常。</summary>
    public static IReadOnlyList<ProcessInfo> Enumerate()
    {
        var list = new List<ProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                list.Add(new ProcessInfo(process.Id, process.ProcessName, TryGetExecutablePath(process.Id)));
            }
            catch
            {
                // 进程在枚举与访问之间退出属正常现象。
            }
            finally
            {
                process.Dispose();
            }
        }

        return list;
    }

    /// <summary>用 <c>QueryFullProcessImageName</c> 取完整路径，可处理 32/64 位与受保护进程。</summary>
    public static string? TryGetExecutablePath(int processId)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            return NativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref size)
                ? buffer.ToString(0, (int)size)
                : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _ = NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>结束进程树；失败返回 false 而不是抛出，由调用方决定如何提示。</summary>
    public static bool TryKillTree(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            return process.WaitForExit(5000);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把文件排队到下次重启时删除（用于被占用的顽固文件）。
    /// 需要管理员权限，失败返回 false。
    /// </summary>
    public static bool TryScheduleDeleteOnReboot(string path)
    {
        try
        {
            return NativeMethods.MoveFileExW(path, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
