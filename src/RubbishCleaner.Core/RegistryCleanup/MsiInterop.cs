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
    /// 枚举所有已安装（或公告）的产品 GUID。
    ///
    /// 这是交叉验证：某些产品的键结构异常时，单查状态可能不准，
    /// 用枚举列表再确认一次"它到底在不在"。
    /// </summary>
    public static HashSet<string> EnumerateProducts()
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

                // 防御：真实机器上几百个产品，给个上限避免死循环
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

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetProductInfoW(
        string szProduct, string szAttribute, StringBuilder lpValueBuf, ref uint pdwValueBuf);
}
