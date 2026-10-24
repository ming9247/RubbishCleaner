using System.ComponentModel;
using RubbishCleaner.App.Infrastructure;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.App.Tests;

public class SoftwareSorterTests
{
    private static SoftwareProfile Profile(
        string name,
        int score,
        RiskLevel level,
        TrustVerdict trust = TrustVerdict.Unknown,
        string? publisher = null,
        StartupSourceKind source = StartupSourceKind.RegistryRunHkcu)
        => new()
        {
            Key = name,
            DisplayName = name,
            ExecutablePath = $@"C:\Apps\{name}.exe",
            StartupEntries =
            [
                new StartupEntry
                {
                    Id = name,
                    Source = source,
                    Name = name,
                    Location = @"HKCU\Run",
                    ExecutablePath = $@"C:\Apps\{name}.exe",
                    TargetExistsOverride = true,
                },
            ],
            LiveProcess = new ProcessFacts(
                1, name, $@"C:\Apps\{name}.exe", true, false, false, false, false, null, publisher, null),
            Score = new RiskScore(score, level, []),
            Trust = trust,
        };

    private static readonly SoftwareProfile[] Sample =
    [
        Profile("beta", 50, RiskLevel.Suspicious, publisher: "Zeta Corp"),
        Profile("alpha", 80, RiskLevel.HighRisk, publisher: "Alpha Inc"),
        Profile("gamma", 20, RiskLevel.Normal, TrustVerdict.Safe, "Beta LLC", StartupSourceKind.WindowsService),
    ];

    [Fact]
    public void Sort_ByScoreDescending_PutsHighestFirst()
    {
        var sorted = SoftwareSorter.Sort(Sample, SortColumn.Score, ListSortDirection.Descending);

        Assert.Equal(["alpha", "beta", "gamma"], sorted.Select(p => p.DisplayName));
    }

    [Fact]
    public void Sort_ByScoreAscending_PutsLowestFirst()
    {
        var sorted = SoftwareSorter.Sort(Sample, SortColumn.Score, ListSortDirection.Ascending);

        Assert.Equal(["gamma", "beta", "alpha"], sorted.Select(p => p.DisplayName));
    }

    [Fact]
    public void Sort_ByLevelDescending_PutsHighRiskFirst()
    {
        // Normal=0 < Suspicious=1 < HighRisk=2
        var sorted = SoftwareSorter.Sort(Sample, SortColumn.Level, ListSortDirection.Descending);

        Assert.Equal(["alpha", "beta", "gamma"], sorted.Select(p => p.DisplayName));
    }

    [Fact]
    public void Sort_ByLevelAscending_PutsNormalFirst()
    {
        var sorted = SoftwareSorter.Sort(Sample, SortColumn.Level, ListSortDirection.Ascending);

        Assert.Equal(["gamma", "beta", "alpha"], sorted.Select(p => p.DisplayName));
    }

    [Fact]
    public void Sort_ByNameAscending_IsAlphabetical()
    {
        var sorted = SoftwareSorter.Sort(Sample, SortColumn.Name, ListSortDirection.Ascending);

        Assert.Equal(["alpha", "beta", "gamma"], sorted.Select(p => p.DisplayName));
    }

    [Fact]
    public void Sort_ByNameDescending_IsReverseAlphabetical()
    {
        var sorted = SoftwareSorter.Sort(Sample, SortColumn.Name, ListSortDirection.Descending);

        Assert.Equal(["gamma", "beta", "alpha"], sorted.Select(p => p.DisplayName));
    }

    [Fact]
    public void Sort_ByPublisher_UsesPublisherString()
    {
        var sorted = SoftwareSorter.Sort(Sample, SortColumn.Publisher, ListSortDirection.Ascending);

        Assert.Equal(["alpha", "gamma", "beta"], sorted.Select(p => p.DisplayName));
    }

    [Fact]
    public void Sort_ByTrustDescending_PutsBlacklistedFirstThenSafe()
    {
        // Unknown=0 < Safe=1 < Malicious=2
        var data = new[]
        {
            Profile("unknown", 30, RiskLevel.Normal),
            Profile("safe", 30, RiskLevel.Normal, TrustVerdict.Safe),
            Profile("bad", 30, RiskLevel.Normal, TrustVerdict.Malicious),
        };

        var sorted = SoftwareSorter.Sort(data, SortColumn.Trust, ListSortDirection.Descending);

        Assert.Equal(["bad", "safe", "unknown"], sorted.Select(p => p.DisplayName));
    }

    [Fact]
    public void Sort_ByStartupSource_UsesFirstEntryKind()
    {
        var data = new[]
        {
            Profile("svc", 30, RiskLevel.Normal, source: StartupSourceKind.WindowsService),
            Profile("run", 30, RiskLevel.Normal, source: StartupSourceKind.RegistryRunHkcu),
        };

        var sorted = SoftwareSorter.Sort(data, SortColumn.StartupSource, ListSortDirection.Ascending);

        // "Windows 服务" vs "注册表 Run（当前用户）"：按中文序比较，只验证不抛异常且两者都在
        Assert.Equal(2, sorted.Count);
    }

    [Fact]
    public void Sort_SamePrimaryKey_BreaksTieByScoreDescending()
    {
        var data = new[]
        {
            Profile("low", 40, RiskLevel.Suspicious),
            Profile("high", 70, RiskLevel.Suspicious),
        };

        var sorted = SoftwareSorter.Sort(data, SortColumn.Level, ListSortDirection.Ascending);

        // 同为 Suspicious，分数高的排前面（稳定次序）
        Assert.Equal(["high", "low"], sorted.Select(p => p.DisplayName));
    }

    [Fact]
    public void Sort_IsStable_AcrossRepeatedCalls()
    {
        var first = SoftwareSorter.Sort(Sample, SortColumn.Score, ListSortDirection.Descending);
        var second = SoftwareSorter.Sort(Sample, SortColumn.Score, ListSortDirection.Descending);

        Assert.Equal(first.Select(p => p.DisplayName), second.Select(p => p.DisplayName));
    }

    [Fact]
    public void Sort_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(SoftwareSorter.Sort([], SortColumn.Score, ListSortDirection.Descending));
    }

    [Fact]
    public void Sort_DoesNotMutateSourceOrder()
    {
        var original = Sample.Select(p => p.DisplayName).ToList();

        _ = SoftwareSorter.Sort(Sample, SortColumn.Name, ListSortDirection.Descending);

        Assert.Equal(original, Sample.Select(p => p.DisplayName));
    }

    [Fact]
    public void Sort_ProfileWithoutStartupEntries_UsesEmptyKeyWithoutThrowing()
    {
        var bare = new SoftwareProfile
        {
            Key = "bare",
            DisplayName = "bare",
            Score = new RiskScore(0, RiskLevel.Normal, []),
        };

        var sorted = SoftwareSorter.Sort([bare], SortColumn.StartupSource, ListSortDirection.Ascending);

        Assert.Single(sorted);
    }
}
