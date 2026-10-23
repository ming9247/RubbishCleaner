using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RubbishCleaner.Common.Win32;

namespace RubbishCleaner.App.Infrastructure;

/// <summary>
/// 文件图标缓存：把 Win32 <c>HICON</c> 转成 WPF <see cref="ImageSource"/>。
///
/// 关键点：
/// 1. 转换后立即 <c>Freeze()</c>，使位图跨线程安全——扫描在后台线程并行构建行视图模型，
///    WPF 的 <c>BitmapSource</c> 默认有线程亲和性，不冻结会在 UI 线程抛异常。
/// 2. 按路径缓存，同一 exe 只提取一次。
/// 3. <c>HICON</c> 句柄用完立刻释放，否则会泄漏 GDI 对象（每行一个图标，量大）。
/// </summary>
public static class IconCache
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>默认图标（文件不存在且无法按扩展名取图标时使用）。</summary>
    private static readonly Lazy<ImageSource?> Fallback = new(CreateFallback, isThreadSafe: true);

    /// <summary>取指定文件的图标；任何失败都返回默认图标，绝不抛异常。</summary>
    public static ImageSource? Get(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Fallback.Value;
        }

        return Cache.GetOrAdd(filePath, static path =>
        {
            var hIcon = ShellIcon.TryExtract(path);
            if (hIcon == IntPtr.Zero)
            {
                return Fallback.Value;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                // 冻结后才能跨线程使用。
                source.Freeze();
                return (ImageSource)source;
            }
            catch
            {
                return Fallback.Value;
            }
            finally
            {
                ShellIcon.Destroy(hIcon);
            }
        });
    }

    /// <summary>用一个中性图标作为兜底，避免列表出现空洞。</summary>
    private static ImageSource? CreateFallback()
    {
        try
        {
            // 取 exe 的通用图标（不依赖具体文件是否存在）。
            var hIcon = ShellIcon.TryExtract("placeholder.exe");
            if (hIcon != IntPtr.Zero)
            {
                try
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(
                        hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally
                {
                    ShellIcon.Destroy(hIcon);
                }
            }
        }
        catch
        {
            // 忽略，返回 null 时 UI 显示空白但不影响功能。
        }

        return null;
    }
}
