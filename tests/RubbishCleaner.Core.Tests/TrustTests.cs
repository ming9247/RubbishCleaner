using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Trust;

namespace RubbishCleaner.Core.Tests;

public class TrustTableLoaderTests
{
    [Fact]
    public void Parse_ValidJson_LoadsRules()
    {
        const string json = """
        {
          "version": 2,
          "updated": "2026-10-01",
          "whitelist": [ { "type": "signer", "value": "Microsoft Corporation" } ],
          "blacklist": [ { "type": "namePattern", "value": "*bad*" } ]
        }
        """;

        var table = TrustTableLoader.Parse(json);

        Assert.Equal(2, table.Version);
        Assert.Single(table.Whitelist);
        Assert.Single(table.Blacklist);
        Assert.Equal(TrustRuleType.Signer, table.Whitelist[0].Type);
        Assert.Equal(TrustRuleType.NamePattern, table.Blacklist[0].Type);
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsEmptyTableWithoutThrowing()
    {
        var table = TrustTableLoader.Parse("{ not json at all ");

        Assert.Empty(table.Whitelist);
        Assert.Empty(table.Blacklist);
    }

    [Fact]
    public void Parse_UnknownRuleType_IsSkipped()
    {
        const string json = """
        { "whitelist": [ { "type": "unknownKind", "value": "x" }, { "type": "name", "value": "ok" } ] }
        """;

        var table = TrustTableLoader.Parse(json);

        Assert.Single(table.Whitelist);
        Assert.Equal("ok", table.Whitelist[0].Value);
    }

    [Fact]
    public void Parse_EmptyValue_IsSkipped()
    {
        const string json = """{ "whitelist": [ { "type": "name", "value": "  " } ] }""";

        Assert.Empty(TrustTableLoader.Parse(json).Whitelist);
    }

    [Fact]
    public void Parse_CommentsAndTrailingCommas_AreTolerated()
    {
        const string json = """
        {
          // 允许注释
          "whitelist": [ { "type": "name", "value": "a" }, ],
        }
        """;

        Assert.Single(TrustTableLoader.Parse(json).Whitelist);
    }

    [Fact]
    public void Merge_DeduplicatesIdenticalRules()
    {
        var a = new TrustTable
        {
            Source = "a",
            Whitelist = [new TrustRule(TrustRuleType.Name, "ctfmon")],
        };
        var b = new TrustTable
        {
            Source = "b",
            Whitelist = [new TrustRule(TrustRuleType.Name, "CTFMON")],
        };

        var merged = TrustTableLoader.Merge([a, b]);

        Assert.Single(merged.Whitelist);
        Assert.Equal("a + b", merged.Source);
    }

    [Fact]
    public void Load_MissingFiles_StillReturnsBuiltInTable()
    {
        var table = TrustTableLoader.Load(
            appDirectoryTablePath: Path.Combine(Path.GetTempPath(), "rc-missing-1.json"),
            userTablePath: Path.Combine(Path.GetTempPath(), "rc-missing-2.json"));

        Assert.NotEmpty(table.Whitelist);
        Assert.NotEmpty(table.Blacklist);
        Assert.Contains("builtin", table.Source);
    }
}

public class TrustMatcherTests
{
    private static TrustMatcher CreateMatcher(params TrustRule[] rules) => new(new TrustTable
    {
        Whitelist = rules.Where(r => r.Note == "white").ToList(),
        Blacklist = rules.Where(r => r.Note == "black").ToList(),
    });

    [Fact]
    public void Match_NoRules_ReturnsUnknown()
    {
        var matcher = new TrustMatcher(TrustTable.Empty);

        Assert.Equal(TrustVerdict.Unknown, matcher.Match(@"C:\a\b.exe", "b", null).Verdict);
    }

    [Fact]
    public void Match_SignerRule_MatchesSubject()
    {
        var matcher = CreateMatcher(new TrustRule(TrustRuleType.Signer, "Microsoft Corporation", "white"));

        var result = matcher.Match(@"C:\x\a.exe", "a", "CN=Microsoft Windows, O=Microsoft Corporation");

        Assert.Equal(TrustVerdict.Safe, result.Verdict);
    }

    [Fact]
    public void Match_BlacklistWinsOverWhitelist()
    {
        // ADR-001：黑名单优先，防止"放进白名单目录即洗白"
        var matcher = CreateMatcher(
            new TrustRule(TrustRuleType.Path, @"C:\Program Files\", "white"),
            new TrustRule(TrustRuleType.Name, "evil", "black"));

        var result = matcher.Match(@"C:\Program Files\evil.exe", "evil", null);

        Assert.Equal(TrustVerdict.Malicious, result.Verdict);
    }

    [Fact]
    public void Match_PathRule_UsesPrefixMatching()
    {
        var matcher = CreateMatcher(new TrustRule(TrustRuleType.Path, @"C:\Windows\System32\", "white"));

        Assert.Equal(TrustVerdict.Safe, matcher.Match(@"C:\Windows\System32\svchost.exe", "svchost", null).Verdict);
        // 前缀但非目录边界，不应命中
        Assert.Equal(TrustVerdict.Unknown, matcher.Match(@"C:\Windows\System32X\a.exe", "a", null).Verdict);
    }

    [Fact]
    public void Match_PathRule_ExpandsEnvironmentVariables()
    {
        var matcher = CreateMatcher(new TrustRule(TrustRuleType.Path, @"%SystemRoot%\System32\", "white"));
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");

        Assert.Equal(TrustVerdict.Safe, matcher.Match(path, "kernel32", null).Verdict);
    }

    [Fact]
    public void Match_NameRule_IsCaseInsensitiveAndIgnoresExtension()
    {
        var matcher = CreateMatcher(new TrustRule(TrustRuleType.Name, "CTFMON", "white"));

        Assert.Equal(TrustVerdict.Safe, matcher.Match(null, "ctfmon.exe", null).Verdict);
    }

    [Fact]
    public void Match_NamePatternRule_SupportsWildcards()
    {
        var matcher = CreateMatcher(new TrustRule(TrustRuleType.NamePattern, "*hao123*", "black"));

        Assert.Equal(TrustVerdict.Malicious, matcher.Match(null, "hao123_setup", null).Verdict);
        Assert.Equal(TrustVerdict.Unknown, matcher.Match(null, "chrome", null).Verdict);
    }

    [Fact]
    public void Match_Sha256Rule_UsesLazyProvider()
    {
        var called = 0;
        var matcher = CreateMatcher(new TrustRule(TrustRuleType.Sha256, "ABCDEF", "black"));

        var result = matcher.Match(@"C:\x\a.exe", "a", null, () =>
        {
            called++;
            return "abcdef";
        });

        Assert.Equal(TrustVerdict.Malicious, result.Verdict);
        Assert.Equal(1, called);
    }

    [Fact]
    public void Match_NoShaRule_DoesNotInvokeProvider()
    {
        var called = 0;
        var matcher = CreateMatcher(new TrustRule(TrustRuleType.Name, "a", "black"));

        _ = matcher.Match(@"C:\x\a.exe", "a", null, () =>
        {
            called++;
            return "abcdef";
        });

        Assert.Equal(0, called); // 惰性：没有 sha256 规则时不应做哈希（大文件哈希很贵）
    }

    [Theory]
    [InlineData("*hao123*", "hao123", true)]
    [InlineData("*hao123*", "hao123_setup", true)]
    [InlineData("*hao123*", "hao", false)]
    [InlineData("svch0st*", "svch0stx", true)]
    [InlineData("svch0st*", "xsvch0st", false)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "abbc", false)]
    public void BuildWildcardRegex_ConvertsWildcards(string pattern, string input, bool expected)
    {
        var regex = TrustMatcher.BuildWildcardRegex(pattern);

        Assert.Equal(expected, regex.IsMatch(input));
    }
}
