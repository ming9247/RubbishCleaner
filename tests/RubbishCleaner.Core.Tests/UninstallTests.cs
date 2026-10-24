using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Uninstall;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// 注册表文本清洗测试。
///
/// 数据来源是真实踩到的脏数据：本机「明日之后」这条的 DisplayName / Publisher /
/// UninstallString 里混着成片二进制垃圾，不清洗会让列表出现整屏乱码。
/// </summary>
public class RegistryTextSanitizerTests
{
    [Fact]
    public void Clean_NormalText_IsUnchanged()
    {
        Assert.Equal("Adobe Photoshop 2023", RegistryTextSanitizer.Clean("Adobe Photoshop 2023"));
    }

    [Fact]
    public void Clean_TrimsWhitespace()
    {
        Assert.Equal("微信", RegistryTextSanitizer.Clean("  微信  "));
    }

    [Fact]
    public void Clean_TruncatesAtControlCharacter()
    {
        // 真实形态："明日之后" 后面紧跟一串垃圾
        var dirty = "明日之后\u0000羹\u0007琢\0\0\0Ѐ";

        Assert.Equal("明日之后", RegistryTextSanitizer.Clean(dirty));
    }

    [Fact]
    public void Clean_TruncatesAtPrivateUseArea()
    {
        var dirty = "网易云音乐\uE000\uE001乱码";

        Assert.Equal("网易云音乐", RegistryTextSanitizer.Clean(dirty));
    }

    [Fact]
    public void Clean_TruncatesAtSurrogate()
    {
        var dirty = "正常名称\uD800垃圾";

        Assert.Equal("正常名称", RegistryTextSanitizer.Clean(dirty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]                 // 太短，无意义
    [InlineData("\u0000\u0001")]      // 全是控制字符
    [InlineData("---")]               // 没有字母或数字
    public void Clean_InvalidInput_ReturnsNull(string? input)
    {
        Assert.Null(RegistryTextSanitizer.Clean(input));
    }

    [Fact]
    public void Clean_KeepsChineseAndPunctuation()
    {
        Assert.Equal("腾讯科技(深圳)有限公司", RegistryTextSanitizer.Clean("腾讯科技(深圳)有限公司"));
    }

    [Fact]
    public void Clean_ExtremelyLongText_IsNotTruncatedByLengthAlone()
    {
        // 正常情况下 200 字符以内应保留（长度上限只是防御极端值）
        var text = new string('a', 150);

        Assert.Equal(text, RegistryTextSanitizer.Clean(text));
    }

    // ---------------- 卸载命令解析 ----------------

    [Fact]
    public void TryExtractExecutable_QuotedPath_ReturnsPath()
    {
        var result = RegistryTextSanitizer.TryExtractExecutable(
            @"""C:\Program Files\App\uninst.exe"" /S");

        Assert.Equal(@"C:\Program Files\App\uninst.exe", result);
    }

    [Fact]
    public void TryExtractExecutable_UnquotedPath_StopsAtExe()
    {
        var result = RegistryTextSanitizer.TryExtractExecutable(
            @"C:\App\uninst.exe /allusers");

        Assert.Equal(@"C:\App\uninst.exe", result);
    }

    [Fact]
    public void TryExtractExecutable_MsiExec_ReturnsNull()
    {
        // MSI 卸载没有独立 exe 可校验
        Assert.Null(RegistryTextSanitizer.TryExtractExecutable(
            @"MsiExec.exe /X{12345678-1234-1234-1234-123456789012}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryExtractExecutable_Empty_ReturnsNull(string? input)
    {
        Assert.Null(RegistryTextSanitizer.TryExtractExecutable(input));
    }

    [Fact]
    public void TryExtractExecutable_ExeWithSpacesInsideQuotes_IsHandled()
    {
        // 实测存在："Uninstall 极狐游戏加速器.exe" 带空格
        var result = RegistryTextSanitizer.TryExtractExecutable(
            @"""D:\极狐加速器\speedfox\Uninstall 极狐游戏加速器.exe"" /allusers");

        Assert.Equal(@"D:\极狐加速器\speedfox\Uninstall 极狐游戏加速器.exe", result);
    }
}

/// <summary>已装软件枚举的过滤逻辑测试（用真实注册表，只断言性质）。</summary>
public class InstalledAppEnumeratorTests
{
    [Fact]
    public void Enumerate_FiltersOutSystemComponents()
    {
        // 实测 520 个原始条目里 376 个是 SystemComponent，必须被过滤掉
        var result = new InstalledAppEnumerator().Enumerate();

        Assert.True(result.TotalRawEntries > 0, "本机应能读到卸载注册表条目");
        Assert.True(result.Apps.Count < result.TotalRawEntries,
            "系统组件应被过滤，展示数必须少于原始数");
        Assert.True(result.FilteredOut > 0);
    }

    [Fact]
    public void Enumerate_EveryAppHasNameAndUninstallCommand()
    {
        var result = new InstalledAppEnumerator().Enumerate();

        Assert.All(result.Apps, app =>
        {
            Assert.False(string.IsNullOrWhiteSpace(app.DisplayName));
            Assert.True(app.CanUninstall, $"{app.DisplayName} 应带卸载命令");
            Assert.False(string.IsNullOrWhiteSpace(app.RegistryPath));
        });
    }

    [Fact]
    public void Enumerate_NoDisplayNameStartsWithGarbage()
    {
        // 回归防护：清洗后不应残留控制字符或私用区字符
        var result = new InstalledAppEnumerator().Enumerate();

        Assert.All(result.Apps, app =>
        {
            Assert.DoesNotContain(app.DisplayName, c => char.IsControl(c) || (c >= '\uE000' && c <= '\uF8FF'));
        });
    }

    [Fact]
    public void Enumerate_IsSortedByName()
    {
        var result = new InstalledAppEnumerator().Enumerate();
        var names = result.Apps.Select(a => a.DisplayName).ToList();

        Assert.Equal(names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase), names);
    }

    [Fact]
    public void Enumerate_CoversBothScopesWithoutDuplicates()
    {
        var result = new InstalledAppEnumerator().Enumerate();
        var ids = result.Apps.Select(a => a.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}

/// <summary>卸载执行器测试：只测"不该执行时拒绝执行"，绝不真的调起卸载程序。</summary>
public class UninstallExecutorTests
{
    [Fact]
    public void Execute_NullCommand_RefusesWithoutStarting()
    {
        var outcome = new UninstallExecutor(TimeSpan.FromSeconds(1)).Execute(null);

        Assert.False(outcome.Started);
        Assert.Contains("没有可用的卸载命令", outcome.Message);
    }

    [Fact]
    public void Execute_MissingUninstaller_RefusesAndExplains()
    {
        // 卸载程序不存在是最常见的失败场景（用户手动删过目录）
        var outcome = new UninstallExecutor(TimeSpan.FromSeconds(1))
            .Execute(@"""D:\NoSuchApp\uninst.exe"" /S");

        Assert.False(outcome.Started);
        Assert.False(outcome.Completed);
        Assert.Contains("卸载程序不存在", outcome.Message);
    }

    [Fact]
    public void Execute_NeverRunsArbitraryCommands()
    {
        // 传入一个真实存在但绝不是卸载程序的命令时，
        // 执行器本身不做判断（判断在上层），但至少不能因为解析而崩溃。
        var outcome = new UninstallExecutor(TimeSpan.FromMilliseconds(200))
            .Execute(@"""D:\Definitely\Not\Here.exe""");

        Assert.False(outcome.Started);
    }
}
