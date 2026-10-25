using Microsoft.Win32;
using RubbishCleaner.Core.RegistryCleanup;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// 孤儿 Uninstall 条目扫描的判据测试（v0.4）。
///
/// <b>核心断言是"不该报的绝不报"</b>：注册表删错影响面远大于文件删错。
/// 判据基于 2026-10-25 真机调研（CC Switch 案例：产品有效但标准 Products 键不存在）。
/// </summary>
public class OrphanUninstallScannerTests
{
    [Fact]
    public async Task ScanAsync_RealMachine_DoesNotReportValidProducts()
    {
        // 本机 343 个 MSI 条目全部是 INSTALLED（含文件已被清掉的 CC Switch），
        // 扫描器一条都不该报——这是"文件没了但产品有效"这个真实形态的回归防护。
        var scanner = new OrphanUninstallScanner();

        var result = await scanner.ScanAsync();

        Assert.NotNull(result);
        Assert.True(result.ScannedCount > 0, "本机应能扫到 MSI 条目");
        Assert.All(result.Entries, e =>
            Assert.True(MsiInterop.IsAbsent(e.MsiState),
                $"报了非 ABSENT 的条目：{e.DisplayName} state={e.MsiState}"));
    }

    [Fact]
    public async Task ScanAsync_EveryReportedEntryHasEvidence()
    {
        var scanner = new OrphanUninstallScanner();

        var result = await scanner.ScanAsync();

        Assert.All(result.Entries, e =>
        {
            Assert.NotEmpty(e.Evidence);
            Assert.Contains(e.Evidence, x => x.Contains("Windows Installer"));
            Assert.Contains(e.Evidence, x => x.Contains("MsiEnumProducts") || x.Contains("已安装产品列表"));
        });
    }

    [Fact]
    public async Task ScanAsync_EveryReportedEntryHasStrictGuidFormat()
    {
        var scanner = new OrphanUninstallScanner();

        var result = await scanner.ScanAsync();

        Assert.All(result.Entries, e =>
            Assert.Matches(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$",
                e.ProductCode));
    }

    [Fact]
    public async Task ScanAsync_DoesNotThrowAndCompletes()
    {
        var scanner = new OrphanUninstallScanner();

        var result = await scanner.ScanAsync();

        Assert.True(result.Duration >= TimeSpan.Zero);
    }
}

/// <summary>MSI 状态语义测试。</summary>
public class MsiInteropTests
{
    [Theory]
    [InlineData(1u, true)]
    [InlineData(2u, true)]
    [InlineData(3u, true)]
    [InlineData(4294967295u, true)]   // ERROR_UNKNOWN_PRODUCT：从未装过，最常见的孤儿形态
    [InlineData(4294967294u, true)]   // ERROR_INVALID_PARAMETER：GUID 非法
    [InlineData(0u, false)]           // UNKNOWN：不确定，不当孤儿
    [InlineData(4u, false)]
    [InlineData(5u, false)]
    [InlineData(6u, false)]
    public void IsAbsent_TrueForAbsentAndUnknownProductStates(uint state, bool expected)
    {
        Assert.Equal(expected, MsiInterop.IsAbsent(state));
    }

    [Fact]
    public void QueryProductState_NeverInstalledGuid_ReturnsUnknownProduct()
    {
        // 这条测试锁住一个真实语义：对"从未装过"的 GUID，
        // MsiQueryProductState 返回的不是 1/2/3 而是 ERROR_UNKNOWN_PRODUCT。
        // 一开始我漏了这一类，导致真机 E2E 扫不到自己造的孤儿条目。
        var state = MsiInterop.QueryProductState("{DEADBEEF-1234-5678-9ABC-DEF012345678}");

        Assert.Equal(MsiInterop.ErrorUnknownProduct, state);
        Assert.True(MsiInterop.IsAbsent(state));
    }

    [Theory]
    [InlineData(4u, true)]
    [InlineData(5u, true)]
    [InlineData(6u, true)]
    [InlineData(0u, false)]
    [InlineData(1u, false)]
    [InlineData(2u, false)]
    [InlineData(3u, false)]
    public void IsPresent_OnlyTrueForPresentStates(uint state, bool expected)
    {
        Assert.Equal(expected, MsiInterop.IsPresent(state));
    }

    [Fact]
    public void IsAbsent_And_IsPresent_AreMutuallyExclusive()
    {
        for (uint state = 0; state <= 8; state++)
        {
            Assert.False(MsiInterop.IsAbsent(state) && MsiInterop.IsPresent(state),
                $"state={state} 不能既 ABSENT 又 PRESENT");
        }
    }

    [Fact]
    public void IsPresent_IsFalseForUnknownProduct()
    {
        Assert.False(MsiInterop.IsPresent(MsiInterop.ErrorUnknownProduct));
    }

    [Fact]
    public void IsAbsent_AndIsPresent_BothFalseForUnknownState()
    {
        // 0 (UNKNOWN) 既不是 ABSENT 也不是 PRESENT → 调用方走"不确定就不报"
        Assert.False(MsiInterop.IsAbsent(0u));
        Assert.False(MsiInterop.IsPresent(0u));
    }

    [Fact]
    public void EnumerateProducts_ReturnsNonEmptyOnRealMachine()
    {
        var products = MsiInterop.EnumerateProducts();

        Assert.NotEmpty(products);
        Assert.All(products, p => Assert.StartsWith("{", p));
    }

    [Fact]
    public void EnumerateProducts_AllEntriesHaveStrictGuidShape()
    {
        var products = MsiInterop.EnumerateProducts();

        Assert.NotEmpty(products);
        Assert.All(products, p => Assert.Matches(
            @"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$", p));
    }

    [Fact]
    public void EnumerateProducts_CoversAllContexts_SupersetOfLegacyEnumeration()
    {
        // 核心回归：新的多上下文枚举只能是"更全"，绝不可能是"更少"。
        // 少枚举 = 把在装的产品误判成孤儿 = 误删，所以这条必须严格成立。
        // 本机实测（2026-10-25，Windows 11 22631 / AMD64 / 非管理员）：
        //   MACHINE 338 + USERMANAGED 0 + USERUNMANAGED 6 = 并集 344；
        //   旧 MsiEnumProducts 也是 344，两者完全相等。
        var multiContext = MsiInterop.EnumerateProducts();
        var legacy = MsiInterop.EnumerateProductsLegacy();

        Assert.NotEmpty(legacy);

        var missing = legacy.Where(p => !multiContext.Contains(p)).ToList();
        Assert.True(
            missing.Count == 0,
            $"多上下文枚举漏掉了旧枚举里的 {missing.Count} 个产品：{string.Join(", ", missing.Take(5))}");

        Assert.True(
            multiContext.Count >= legacy.Count,
            $"多上下文枚举数量 {multiContext.Count} 少于旧枚举 {legacy.Count}");
    }

    [Fact]
    public void EnumerateProducts_ProducesNoDuplicates_AndIsCaseInsensitive()
    {
        // HashSet 用 OrdinalIgnoreCase：同一 GUID 大小写不同不该算两条，
        // 也不该让 Ordinal 比较的调用方（OrphanCleaner 的 Contains）漏判。
        var products = MsiInterop.EnumerateProducts();

        Assert.NotEmpty(products);
        Assert.Equal(products.Count, products.Select(p => p.ToUpperInvariant()).Distinct().Count());

        var sample = products.First();
        Assert.Contains(sample.ToLowerInvariant(), products);
    }

    [Fact]
    public void InstallContextConstants_MatchWindowsInstallerDocumentedValues()
    {
        // MsiEnumProductsEx 的 dwContext 不是 1/2/4 的自然顺序：
        // USERMANAGED=1、USERUNMANAGED=2、MACHINE=4。
        // 写错值在真机上直接得到 ERROR_INVALID_PARAMETER(87)——本机已实测踩到过。
        var type = typeof(MsiInterop);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;

        Assert.Equal(4u, (uint)type.GetField("MsiInstallContextMachine", flags)!.GetValue(null)!);
        Assert.Equal(1u, (uint)type.GetField("MsiInstallContextUserManaged", flags)!.GetValue(null)!);
        Assert.Equal(2u, (uint)type.GetField("MsiInstallContextUserUnmanaged", flags)!.GetValue(null)!);
        Assert.Equal(259u, (uint)type.GetField("ErrorNoMoreItems", flags)!.GetValue(null)!);
    }

    [Fact]
    public void EnumerateProducts_SingleContextProbe_ReturnsMachineProducts()
    {
        // 逐上下文直接探针：MACHINE 上下文在本机应能取到产品（338 个，实测），
        // 用来证明 Ex API 的 P/Invoke 签名（参数顺序/缓冲区）是对的——
        // 签错时会直接返回 ERROR_INVALID_PARAMETER(87) 而拿到 0 个。
        const uint machineContext = 4;
        var (products, succeeded) = MsiInterop.EnumerateProductsInContext(machineContext);

        Assert.True(succeeded, "MACHINE 上下文枚举应成功");
        Assert.NotEmpty(products);
        Assert.All(products, p => Assert.StartsWith("{", p));
    }
}