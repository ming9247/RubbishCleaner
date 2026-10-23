using System.Runtime.InteropServices;
using System.Text;

namespace RubbishCleaner.Common.Win32;

/// <summary>
/// 基于 <c>WinVerifyTrust</c> 的 Authenticode 校验，支持<b>目录签名（catalog signature）</b>。
///
/// 为什么必须有它：Windows 自带的大量系统二进制（如 <c>spoolsv.exe</c>、<c>SearchIndexer.exe</c>）
/// 并没有内嵌签名，而是由 <c>%SystemRoot%\System32\CatRoot</c> 下的安全目录（.cat）签名。
/// 只用 <see cref="System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(string)"/>
/// 会把它们全部误判为"无签名"，进而把微软系统服务推成"高风险"。
/// </summary>
internal static class AuthenticodeVerifier
{
    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_CHOICE_CATALOG = 2;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_SAFER_FLAG = 0x100;

    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>校验结果：是否有效 + 签名主体。</summary>
    internal readonly record struct Result(bool IsValid, string? Subject, string Method);

    public static Result Verify(string filePath)
    {
        // 1) 先试内嵌签名（最快，且大多数第三方软件走这条路）
        var embedded = VerifyWithFileChoice(filePath);
        if (embedded.IsValid)
        {
            return embedded;
        }

        // 2) 再试目录签名（Windows 系统组件的常见形态）
        var catalog = VerifyWithCatalog(filePath);
        if (catalog.IsValid)
        {
            return catalog;
        }

        return new Result(false, null, "none");
    }

    /// <summary>校验内嵌在文件里的签名。</summary>
    private static Result VerifyWithFileChoice(string filePath)
    {
        var fileInfoPtr = IntPtr.Zero;
        var pathPtr = IntPtr.Zero;
        var trustDataPtr = IntPtr.Zero;

        try
        {
            pathPtr = Marshal.StringToHGlobalUni(filePath);

            var fileInfo = new WinTrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                pcwszFilePath = pathPtr,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };

            fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

            var trustData = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pInfo = fileInfoPtr,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_SAFER_FLAG,
            };

            trustDataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trustData, trustDataPtr, false);

            var action = WinTrustActionGenericVerifyV2;
            var status = WinVerifyTrust(IntPtr.Zero, ref action, trustDataPtr);

            // 无论成功与否都必须关闭状态，否则会泄漏状态句柄。
            CloseState(trustDataPtr);

            if (status != 0)
            {
                return new Result(false, null, "none");
            }

            return new Result(true, TryReadSubject(filePath), "embedded");
        }
        catch
        {
            return new Result(false, null, "none");
        }
        finally
        {
            FreeAll(fileInfoPtr, pathPtr, trustDataPtr);
        }
    }

    /// <summary>通过安全目录（.cat）校验，覆盖系统组件的目录签名。</summary>
    private static Result VerifyWithCatalog(string filePath)
    {
        if (!CryptCATAdminAcquireContext2(out var catAdmin, IntPtr.Zero, "SHA256", IntPtr.Zero, 0)
            && !CryptCATAdminAcquireContext2(out catAdmin, IntPtr.Zero, "SHA1", IntPtr.Zero, 0))
        {
            return new Result(false, null, "none");
        }

        var hFile = IntPtr.Zero;
        var hashPtr = IntPtr.Zero;
        var memberTagPtr = IntPtr.Zero;
        var catalogPathPtr = IntPtr.Zero;
        var memberPathPtr = IntPtr.Zero;
        var catInfoPtr = IntPtr.Zero;
        var trustDataPtr = IntPtr.Zero;
        var catalogInfoPtr = IntPtr.Zero;
        var catalogContext = IntPtr.Zero;

        try
        {
            hFile = CreateFileW(filePath, 0x80000000 /* GENERIC_READ */, 1 /* FILE_SHARE_READ */,
                IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (hFile == IntPtr.Zero || hFile == new IntPtr(-1))
            {
                return new Result(false, null, "none");
            }

            uint hashLength = 0;
            _ = CryptCATAdminCalcHashFromFileHandle2(catAdmin, hFile, ref hashLength, IntPtr.Zero, 0);
            if (hashLength == 0)
            {
                return new Result(false, null, "none");
            }

            hashPtr = Marshal.AllocHGlobal((int)hashLength);
            if (!CryptCATAdminCalcHashFromFileHandle2(catAdmin, hFile, ref hashLength, hashPtr, 0))
            {
                return new Result(false, null, "none");
            }

            var hashBytes = new byte[hashLength];
            Marshal.Copy(hashPtr, hashBytes, 0, (int)hashLength);
            var memberTag = Convert.ToHexString(hashBytes);

            catalogContext = CryptCATAdminEnumCatalogFromHash(catAdmin, hashPtr, hashLength, 0, IntPtr.Zero);
            if (catalogContext == IntPtr.Zero)
            {
                return new Result(false, null, "none");
            }

            catalogInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<CatalogInfo>());
            var catalogInfo = new CatalogInfo { cbStruct = (uint)Marshal.SizeOf<CatalogInfo>() };
            Marshal.StructureToPtr(catalogInfo, catalogInfoPtr, false);

            if (!CryptCATCatalogInfoFromContext(catalogContext, catalogInfoPtr, 0))
            {
                return new Result(false, null, "none");
            }

            catalogInfo = Marshal.PtrToStructure<CatalogInfo>(catalogInfoPtr);
            if (string.IsNullOrWhiteSpace(catalogInfo.wszCatalogFile))
            {
                return new Result(false, null, "none");
            }

            catalogPathPtr = Marshal.StringToHGlobalUni(catalogInfo.wszCatalogFile);
            memberPathPtr = Marshal.StringToHGlobalUni(filePath);
            memberTagPtr = Marshal.StringToHGlobalUni(memberTag);

            catInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustCatalogInfo>());

            var catalogTrustInfo = new WinTrustCatalogInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustCatalogInfo>(),
                dwCatalogVersion = 0,
                pcwszCatalogFilePath = catalogPathPtr,
                pcwszMemberTag = memberTagPtr,
                pcwszMemberFilePath = memberPathPtr,

                // 传已打开的文件句柄：WinVerifyTrust 会用它重新计算哈希并与目录比对。
                // 传 IntPtr.Zero 时也能工作，但会走"按路径重新打开文件"的慢路径。
                hMemberFile = hFile,
                pbCalculatedFileHash = hashPtr,
                cbCalculatedFileHash = hashLength,

                // 关键坑（实测踩过，会直接 AccessViolation）：
                // pcCatalogContext 类型是 PCATALOG_INFO，WinVerifyTrust 会把它当作
                // CATALOG_INFO 结构指针解引用。这里必须传 NULL，
                // 传枚举句柄或 CATALOG_INFO 指针都会导致读取受保护内存而崩溃。
                pcCatalogContext = IntPtr.Zero,
                hCatAdmin = catAdmin,
            };

            Marshal.StructureToPtr(catalogTrustInfo, catInfoPtr, false);

            var trustData = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_CATALOG,
                pInfo = catInfoPtr,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_SAFER_FLAG,
            };

            trustDataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trustData, trustDataPtr, false);

            var action = WinTrustActionGenericVerifyV2;
            var status = WinVerifyTrust(IntPtr.Zero, ref action, trustDataPtr);

            CloseState(trustDataPtr);

            return status == 0
                ? new Result(true, TryReadSubject(filePath), "catalog")
                : new Result(false, null, "none");
        }
        catch
        {
            return new Result(false, null, "none");
        }
        finally
        {
            if (catalogContext != IntPtr.Zero)
            {
                _ = CryptCATAdminReleaseCatalogContext(catAdmin, catalogContext, 0);
            }

            _ = CryptCATAdminReleaseContext(catAdmin, 0);

            if (hFile != IntPtr.Zero && hFile != new IntPtr(-1))
            {
                _ = CloseHandle(hFile);
            }

            foreach (var ptr in new[] { hashPtr, memberTagPtr, catalogPathPtr, memberPathPtr, catInfoPtr, trustDataPtr, catalogInfoPtr })
            {
                if (ptr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
        }
    }

    /// <summary>用 X509 读取签名主体（仅内嵌签名有效；目录签名取不到主体，退回 null）。</summary>
    private static string? TryReadSubject(string filePath)
    {
        try
        {
            using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate
                .CreateFromSignedFile(filePath);
            return certificate.Subject;
        }
        catch
        {
            return null;
        }
    }

    private static void CloseState(IntPtr trustDataPtr)
    {
        if (trustDataPtr == IntPtr.Zero)
        {
            return;
        }

        var trustData = Marshal.PtrToStructure<WinTrustData>(trustDataPtr);
        trustData.dwStateAction = WTD_STATEACTION_CLOSE;
        Marshal.StructureToPtr(trustData, trustDataPtr, false);

        var action = WinTrustActionGenericVerifyV2;
        _ = WinVerifyTrust(IntPtr.Zero, ref action, trustDataPtr);
    }

    private static void FreeAll(params IntPtr[] pointers)
    {
        foreach (var ptr in pointers)
        {
            if (ptr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustCatalogInfo
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public IntPtr pcwszCatalogFilePath;
        public IntPtr pcwszMemberTag;
        public IntPtr pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pInfo;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogInfo
    {
        public uint cbStruct;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string wszCatalogFile;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true, SetLastError = false)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(
        out IntPtr phCatAdmin,
        IntPtr pgSubsystem,
        [MarshalAs(UnmanagedType.LPWStr)] string pwszHashAlgorithm,
        IntPtr pStrongHashPolicy,
        uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(
        IntPtr hCatAdmin,
        IntPtr hFile,
        ref uint pcbHash,
        IntPtr pbHash,
        uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(
        IntPtr hCatAdmin,
        IntPtr pbHash,
        uint cbHash,
        uint dwFlags,
        IntPtr phPrevCatInfo);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATCatalogInfoFromContext(
        IntPtr hCatInfo,
        IntPtr psCatInfo,
        uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseCatalogContext(
        IntPtr hCatAdmin,
        IntPtr hCatInfo,
        uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
