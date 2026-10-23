namespace RubbishCleaner.Core.Models;

/// <summary>单条证据，用于向用户解释"为什么这样判定"。</summary>
public sealed record Evidence(string Code, string Message, EvidenceSeverity Severity)
{
    public static Evidence Info(string code, string message) => new(code, message, EvidenceSeverity.Info);

    public static Evidence Warning(string code, string message) => new(code, message, EvidenceSeverity.Warning);

    public static Evidence Critical(string code, string message) => new(code, message, EvidenceSeverity.Critical);
}

/// <summary>单条评分规则的命中情况（未命中的规则也保留，便于 UI 展示"命中 x/y"）。</summary>
/// <param name="NotApplicable">
/// 该项不适用于此对象（例如未运行的进程无法判断"有无窗口"）。
/// 不计入分母，否则会把"无法判断"错算成"已排除嫌疑"，从而压低分数。
/// </param>
public sealed record ScoreItem(string RuleId, string Description, int Weight, bool Hit, bool NotApplicable = false)
{
    /// <summary>实际计入总分的分值：不适用或未命中的规则一律为 0。</summary>
    public int Contribution => Hit && !NotApplicable ? Weight : 0;
}

/// <summary>评分结果。</summary>
public sealed record RiskScore(int Total, RiskLevel Level, IReadOnlyList<ScoreItem> Items, bool Suppressed = false)
{
    public static readonly RiskScore Empty = new(0, RiskLevel.Normal, []);

    /// <summary>命中的规则数（不含不适用项）。</summary>
    public int HitCount => Items.Count(i => i.Hit && !i.NotApplicable);

    /// <summary>实际参与判定的规则数（排除不适用项）。</summary>
    public int RuleCount => Items.Count(i => !i.NotApplicable);
}
