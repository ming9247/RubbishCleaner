using System.Runtime.InteropServices;

namespace RubbishCleaner.Common.Win32;

/// <summary>
/// 把路径移入回收站的结果。
///
/// <para>
/// 刻意返回<b>结果对象</b>而不是 <see cref="bool"/> 或抛异常：
/// "删除失败"的原因（不存在 / 被占用 / 权限不足 / 没有回收站）必须能原样展示给用户，
/// 否则用户只会看到一个"失败"而不知道该关哪个程序或该提权。
/// 与 <c>RegistryDeleteResult</c> 保持同一种形态。
/// </para>
/// </summary>
/// <param name="Success">是否成功移入回收站（<b>只有</b>确认真的进了回收站才为 true）。</param>
/// <param name="Message">中文说明，可直接显示在界面上。</param>
/// <param name="ErrorCode">底层返回码；成功时为 0，语义失败（例如路径预检不通过）时也为 0。</param>
public sealed record RecycleBinResult(bool Success, string Message, int ErrorCode = 0)
{
    /// <summary>成功：路径已进入回收站，用户随时可从"回收站"还原。</summary>
    public static RecycleBinResult Ok(string message = "已移入回收站，可在回收站中还原")
        => new(true, message);

    /// <summary>失败：目标<b>没有被删除</b>（失败即保持原状，这是本类的基本承诺）。</summary>
    public static RecycleBinResult Fail(string message, int errorCode = 0)
        => new(false, message, errorCode);
}

/// <summary>
/// 把文件 / 目录<b>移入回收站</b>的能力 —— 本工具唯一的"删除"方式。
///
/// <para><b>为什么是回收站（可还原性 &gt; 速度）</b></para>
/// <para>
/// 残留判定本质上是启发式的：无论证据多硬，都存在把"用户其实还要用的目录"判成残留的可能。
/// 一旦永久删除，一次误判就变成不可恢复的数据损失，而且用户往往几天后才发现。
/// 移入回收站把这笔"误判成本"从永久降为可承受：用户右键就能还原，不装本工具也能还原。
/// 所以本类绝不提供 <c>File.Delete</c> / <c>Directory.Delete</c> 之类兜底，
/// 失败时宁可让清理任务报错，也不退回永久删除。
/// </para>
///
/// <para><b>为什么用 SHFileOperationW 而不是其它方案</b></para>
/// <list type="number">
/// <item>不用 <c>Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(..., RecycleOption.SendToRecycleBin)</c>：
/// 只为一个 API 给自包含发布带上 VisualBasic 运行时依赖，不划算。</item>
/// <item>不用 <c>IFileOperation</c>（COM 版）：需自己实现 IFileOperationProgressSink、
/// 处理 COM 单元线程模型，代码量是本实现的数倍，收益（动画进度）对本工具无价值。</item>
/// <item><c>SHFileOperationW</c> 是老 API 但行为稳定、回收站语义由 shell 自己完成，
/// 且没有"部分删除后返回错误"的花样。</item>
/// </list>
///
/// <para><b>三个必须踩准的细节（踩错就表现为"莫名其妙的失败"）</b></para>
/// <list type="number">
/// <item><b>pFrom 必须双 null 结尾</b>：该字段是 shell 的"多路径列表"格式 ——
/// 单路径时是"路径 + '\0'" 再叠加 C 字符串自身的结尾 '\0'，即连续两个 0。
/// 只传一个 null 结尾时 shell 会继续往后读到野内存，结果是随机失败甚至崩溃。
/// .NET 的 <c>string</c> 本身不含结尾 0，所以必须手工分配缓冲区，
/// 见 <see cref="AllocateDoubleNullTerminated"/>。</item>
/// <item><b>返回码是 win32 / DE_* 错误码，不是 HRESULT</b>：因此不能拿
/// <c>Marshal.GetLastWin32Error()</c> 顶替 —— 那个值是 shell 内部调用残留的
/// LastError，与本次结果无关，看着像"有值"其实毫无意义。必须解读函数返回值本身，
/// 见 <see cref="DescribeError"/>。</item>
/// <item><b>结构体布局</b>：<see cref="SHFILEOPSTRUCT"/> 里有 <see cref="IntPtr"/> 与
/// <c>WORD</c> 混排，32/64 位下的偏移不同。这里用 <c>Pack = 8</c> + 显式 IntPtr，
/// 两种位宽下与 native 布局一致（写成 <c>Pack = 1</c> 是常见错误：会把 pFrom 挤到
/// 32 位偏移上，64 位进程里整份结构立刻错位）。</item>
/// </list>
///
/// <para><b>安全闸门（按执行顺序，任何一条不通过都直接失败，绝不放行）</b></para>
/// <list type="number">
/// <item>路径非空且<b>必须是绝对路径</b>（相对路径会被 shell 按当前工作目录解释，误删风险极高）。</item>
/// <item>目标<b>必须真实存在</b>（文件或目录）：不存在的路径要给出确切原因，而不是让 API 返回含糊的错误码。</item>
/// <item>拒绝驱动器根目录，以及包含系统 / 用户关键目录的路径（例如整个 <c>C:\Windows</c>）。</item>
/// <item>目标所在卷<b>必须确实有回收站</b>（本地固定磁盘）：网络共享、未启用回收站的可移动磁盘上，
/// <c>FOF_ALLOWUNDO</c> 会被 shell 静默降级成<b>永久删除</b> —— 这正是产品红线，因此提前拒绝。</item>
/// <item>成功后<b>复核目标已不存在</b>再报成功（对齐 <c>RegistryWriter</c> 的"删后复核"）。</item>
/// </list>
///
/// <para><b>线程</b>：<c>SHFileOperationW</c> 本身不要求 STA，参数已全部关掉 UI
/// （<c>FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI</c>），不会弹窗阻塞自动化。
/// 不加 <c>FOF_NOCONFIRMMKDIR</c>：它只影响"是否询问创建目录"，与删除无关，
/// 混进来会让读者误解这组 flag 的意图。</para>
/// </summary>
public static class RecycleBin
{
    // ---- SHFILEOPSTRUCT.wFunc（shellapi.h）----
    /// <summary>删除操作。配合 <see cref="FOF_ALLOWUNDO"/> 时删除 = 移入回收站。</summary>
    private const uint FO_DELETE = 0x0003;

    // ---- SHFILEOPSTRUCT.fFlags（FILEOP_FLAGS，WORD）----
    /// <summary>不显示进度对话框。</summary>
    private const ushort FOF_SILENT = 0x0004;

    /// <summary>不弹出"确定要删除吗"确认框。本工具自己已经有二次确认，不需要 shell 再问一遍。</summary>
    private const ushort FOF_NOCONFIRMATION = 0x0010;

    /// <summary><b>核心开关</b>：允许撤销 —— 即走回收站而不是永久删除。</summary>
    private const ushort FOF_ALLOWUNDO = 0x0040;

    /// <summary>出错时不弹 UI，错误码由函数返回值交给我们处理。</summary>
    private const ushort FOF_NOERRORUI = 0x0400;

    /// <summary>本实现使用的标志组合：静默 + 不确认 + 可还原 + 不弹错误框。</summary>
    private const ushort Flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI;

    /// <summary>成功返回码。</summary>
    private const int SuccessCode = 0;

    /// <summary>
    /// <c>SHFileOperationW</c> 的路径参数受 <c>MAX_PATH</c>(260) 限制，超长路径会以
    /// "文件名无效"之类的错误码失败。提前判掉，给用户一句能看懂的话。
    /// </summary>
    private const int MaxPathLength = 260;

    /// <summary>用户主目录等关键锚点，用于拒绝"看似普通、实则要命"的目标。</summary>
    private static readonly string[] ProtectedRoots = BuildProtectedRoots();

    /// <summary>
    /// SHFILEOPSTRUCT（shellapi.h）。
    /// <para>
    /// 布局注意：<c>Pack = 8</c> 与 native 一致（x64: 0/8/16/24/32/36/40/48，共 56 字节；
    /// x86: 0/4/8/12/16/20/24/28，共 32 字节）。<c>fFlags</c> 是 WORD（2 字节），
    /// 之后 <c>BOOL</c> 会按 4 字节对齐，这个空档由运行时自动补齐，所以两个位宽都对得上。
    /// <c>lpszProgressTitle</c> 用 <see cref="IntPtr"/> 而非 string，避免结构体内做字符串封送
    /// （我们永远传 null，不需要那个开销）。
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        /// <summary>父窗口句柄，用于让潜在对话框模态化；本实现全部关 UI，固定传 0。</summary>
        public IntPtr hwnd;

        /// <summary>操作类型，见 <see cref="FO_DELETE"/>。</summary>
        public uint wFunc;

        /// <summary>
        /// 源路径列表，<b>双 null 结尾</b>。必须是 shell 能解析的绝对路径。
        /// </summary>
        public IntPtr pFrom;

        /// <summary>目标路径；<see cref="FO_DELETE"/> 时为 null。</summary>
        public IntPtr pTo;

        /// <summary>标志位，见 <see cref="Flags"/>。</summary>
        public ushort fFlags;

        /// <summary>操作是否被用户中止。<b>必须检查</b>：中止时返回码可能仍是 0。</summary>
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;

        /// <summary>映射表句柄，删除操作不用，传 null。</summary>
        public IntPtr hNameMappings;

        /// <summary>进度对话框标题；我们关掉了 UI，传 null。</summary>
        public IntPtr lpszProgressTitle;
    }

    /// <summary>
    /// 执行 shell 文件操作。约定：<c>SetLastError</c> 不开 —— 本 API 的错误信息在返回值里，
    /// 依赖 LastError 只会引入噪声。
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>
    /// 把指定的文件或目录<b>移入回收站</b>。
    ///
    /// <para>
    /// 语义保证：
    /// 1. 只做"移入回收站"，<b>不会</b>永久删除任何数据；
    /// 2. 任何一步不满足前置条件就返回失败，<b>失败时目标保持原状</b>；
    /// 3. 不抛异常，所有异常都转成 <see cref="RecycleBinResult"/>。
    /// </para>
    /// </summary>
    /// <param name="path">要移入回收站的绝对路径（文件或目录）。</param>
    /// <returns>结果对象；<see cref="RecycleBinResult.Success"/> 为 true 时目标确实已在回收站里。</returns>
    public static RecycleBinResult MoveToRecycleBin(string? path)
    {
        // ---- 闸门 1：非空 + 绝对路径 ----
        if (string.IsNullOrWhiteSpace(path))
        {
            return RecycleBinResult.Fail("路径为空，未做任何操作");
        }

        if (!Path.IsPathRooted(path))
        {
            // 相对路径会被 shell 按"当前工作目录"解释，同一份输入在不同进程里指向不同目标，
            // 这在删除场景下是纯粹的风险，直接拒绝。
            return RecycleBinResult.Fail($"不是绝对路径，为安全起见中止：「{path}」");
        }

        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex)
        {
            return RecycleBinResult.Fail($"路径无法规范化：{ex.Message}");
        }

        // ---- 闸门 2：必须真实存在 ----
        // File.Exists / Directory.Exists 不抛异常，对非法字符也只返回 false，正合适。
        var isDirectory = Directory.Exists(fullPath);
        var isFile = File.Exists(fullPath);
        if (!isDirectory && !isFile)
        {
            return RecycleBinResult.Fail("目标不存在（可能已被其他程序或用户删除/移动），未做任何操作");
        }

        // ---- 闸门 3：拒绝危险目标（盘根、系统/用户关键目录）----
        var dangerReason = FindDangerReason(fullPath);
        if (dangerReason is not null)
        {
            return RecycleBinResult.Fail(dangerReason);
        }

        // ---- 闸门 4：目标所在卷必须真的有回收站 ----
        // 这一步是"可还原性"的技术底座：FOF_ALLOWUNDO 在没有回收站的卷上会被 shell
        // 静默降级为永久删除，且返回码仍是 0。宁可不做，也不能悄悄变永久删除。
        var volumeReason = CheckRecycleSupport(fullPath);
        if (volumeReason is not null)
        {
            return RecycleBinResult.Fail(volumeReason);
        }

        if (fullPath.Length >= MaxPathLength)
        {
            return RecycleBinResult.Fail(
                $"路径长度 {fullPath.Length} 超过 {MaxPathLength - 1} 个字符，回收站 API 不支持超长路径，未做任何操作");
        }

        // ---- 调用 shell，缓冲区必须手工释放 ----
        var fromBuffer = IntPtr.Zero;
        try
        {
            // 双 null 结尾是这个 API 最容易踩的坑，单独封装并注释，避免有人"顺手优化"成 string。
            fromBuffer = AllocateDoubleNullTerminated(fullPath);

            var fileOp = new SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = FO_DELETE,
                pFrom = fromBuffer,
                pTo = IntPtr.Zero,      // FO_DELETE 不需要目标路径
                fFlags = Flags,
                fAnyOperationsAborted = false,
                hNameMappings = IntPtr.Zero,
                lpszProgressTitle = IntPtr.Zero,
            };

            var code = SHFileOperationW(ref fileOp);

            if (code != SuccessCode)
            {
                return RecycleBinResult.Fail(DescribeError(code, fullPath), code);
            }

            // 返回 0 但被中止：文件原封不动，不能报成功。
            if (fileOp.fAnyOperationsAborted)
            {
                return RecycleBinResult.Fail("操作被中止（目标被占用或系统拒绝），文件未移入回收站", code);
            }

            // ---- 闸门 5：删后复核 ----
            // 只有确认目标真的不在了才报成功，避免"接口说成功、界面显示已清理、文件却还在"的静默失败。
            if (Directory.Exists(fullPath) || File.Exists(fullPath))
            {
                return RecycleBinResult.Fail("系统报告操作成功，但目标仍然存在，请手动确认是否已进入回收站", code);
            }

            return RecycleBinResult.Ok($"已移入回收站：{fullPath}");
        }
        catch (DllNotFoundException)
        {
            return RecycleBinResult.Fail("当前系统找不到 shell32.dll，无法移入回收站");
        }
        catch (EntryPointNotFoundException)
        {
            return RecycleBinResult.Fail("当前系统不支持 SHFileOperationW，无法移入回收站");
        }
        catch (Exception ex)
        {
            // 兜底：绝不因为一个异常把"删除"降级成别的实现（尤其是永久删除）。
            return RecycleBinResult.Fail($"移入回收站时发生异常：{ex.Message}");
        }
        finally
        {
            if (fromBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(fromBuffer);
            }
        }
    }

    /// <summary>
    /// 分配 <b>双 null 结尾</b> 的 Unicode 路径缓冲区。
    ///
    /// <para>
    /// <c>pFrom</c> 是 shell 的多路径列表：每个路径以单个 0 结束，整个列表再以一个 0 结束。
    /// 只放一个路径时，内存里必须是「字符… + '\0' + '\0'」。数量上等于
    /// <c>路径长度 + 2</c> 个 char。
    /// </para>
    /// <para>
    /// 这里不用 <c>Marshal.StringToHGlobalUni(path + "\0")</c> 之类的写法：
    /// 那种"字符串里塞 0"的写法依赖读者知道 .NET 会再补一个 0，极易被当成多余的 0 删掉；
    /// 手工写 char 数组能把意图摆在明面上。
    /// </para>
    /// <para>返回的缓冲区必须由调用方用 <see cref="Marshal.FreeHGlobal(IntPtr)"/> 释放。</para>
    /// </summary>
    private static IntPtr AllocateDoubleNullTerminated(string path)
    {
        var chars = new char[path.Length + 2];
        path.CopyTo(0, chars, 0, path.Length);
        // chars[path.Length] 与 chars[path.Length + 1] 保持默认值 '\0' —— 这就是双 null 结尾。
        var buffer = Marshal.AllocHGlobal(chars.Length * sizeof(char));
        Marshal.Copy(chars, 0, buffer, chars.Length);
        return buffer;
    }

    /// <summary>
    /// 把 <c>SHFileOperationW</c> 的返回码翻译成能指导用户下一步操作的中文。
    ///
    /// <para>
    /// 该 API 返回的是 win32 错误码与 shell 自己的 <c>DE_*</c> 码混用的一套整数，
    /// <b>不是 HRESULT</b>，所以下面按十六进制逐一对照。
    /// </para>
    /// </summary>
    private static string DescribeError(int code, string path) => code switch
    {
        2 => "文件未找到：目标可能已被其他程序删除或移动",
        3 => "路径不存在：目标可能已被其他程序删除或移动",
        5 => "访问被拒绝：权限不足（可尝试以管理员身份运行），或目标受系统保护",
        0x20 => "文件被另一个程序占用，请关闭相关程序后重试",
        0x21 => "文件正被使用中（共享冲突），请关闭相关程序后重试",
        0x41 => "目录不为空，无法删除",
        0x71 => "源路径与目标路径相同，操作被拒绝",
        0x75 => "操作被取消：目标被占用或系统拒绝执行（DE_OPCANCELLED）",
        0x78 => "无法访问源文件：权限不足或文件被占用（DE_ACCESSDENIEDSRC，十六进制 0x78 / 十进制 120）",
        0x7B => "目标路径过深（DE_PATHTOODEEP）",
        0x7C => "文件名无效、路径不存在或过长（DE_INVALIDFILES，十六进制 0x7C / 十进制 124）",
        0x81 => "文件名过长（DE_FILENAMETOOLONG）",
        _ => $"操作失败：shell 返回错误码 0x{code:X}（十进制 {code}），目标「{path}」未被移动",
    };

    /// <summary>
    /// 拒绝"删了会出大事"的目标：驱动器根目录，以及系统 / 用户关键目录本身或其祖先。
    /// <para>
    /// 这是一道纯防呆闸门（调用方本应先用安全闸门筛过），但代价极小：
    /// 一次路径比较换掉"误传 <c>C:\</c> 就毁掉整台机器"的可能性。
    /// </para>
    /// </summary>
    private static string? FindDangerReason(string fullPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(fullPath) ?? string.Empty);
        if (root.Length > 0 && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return "目标是驱动器根目录，拒绝操作";
        }

        foreach (var anchor in ProtectedRoots)
        {
            // 命中锚点本身：例如 C:\Windows
            if (string.Equals(fullPath, anchor, StringComparison.OrdinalIgnoreCase))
            {
                return $"目标是系统/用户关键目录「{anchor}」，拒绝操作";
            }

            // 目标是锚点的祖先：例如 C:\ 或 C:\Users（会连带删掉 C:\Users\xxx）
            if (anchor.StartsWith(fullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return $"目标包含了系统/用户关键目录「{anchor}」，拒绝操作";
            }
        }

        return null;
    }

    /// <summary>
    /// 判断目标所在卷是否真的支持回收站。
    /// <para>
    /// <b>为什么必须查</b>：<c>FOF_ALLOWUNDO</c> 只是"允许撤销"的意思，
    /// 在不支持回收站的位置（网络共享、没开回收站的 U 盘）shell 会直接永久删除，
    /// 而且返回码依然是 0。这对"只移入回收站"的产品红线是不可接受的，
    /// 因此这里<b>只放行本地固定磁盘</b>，其余一律拒绝并说明原因。
    /// </para>
    /// </summary>
    /// <returns>不可回收时的中文原因；可回收时返回 null。</returns>
    private static string? CheckRecycleSupport(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return "无法确定目标所在的卷，为安全起见中止";
        }

        // UNC 路径（\\server\share\…）：网络位置不可能有本地回收站。
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return "目标是网络共享路径：网络位置没有回收站，删除不可还原，为安全起见中止";
        }

        try
        {
            var drive = new DriveInfo(root);
            return drive.DriveType switch
            {
                DriveType.Fixed => null,

                // 可移动磁盘只有"启用回收站"时才有回收站，无法可靠探测，一律拒绝。
                DriveType.Removable => "目标位于可移动磁盘：该磁盘未必启用回收站，删除可能不可还原，为安全起见中止",

                DriveType.Network => "目标位于网络驱动器：网络位置没有回收站，为安全起见中止",
                DriveType.CDRom => "目标是光盘等只读介质，无法删除",
                DriveType.Ram => "目标位于内存盘：内存盘没有回收站，为安全起见中止",
                _ => $"目标所在卷类型为 {drive.DriveType}，不保证有回收站，为安全起见中止",
            };
        }
        catch (Exception ex)
        {
            return $"无法读取目标所在卷信息（{ex.Message}），为安全起见中止";
        }
    }

    /// <summary>收集需要保护的系统 / 用户关键目录；取不到的（例如非 Windows 环境）自动跳过。</summary>
    private static string[] BuildProtectedRoots()
    {
        var list = new List<string>();
        Add(Environment.SpecialFolder.Windows);
        Add(Environment.SpecialFolder.System);
        Add(Environment.SpecialFolder.SystemX86);
        Add(Environment.SpecialFolder.ProgramFiles);
        Add(Environment.SpecialFolder.ProgramFilesX86);
        Add(Environment.SpecialFolder.CommonApplicationData);
        Add(Environment.SpecialFolder.UserProfile);
        return [.. list];

        void Add(Environment.SpecialFolder folder)
        {
            var value = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(value))
            {
                list.Add(Path.TrimEndingDirectorySeparator(value));
            }
        }
    }
}
