using Microsoft.Win32;

namespace RubbishCleaner.Core.Detection;

/// <summary>
/// 判断一份任务定义文件是否还**注册**在「任务计划程序」里（v0.9.5）。
///
/// <para><b>为什么需要这个判断</b>：<c>%SystemRoot%\System32\Tasks</c> 下的 XML 只是"定义文件"，
/// 任务计划程序是否列出它，取决于注册表
/// <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree</c> 里有没有对应项。</para>
///
/// <para>真机上踩到的坑（v0.9.4 用户反馈）：OneDrive 升级后留下了一份定义文件但没有注册项，
/// 于是「任务计划程序」里翻不到、<c>schtasks /Delete</c> 报「找不到该程序」，
/// 而我们却让用户去执行那条命令。正确做法是删那份**定义文件**。</para>
///
/// <para>返回值三态：<c>true</c>=已注册；<c>false</c>=只剩定义文件；<c>null</c>=无权判断（非管理员等，
/// 此时调用方不应该断言"未注册"）。</para>
/// </summary>
public static class ScheduledTaskRegistration
{
    private const string TreePath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree";

    /// <summary>任务名（可带前导反斜杠）在 TaskCache\Tree 里是否还存在。</summary>
    public static bool? IsRegistered(string taskName)
    {
        if (string.IsNullOrWhiteSpace(taskName))
        {
            return null;
        }

        try
        {
            var relative = taskName.TrimStart('\\');
            using var key = Registry.LocalMachine.OpenSubKey(TreePath + "\\" + relative);
            return key is not null;
        }
        catch (System.Security.SecurityException)
        {
            return null;   // 非管理员读不到 TaskCache，不能断言"未注册"
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }
}
