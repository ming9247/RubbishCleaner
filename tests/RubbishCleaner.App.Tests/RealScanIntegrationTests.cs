using System.ComponentModel;
using System.Text;
using RubbishCleaner.App.Infrastructure;
using RubbishCleaner.Core.Detection;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Trust;

namespace RubbishCleaner.App.Tests;

/// <summary>
/// 用<b>真实系统扫描结果</b>验证排序与导出。
/// 这类测试依赖运行环境（不同机器结果不同），因此只断言"性质"而不是具体条目：
/// 顺序单调性、不丢项、不重复、格式合法。这样在任何机器上都稳定通过，
/// 同时又能真实覆盖整条链路（引擎 → 排序 → 导出）。
/// </summary>
public class RealScanIntegrationTests
{
    private static async Task<ScanResult> ScanAsync()
        => await StartupScanner.CreateDefault(TrustTableLoader.Load()).ScanAsync();

    [Fact]
    public async Task RealScan_ProducesProfiles()
    {
        var result = await ScanAsync();

        Assert.NotEmpty(result.Profiles);
        Assert.All(result.Profiles, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.DisplayName));
            Assert.InRange(p.Score.Total, 0, 100);
        });
    }

    [Theory]
    [InlineData(SortColumn.Score, ListSortDirection.Descending)]
    [InlineData(SortColumn.Score, ListSortDirection.Ascending)]
    [InlineData(SortColumn.Level, ListSortDirection.Descending)]
    [InlineData(SortColumn.Name, ListSortDirection.Ascending)]
    [InlineData(SortColumn.Publisher, ListSortDirection.Ascending)]
    [InlineData(SortColumn.Trust, ListSortDirection.Descending)]
    [InlineData(SortColumn.StartupSource, ListSortDirection.Ascending)]
    public async Task RealScan_Sort_PreservesAllItems(SortColumn column, ListSortDirection direction)
    {
        var result = await ScanAsync();
        var sorted = SoftwareSorter.Sort(result.Profiles, column, direction);

        Assert.Equal(result.Profiles.Count, sorted.Count);
        Assert.Equal(
            result.Profiles.Select(p => p.DisplayName).OrderBy(n => n),
            sorted.Select(p => p.DisplayName).OrderBy(n => n));
    }

    [Fact]
    public async Task RealScan_SortByScoreDescending_IsMonotonic()
    {
        var result = await ScanAsync();
        var sorted = SoftwareSorter.Sort(result.Profiles, SortColumn.Score, ListSortDirection.Descending);

        for (var i = 1; i < sorted.Count; i++)
        {
            Assert.True(
                sorted[i - 1].Score.Total >= sorted[i].Score.Total,
                $"第 {i} 项分数 {sorted[i].Score.Total} 大于前一项 {sorted[i - 1].Score.Total}");
        }
    }

    [Fact]
    public async Task RealScan_SortByNameAscending_IsMonotonic()
    {
        var result = await ScanAsync();
        var sorted = SoftwareSorter.Sort(result.Profiles, SortColumn.Name, ListSortDirection.Ascending);

        for (var i = 1; i < sorted.Count; i++)
        {
            var cmp = string.Compare(sorted[i - 1].DisplayName, sorted[i].DisplayName, StringComparison.CurrentCultureIgnoreCase);
            Assert.True(cmp <= 0, $"'{sorted[i - 1].DisplayName}' 应排在 '{sorted[i].DisplayName}' 之前");
        }
    }

    [Fact]
    public async Task RealScan_HitCountNeverExceedsRuleCount()
    {
        // 回归防护：曾经 NotApplicable 规则被计入命中，导致 "4/3 条规则命中" 这种矛盾显示
        var result = await ScanAsync();

        Assert.All(result.Profiles, p =>
        {
            Assert.True(p.Score.HitCount <= p.Score.RuleCount,
                $"{p.DisplayName}: 命中 {p.Score.HitCount} > 适用规则 {p.Score.RuleCount}");
        });
    }

    [Fact]
    public async Task RealScan_NotApplicableRules_NeverContributeScore()
    {
        var result = await ScanAsync();

        foreach (var profile in result.Profiles)
        {
            var expected = profile.Score.Items
                .Where(i => i.Hit && !i.NotApplicable)
                .Sum(i => i.Weight);

            Assert.Equal(Math.Clamp(expected, 0, 100), profile.Score.Total);
        }
    }

    [Fact]
    public async Task RealScan_CsvExport_EscapesAndQuotesCorrectly()
    {
        var result = await ScanAsync();
        var csv = BuildCsv(result.Profiles);

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(result.Profiles.Count + 1, lines.Length);   // 表头 + 每项一行
        Assert.StartsWith("风险等级,分数,名称", lines[0]);

        // 每个字段要么不含逗号，要么被引号包裹
        foreach (var line in lines.Skip(1))
        {
            Assert.True(CountTopLevelCommas(line) == 9, $"字段数应为 10：{line}");
        }
    }

    [Fact]
    public async Task RealScan_CsvExport_ContainsKnownProgram()
    {
        var result = await ScanAsync();
        var csv = BuildCsv(result.Profiles);
        var sample = result.Profiles[0];

        Assert.Contains(Csv(sample.DisplayName), csv);
    }

    // ---------- 与 MainViewModel 中一致的导出辅助（保持同步） ----------

    private static string BuildCsv(IEnumerable<SoftwareProfile> profiles)
    {
        var builder = new StringBuilder();
        builder.AppendLine("风险等级,分数,名称,可执行文件路径,启动方式,发行商,特征库,运行状态,命中规则数,命中规则");

        foreach (var p in profiles)
        {
            var rules = string.Join(" | ", p.Score.Items.Where(i => i.Hit && !i.NotApplicable).Select(i => i.Description));
            builder.AppendLine(string.Join(',', new[]
            {
                Csv(p.Score.Level.ToDisplayName()),
                p.Score.Total.ToString(),
                Csv(p.DisplayName),
                Csv(p.ExecutablePath ?? string.Empty),
                Csv(p.StartupSummary),
                Csv(p.Publisher ?? "（无签名）"),
                Csv(p.Trust.ToString()),
                Csv(p.IsRunning ? "运行中" : "未运行"),
                Csv($"{p.Score.HitCount}/{p.Score.RuleCount}"),
                Csv(rules),
            }));
        }

        return builder.ToString();
    }

    private static string Csv(string value)
        => value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";

    /// <summary>统计不在引号内的逗号数量，用于校验字段数。</summary>
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
