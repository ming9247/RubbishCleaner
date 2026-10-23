using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace RubbishCleaner.Common.Win32;

/// <summary>管理员权限检测与提权重启。</summary>
public static class PrivilegeHelper
{
    /// <summary>UAC 被用户取消时 <see cref="Process.Start(ProcessStartInfo)"/> 返回的错误码。</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 以管理员身份重新启动自身。用户取消 UAC 时返回 false 而不抛异常。
    /// </summary>
    public static bool TryRelaunchElevated(string? executablePath = null)
    {
        try
        {
            var path = executablePath ?? Environment.ProcessPath;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            var startInfo = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory,
            };

            _ = Process.Start(startInfo);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
}
