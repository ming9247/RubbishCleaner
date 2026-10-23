using System.Runtime.InteropServices;

namespace RubbishCleaner.Common.Win32;

/// <summary>
/// 从文件提取 Shell 图标。
///
/// 设计约束：本类只返回 Win32 的 <c>HICON</c> 句柄（<see cref="IntPtr"/>），
/// <b>不引用任何 WPF 类型</b>——Common 层必须保持 UI 无关（技术文档 §1.1）。
/// 句柄到 <c>ImageSource</c> 的转换由 App 层完成。
///
/// 调用方<b>必须</b>在用完后调用 <see cref="Destroy"/> 释放句柄，否则会泄漏 GDI 资源。
/// </summary>
public static class ShellIcon
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    /// <summary>提取文件图标句柄；失败返回 <see cref="IntPtr.Zero"/>。</summary>
    /// <param name="filePath">目标文件路径。</param>
    /// <param name="large">true 取 32×32，false 取 16×16。</param>
    public static IntPtr TryExtract(string? filePath, bool large = false)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return IntPtr.Zero;
        }

        try
        {
            var flags = SHGFI_ICON | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON);

            // 文件不存在时（例如已被卸载的残留项）退回"按扩展名取默认图标"，
            // 至少还能给用户一个可辨识的类型图标，而不是空白。
            if (!File.Exists(filePath))
            {
                flags |= SHGFI_USEFILEATTRIBUTES;
            }

            var info = new ShFileInfo();
            var result = SHGetFileInfo(filePath, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);

            return result == IntPtr.Zero ? IntPtr.Zero : info.hIcon;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>释放 <see cref="TryExtract"/> 返回的图标句柄。</summary>
    public static void Destroy(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _ = DestroyIcon(hIcon);
        }
        catch
        {
            // 释放失败不应影响主流程。
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref ShFileInfo psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
