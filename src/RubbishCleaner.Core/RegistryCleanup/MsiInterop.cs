using System.Runtime.InteropServices;
using System.Text;

namespace RubbishCleaner.Core.RegistryCleanup;

/// <summary>
/// Windows Installer（msi.dll）的最小 P/Invoke 封装。
///
/// <b>为什么必须用 API 而不是查注册表</b>：判断"这个 MSI 产品还在不在"，
/// 唯一可靠的来源是 Windows Installer 自己的数据库。
/// 2026-10-25 真机调研发现：产品完好但标准 Products 键全都不存在是<b>真实存在的形态</b>
/// （实测 CC Switch），按注册表键判孤儿会误删。
/// </summary>
internal static class MsiInterop
{
    /// <summary>产品状态：未安装（MSI 文档里 1/2/3 同义，都表示 ABSENT）。</summary>
    private const uint InstallStateAbsent = 1;

    /// <summary>产品已安装。</summary>
    private const uint InstallStateInstalled = 5;

    /// <summary>仅公告安装（算已安装，不能当孤儿）。</summary>
    private const uint InstallStateAdvertised = 4;

    /// <summary>公告或已安装。</summary>
    private const uint InstallStateAdvertisedOrInstalled = 6;

    /// <summary>
    /// 查询单个产品的安装状态。
    ///
    /// 返回值语义（MSI 文档 <c>INSTALLSTATE</c>）：
    /// 0=UNKNOWN 1/2/3=ABSENT 4=ADVERTISED 5=INSTALLED 6=ADVERTISED_OR_INSTALLED。
    /// </summary>
    public static uint QueryProductState(string productCode)
    {
        try
        {
            return MsiQueryProductStateW(productCode);
        }
        catch (DllNotFoundException)
        {
            return uint.MaxValue;
        }
        catch (EntryPointNotFoundException)
        {
            return uint.MaxValue;
        }
    }

    /// <summary>
    /// 枚举所有已安装（或公告）的产品 GUID，<b>覆盖三个安装上下文</b>。
    ///
    /// 这是交叉验证：某些产品的键结构异常时，单查状态可能不准，
    /// 用枚举列表再确认一次"它到底在不在"。
    ///
    /// <b>为什么不能只用 <c>MsiEnumProducts</c></b>（v0.4.0 修复）：
    /// 它只枚举「当前用户 + 默认机器上下文」，既不区分 32/64 位视图，
    /// 也看不到「每用户托管/每用户非托管」的安装。
    /// 设计草案第六节把这一点列为已知风险：漏枚举 → 把在装的产品误判成孤儿 → 误删。
    ///
    /// 改用 <c>MsiEnumProductsExW</c> 依次枚举 MACHINE / USERMANAGED / USERUNMANAGED 三个上下文
    /// （<c>szUserSid = NULL</c> 表示当前用户，<c>szProductCode = NULL</c> 表示该上下文下全部产品），
    /// 结果并集。任一上下文失败只记录并继续下一个；三个全部失败才回退到旧的 <c>MsiEnumProductsW</c>，
    /// 保证行为只会"更全"，不会"更少"。
    ///
    /// 本机（Windows 11 22631，AMD64，非管理员）只读实测：
    /// <list type="bullet">
    /// <item>旧 <c>MsiEnumProducts</c> 344 个；</item>
    /// <item>MACHINE 338 个、USERMANAGED 0 个、USERUNMANAGED 6 个，并集 <b>344</b> 个；</item>
    /// <item>并集与旧枚举<b>完全相等</b>（旧有而并集没有的 0 个，并集有而旧没有的 0 个）——
    /// 本机恰好没有"藏在别的上下文里"的产品，但 USERUNMANAGED 的 6 个说明
    /// 旧 API 确实是通过别的路径覆盖到它们的，显式枚举更可靠。</item>
    /// </list>
    /// </summary>
    public static HashSet<string> EnumerateProducts()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var succeededContexts = 0;

        foreach (var context in EnumerationContexts)
        {
            var (contextSet, ok) = EnumerateProductsInContext(context);
            if (!ok)
            {
                continue;
            }

            succeededContexts++;
            set.UnionWith(contextSet);
        }

        // 三个上下文全部不可用（没有 msi.dll / API 不可用）：退回旧 API，保持原有降级语义
        if (succeededContexts == 0)
        {
            return EnumerateProductsLegacy();
        }

        return set;
    }

    /// <summary>
    /// 依次必须覆盖的安装上下文（MSI 的 <c>MSIINSTALLCONTEXT</c> 位掩码）。
    /// 顺序固定，失败即跳过下一个。
    /// </summary>
    private static readonly uint[] EnumerationContexts =
    [
        MsiInstallContextMachine,
        MsiInstallContextUserManaged,
        MsiInstallContextUserUnmanaged,
    ];

    /// <summary>
    /// 枚举单个上下文。返回 <c>(集合, 是否成功)</c>：
    /// 成功表示"这个上下文问到了答案"（哪怕是 0 个产品），
    /// 失败表示 API 报错（例如 msi.dll 不可用），调用方据此决定是否回退。
    /// </summary>
    internal static (HashSet<string> Products, bool Succeeded) EnumerateProductsInContext(uint context)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new StringBuilder(64);
        var sidBuffer = new StringBuilder(256);
        uint sidLength = 256;
        uint index = 0;

        try
        {
            while (true)
            {
                buffer.Clear();
                sidLength = 256;

                // szUserSid = null → 当前用户；szProductCode = null → 该上下文下全部产品。
                var rc = MsiEnumProductsExW(
                    null, null, context, index, buffer, out _, sidBuffer, ref sidLength);

                if (rc == 0)
                {
                    set.Add(buffer.ToString());
                    index++;

                    // 防御：真实机器上几百个产品，给个上限避免死循环
                    if (index > 100_000)
                    {
                        return (set, true);
                    }

                    continue;
                }

                // ERROR_NO_MORE_ITEMS(259)：正常"枚举到头了"。这是本机实测的常态——
                // 三个上下文都是取完最后一个产品后才返回 259。
                if (rc == ErrorNoMoreItems)
                {
                    return (set, true);
                }

                // 其他错误码：已经拿到结果就保留（部分成功），一条都没拿到就报"该上下文失败"，
                // 让上层决定是否回退到旧 API——绝不能用"空集合"冒充"没有已安装产品"，
                // 那会让闸门 R5 失效（把在装的产品当成孤儿）。
                return (set, index > 0);
            }
        }
        catch (DllNotFoundException)
        {
            return (set, false);
        }
        catch (EntryPointNotFoundException)
        {
            return (set, false);
        }
    }

    /// <summary>
    /// 旧实现：<c>MsiEnumProducts</c>，只覆盖当前用户 / 默认机器上下文。
    /// 仅在三个显式上下文全部不可用时作为兜底使用。
    /// </summary>
    internal static HashSet<string> EnumerateProductsLegacy()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new StringBuilder(64);
        uint index = 0;

        try
        {
            while (MsiEnumProductsW(index, buffer) == 0)
            {
                set.Add(buffer.ToString());
                index++;
                buffer.Clear();

                if (index > 100_000)
                {
                    break;
                }
            }
        }
        catch (DllNotFoundException)
        {
            // 没有 msi.dll 的环境（极罕见）：返回空集合，调用方会降级
        }
        catch (EntryPointNotFoundException)
        {
        }

        return set;
    }

    /// <summary>读取产品的一个属性（例如 <c>LocalPackage</c>）；失败返回 null。</summary>
    public static string? GetProductInfo(string productCode, string property)
    {
        var buffer = new StringBuilder(1024);
        uint length = 1024;

        try
        {
            var rc = MsiGetProductInfoW(productCode, property, buffer, ref length);
            return rc == 0 ? buffer.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// <c>MsiQueryProductState</c> 的"未知产品"返回值（ERROR_UNKNOWN_PRODUCT）。
    ///
    /// <b>这是最常见的孤儿形态</b>：注册表里有一条 Uninstall 条目，
    /// 但 Windows Installer 数据库里<b>从来没有过</b>这个产品。
    /// 实测：随便造一个 GUID 查询即返回 4294967295。
    /// </summary>
    public const uint ErrorUnknownProduct = 4294967295u;

    /// <summary>参数无效（ERROR_INVALID_PARAMETER）。</summary>
    public const uint ErrorInvalidParameter = 4294967294u;

    /// <summary>
    /// 枚举到头（ERROR_NO_MORE_ITEMS = 259）。
    /// <c>MsiEnumProducts*</c> 系列用这个值表示"没有更多产品了"，不是错误。
    /// </summary>
    private const uint ErrorNoMoreItems = 259;

    /// <summary>
    /// <c>MsiEnumProductsEx</c> 的上下文掩码：<b>每机器</b>安装（MSIINSTALLCONTEXT_MACHINE = 4）。
    /// 注意这几个值不是 1/2/4 的自然顺序：USERMANAGED=1、USERUNMANAGED=2、MACHINE=4。
    /// 本机实测写错值会直接得到 ERROR_INVALID_PARAMETER(87)。
    /// </summary>
    private const uint MsiInstallContextMachine = 4;

    /// <summary>每用户「托管」安装（MSIINSTALLCONTEXT_USERMANAGED = 1）。</summary>
    private const uint MsiInstallContextUserManaged = 1;

    /// <summary>每用户「非托管」安装（MSIINSTALLCONTEXT_USERUNMANAGED = 2）。</summary>
    private const uint MsiInstallContextUserUnmanaged = 2;

    /// <summary>
    /// 状态值是否表示"产品已不存在"。
    ///
    /// 三类都算：
    /// <list type="bullet">
    /// <item><b>1/2/3 (ABSENT)</b>：产品曾经装过，现在已卸载；</item>
    /// <item><b>ERROR_UNKNOWN_PRODUCT</b>：MSI 数据库里从来没有这个产品
    /// ——实测这是最常见的孤儿形态；</item>
    /// <item><b>ERROR_INVALID_PARAMETER</b>：GUID 格式非法（键名已通过正则校验，属异常数据）。</item>
    /// </list>
    /// </summary>
    public static bool IsAbsent(uint state)
        => state is 1 or 2 or 3 or ErrorUnknownProduct or ErrorInvalidParameter;

    /// <summary>状态值是否表示"产品存在"（已安装 / 公告 / 公告或已安装）。</summary>
    public static bool IsPresent(uint state)
        => state is InstallStateAdvertised or InstallStateInstalled or InstallStateAdvertisedOrInstalled;

    /// <summary>把状态值翻译成中文，用于证据链。</summary>
    public static string DescribeState(uint state) => state switch
    {
        0 => "UNKNOWN（未知）",
        1 or 2 or 3 => "ABSENT（未安装）",
        4 => "ADVERTISED（仅公告安装）",
        5 => "INSTALLED（已安装）",
        6 => "ADVERTISED_OR_INSTALLED（公告或已安装）",
        ErrorUnknownProduct => "未知产品（Windows Installer 数据库里从未有过此产品）",
        ErrorInvalidParameter => "参数无效（GUID 格式非法）",
        _ => $"未知状态 {state}",
    };

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiQueryProductStateW(string szProduct);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnumProductsW(uint iProductIndex, StringBuilder lpProductBuf);

    /// <summary>
    /// 按安装上下文枚举产品（<c>MsiEnumProductsExW</c>）。
    /// 能同时覆盖 32/64 位视图与多用户上下文，是 <c>MsiEnumProducts</c> 的超集。
    /// </summary>
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnumProductsExW(
        string? szProductCode,
        string? szUserSid,
        uint dwContext,
        uint dwIndex,
        StringBuilder szInstalledProductCode,
        out uint pdwInstalledContext,
        StringBuilder szSid,
        ref uint pcchSid);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetProductInfoW(
        string szProduct, string szAttribute, StringBuilder lpValueBuf, ref uint pdwValueBuf);
}
