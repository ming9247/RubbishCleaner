namespace RubbishCleaner.Core.Models;

/// <summary>单个数据源的采集结果。</summary>
public sealed record SourceResult(
    StartupSourceKind Kind,
    IReadOnlyList<StartupEntry> Entries,
    bool Degraded = false,
    string? Error = null)
{
    public static SourceResult Empty(StartupSourceKind kind) => new(kind, []);

    public static SourceResult Failed(StartupSourceKind kind, string error) => new(kind, [], true, error);
}

/// <summary>一次扫描的完整结果。</summary>
public sealed class ScanResult
{
    public IReadOnlyList<SoftwareProfile> Profiles { get; init; } = [];

    /// <summary>降级的数据源（权限不足或读取异常）。</summary>
    public IReadOnlyList<SourceResult> DegradedSources { get; init; } = [];

    public TimeSpan Duration { get; init; }

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;

    public bool IsElevated { get; init; }

    public int HighRiskCount => Profiles.Count(p => p.Score.Level == RiskLevel.HighRisk);

    public int SuspiciousCount => Profiles.Count(p => p.Score.Level == RiskLevel.Suspicious);

    public int NormalCount => Profiles.Count(p => p.Score.Level == RiskLevel.Normal);

    public int TotalStartupEntries => Profiles.Sum(p => p.StartupEntries.Count);
}

/// <summary>扫描进度通知。</summary>
public sealed record ScanProgress(string Stage, int Completed, int Total, string? Detail = null)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp(Completed * 100d / Total, 0, 100);
}
