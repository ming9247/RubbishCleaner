using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Trust;

/// <summary>
/// 规则来源层。合并表是「内置 → 程序目录 → 用户层」追加而成的，
/// 只有用户层可写，前两层永远只读（见 ADR-008）。
/// </summary>
public enum TrustRuleLayer
{
    /// <summary>内置表（代码里写死，随程序发布）。</summary>
    BuiltIn,

    /// <summary>程序目录 <c>Resources\trust-table.json</c>（随安装包发布，可被安装程序覆盖）。</summary>
    AppDirectory,

    /// <summary>用户层 <c>%ProgramData%\RubbishCleaner\trust-table.json</c>（唯一可编辑的一层）。</summary>
    User,
}

/// <summary>
/// 规则类型在 JSON 里的写法与中文显示名。
/// 编辑器的下拉框、序列化与导入解析共用这一份映射，避免三处各写一份字符串。
/// </summary>
public sealed record TrustRuleTypeOption(TrustRuleType Type, string WireName, string DisplayName)
{
    /// <summary>全部合法类型（顺序与 <see cref="TrustRuleType"/> 声明顺序一致）。</summary>
    public static IReadOnlyList<TrustRuleTypeOption> All { get; } = new List<TrustRuleTypeOption>
    {
        new(TrustRuleType.Signer, "signer", "签名者"),
        new(TrustRuleType.Path, "path", "路径"),
        new(TrustRuleType.Name, "name", "进程名"),
        new(TrustRuleType.NamePattern, "namePattern", "进程名通配"),
        new(TrustRuleType.Sha256, "sha256", "文件哈希"),
    };

    /// <summary>按类型取选项；类型非法时返回一个可显示的兜底项（不抛异常，坏数据不能拖垮 UI）。</summary>
    public static TrustRuleTypeOption From(TrustRuleType type)
    {
        foreach (var option in All)
        {
            if (option.Type == type)
            {
                return option;
            }
        }

        var fallback = type.ToString();
        return new TrustRuleTypeOption(type, fallback, fallback);
    }

    /// <summary>把 JSON 里的 type 文本解析成枚举；未知写法返回 false。</summary>
    public static bool TryParseWireName(string? text, out TrustRuleType type)
    {
        var trimmed = text?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            foreach (var option in All)
            {
                if (string.Equals(option.WireName, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    type = option.Type;
                    return true;
                }
            }
        }

        type = default;
        return false;
    }

    public override string ToString() => DisplayName;
}

/// <summary>
/// 编辑器里的一条规则：规则本体 + 它属于白/黑名单 + 它来自哪一层。
///
/// <para>为什么不给 <see cref="TrustRule"/> 加来源字段：合并后的表是按层追加的，
/// 模型加字段会污染序列化与现有测试；来源在这里按「该规则出现在哪一层」反推即可。</para>
/// </summary>
public sealed record TrustRuleEntry(TrustRule Rule, bool IsBlacklist, TrustRuleLayer Layer)
{
    /// <summary>内置层与程序目录层只读；只有用户层可改可删。</summary>
    public bool IsReadOnly => Layer != TrustRuleLayer.User;

    public TrustRuleType Type => Rule.Type;

    public string Value => Rule.Value;

    public string? Note => Rule.Note;

    /// <summary>列表显示用：白名单 / 黑名单。</summary>
    public string ListText => IsBlacklist ? "黑名单" : "白名单";

    /// <summary>列表显示用：规则类型中文名。</summary>
    public string TypeText => TrustRuleTypeOption.From(Rule.Type).DisplayName;

    /// <summary>列表显示用：来源层中文名。</summary>
    public string LayerText => TrustTableEditor.DescribeLayer(Layer);

    /// <summary>列表显示用：是否可编辑。</summary>
    public string EditText => IsReadOnly ? "只读" : "可编辑";

    public override string ToString() => $"{LayerText}|{ListText}|{TypeText}:{Value}";
}
