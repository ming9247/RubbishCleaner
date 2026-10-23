namespace RubbishCleaner.Core.Detection;

/// <summary>生成稳定的启动项标识：同一点位在多次扫描间 Id 不变，便于用户反馈与去重。</summary>
public static class EntryId
{
    public static string Create(Models.StartupSourceKind kind, string location, string name)
    {
        var raw = $"{kind}|{location}|{name}".ToLowerInvariant();
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16];
    }
}
