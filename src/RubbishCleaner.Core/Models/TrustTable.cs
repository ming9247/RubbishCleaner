namespace RubbishCleaner.Core.Models;

/// <summary>确认表规则类型。</summary>
public enum TrustRuleType
{
    /// <summary>签名主体包含指定文本。</summary>
    Signer,

    /// <summary>可执行文件路径前缀（目录须以反斜杠结尾）。</summary>
    Path,

    /// <summary>进程名（不含扩展名）精确匹配。</summary>
    Name,

    /// <summary>进程名或文件名通配符匹配。</summary>
    NamePattern,

    /// <summary>文件 SHA256 精确匹配。</summary>
    Sha256,
}

/// <summary>一条确认表规则。</summary>
public sealed record TrustRule(TrustRuleType Type, string Value, string? Note = null)
{
    public override string ToString() => $"{Type}:{Value}";
}

/// <summary>确认表：白名单 + 黑名单。</summary>
public sealed class TrustTable
{
    public int Version { get; init; } = 1;

    public string? Updated { get; init; }

    /// <summary>表来源描述（内置 / 程序目录 / 用户目录），用于 UI 展示。</summary>
    public string Source { get; init; } = "builtin";

    public IReadOnlyList<TrustRule> Whitelist { get; init; } = [];

    public IReadOnlyList<TrustRule> Blacklist { get; init; } = [];

    public static TrustTable Empty { get; } = new();
}

/// <summary>确认表匹配结果。</summary>
public sealed record TrustMatch(TrustVerdict Verdict, TrustRule? Rule, string? Reason)
{
    public static readonly TrustMatch None = new(TrustVerdict.Unknown, null, null);
}
