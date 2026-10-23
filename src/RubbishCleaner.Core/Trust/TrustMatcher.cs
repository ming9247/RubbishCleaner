using System.Text.RegularExpressions;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Trust;

/// <summary>
/// 确认表匹配器。
/// 判定优先级见 ADR-001：<b>黑名单优先于白名单</b>，
/// 防止"把程序放进白名单目录即可洗白"的绕过手法。
/// </summary>
public sealed class TrustMatcher
{
    private readonly TrustTable _table;
    private readonly List<CompiledRule> _blacklist;
    private readonly List<CompiledRule> _whitelist;

    public TrustMatcher(TrustTable table)
    {
        _table = table ?? TrustTable.Empty;
        _blacklist = _table.Blacklist.Select(Compile).ToList();
        _whitelist = _table.Whitelist.Select(Compile).ToList();
    }

    /// <summary>对单个可执行文件做判定。</summary>
    /// <param name="executablePath">可执行文件绝对路径（可为 null）。</param>
    /// <param name="processName">进程名（不含扩展名）。</param>
    /// <param name="signerSubject">签名主体。</param>
    /// <param name="sha256Provider">惰性哈希提供者，仅当表中存在 sha256 规则时才调用。</param>
    public TrustMatch Match(
        string? executablePath,
        string? processName,
        string? signerSubject,
        Func<string?>? sha256Provider = null)
    {
        var context = new MatchContext(executablePath, processName, signerSubject, sha256Provider);

        var blackHit = FirstMatch(_blacklist, context);
        if (blackHit is not null)
        {
            return new TrustMatch(TrustVerdict.Malicious, blackHit.Rule, $"命中黑名单 {blackHit.Rule} {blackHit.Rule.Note}".Trim());
        }

        var whiteHit = FirstMatch(_whitelist, context);
        if (whiteHit is not null)
        {
            return new TrustMatch(TrustVerdict.Safe, whiteHit.Rule, $"命中白名单 {whiteHit.Rule} {whiteHit.Rule.Note}".Trim());
        }

        return TrustMatch.None;
    }

    private static CompiledRule? FirstMatch(List<CompiledRule> rules, MatchContext context)
        => rules.FirstOrDefault(rule => rule.IsMatch(context));

    private static CompiledRule Compile(TrustRule rule) => new(rule, rule.Type switch
    {
        TrustRuleType.NamePattern => BuildWildcardRegex(rule.Value),
        _ => null,
    });

    /// <summary>通配符转正则：<c>*</c> → <c>.*</c>，<c>?</c> → <c>.</c>，其余字符转义。</summary>
    internal static Regex BuildWildcardRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private sealed record MatchContext(
        string? ExecutablePath,
        string? ProcessName,
        string? SignerSubject,
        Func<string?>? Sha256Provider);

    private sealed class CompiledRule
    {
        private readonly Regex? _wildcard;

        public TrustRule Rule { get; }

        public CompiledRule(TrustRule rule, Regex? wildcard)
        {
            Rule = rule;
            _wildcard = wildcard;
        }

        public bool IsMatch(MatchContext context)
        {
            var value = Rule.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            switch (Rule.Type)
            {
                case TrustRuleType.Signer:
                    return context.SignerSubject is not null
                           && context.SignerSubject.Contains(value, StringComparison.OrdinalIgnoreCase);

                case TrustRuleType.Path:
                {
                    if (string.IsNullOrWhiteSpace(context.ExecutablePath))
                    {
                        return false;
                    }

                    var expanded = Environment.ExpandEnvironmentVariables(value);
                    // 目录规则必须以分隔符结尾，避免 "C:\Program Files\Foo" 误匹配 "C:\Program Files\Foobar"
                    return context.ExecutablePath.StartsWith(expanded, StringComparison.OrdinalIgnoreCase);
                }

                case TrustRuleType.Name:
                {
                    var name = Path.GetFileNameWithoutExtension(context.ProcessName ?? context.ExecutablePath ?? string.Empty);
                    return !string.IsNullOrEmpty(name)
                           && string.Equals(name, Path.GetFileNameWithoutExtension(value), StringComparison.OrdinalIgnoreCase);
                }

                case TrustRuleType.NamePattern:
                {
                    var name = Path.GetFileNameWithoutExtension(context.ProcessName ?? string.Empty);
                    var fileName = Path.GetFileName(context.ExecutablePath ?? string.Empty);
                    return (_wildcard?.IsMatch(name) == true) || (_wildcard?.IsMatch(fileName) == true);
                }

                case TrustRuleType.Sha256:
                {
                    var hash = context.Sha256Provider?.Invoke();
                    return hash is not null && string.Equals(hash, value, StringComparison.OrdinalIgnoreCase);
                }

                default:
                    return false;
            }
        }
    }
}
