using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Trust;

/// <summary>
/// 编译进程序的内置确认表，作为最后兜底：即使外部 JSON 丢失也不会失去白名单保护。
/// 合规说明：黑名单只收录公开可查的 PUP/PUA 特征（进程名模式、路径特征），
/// 不做主观定性，用户可在 %ProgramData%\RubbishCleaner\trust-table.json 中自行增删。
/// </summary>
public static class BuiltInTrustTable
{
    public static TrustTable Create() => new()
    {
        Version = 1,
        Updated = "2026-10-23",
        Source = "builtin",
        Whitelist =
        [
            new TrustRule(TrustRuleType.Signer, "Microsoft Corporation", "微软签名"),
            new TrustRule(TrustRuleType.Signer, "Microsoft Windows", "Windows 组件"),
            new TrustRule(TrustRuleType.Path, @"%SystemRoot%\System32\", "系统目录"),
            new TrustRule(TrustRuleType.Path, @"%SystemRoot%\SysWOW64\", "系统目录（32 位）"),
            new TrustRule(TrustRuleType.Path, @"%SystemRoot%\explorer.exe", "资源管理器"),
            new TrustRule(TrustRuleType.Name, "ctfmon", "输入法框架"),
            new TrustRule(TrustRuleType.Name, "explorer", "资源管理器"),
            new TrustRule(TrustRuleType.Name, "SecurityHealthSystray", "Windows 安全中心"),
        ],
        Blacklist =
        [
            // 示例规模的公开 PUP 特征，v0.2 起由在线特征库更新
            new TrustRule(TrustRuleType.NamePattern, "*hao123*", "捆绑导航"),
            new TrustRule(TrustRuleType.NamePattern, "*2345*", "捆绑推广"),
            new TrustRule(TrustRuleType.NamePattern, "*sogou*push*", "推送组件"),
            new TrustRule(TrustRuleType.NamePattern, "*.流氓*", "名称含明显恶意字样"),
            new TrustRule(TrustRuleType.NamePattern, "svch0st*", "伪装系统进程"),
            new TrustRule(TrustRuleType.NamePattern, "scvhost*", "伪装系统进程"),
        ],
    };
}
