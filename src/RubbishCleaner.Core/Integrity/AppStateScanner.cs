using System.Diagnostics;
using System.Text.RegularExpressions;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.RegistryCleanup;
using RubbishCleaner.Core.Uninstall;

namespace RubbishCleaner.Core.Integrity;

/// <summary>
/// 文件与注册表状态一致性扫描器（v0.5，<b>只读报告，永不自动清理</b>）。
///
/// <para>来源是 v0.4 的真机案例（CC Switch）：文件已被清掉，但 Windows Installer
/// 仍认为产品已安装（MsiQueryProductState = INSTALLED），于是「应用和功能」里
/// 留着一条点卸载会失败的条目。v0.4 的孤儿清理对这种"半死不活"状态没有交代，
/// 本扫描器把它显式报告出来。</para>
///
/// <para><b>为什么没有任何修复动作</b>：与 v0.3「代码里不存在永久删除分支」同一手法——
/// 本类连清理的预留接口都没有，危险的事在类型上不可表达。产出的
/// <see cref="AppStateIssue.CanAutoFix"/> 在契约里写死为 false。</para>
///
/// <para>两类判定（见 <see cref="AppStateIssueKind"/>）：</para>
/// <list type="bullet">
/// <item><see cref="AppStateIssueKind.InstallLocationMissing"/>：条目记录的安装目录已不存在。
/// v0.4 的教训：InstallLocation 常为空或不准——为空的一律跳过不计问题，宁可漏报不误报；</item>
/// <item><see cref="AppStateIssueKind.CachedPackageMissing"/>：Windows Installer 缓存的
/// 安装包副本（LocalPackage）已不存在。<b>这条常见且通常无害</b>——很多清理工具会把
/// C:\Windows\Installer 当垃圾清掉，报告里要明确说"通常无害"，避免吓到用户。</item>
/// </list>
///
/// <para><b>与 v0.4 的边界</b>：MSI 状态为 ABSENT 的条目走 v0.4 的孤儿清理
/// （<c>OrphanUninstallScanner</c>），不属于这里——本扫描器只在 MSI 确认
/// "已安装"（IsPresent）时才报告。</para>
/// </summary>
public sealed class AppStateScanner : IAppStateScanner
{
    /// <summary>产品 GUID 的严格形态：{8-4-4-4-12}（与 v0.4 的 OrphanUninstallScanner 同一规则）。</summary>
    private static readonly Regex GuidPattern = new(
        @"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Windows Installer 的缓存包属性名（MsiGetProductInfo 的第二个参数）。</summary>
    private const string LocalPackageProperty = "LocalPackage";

    private readonly Func<InstalledAppsResult> _appsProvider;
    private readonly Func<string, uint> _queryProductState;
    private readonly Func<string, string?> _getLocalPackage;

    /// <summary>
    /// 生产构造函数。
    ///
    /// <para><b>为什么注入 <c>Func&lt;InstalledAppsResult&gt;</c> 而不是条目列表</b>：
    /// 枚举 Uninstall 键要读几百个注册表子键，不便宜。注入 thunk 后枚举被推迟到
    /// <see cref="ScanAsync"/> 内部的 Task.Run 里执行（不占 UI 线程、构造零开销），
    /// 也让"每次扫描重新取一份列表"成为默认——应用列表在扫描间隙可能变化。
    /// 测试则直接注入返回假数据的 thunk，不必碰注册表。</para>
    /// </summary>
    public AppStateScanner(Func<InstalledAppsResult> appsProvider)
        : this(
            appsProvider,
            code => MsiInterop.QueryProductState(code),
            code => MsiInterop.GetProductInfo(code, LocalPackageProperty))
    {
    }

    /// <summary>
    /// 测试构造函数：把 msi.dll 的两个只读查询换成可注入的假实现。
    ///
    /// <para><b>为什么需要这道缝</b>：MsiInterop 是静态 P/Invoke 封装，单测没法让
    /// 它对一个假 GUID 返回 INSTALLED。两个委托都是纯查询，不产生任何副作用，
    /// 生产路径走默认的 MsiInterop。</para>
    /// </summary>
    internal AppStateScanner(
        Func<InstalledAppsResult> appsProvider,
        Func<string, uint>? queryProductState,
        Func<string, string?>? getLocalPackage)
    {
        _appsProvider = appsProvider;
        _queryProductState = queryProductState ?? (code => MsiInterop.QueryProductState(code));
        _getLocalPackage = getLocalPackage ?? (code => MsiInterop.GetProductInfo(code, LocalPackageProperty));
    }

    public Task<AppStateScanResult> ScanAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(progress, cancellationToken), cancellationToken);

    private AppStateScanResult Scan(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var degraded = new List<string>();

        InstalledAppsResult apps;
        try
        {
            progress?.Report("正在读取已安装软件列表…");
            apps = _appsProvider();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 整个数据源拿不到：结果没有意义，降级返回空报告而不是抛出
            return new AppStateScanResult
            {
                Entries = [],
                CheckedCount = 0,
                DegradedReason = $"已安装软件列表获取失败，本次没有扫描：{ex.Message}",
                Duration = watch.Elapsed,
            };
        }

        if (apps.HasError)
        {
            degraded.Add($"软件列表枚举时有读取失败（结果可能不完整）：{apps.Error}");
        }

        var issues = new List<AppStateIssue>();
        var checkedCount = 0;

        foreach (var app in apps.Apps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkedCount++;
            progress?.Report($"正在检查 {app.DisplayName}…");

            try
            {
                issues.AddRange(Evaluate(app));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单个条目判不出：只降级，不中断整轮扫描
                degraded.Add($"「{app.DisplayName}」（{app.Id}）检查失败：{ex.Message}");
            }
        }

        watch.Stop();
        progress?.Report("检查完成");

        return new AppStateScanResult
        {
            Entries = issues,
            CheckedCount = checkedCount,
            DegradedReason = degraded.Count > 0 ? string.Join("；", degraded) : null,
            Duration = watch.Elapsed,
        };
    }

    /// <summary>
    /// 判定单个条目，返回它贡献的所有问题（0 到 2 条）。
    ///
    /// <para>顺序上先做 MSI 状态闸门：本扫描器的两类判定都以"Windows Installer 说产品已装"
    /// 为前提。状态 ABSENT 的条目是 v0.4 孤儿清理的地盘；状态未知（msi.dll 不可用等）
    /// 的一律跳过——不确定就不报。</para>
    /// </summary>
    private IEnumerable<AppStateIssue> Evaluate(InstalledApp app)
    {
        string? productCode = null;

        if (app.IsMsi)
        {
            productCode = TryGetProductCode(app);
            if (productCode is null)
            {
                // IsMsi 但条目名不是严格 GUID：无法可靠地查询 MSI 数据库，
                // 两类判定都失去前提，保守跳过
                yield break;
            }

            var state = _queryProductState(productCode);
            if (!MsiInterop.IsPresent(state))
            {
                // ABSENT（或 UNKNOWN / API 不可用）都不报：
                // ABSENT 走 v0.4 的孤儿清理；其余属于"不确定"，不报
                yield break;
            }
        }

        var locationIssue = CheckInstallLocation(app, productCode is not null);
        if (locationIssue is not null)
        {
            yield return locationIssue;
        }

        if (productCode is not null)
        {
            var cachedIssue = CheckCachedPackage(app, productCode);
            if (cachedIssue is not null)
            {
                yield return cachedIssue;
            }
        }
    }

    /// <summary>
    /// InstallLocationMissing 判定：条目记录的安装目录在磁盘上已不存在。
    ///
    /// <para>v0.4 的教训：InstallLocation 常为空或不准，所以——</para>
    /// <list type="bullet">
    /// <item>为空 / 空白：直接跳过，不计问题；</item>
    /// <item>相对路径：没有判定意义（卸载器常按工作目录写相对值），跳过；</item>
    /// <item>指向文件而非目录的、以及"不确定"的情况：跳过，宁可漏报不误报；</item>
    /// <item>只有"目录确实不存在"才报告。</item>
    /// </list>
    /// </summary>
    private static AppStateIssue? CheckInstallLocation(InstalledApp app, bool msiConfirmed)
    {
        var raw = app.InstallLocation;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // 注册表里的值可能带引号、带环境变量、结尾带分隔符，先规范化再判定
        var path = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
        path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (path.Length == 0 || !Path.IsPathRooted(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        if (File.Exists(path) || Directory.Exists(path))
        {
            return null;
        }

        var evidence = new List<string>
        {
            $"条目记录的安装位置：{path}",
            "该目录在磁盘上已不存在",
            $"注册表条目：{app.RegistryPath}",
        };
        if (msiConfirmed)
        {
            evidence.Add("Windows Installer 确认该产品仍处于已安装状态");
        }

        return new AppStateIssue
        {
            Kind = AppStateIssueKind.InstallLocationMissing,
            DisplayName = app.DisplayName,
            ProductCode = null,
            Evidence = evidence,
            Suggestions =
            [
                "如果该软件仍在使用：重新安装一次即可让注册表与磁盘重新一致。",
                "如果不再使用：先在「应用和功能」里正常卸载；若卸载报错，说明条目已失效，可按上面给出的注册表位置手动清理。",
            ],
        };
    }

    /// <summary>
    /// CachedPackageMissing 判定：Windows Installer 缓存的安装包副本已不存在。
    ///
    /// <para><b>这条通常无害</b>：LocalPackage 指向的 .msi（默认在 C:\Windows\Installer 下）
    /// 只是 Installer 用来做修复/卸载的副本，很多清理工具会把整个缓存目录当垃圾清掉。
    /// 已安装的软件照常运行，所以报告必须明确说"通常无害，可忽略"。</para>
    ///
    /// <para>拿不到 LocalPackage（属性查询失败）时不报：无法判定就不报。</para>
    /// </summary>
    private AppStateIssue? CheckCachedPackage(InstalledApp app, string productCode)
    {
        var localPackage = _getLocalPackage(productCode);
        if (string.IsNullOrWhiteSpace(localPackage))
        {
            return null;
        }

        if (File.Exists(localPackage))
        {
            return null;
        }

        return new AppStateIssue
        {
            Kind = AppStateIssueKind.CachedPackageMissing,
            DisplayName = app.DisplayName,
            ProductCode = productCode,
            Evidence =
            [
                $"Windows Installer 缓存的安装包（{LocalPackageProperty}）：{localPackage}",
                "该缓存文件在磁盘上已不存在",
                $"产品码：{productCode}",
            ],
            Suggestions =
            [
                "通常无害，可以忽略。",
                "缺失的只是 Windows Installer 的安装包缓存副本（多在 C:\\Windows\\Installer 下），不影响软件运行；仅当修复或卸载该软件失败时，才需要重新获取安装包。",
            ],
        };
    }

    /// <summary>
    /// 从条目里取 MSI 产品码。
    ///
    /// <para><see cref="InstalledApp"/> 本身不携带产品码：枚举器把条目标识放在
    /// RegistryPath 的最后一段（Uninstall 子键名）。MSI 条目的子键名按约定就是
    /// 产品码 GUID，但存在非 GUID 的异常条目（v0.4 调研记录过），所以必须用
    /// 严格形态校验，不是 GUID 的宁可跳过也不猜。</para>
    /// </summary>
    private static string? TryGetProductCode(InstalledApp app)
    {
        var lastSeparator = app.RegistryPath.LastIndexOf('\\');
        var subKeyName = lastSeparator >= 0 ? app.RegistryPath[(lastSeparator + 1)..] : app.RegistryPath;
        return GuidPattern.IsMatch(subKeyName) ? subKeyName : null;
    }
}
