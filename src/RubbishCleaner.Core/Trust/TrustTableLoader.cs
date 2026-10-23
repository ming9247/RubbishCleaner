using System.Text.Json;
using System.Text.Json.Serialization;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Trust;

/// <summary>
/// 确认表加载器。
/// 加载顺序：内置表 → 程序目录 <c>Resources\trust-table.json</c> → 用户表 <c>%ProgramData%\RubbishCleaner\trust-table.json</c>，
/// 后加载的表<b>追加</b>到已有规则之上（而不是替换），避免用户误删内置白名单导致误报（见 ADR-008）。
/// </summary>
public static class TrustTableLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static string UserTablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RubbishCleaner",
        "trust-table.json");

    /// <summary>从字符串加载（便于单元测试）。</summary>
    public static TrustTable Parse(string json, string source = "custom")
    {
        try
        {
            var dto = JsonSerializer.Deserialize<TrustTableDto>(json, JsonOptions);
            if (dto is null)
            {
                return TrustTable.Empty;
            }

            return new TrustTable
            {
                Version = dto.Version <= 0 ? 1 : dto.Version,
                Updated = dto.Updated,
                Source = source,
                Whitelist = Convert(dto.Whitelist),
                Blacklist = Convert(dto.Blacklist),
            };
        }
        catch (JsonException)
        {
            // 表文件损坏时退回空表，绝不能因此让扫描整体失败。
            return TrustTable.Empty;
        }
    }

    /// <summary>从文件加载；文件不存在返回 null。</summary>
    public static TrustTable? TryLoadFile(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path), path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 按优先级组装最终确认表。任一层缺失都会被跳过。
    /// </summary>
    public static TrustTable Load(string? appDirectoryTablePath = null, string? userTablePath = null)
    {
        var tables = new List<TrustTable> { BuiltInTrustTable.Create() };

        var appTable = TryLoadFile(appDirectoryTablePath ?? DefaultAppTablePath());
        if (appTable is not null)
        {
            tables.Add(appTable);
        }

        var userTable = TryLoadFile(userTablePath ?? UserTablePath);
        if (userTable is not null)
        {
            tables.Add(userTable);
        }

        return Merge(tables);
    }

    public static string DefaultAppTablePath()
        => Path.Combine(AppContext.BaseDirectory, "Resources", "trust-table.json");

    /// <summary>追加合并：白名单与黑名单分别去重拼接。</summary>
    public static TrustTable Merge(IEnumerable<TrustTable> tables)
    {
        var whitelist = new List<TrustRule>();
        var blacklist = new List<TrustRule>();
        var sources = new List<string>();
        var version = 1;
        string? updated = null;

        foreach (var table in tables)
        {
            sources.Add(table.Source);
            version = Math.Max(version, table.Version);
            updated = table.Updated ?? updated;

            foreach (var rule in table.Whitelist)
            {
                if (!whitelist.Any(r => r.Type == rule.Type
                                        && string.Equals(r.Value, rule.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    whitelist.Add(rule);
                }
            }

            foreach (var rule in table.Blacklist)
            {
                if (!blacklist.Any(r => r.Type == rule.Type
                                        && string.Equals(r.Value, rule.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    blacklist.Add(rule);
                }
            }
        }

        return new TrustTable
        {
            Version = version,
            Updated = updated,
            Source = string.Join(" + ", sources),
            Whitelist = whitelist,
            Blacklist = blacklist,
        };
    }

    private static IReadOnlyList<TrustRule> Convert(List<TrustRuleDto>? dtos)
    {
        if (dtos is null)
        {
            return [];
        }

        var rules = new List<TrustRule>(dtos.Count);
        foreach (var dto in dtos)
        {
            if (string.IsNullOrWhiteSpace(dto.Value))
            {
                continue;
            }

            if (!Enum.TryParse<TrustRuleType>(dto.Type, ignoreCase: true, out var type))
            {
                continue; // 未知类型跳过，保证向前兼容
            }

            rules.Add(new TrustRule(type, dto.Value.Trim(), dto.Note));
        }

        return rules;
    }

    private sealed class TrustTableDto
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("updated")]
        public string? Updated { get; set; }

        [JsonPropertyName("whitelist")]
        public List<TrustRuleDto>? Whitelist { get; set; }

        [JsonPropertyName("blacklist")]
        public List<TrustRuleDto>? Blacklist { get; set; }
    }

    private sealed class TrustRuleDto
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;

        [JsonPropertyName("note")]
        public string? Note { get; set; }
    }
}
