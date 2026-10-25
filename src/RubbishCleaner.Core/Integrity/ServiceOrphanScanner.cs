using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Integrity;

/// <summary>
/// 服务 ImagePath 孤儿扫描器（v0.5）——<b>只读报告，永不自动清理</b>。
///
/// <para>判据（任务里逐条踩过坑后定下的）：服务的 <c>ImagePath</c> 展开环境变量、
/// 剥掉命令行参数后得到一个<b>绝对 Win32 路径</b>，且该路径<b>不在系统目录</b>下，且文件不存在。</para>
///
/// <para>刻意跳过的形态（计入 <see cref="ServiceOrphanScanResult.SkippedCount"/>，每类都有真机教训）：</para>
/// <list type="bullet">
/// <item><b>ImagePath 为空/缺失</b>——内核驱动常这样，没有可判定的文件；</item>
/// <item><b>svchost 宿主</b>——真实 DLL 在 <c>ServiceDll</c> 参数里，本版不深挖（深挖会把
/// 共享宿主里的正常服务误判）；</item>
/// <item><b>内核形态路径</b>（<c>\SystemRoot\</c>、<c>\??\</c>、<c>\Device\</c>、
/// <c>system32\drivers\</c> 等开头）——这是 NT 对象管理器的路径语义，不是 Win32 的，
/// 拿去做 <c>File.Exists</c> 必然失真；</item>
/// <item><b>落在系统目录</b>（Windows 目录及其下 System32/SysWOW64）——v0.5-孤儿CLSID调研.md 的教训：
/// 系统组件的文件可能由 Windows Update 回装，且权限陷阱会让存在性检查失真；</item>
/// <item><b>相对路径</b>——解析不出绝对 Win32 路径，保守不报。</item>
/// </list>
///
/// <para>为什么运行状态不用 <c>System.ServiceProcess.ServiceController</c>：该类型在 .NET 8
/// 位于 Windows 桌面扩展程序集 <c>System.ServiceProcess.ServiceController</c>，
/// 本工程的 SDK 引用集里没有（编译报 CS1069，2026-10-25 实测），而加引用要动 csproj——
/// 改用 advapi32 的三个<b>只读</b> P/Invoke（见 <see cref="ServiceStateNative"/>）零依赖替代。</para>
///
/// <para>「永不自动清理」在类型上锁死：<see cref="ServiceOrphanEntry.CanAutoClean"/> 恒为 false
/// （契约层写死），本类不提供任何删除/修复方法，连预留接口都没有。</para>
/// </summary>
public sealed class ServiceOrphanScanner : IServiceOrphanScanner
{
    /// <summary>生产默认的服务表位置。</summary>
    public const string DefaultServicesRootPath = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>跳过原因的短语口径（进度汇报里按此归并计数）。</summary>
    private const string SkipMissingImagePath = "ImagePath 缺失";
    private const string SkipKernelPath = "内核形态路径";
    private const string SkipSvchost = "svchost 宿主";
    private const string SkipSystemDirectory = "系统目录";
    private const string SkipRelativePath = "相对路径";
    private const string SkipUnresolvable = "无法解析";

    /// <summary>内核形态路径前缀（与大小写无关）。注意裸 <c>system32\</c> 前缀同时覆盖了 drivers 形态。</summary>
    private static readonly string[] KernelPrefixes =
    [
        @"\SystemRoot\",
        @"\??\",
        @"\Device\",
        @"system32\",
        @"syswow64\",
    ];

    private readonly IServiceRecordSource _source;
    private readonly string _rootPath;
    private readonly ServiceStateQuery? _stateQuery;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string> _expand;
    private readonly string _windowsDirectory;

    /// <summary>
    /// 生产构造：读真实注册表、查真实 SCM、查真实文件系统。
    /// <paramref name="servicesRootPath"/> 仅供测试换根键路径（生产默认 CurrentControlSet）。
    /// </summary>
    public ServiceOrphanScanner(string? servicesRootPath = null)
        : this(
            new RegistryServiceRecordSource(),
            servicesRootPath ?? DefaultServicesRootPath,
            ServiceStateNative.QueryState,
            fileExists: null,
            environmentExpander: null,
            windowsDirectory: null)
    {
    }

    /// <summary>
    /// 测试构造：全链路注入假实现，绝不碰真实服务表。
    /// 真实注册表在单测里不可写（HKLM 要管理员），所以服务数据走 <see cref="IServiceRecordSource"/> 缝隙。
    /// </summary>
    internal ServiceOrphanScanner(
        IServiceRecordSource source,
        string? servicesRootPath,
        ServiceStateQuery? stateQuery,
        Func<string, bool>? fileExists,
        Func<string, string>? environmentExpander,
        string? windowsDirectory)
    {
        _source = source;
        _rootPath = servicesRootPath ?? DefaultServicesRootPath;
        _stateQuery = stateQuery;
        _fileExists = fileExists ?? DefaultFileExists;
        _expand = environmentExpander ?? DefaultExpand;
        _windowsDirectory = windowsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    }

    /// <summary>真实文件存在性检查（默认实现，测试可注入替换）。</summary>
    private static bool DefaultFileExists(string path) => File.Exists(path);

    /// <summary>环境变量展开（默认实现，测试可注入替换）。</summary>
    private static string DefaultExpand(string raw) => Environment.ExpandEnvironmentVariables(raw);

    /// <inheritdoc/>
    public Task<ServiceOrphanScanResult> ScanAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(progress, cancellationToken), cancellationToken);

    private ServiceOrphanScanResult Scan(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var degraded = new List<string>();

        progress?.Report($"正在读取服务表 HKLM\\{_rootPath}…");

        var (records, readError) = _source.Read(_rootPath);
        if (readError is not null)
        {
            degraded.Add(readError);
        }

        if (records.Count == 0)
        {
            // 服务表一条都读不到：结果整体不可信，按降级口径交代而不是返回"0 孤儿"装作扫过。
            // 真实 Windows 的服务表不可能为空——空列表本身就是枚举静默失败的一种形态。
            if (degraded.Count == 0)
            {
                degraded.Add($"HKLM\\{_rootPath} 下没有读到任何服务子键（正常系统不可能为空，视为枚举失败）");
            }

            progress?.Report("服务表读取失败，本次没有扫描");
            return new ServiceOrphanScanResult
            {
                Entries = [],
                ScannedCount = 0,
                SkippedCount = 0,
                DegradedReason = string.Join("；", degraded),
                Duration = watch.Elapsed,
            };
        }

        var entries = new List<ServiceOrphanEntry>();
        var scanned = 0;
        var skipped = 0;
        var skipTally = new Dictionary<string, int>();
        var stateFailures = 0;
        string? firstStateError = null;

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;
            if (scanned % 50 == 0)
            {
                progress?.Report($"已检查 {scanned}/{records.Count} 个服务…");
            }

            var verdict = Evaluate(record);
            if (!verdict.IsOrphan)
            {
                // SkipReason 为空 = 文件在，服务健康；非空 = 命中跳过规则。
                if (verdict.SkipReason is not null)
                {
                    skipped++;
                    skipTally[verdict.SkipReason] = skipTally.GetValueOrDefault(verdict.SkipReason) + 1;
                }

                continue;
            }

            // 运行状态只对孤儿候选查（健康服务没必要逐个问 SCM）。
            // 查询失败不挡判定：状态栏显示"未知"，同时计入降级说明。
            // （测试注入 null 委托 = 不查 SCM，显示"未知"但不降级；真正的查询失败才降级。）
            var stateText = "未知";
            if (_stateQuery is { } query)
            {
                try
                {
                    var state = query(record.Name);
                    if (state is null)
                    {
                        stateFailures++;
                    }
                    else
                    {
                        stateText = state;
                    }
                }
                catch (Exception ex)
                {
                    stateFailures++;
                    firstStateError ??= ex.Message;
                }
            }

            entries.Add(new ServiceOrphanEntry
            {
                ServiceName = record.Name,
                DisplayName = record.DisplayName is string dn ? RegistryTextSanitizer.Clean(dn) : null,
                ImagePathRaw = record.ImagePathRaw ?? string.Empty,
                ResolvedPath = verdict.ResolvedPath!,
                StartModeText = DescribeStart(record.Start),
                StateText = stateText,
                Evidence =
                [
                    $"注册表 ImagePath 原值：{record.ImagePathRaw}",
                    $"展开并剥离参数后的可执行文件：{verdict.ResolvedPath}",
                    "该文件在磁盘上已不存在（文件存在性检查）",
                    $"启动类型：{DescribeStart(record.Start)}；服务类型：{DescribeType(record.Type)}",
                    $"运行状态：{stateText}",
                    "本工具不会自动清理服务条目；如确认是残留，可手动执行：" + ManualCommandFor(record.Name),
                ],
            });
        }

        if (stateFailures > 0)
        {
            degraded.Add($"有 {stateFailures} 个服务的运行状态查询失败"
                         + (firstStateError is null ? string.Empty : $"（{firstStateError}）")
                         + "，这些条目的运行状态一栏不可信");
        }

        var skipSummary = string.Join("；", skipTally.Select(kv => $"{kv.Value} 个{kv.Key}"));
        progress?.Report(
            $"扫描完成：检查 {scanned} 个服务，跳过 {skipped} 个（{skipSummary}），孤儿 {entries.Count} 个");

        return new ServiceOrphanScanResult
        {
            Entries = entries,
            ScannedCount = scanned,
            SkippedCount = skipped,
            DegradedReason = degraded.Count > 0 ? string.Join("；", degraded) : null,
            Duration = watch.Elapsed,
        };
    }

    /// <summary>
    /// 判定单条服务。三种出口：孤儿 / 跳过（<see cref="Verdict.SkipReason"/> 说明原因）/ 健康（文件在）。
    /// 内部方法 + 纯函数（除了文件存在性查询），单测逐分支覆盖。
    /// </summary>
    internal Verdict Evaluate(ServiceRecord record)
    {
        var raw = record.ImagePathRaw;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Verdict.Skip(SkipMissingImagePath);
        }

        string expanded;
        try
        {
            expanded = _expand(raw.Trim());
        }
        catch (Exception ex)
        {
            // 展开失败说明值里有畸形的环境变量引用，解析结果不可信，保守跳过。
            return Verdict.Skip($"{SkipUnresolvable}（环境变量展开失败：{ex.Message}）");
        }

        foreach (var prefix in KernelPrefixes)
        {
            if (expanded.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return Verdict.Skip(SkipKernelPath);
            }
        }

        var head = ExtractExecutableHead(expanded);
        if (string.IsNullOrWhiteSpace(head))
        {
            return Verdict.Skip(SkipUnresolvable);
        }

        // svchost 必须在系统目录判断之前：宿主本身就住在 System32 里，
        // 先判系统目录会把"svchost 宿主"误归成"系统目录"，口径就乱了。
        if (Path.GetFileName(head).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
        {
            return Verdict.Skip(SkipSvchost);
        }

        if (!Path.IsPathRooted(head))
        {
            return Verdict.Skip(SkipRelativePath);
        }

        string full;
        try
        {
            full = Path.GetFullPath(head);
        }
        catch (Exception)
        {
            return Verdict.Skip(SkipUnresolvable);
        }

        var windowsRoot = _windowsDirectory.TrimEnd('\\', '/');
        if (windowsRoot.Length > 0
            && full.StartsWith(windowsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            // Windows 目录整体跳过（System32/SysWOW64 都在其下）：
            // 系统组件的文件可能由 Windows Update 回装，存在性检查在这里没有判定力。
            return Verdict.Skip(SkipSystemDirectory);
        }

        return _fileExists(full) ? Verdict.Healthy() : Verdict.Orphan(full);
    }

    /// <summary>
    /// 从展开后的命令行里切出可执行文件路径。
    /// 引号包裹直接取引号内；无引号取第一个 <c>.exe</c> 之前的部分——
    /// 这样 <c>C:\Program Files\A B\svc.exe -x</c> 这种含空格的未加引号形态也能切对
    /// （服务表的 ImagePath 不会出现"参数在前、主程序在后"的形态）。
    /// </summary>
    internal static string? ExtractExecutableHead(string expandedCommand)
    {
        var s = expandedCommand.Trim();
        if (s.Length == 0)
        {
            return null;
        }

        if (s[0] == '"')
        {
            var closing = s.IndexOf('"', 1);
            var head = closing > 0 ? s[1..closing] : s[1..];
            return head.Trim().Length > 0 ? head.Trim() : null;
        }

        var exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe >= 0)
        {
            return s[..(exe + 4)];
        }

        var space = s.IndexOfAny([' ', '\t']);
        return space > 0 ? s[..space] : s;
    }

    /// <summary>Start 值翻译（SERVICE_BOOT_START 等文档化常量）。</summary>
    internal static string DescribeStart(int? start) => start switch
    {
        0 => "引导加载(0)",
        1 => "系统启动(1)",
        2 => "自动(2)",
        3 => "手动(3)",
        4 => "禁用(4)",
        null => "未设置",
        _ => $"未知({start})",
    };

    /// <summary>
    /// Type 值翻译。位判据用 SERVICE_WIN32_OWN_PROCESS(0x10)/SERVICE_WIN32_SHARE_PROCESS(0x20)，
    /// 其上的 0x100 是 INTERACTIVE_PROCESS（可与桌面交互），单独标出来便于用户识别风险。
    /// </summary>
    internal static string DescribeType(int? type) => type switch
    {
        null => "未设置",
        1 => "内核驱动(1)",
        2 => "文件系统驱动(2)",
        _ when (type.Value & 0x30) != 0 => (type.Value & 0x100) != 0 ? "Win32 服务(可交互)" : "Win32 服务",
        _ => $"其他({type})",
    };

    private static string ManualCommandFor(string serviceName) => $"sc delete {serviceName}";

    /// <summary>单条服务的判定结果。</summary>
    internal sealed record Verdict(bool IsOrphan, string? SkipReason, string? ResolvedPath)
    {
        /// <summary>命中跳过规则。</summary>
        public static Verdict Skip(string reason) => new(false, reason, null);

        /// <summary>健康（可执行文件存在），既不是孤儿也无需交代原因。</summary>
        public static Verdict Healthy() => new(false, null, null);

        /// <summary>孤儿：路径可解析、非系统目录、且文件不存在。</summary>
        public static Verdict Orphan(string resolvedPath) => new(true, null, resolvedPath);
    }
}

/// <summary>服务运行状态查询委托：返回"运行中/已停止"等文本；null 表示查询失败。</summary>
public delegate string? ServiceStateQuery(string serviceName);

/// <summary>服务表读取的内部缝隙：生产实现读注册表，测试注入假数据（HKLM 在单测里不可写）。</summary>
internal interface IServiceRecordSource
{
    /// <summary>读取服务记录；error 非空表示读取不完整。</summary>
    (IReadOnlyList<ServiceRecord> Records, string? Error) Read(string rootPath);
}

/// <summary>一条服务记录（注册表子键的五个值）。</summary>
internal sealed record ServiceRecord(
    string Name,
    string? DisplayName,
    string? ImagePathRaw,
    int? Start,
    int? Type);

/// <summary>
/// 生产数据源：枚举 HKLM\{rootPath} 下的服务子键。
/// 只读打开（writable: false），单个键的 ACL 拒绝只跳过该键，不拖垮整次扫描。
/// </summary>
internal sealed class RegistryServiceRecordSource : IServiceRecordSource
{
    public (IReadOnlyList<ServiceRecord>, string?) Read(string rootPath)
    {
        var records = new List<ServiceRecord>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var services = baseKey.OpenSubKey(rootPath, writable: false);
            if (services is null)
            {
                return (records, $"注册表路径不存在：HKLM\\{rootPath}");
            }

            foreach (var name in services.GetSubKeyNames())
            {
                try
                {
                    using var sub = services.OpenSubKey(name, writable: false);
                    if (sub is null)
                    {
                        continue;
                    }

                    records.Add(new ServiceRecord(
                        name,
                        sub.GetValue("DisplayName") as string,
                        sub.GetValue("ImagePath") as string,
                        sub.GetValue("Start") as int?,
                        sub.GetValue("Type") as int?));
                }
                catch (Exception)
                {
                    // 单个服务键读不了（ACL/损坏）就跳过，不影响其他条目。
                }
            }

            return (records, null);
        }
        catch (Exception ex)
        {
            return (records, $"读取 HKLM\\{rootPath} 失败：{ex.Message}");
        }
    }
}

/// <summary>
/// 通过 advapi32 查询服务运行状态（<b>只读</b>：只用 SC_MANAGER_CONNECT + SERVICE_QUERY_STATUS 权限，
/// 没有任何启动/停止/删除调用）。
///
/// <para>为什么不用 <c>System.ServiceProcess.ServiceController</c>：该类型在 .NET 8 被移出共享框架、
/// 放进 NuGet 包 <c>System.ServiceProcess.ServiceController</c>，本工程的引用集解析不到
/// （CS1069，2026-10-25 实测），而加包要动 csproj。改用 3 个 P/Invoke 零依赖替代。</para>
///
/// <para>失败面：OpenSCManager / OpenService / QueryServiceStatus 失败时返回 0/false 而不是抛异常
/// （受限环境、服务名不存在、句柄失效都走这条路），统一折叠成 null 交给扫描器记降级。</para>
/// </summary>
internal static class ServiceStateNative
{
    private const uint ScManagerConnect = 0x1;
    private const uint ServiceQueryStatus = 0x4;
    private const int ServiceStopped = 0x1;
    private const int ServiceRunning = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, ref SERVICE_STATUS serviceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    /// <summary>查询服务运行状态；任何失败都返回 null。</summary>
    internal static string? QueryState(string serviceName)
    {
        var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var service = OpenServiceW(manager, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                SERVICE_STATUS status = default;
                if (!QueryServiceStatus(service, ref status))
                {
                    return null;
                }

                return status.CurrentState switch
                {
                    ServiceRunning => "运行中",
                    ServiceStopped => "已停止",
                    var other => $"其他状态(0x{other:X})",
                };
            }
            finally
            {
                _ = CloseServiceHandle(service);
            }
        }
        finally
        {
            _ = CloseServiceHandle(manager);
        }
    }
}
