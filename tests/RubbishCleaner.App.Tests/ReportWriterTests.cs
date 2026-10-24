using System.IO;
using RubbishCleaner.App.Infrastructure;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.App.Tests;

public class ReportWriterTests
{
    private static SoftwareProfile Profile(
        string name,
        int score = 70,
        RiskLevel level = RiskLevel.Suspicious,
        string? path = @"C:\Apps\a.exe",
        string? publisher = "Test Vendor",
        TrustVerdict trust = TrustVerdict.Unknown,
        bool running = true)
        => new()
        {
            Key = name,
            DisplayName = name,
            ExecutablePath = path,
            StartupEntries =
            [
                new StartupEntry
                {
                    Id = name,
                    Source = StartupSourceKind.RegistryRunHkcu,
                    Name = name,
                    Location = @"HKCU\Run",
                    ExecutablePath = path,
                    TargetExistsOverride = true,
                },
            ],
            LiveProcess = new ProcessFacts(1, name, path, running, false, false, true, false, null, publisher, null),
            Score = new RiskScore(score, level,
            [
                new ScoreItem("AUTOSTART", "存在自启动项", 30, true),
                new ScoreItem("NO_VISIBLE_WINDOW", "后台常驻且无可见窗口", 25, false, NotApplicable: true),
            ]),
            Trust = trust,
            Evidences = [Evidence.Info("AUTOSTART_RegistryRunHkcu", "注册表 Run（当前用户）：a")],
        };

    private static readonly DateTime FixedTime = new(2026, 10, 24, 1, 2, 3);

    // ---------------- CSV ----------------

    [Fact]
    public void Csv_HasHeaderAndOneRowPerProfile()
    {
        var profiles = new[] { Profile("a"), Profile("b"), Profile("c") };

        var csv = ReportWriter.Build(profiles, ReportFormat.Csv, "统计", "builtin", FixedTime);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, lines.Length);
        Assert.StartsWith("风险等级,分数,名称", lines[0]);
    }

    [Fact]
    public void Csv_ContainsAllFields()
    {
        var csv = ReportWriter.Build([Profile("myapp")], ReportFormat.Csv, "", "", FixedTime);

        Assert.Contains("myapp", csv);
        Assert.Contains(@"C:\Apps\a.exe", csv);
        Assert.Contains("Test Vendor", csv);
        Assert.Contains("70", csv);
        Assert.Contains("疑似", csv);
        Assert.Contains("未收录", csv);
    }

    [Fact]
    public void Csv_EscapesCommasInFields()
    {
        var csv = ReportWriter.Build([Profile("a,b")], ReportFormat.Csv, "", "", FixedTime);

        // 含逗号的字段必须被引号包裹
        Assert.Contains("\"a,b\"", csv);
    }

    [Fact]
    public void Csv_EscapesQuotesByDoubling()
    {
        var csv = ReportWriter.Build([Profile("say \"hi\"")], ReportFormat.Csv, "", "", FixedTime);

        Assert.Contains("\"say \"\"hi\"\"\"", csv);
    }

    [Fact]
    public void Csv_EscapesNewlines()
    {
        var csv = ReportWriter.Build([Profile("line1\nline2")], ReportFormat.Csv, "", "", FixedTime);

        Assert.Contains("\"line1\nline2\"", csv);
    }

    [Fact]
    public void Csv_HandlesMissingPathAndPublisher()
    {
        var csv = ReportWriter.Build(
            [Profile("nopath", path: null, publisher: null)],
            ReportFormat.Csv, "", "", FixedTime);

        Assert.Contains("（无签名）", csv);
    }

    [Fact]
    public void Csv_ExcludesNotApplicableRulesFromRuleList()
    {
        // "不适用"的规则不应出现在报告的"命中规则"列
        var csv = ReportWriter.Build([Profile("x")], ReportFormat.Csv, "", "", FixedTime);

        Assert.Contains("存在自启动项", csv);
        Assert.DoesNotContain("后台常驻且无可见窗口", csv);
    }

    [Fact]
    public void Csv_AllRowsHaveTenFields()
    {
        var profiles = new[] { Profile("a,b"), Profile("c\"d"), Profile("plain") };

        var csv = ReportWriter.Build(profiles, ReportFormat.Csv, "", "", FixedTime);

        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            Assert.Equal(9, CountTopLevelCommas(line));
        }
    }

    // ---------------- HTML ----------------

    [Fact]
    public void Html_IsWellFormedDocument()
    {
        var html = ReportWriter.Build([Profile("a")], ReportFormat.Html, "统计", "builtin", FixedTime);

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Contains("</html>", html);
        Assert.Contains("RubbishCleaner 扫描报告", html);
    }

    [Fact]
    public void Html_EscapesSpecialCharacters()
    {
        var html = ReportWriter.Build([Profile("<script>x</script>")], ReportFormat.Html, "", "", FixedTime);

        // 必须转义，否则生成的报告有 XSS 风险（程序名可能被恶意构造）
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Html_EscapesAmpersandFirst()
    {
        var html = ReportWriter.Build([Profile("a&b")], ReportFormat.Html, "", "", FixedTime);

        Assert.Contains("a&amp;b", html);
        Assert.DoesNotContain("&amp;amp;", html); // 不能双重转义
    }

    [Fact]
    public void Html_IncludesSeverityClassForPill()
    {
        var html = ReportWriter.Build(
            [Profile("risky", level: RiskLevel.HighRisk)],
            ReportFormat.Html, "", "", FixedTime);

        Assert.Contains("pill HighRisk", html);
    }

    [Fact]
    public void Html_ContainsSummaryAndTrustTableInfo()
    {
        var html = ReportWriter.Build([Profile("a")], ReportFormat.Html, "共 3 项", "builtin + user", FixedTime);

        Assert.Contains("共 3 项", html);
        Assert.Contains("builtin + user", html);
        Assert.Contains("2026-10-24 01:02:03", html);
    }

    // ---------------- 文件名与写盘 ----------------

    [Fact]
    public void SuggestFileName_UsesCorrectExtension()
    {
        Assert.EndsWith(".csv", ReportWriter.SuggestFileName(ReportFormat.Csv, FixedTime));
        Assert.EndsWith(".html", ReportWriter.SuggestFileName(ReportFormat.Html, FixedTime));
    }

    [Fact]
    public void SuggestFileName_ContainsTimestamp()
    {
        var name = ReportWriter.SuggestFileName(ReportFormat.Csv, FixedTime);

        Assert.Contains("20261024-010203", name);
    }

    [Fact]
    public void WriteTo_CreatesFileWithBom()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rc-report-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var path = ReportWriter.WriteTo(dir, [Profile("a")], ReportFormat.Csv, "", "", FixedTime);

            Assert.True(File.Exists(path));

            // BOM 必须在，否则 Excel 打开中文乱码
            var bytes = File.ReadAllBytes(path);
            Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void WriteTo_HtmlIsUtf8WithBomAndReadable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rc-report-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var path = ReportWriter.WriteTo(dir, [Profile("测试程序")], ReportFormat.Html, "", "", FixedTime);
            var text = File.ReadAllText(path);

            Assert.Contains("测试程序", text);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Build_EmptyList_ProducesHeaderOnlyCsv()
    {
        var csv = ReportWriter.Build([], ReportFormat.Csv, "", "", FixedTime);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Single(lines);
    }

    [Fact]
    public void Build_UnknownFormat_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ReportWriter.Build([], (ReportFormat)99, "", "", FixedTime));
    }

    private static int CountTopLevelCommas(string line)
    {
        var count = 0;
        var inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (ch == ',' && !inQuotes)
            {
                count++;
            }
        }

        return count;
    }
}
