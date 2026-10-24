using RubbishCleaner.Core.Leftover;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// 软件名归一化测试。
///
/// 这个类只服务于"防止把在用的目录误报成残留"，所以测试的重点是
/// <b>同名不同写法必须能对上</b>（多认领是安全的），而不是"不同软件必须区分开"。
/// </summary>
public class SoftwareNameNormalizerTests
{
    [Theory]
    [InlineData("Adobe Photoshop 2023", "adobephotoshop2023")]
    [InlineData("Visual Studio Code (User)", "visualstudiocodeuser")]
    [InlineData("PDFgear 2.1.15", "pdfgear2115")]
    [InlineData("微信开发者工具", "微信开发者工具")]
    [InlineData("  多个   空格  ", "多个空格")]
    public void Normalize_KeepsOnlyLettersAndDigits(string input, string expected)
    {
        Assert.Equal(expected, SoftwareNameNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    public void Normalize_EmptyInput_ReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, SoftwareNameNormalizer.Normalize(input));
    }

    [Fact]
    public void Variants_StripsTrailingVersionDigits()
    {
        var variants = SoftwareNameNormalizer.Variants("Adobe After Effects 2023");

        Assert.Contains("adobeaftereffects2023", variants);
        Assert.Contains("adobeaftereffects", variants);
    }

    [Fact]
    public void Variants_KeepsShortNameIntact()
    {
        // "7-Zip" → "7zip"，去尾数字后只剩 "7"，必须拒绝这条变体，否则会退化成空匹配
        var variants = SoftwareNameNormalizer.Variants("7-Zip");

        Assert.Contains("7zip", variants);
        Assert.DoesNotContain("7", variants);
    }

    [Fact]
    public void Variants_TooShort_ReturnsEmpty()
    {
        Assert.Empty(SoftwareNameNormalizer.Variants("ab"));
        Assert.Empty(SoftwareNameNormalizer.Variants(null));
    }

    [Fact]
    public void Words_SplitsAndDropsShortTokens()
    {
        var words = SoftwareNameNormalizer.Words("Autodesk, Inc.");

        Assert.Contains("autodesk", words);
        Assert.DoesNotContain("inc", words);   // 3 个字符，低于默认阈值 4
    }

    [Fact]
    public void Words_KeepsChineseVendorName()
    {
        var words = SoftwareNameNormalizer.Words("杭州网易雷火科技有限公司");

        Assert.Contains("杭州网易雷火科技有限公司", words);
    }

    [Theory]
    [InlineData("Foo (x86)", "Foo")]
    [InlineData("Bar v2", "Bar")]
    [InlineData("Baz V3 Beta", "Baz")]
    public void CleanDirectoryNameForDisplay_RemovesCommonNoise(string input, string expected)
    {
        Assert.Equal(expected, SoftwareNameNormalizer.CleanDirectoryNameForDisplay(input));
    }

    [Fact]
    public void CleanDirectoryNameForDisplay_ShortNameIsNotTruncated()
    {
        // marker 出现在前 2 个字符时不动，避免 "V2Ray" 被切成 "V"
        Assert.Equal("V2Ray", SoftwareNameNormalizer.CleanDirectoryNameForDisplay("V2Ray"));
    }
}
