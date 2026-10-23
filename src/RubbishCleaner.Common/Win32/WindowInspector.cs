using System.Runtime.InteropServices;

namespace RubbishCleaner.Common.Win32;

/// <summary>单个进程的窗口可见性事实。</summary>
public readonly record struct WindowFacts(bool HasVisibleWindow, bool HasTaskbarButton, int VisibleWindowCount);

/// <summary>
/// 通过一次 <c>EnumWindows</c> 全量枚举，建立 pid → 窗口事实的映射。
/// 相比"每个进程各枚举一遍窗口"的 O(n²) 做法，本方式在几百个进程时仍是毫秒级。
/// </summary>
public static class WindowInspector
{
    /// <summary>UWP 应用由该进程承载窗口，其子进程本身没有窗口，需特判避免误判为"无界面"。</summary>
    private const string UwpFrameHost = "ApplicationFrameHost";

    /// <summary>
    /// 枚举当前桌面所有可见顶层窗口，返回 pid 到窗口事实的字典。
    /// 无窗口的进程不会出现在字典中，调用方按"无窗口"处理。
    /// </summary>
    public static IReadOnlyDictionary<int, WindowFacts> Capture()
    {
        var accumulator = new Dictionary<int, (int Count, bool Taskbar)>();

        // 委托必须保持强引用直到枚举结束，否则会被 GC 回收导致崩溃。
        NativeMethods.EnumWindowsProc callback = (hWnd, lParam) =>
        {
            _ = lParam;
            try
            {
                if (!NativeMethods.IsWindowVisible(hWnd))
                {
                    return true;
                }

                // 无标题的窗口通常是消息窗、输入法候选窗等，不计入"可见界面"。
                if (NativeMethods.GetWindowTextLength(hWnd) == 0)
                {
                    return true;
                }

                _ = NativeMethods.GetWindowThreadProcessId(hWnd, out var pid); // 返回值是线程 id，此处只需 pid
                if (pid == 0)
                {
                    return true;
                }

                var exStyle = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                var isToolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
                var isAppWindow = (exStyle & NativeMethods.WS_EX_APPWINDOW) != 0;
                var hasOwner = NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER) != IntPtr.Zero;

                // 任务栏显示规则：显式声明 APPWINDOW，或既非工具窗也无 owner。
                var showsInTaskbar = isAppWindow || (!isToolWindow && !hasOwner);

                var key = (int)pid;
                if (accumulator.TryGetValue(key, out var existing))
                {
                    accumulator[key] = (existing.Count + 1, existing.Taskbar || showsInTaskbar);
                }
                else
                {
                    accumulator[key] = (1, showsInTaskbar);
                }
            }
            catch
            {
                // 单个窗口的异常不能中断整轮枚举。
            }

            return true;
        };

        _ = NativeMethods.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        var result = new Dictionary<int, WindowFacts>(accumulator.Count);
        foreach (var (pid, value) in accumulator)
        {
            result[pid] = new WindowFacts(true, value.Taskbar, value.Count);
        }

        return result;
    }

    /// <summary>UWP 宿主进程名判断，供上层做"有窗口"兜底。</summary>
    public static bool IsUwpFrameHost(string processName)
        => string.Equals(processName, UwpFrameHost, StringComparison.OrdinalIgnoreCase);
}
