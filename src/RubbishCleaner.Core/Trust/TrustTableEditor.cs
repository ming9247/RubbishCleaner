using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Trust;

/// <summary>编辑操作的结果状态。</summary>
public enum TrustEditStatus
{
    /// <summary>成功（已落盘）。</summary>
    Ok,

    /// <summary>输入不合法（类型未知、值为空、路径未以反斜杠结尾等）。</summary>
    Invalid,

    /// <summary>与已有规则重复（消息里会写明冲突所在层）。</summary>
    Duplicate,

    /// <summary>目标是只读层（内置 / 程序目录），不允许改删。</summary>
    ReadOnly,

    /// <summary>用户层里找不到要改删的规则。</summary>
    NotFound,

    /// <summary>写盘失败（磁盘、权限、校验未通过等）。</summary>
    IoError,
}

/// <summary>单条规则编辑的结果。</summary>
public sealed record TrustEditResult(TrustEditStatus Status, string Message, TrustTable? Table = null)
{
    public bool Succeeded => Status == TrustEditStatus.Ok;
}

/// <summary>导入结果：<paramref name="Invalid"/> 与 <paramref name="Skipped"/> 是两个独立计数。</summary>
public sealed record TrustImportResult(
    TrustEditStatus Status,
    string Message,
    int Added,
    int Invalid,
    int Skipped,
    TrustTable? Table = null)
{
    public bool Succeeded => Status == TrustEditStatus.Ok;
}

/// <summary>当前确认表的快照：合并表 + 用户层原表 + 带来源的规则列表。</summary>
public sealed record TrustTableSnapshot(
    TrustTable Merged,
    TrustTable UserLayer,
    IReadOnlyList<TrustRuleEntry> Entries)
{
    public static TrustTableSnapshot Empty { get; } = new(TrustTable.Empty, TrustTable.Empty, []);
}

/// <summary>
/// 特征库编辑器：唯一允许写用户层 <c>%ProgramData%\RubbishCleaner\trust-table.json</c> 的地方。
///
/// <para>职责边界：</para>
/// <list type="bullet">
/// <item>内置层与程序目录层只读，删除/修改只作用于用户层（ADR-008：追加合并不许误删内置白名单）。</item>
/// <item>任何用户层写入都把 <c>version</c> 提到「三层合并 version + 1」，
/// 否则 <see cref="TrustTableUpdater"/> 会因为 <c>remote &lt;= local</c> 而拉不到更新。</item>
/// <item>落盘一律先写 <c>.tmp</c>、回读校验、再 <see cref="File.Move(string, string, bool)"/> 覆盖，
/// 与 <see cref="TrustTableUpdater"/> 保持一致，避免半截文件。</item>
/// <item>解析与序列化都在这里（不碰 UI），UI 只负责选文件，便于单元测试。</item>
/// </list>
/// </summary>
public sealed class TrustTableEditor
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // 中文直接输出，别转成 \uXXXX，用户要能直接读表。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Func<DateTime> _clock;

    /// <param name="userTablePath">用户层路径；默认 <see cref="TrustTableLoader.UserTablePath"/>。</param>
    /// <param name="appTablePath">程序目录层路径；默认 <see cref="TrustTableLoader.DefaultAppTablePath"/>。</param>
    /// <param name="clock">时钟，便于测试固定 updated 日期。</param>
    public TrustTableEditor(string? userTablePath = null, string? appTablePath = null, Func<DateTime>? clock = null)
    {
        UserTablePath = userTablePath ?? TrustTableLoader.UserTablePath;
        AppTablePath = appTablePath ?? TrustTableLoader.DefaultAppTablePath();
        _clock = clock ?? (() => DateTime.Now);
    }

    /// <summary>用户层文件路径（唯一可写的一层）。</summary>
    public string UserTablePath { get; }

    /// <summary>程序目录层文件路径（只读）。</summary>
    public string AppTablePath { get; }

    /// <summary>用户层文件当前是否存在（首次编辑会自动创建）。</summary>
    public bool UserTableExists
    {
        get
        {
            try
            {
                return File.Exists(UserTablePath);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>层的中文名，UI 与错误消息共用。</summary>
    public static string DescribeLayer(TrustRuleLayer layer) => layer switch
    {
        TrustRuleLayer.BuiltIn => "内置层",
        TrustRuleLayer.AppDirectory => "程序目录层",
        _ => "用户层",
    };

    /// <summary>
    /// 校验并规范化一条规则：去空白、类型必须在白名单内、值非空、路径必须以反斜杠结尾。
    /// 返回 false 时 <paramref name="error"/> 是可以直接显示给用户的中文原因。
    /// </summary>
    public static bool TryNormalize(TrustRule? rule, out TrustRule normalized, out string error)
    {
        normalized = rule!;
        if (rule is null)
        {
            error = "规则不能为空。";
            return false;
        }

        if (!Enum.IsDefined(rule.Type))
        {
            error = $"未知的规则类型「{rule.Type}」。";
            return false;
        }

        var value = rule.Value?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            error = "规则值不能为空。";
            return false;
        }

        if (rule.Type == TrustRuleType.Path && !value.EndsWith('\\'))
        {
            error = "路径类型规则的 value 必须以反斜杠（\\）结尾。";
            return false;
        }

        var note = string.IsNullOrWhiteSpace(rule.Note) ? null : rule.Note.Trim();
        normalized = new TrustRule(rule.Type, value, note);
        error = string.Empty;
        return true;
    }

    /// <summary>读取当前三层状态（不写盘）。</summary>
    public TrustTableSnapshot Snapshot()
    {
        var (builtIn, app, user) = LoadLayers();
        var merged = TrustTableLoader.Load(AppTablePath, UserTablePath);
        return new TrustTableSnapshot(merged, user, BuildEntries(builtIn, app, user, merged));
    }

    /// <summary>新增一条规则到用户层（白名单或黑名单）。</summary>
    public TrustEditResult Add(TrustRule rule, bool isBlacklist)
    {
        if (!TryNormalize(rule, out var normalized, out var error))
        {
            return new TrustEditResult(TrustEditStatus.Invalid, error);
        }

        var (builtIn, app, user) = LoadLayers();
        var merged = TrustTableLoader.Load(AppTablePath, UserTablePath);

        var conflict = FindLayer(normalized, isBlacklist, builtIn, app, user);
        if (conflict is not null)
        {
            return new TrustEditResult(
                TrustEditStatus.Duplicate,
                $"该规则已存在于{DescribeLayer(conflict.Value)}：{normalized}，未做修改。");
        }

        var whitelist = user.Whitelist.ToList();
        var blacklist = user.Blacklist.ToList();
        (isBlacklist ? blacklist : whitelist).Add(normalized);

        var saved = SaveUserLayer(whitelist, blacklist, merged);
        return saved.Succeeded
            ? saved with { Message = $"已添加规则：{normalized}（用户层版本 v{saved.Table!.Version}）。" }
            : saved;
    }

    /// <summary>
    /// 修改用户层里的一条规则（可改值、备注，也可在白/黑名单之间移动）。
    /// 只读层的规则会被拒绝并说明原因。
    /// </summary>
    public TrustEditResult Update(
        TrustRule original,
        bool originalIsBlacklist,
        TrustRule updated,
        bool updatedIsBlacklist)
    {
        if (original is null)
        {
            return new TrustEditResult(TrustEditStatus.Invalid, "规则不能为空。");
        }

        var (builtIn, app, user) = LoadLayers();
        var merged = TrustTableLoader.Load(AppTablePath, UserTablePath);

        var userList = ListOf(user, originalIsBlacklist);
        var index = IndexOfKey(userList, original);
        if (index < 0)
        {
            return NotEditable(original, originalIsBlacklist, builtIn, app, user);
        }

        if (!TryNormalize(updated, out var normalized, out var error))
        {
            return new TrustEditResult(TrustEditStatus.Invalid, error);
        }

        var conflict = FindLayer(normalized, updatedIsBlacklist, builtIn, app, user, skipInUserLayer: original);
        if (conflict is not null)
        {
            return new TrustEditResult(
                TrustEditStatus.Duplicate,
                $"该规则已存在于{DescribeLayer(conflict.Value)}：{normalized}，未做修改。");
        }

        var whitelist = user.Whitelist.ToList();
        var blacklist = user.Blacklist.ToList();
        var fromList = originalIsBlacklist ? blacklist : whitelist;
        fromList.RemoveAt(index);
        (updatedIsBlacklist ? blacklist : whitelist).Add(normalized);

        var saved = SaveUserLayer(whitelist, blacklist, merged);
        return saved.Succeeded
            ? saved with { Message = $"已更新规则：{original} → {normalized}（用户层版本 v{saved.Table!.Version}）。" }
            : saved;
    }

    /// <summary>删除用户层里的一条规则（允许删到空表；只读层拒绝并说明原因）。</summary>
    public TrustEditResult Delete(TrustRule rule, bool isBlacklist)
    {
        if (rule is null)
        {
            return new TrustEditResult(TrustEditStatus.Invalid, "规则不能为空。");
        }

        var (builtIn, app, user) = LoadLayers();
        var merged = TrustTableLoader.Load(AppTablePath, UserTablePath);

        var userList = ListOf(user, isBlacklist);
        var index = IndexOfKey(userList, rule);
        if (index < 0)
        {
            return NotEditable(rule, isBlacklist, builtIn, app, user);
        }

        var whitelist = user.Whitelist.ToList();
        var blacklist = user.Blacklist.ToList();
        (isBlacklist ? blacklist : whitelist).RemoveAt(index);

        var saved = SaveUserLayer(whitelist, blacklist, merged);
        return saved.Succeeded
            ? saved with { Message = $"已删除规则：{rule}（用户层版本 v{saved.Table!.Version}）。" }
            : saved;
    }

    /// <summary>
    /// 导入外部 JSON：只把用户层还没有的规则追加进用户层。
    ///
    /// <para>硬约束：如果导入的是「合并表」导出文件，内置层与程序目录层的规则已经在合并表里，
    /// 会被当成重复跳过，<b>绝不会被复制进用户层</b>（否则用户层会越积越大且无法删除）。</para>
    /// </summary>
    public TrustImportResult Import(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new TrustImportResult(TrustEditStatus.Invalid, "导入失败：文件内容为空。", 0, 0, 0);
        }

        ImportDocumentDto? document;
        try
        {
            document = JsonSerializer.Deserialize<ImportDocumentDto>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            return new TrustImportResult(
                TrustEditStatus.Invalid,
                $"导入失败：文件不是合法的 JSON（{ex.Message}）。",
                0,
                0,
                0);
        }

        if (document is null)
        {
            return new TrustImportResult(TrustEditStatus.Invalid, "导入失败：文件内容为空。", 0, 0, 0);
        }

        var (builtIn, app, user) = LoadLayers();
        var merged = TrustTableLoader.Load(AppTablePath, UserTablePath);

        var whitelist = user.Whitelist.ToList();
        var blacklist = user.Blacklist.ToList();
        var candidateUser = new TrustTable { Whitelist = whitelist, Blacklist = blacklist };

        var added = 0;
        var invalid = 0;
        var skipped = 0;
        var problems = new List<string>();

        void Take(List<ImportRuleDto>? dtos, bool isBlacklist, string listName)
        {
            if (dtos is null)
            {
                return;
            }

            foreach (var dto in dtos)
            {
                if (dto is null)
                {
                    invalid++;
                    problems.Add($"{listName}：存在空规则。");
                    continue;
                }

                if (!TrustRuleTypeOption.TryParseWireName(dto.Type, out var type))
                {
                    invalid++;
                    problems.Add($"{listName}：未知的规则类型「{dto.Type}」。");
                    continue;
                }

                // 先查重、后校验：合并表导出文件里带着内置层/程序目录层的规则，它们本来就在表里，
                // 直接算「跳过重复」即可。否则内置表里 %SystemRoot%\explorer.exe 这类不带反斜杠的
                // 路径规则会被报成「无效」，用户会以为导出文件坏了。
                var candidate = new TrustRule(type, (dto.Value ?? string.Empty).Trim(), null);
                if (FindLayer(candidate, isBlacklist, builtIn, app, candidateUser) is not null)
                {
                    skipped++;
                    continue;
                }

                if (!TryNormalize(new TrustRule(type, dto.Value ?? string.Empty, dto.Note), out var rule, out var error))
                {
                    invalid++;
                    problems.Add($"{listName}：{error}");
                    continue;
                }

                (isBlacklist ? blacklist : whitelist).Add(rule);
                added++;
            }
        }

        Take(document.Whitelist, false, "白名单");
        Take(document.Blacklist, true, "黑名单");

        var summary = $"新增 {added} 条，跳过重复 {skipped} 条，无效 {invalid} 条。";
        if (problems.Count > 0)
        {
            var detail = string.Join(" ", problems.Take(3));
            summary += problems.Count > 3 ? $" 无效明细：{detail} …" : $" 无效明细：{detail}";
        }

        if (added == 0)
        {
            var status = invalid > 0 ? TrustEditStatus.Invalid : TrustEditStatus.Ok;
            var prefix = invalid > 0 ? "导入未完成：" : "导入完成：";
            return new TrustImportResult(status, prefix + summary, added, invalid, skipped);
        }

        var saved = SaveUserLayer(whitelist, blacklist, merged);
        if (!saved.Succeeded)
        {
            return new TrustImportResult(saved.Status, saved.Message, added, invalid, skipped);
        }

        return new TrustImportResult(
            TrustEditStatus.Ok,
            $"导入完成：{summary} 用户层版本 v{saved.Table!.Version}。",
            added,
            invalid,
            skipped,
            saved.Table);
    }

    /// <summary>导出用户层（默认导出，可与导入形成闭环）。</summary>
    public string ExportUserLayer()
    {
        var user = TrustTableLoader.TryLoadFile(UserTablePath) ?? TrustTable.Empty;
        return Serialize(user);
    }

    /// <summary>导出三层合并后的全量规则（文件名/标题必须写明是「合并表」，导入时只取用户层没有的部分）。</summary>
    public string ExportMergedTable() => Serialize(TrustTableLoader.Load(AppTablePath, UserTablePath));

    private TrustEditResult NotEditable(
        TrustRule rule,
        bool isBlacklist,
        TrustTable builtIn,
        TrustTable? app,
        TrustTable user)
    {
        var layer = FindLayer(rule, isBlacklist, builtIn, app, user);
        return layer is null
            ? new TrustEditResult(TrustEditStatus.NotFound, $"用户层中找不到该规则：{rule}。")
            : new TrustEditResult(
                TrustEditStatus.ReadOnly,
                $"{DescribeLayer(layer.Value)}的规则不可修改或删除，只能编辑用户层规则：{rule}。");
    }

    private TrustEditResult SaveUserLayer(
        IReadOnlyList<TrustRule> whitelist,
        IReadOnlyList<TrustRule> blacklist,
        TrustTable merged)
    {
        // version 必须严格大于三层合并后的当前值，否则 TrustTableUpdater 判定「没有更新」。
        var table = new TrustTable
        {
            Version = merged.Version + 1,
            Updated = _clock().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Whitelist = whitelist,
            Blacklist = blacklist,
        };

        var json = Serialize(table);
        var target = UserTablePath;
        var staging = target + ".tmp";

        try
        {
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(staging, json);

            // 回读校验：解析失败会被 TryLoadFile 兜成空表，规则数对不上即判定写入不可信。
            var verify = TrustTableLoader.TryLoadFile(staging);
            if (verify is null
                || verify.Whitelist.Count != whitelist.Count
                || verify.Blacklist.Count != blacklist.Count)
            {
                return new TrustEditResult(TrustEditStatus.IoError, "保存失败：写入校验未通过，已放弃本次修改。");
            }

            File.Move(staging, target, overwrite: true);
        }
        catch (Exception ex)
        {
            return new TrustEditResult(TrustEditStatus.IoError, $"保存失败：{ex.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(staging))
                {
                    File.Delete(staging);
                }
            }
            catch
            {
                // 临时文件清理失败不影响结果。
            }
        }

        return new TrustEditResult(
            TrustEditStatus.Ok,
            string.Empty,
            TrustTableLoader.Load(AppTablePath, UserTablePath));
    }

    private (TrustTable BuiltIn, TrustTable? App, TrustTable User) LoadLayers()
    {
        var builtIn = BuiltInTrustTable.Create();
        var app = TrustTableLoader.TryLoadFile(AppTablePath);
        var user = TrustTableLoader.TryLoadFile(UserTablePath) ?? TrustTable.Empty;
        return (builtIn, app, user);
    }

    private static IReadOnlyList<TrustRule> ListOf(TrustTable table, bool isBlacklist)
        => isBlacklist ? table.Blacklist : table.Whitelist;

    private static bool SameKey(TrustRule left, TrustRule right)
        => left.Type == right.Type
           && string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);

    private static int IndexOfKey(IReadOnlyList<TrustRule> rules, TrustRule rule)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            if (SameKey(rules[i], rule))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 按「内置 → 程序目录 → 用户层」的顺序找这条规则属于哪一层。
    /// 顺序必须和 <see cref="TrustTableLoader.Merge"/> 的追加顺序一致：
    /// 同一条规则出现在多层时，合并表里保留的是靠前那层的实例。
    /// </summary>
    private static TrustRuleLayer? FindLayer(
        TrustRule rule,
        bool isBlacklist,
        TrustTable builtIn,
        TrustTable? app,
        TrustTable user,
        TrustRule? skipInUserLayer = null)
    {
        if (IndexOfKey(ListOf(builtIn, isBlacklist), rule) >= 0)
        {
            return TrustRuleLayer.BuiltIn;
        }

        if (app is not null && IndexOfKey(ListOf(app, isBlacklist), rule) >= 0)
        {
            return TrustRuleLayer.AppDirectory;
        }

        var userList = ListOf(user, isBlacklist);
        for (var i = 0; i < userList.Count; i++)
        {
            if (skipInUserLayer is not null && SameKey(userList[i], skipInUserLayer))
            {
                continue; // 正在被替换的那条不算重复。
            }

            if (SameKey(userList[i], rule))
            {
                return TrustRuleLayer.User;
            }
        }

        return null;
    }

    private static IReadOnlyList<TrustRuleEntry> BuildEntries(
        TrustTable builtIn,
        TrustTable? app,
        TrustTable user,
        TrustTable merged)
    {
        var entries = new List<TrustRuleEntry>(merged.Whitelist.Count + merged.Blacklist.Count);

        foreach (var rule in merged.Whitelist)
        {
            entries.Add(new TrustRuleEntry(rule, false, ResolveLayer(rule, false, builtIn, app, user)));
        }

        foreach (var rule in merged.Blacklist)
        {
            entries.Add(new TrustRuleEntry(rule, true, ResolveLayer(rule, true, builtIn, app, user)));
        }

        return entries;
    }

    private static TrustRuleLayer ResolveLayer(
        TrustRule rule,
        bool isBlacklist,
        TrustTable builtIn,
        TrustTable? app,
        TrustTable user)
        => FindLayer(rule, isBlacklist, builtIn, app, user) ?? TrustRuleLayer.User;

    private static string Serialize(TrustTable table)
    {
        var document = new ImportDocumentDto
        {
            Version = table.Version,
            Updated = table.Updated,
            Whitelist = table.Whitelist.Select(ToDto).ToList(),
            Blacklist = table.Blacklist.Select(ToDto).ToList(),
        };

        return JsonSerializer.Serialize(document, WriteOptions);
    }

    private static ImportRuleDto ToDto(TrustRule rule) => new()
    {
        Type = TrustRuleTypeOption.From(rule.Type).WireName,
        Value = rule.Value,
        Note = rule.Note,
    };

    private sealed class ImportDocumentDto
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("updated")]
        public string? Updated { get; set; }

        [JsonPropertyName("whitelist")]
        public List<ImportRuleDto>? Whitelist { get; set; }

        [JsonPropertyName("blacklist")]
        public List<ImportRuleDto>? Blacklist { get; set; }
    }

    private sealed class ImportRuleDto
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;

        [JsonPropertyName("note")]
        public string? Note { get; set; }
    }
}
