using System.Runtime.InteropServices;
using System.Text;

namespace RubbishCleaner.Common.Win32;

/// <summary>快捷方式解析结果。</summary>
public sealed record ShellLinkInfo(string? TargetPath, string? Arguments, string? WorkingDirectory, string? Description, string? Error);

/// <summary>
/// .lnk 文件解析，基于 Shell 的 <c>IShellLink</c> COM 接口。
/// 启动文件夹里的自启动项几乎都是快捷方式，必须解析出真实目标才能参与评分。
/// </summary>
public static class ShellLinkReader
{
    private const uint SLGP_RAWPATH = 0x00000004;
    private const uint STGM_READ = 0x00000000;

    /// <summary>解析快捷方式；任何失败都返回带 <see cref="ShellLinkInfo.Error"/> 的结果，不抛异常。</summary>
    public static ShellLinkInfo Read(string linkPath)
    {
        if (!File.Exists(linkPath))
        {
            return new ShellLinkInfo(null, null, null, null, "文件不存在");
        }

        object? comObject = null;
        try
        {
            comObject = new ShellLinkCoClass();
            var link = (IShellLinkW)comObject;
            ((IPersistFile)comObject).Load(linkPath, STGM_READ);

            var pathBuffer = new StringBuilder(1024);
            _ = link.GetPath(pathBuffer, pathBuffer.Capacity, IntPtr.Zero, SLGP_RAWPATH);

            var argsBuffer = new StringBuilder(1024);
            link.GetArguments(argsBuffer, argsBuffer.Capacity);

            var workDirBuffer = new StringBuilder(1024);
            link.GetWorkingDirectory(workDirBuffer, workDirBuffer.Capacity);

            var descBuffer = new StringBuilder(1024);
            link.GetDescription(descBuffer, descBuffer.Capacity);

            var target = pathBuffer.Length > 0 ? pathBuffer.ToString() : null;
            return new ShellLinkInfo(
                target,
                argsBuffer.Length > 0 ? argsBuffer.ToString() : null,
                workDirBuffer.Length > 0 ? workDirBuffer.ToString() : null,
                descBuffer.Length > 0 ? descBuffer.ToString() : null,
                null);
        }
        catch (COMException ex)
        {
            return new ShellLinkInfo(null, null, null, null, $"COM 错误 0x{ex.HResult:X8}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new ShellLinkInfo(null, null, null, null, ex.Message);
        }
        finally
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
            {
                _ = Marshal.ReleaseComObject(comObject);
            }
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLinkCoClass
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        [PreserveSig]
        int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
