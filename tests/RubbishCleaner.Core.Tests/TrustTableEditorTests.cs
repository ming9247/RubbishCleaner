using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Trust;
using Xunit;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// TrustTableEditor 单元测试(v0.9)：应用内特征库离线编辑。
/// 覆盖：新增/修改/删除、重复(写明冲突层)、非法输入、version 提升、
/// 导入导出往返、合并表重导入不把内置层与程序目录层复制进用户层、
/// 删到空表合法、用户层缺失视为空表、updated 取注入时钟。
/// </summary>
public class TrustTableEditorTests : IDisposable
{
    private static readonly DateTime FixedNow = new(2026, 10, 27, 9, 30, 0, DateTimeKind.Local);

    private readonly string _root;
    private readonly string _userPath;
    private readonly string _appPath;

    public TrustTableEditorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rc-trust-editor-" + Guid.NewGuid().ToString("N"));
        // 故意放在还不存在的子目录里：首次编辑必须自己建目录。
        _userPath = Path.Combine(_root, "user", "trust-table.json");
        _appPath = Path.Combine(_root, "app", "trust-table.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    // ───────── 新增 ─────────

    [Fact]
    public void Add_WhenUserTableMissing_CreatesFileAndDirectory()
    {
        var editor = CreateEditor();
        Assert.False(editor.UserTableExists);

        var result = editor.Add(new TrustRule(TrustRuleType.Name, "FreshProcess"), isBlacklist: false);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(_userPath));
        Assert.True(editor.UserTableExists);

        var user = TrustTableLoader.TryLoadFile(_userPath);
        Assert.NotNull(user);
        Assert.Contains(user!.Whitelist, r => r.Value == "FreshProcess");
        Assert.Empty(user.Blacklist);
    }

    [Fact]
    public void Add_WhenUserTableMissing_UserLayerCountsAsEmpty()
    {
        var snapshot = CreateEditor().Snapshot();

        Assert.Empty(snapshot.UserLayer.Whitelist);
        Assert.Empty(snapshot.UserLayer.Blacklist);
        Assert.DoesNotContain(snapshot.Entries, e => e.Layer == TrustRuleLayer.User);
        Assert.NotEmpty(snapshot.Entries);
    }

    [Fact]
    public void Add_BlacklistRule_GoesToBlacklistList()
    {
        var editor = CreateEditor();

        var result = editor.Add(new TrustRule(TrustRuleType.NamePattern, "*bad*"), isBlacklist: true);

        Assert.True(result.Succeeded);
        var user = TrustTableLoader.TryLoadFile(_userPath)!;
        Assert.Empty(user.Whitelist);
        Assert.Single(user.Blacklist);
        Assert.Equal(TrustRuleType.NamePattern, user.Blacklist[0].Type);
    }

    [Fact]
    public void Add_KeepsExistingUserRules()
    {
        WriteUserTable(
            2,
            """{ "type": "name", "value": "KeepA" }, { "type": "signer", "value": "KeepB" }""",
            null);
        var editor = CreateEditor();

        var result = editor.Add(new TrustRule(TrustRuleType.Name, "Added"), isBlacklist: false);

        Assert.True(result.Succeeded);
        var user = TrustTableLoader.TryLoadFile(_userPath)!;
        Assert.Equal(3, user.Whitelist.Count);
        Assert.Contains(user.Whitelist, r => r.Value == "KeepA");
        Assert.Contains(user.Whitelist, r => r.Value == "KeepB");
        Assert.Contains(user.Whitelist, r => r.Value == "Added");
    }

    [Fact]
    public void Add_TrimsValueAndNote()
    {
        var editor = CreateEditor();

        var result = editor.Add(new TrustRule(TrustRuleType.Name, "  Padded  ", "  备注  "), isBlacklist: false);

        Assert.True(result.Succeeded);
        var user = TrustTableLoader.TryLoadFile(_userPath)!;
        Assert.Equal("Padded", user.Whitelist[0].Value);
        Assert.Equal("备注", user.Whitelist[0].Note);
    }

    // ───────── 重复：拒绝并写明冲突层 ─────────

    [Fact]
    public void Add_DuplicateOfBuiltInRule_IsRejectedAndNamesBuiltInLayer()
    {
        var builtInRule = BuiltInTrustTable.Create().Whitelist[0];
        var editor = CreateEditor();

        var result = editor.Add(builtInRule, isBlacklist: false);

        Assert.Equal(TrustEditStatus.Duplicate, result.Status);
        Assert.False(result.Succeeded);
        Assert.Contains("内置层", result.Message);
        Assert.False(File.Exists(_userPath));
    }

    [Fact]
    public void Add_DuplicateOfAppDirectoryRule_IsRejectedAndNamesAppLayer()
    {
        WriteAppTable(3, """{ "type": "name", "value": "AppOnlyProcess" }""");
        var editor = CreateEditor();

        var result = editor.Add(new TrustRule(TrustRuleType.Name, "AppOnlyProcess"), isBlacklist: false);

        Assert.Equal(TrustEditStatus.Duplicate, result.Status);
        Assert.Contains("程序目录层", result.Message);
        Assert.False(File.Exists(_userPath));
    }

    [Fact]
    public void Add_DuplicateOfUserLayerRule_IsRejectedAndNamesUserLayer()
    {
        var editor = CreateEditor();
        Assert.True(editor.Add(new TrustRule(TrustRuleType.Name, "Twice"), isBlacklist: false).Succeeded);

        var result = editor.Add(new TrustRule(TrustRuleType.Name, "Twice"), isBlacklist: false);

        Assert.Equal(TrustEditStatus.Duplicate, result.Status);
        Assert.Contains("用户层", result.Message);
        Assert.Single(TrustTableLoader.TryLoadFile(_userPath)!.Whitelist);
    }

    [Fact]
    public void Add_DuplicateIgnoringCase_IsRejected()
    {
        var editor = CreateEditor();
        Assert.True(editor.Add(new TrustRule(TrustRuleType.Name, "CaseTest"), isBlacklist: false).Succeeded);

        var result = editor.Add(new TrustRule(TrustRuleType.Name, "casetest"), isBlacklist: false);

        Assert.Equal(TrustEditStatus.Duplicate, result.Status);
    }

    // ───────── 非法输入 ─────────

    [Fact]
    public void Add_EmptyValue_IsInvalid()
    {
        var result = CreateEditor().Add(new TrustRule(TrustRuleType.Name, "   "), isBlacklist: false);

        Assert.Equal(TrustEditStatus.Invalid, result.Status);
        Assert.Contains("不能为空", result.Message);
        Assert.False(File.Exists(_userPath));
    }

    [Fact]
    public void Add_PathWithoutTrailingBackslash_IsInvalid()
    {
        var result = CreateEditor().Add(new TrustRule(TrustRuleType.Path, @"C:\Tools\Thing"), isBlacklist: false);

        Assert.Equal(TrustEditStatus.Invalid, result.Status);
        Assert.Contains("反斜杠", result.Message);
    }

    [Fact]
    public void Add_PathWithTrailingBackslash_IsAccepted()
    {
        var result = CreateEditor().Add(new TrustRule(TrustRuleType.Path, @"C:\Tools\Thing\"), isBlacklist: false);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Add_UndefinedRuleType_IsInvalid()
    {
        var result = CreateEditor().Add(new TrustRule((TrustRuleType)99, "x"), isBlacklist: false);

        Assert.Equal(TrustEditStatus.Invalid, result.Status);
        Assert.Contains("未知的规则类型", result.Message);
    }

    [Fact]
    public void TryNormalize_NullRule_ReturnsFalse()
    {
        var ok = TrustTableEditor.TryNormalize(null, out _, out var error);

        Assert.False(ok);
        Assert.Contains("不能为空", error);
    }

    // ───────── version 提升 ─────────

    [Fact]
    public void Add_BumpsVersionAboveMergedVersion()
    {
        WriteUserTable(4, """{ "type": "name", "value": "Existing" }""", null);
        var editor = CreateEditor();
        var before = TrustTableLoader.Load(_appPath, _userPath).Version;

        var result = editor.Add(new TrustRule(TrustRuleType.Name, "NewOne"), isBlacklist: false);

        Assert.True(result.Succeeded);
        Assert.True(result.Table!.Version > before);
        Assert.Equal(before + 1, result.Table.Version);
    }

    [Fact]
    public void Add_UsesMergedVersionNotUserVersion()
    {
        // 用户层落后于程序目录层：必须按三层合并后的版本 +1，否则客户端判定没有更新。
        WriteUserTable(2, """{ "type": "name", "value": "StaleUser" }""", null);
        WriteAppTable(9, """{ "type": "name", "value": "AppOnlyProcess" }""");
        var editor = CreateEditor();

        var result = editor.Add(new TrustRule(TrustRuleType.Name, "Bumped"), isBlacklist: false);

        Assert.True(result.Succeeded);
        Assert.Equal(10, result.Table!.Version);
        Assert.Equal(10, TrustTableLoader.TryLoadFile(_userPath)!.Version);
    }

    [Fact]
    public void Add_WritesUpdatedWithInjectedClock()
    {
        var result = CreateEditor().Add(new TrustRule(TrustRuleType.Name, "Dated"), isBlacklist: false);

        Assert.True(result.Succeeded);
        Assert.Equal("2026-10-27", TrustTableLoader.TryLoadFile(_userPath)!.Updated);
    }

    [Fact]
    public void Add_LeavesNoStagingFile()
    {
        var editor = CreateEditor();

        Assert.True(editor.Add(new TrustRule(TrustRuleType.Name, "Atomic"), isBlacklist: false).Succeeded);

        Assert.False(File.Exists(_userPath + ".tmp"));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(_userPath)!));
    }

    // ───────── 修改 ─────────

    [Fact]
    public void Update_UserLayerRule_ReplacesValueAndKeepsOthers()
    {
        WriteUserTable(2, """{ "type": "name", "value": "OldName" }""", """{ "type": "name", "value": "OtherName" }""");
        var editor = CreateEditor();

        var result = editor.Update(
            new TrustRule(TrustRuleType.Name, "OldName"),
            originalIsBlacklist: false,
            new TrustRule(TrustRuleType.Name, "NewName", "改过"),
            updatedIsBlacklist: false);

        Assert.True(result.Succeeded);
        var user = TrustTableLoader.TryLoadFile(_userPath)!;
        Assert.Contains(user.Whitelist, r => r.Value == "NewName" && r.Note == "改过");
        Assert.DoesNotContain(user.Whitelist, r => r.Value == "OldName");
        Assert.Contains(user.Blacklist, r => r.Value == "OtherName");
    }

    [Fact]
    public void Update_UserLayerRuleIntoAnotherUserRule_IsRejectedAsDuplicate()
    {
        WriteUserTable(
            2,
            """{ "type": "name", "value": "First" }, { "type": "name", "value": "Second" }""",
            null);
        var editor = CreateEditor();

        var result = editor.Update(
            new TrustRule(TrustRuleType.Name, "First"),
            originalIsBlacklist: false,
            new TrustRule(TrustRuleType.Name, "Second"),
            updatedIsBlacklist: false);

        Assert.Equal(TrustEditStatus.Duplicate, result.Status);
        Assert.Contains("用户层", result.Message);
        var user = TrustTableLoader.TryLoadFile(_userPath)!;
        Assert.Contains(user.Whitelist, r => r.Value == "First");
    }

    [Fact]
    public void Update_KeepingOwnValue_IsAllowed()
    {
        WriteUserTable(2, """{ "type": "name", "value": "SameValue" }""", null);
        var editor = CreateEditor();

        var result = editor.Update(
            new TrustRule(TrustRuleType.Name, "SameValue"),
            originalIsBlacklist: false,
            new TrustRule(TrustRuleType.Name, "SameValue", "只改备注"),
            updatedIsBlacklist: false);

        Assert.True(result.Succeeded);
        Assert.Equal("只改备注", TrustTableLoader.TryLoadFile(_userPath)!.Whitelist[0].Note);
    }

    [Fact]
    public void Update_BuiltInRule_IsRejectedAsReadOnly()
    {
        var builtInRule = BuiltInTrustTable.Create().Whitelist[0];
        var editor = CreateEditor();

        var result = editor.Update(
            builtInRule,
            originalIsBlacklist: false,
            new TrustRule(builtInRule.Type, "Hijacked"),
            updatedIsBlacklist: false);

        Assert.Equal(TrustEditStatus.ReadOnly, result.Status);
        Assert.Contains("内置层", result.Message);
        Assert.False(File.Exists(_userPath));
    }

    // ───────── 删除 ─────────

    [Fact]
    public void Delete_UserLayerRule_RemovesIt()
    {
        WriteUserTable(2, """{ "type": "name", "value": "RemoveMe" }""", null);
        var editor = CreateEditor();

        var result = editor.Delete(new TrustRule(TrustRuleType.Name, "RemoveMe"), isBlacklist: false);

        Assert.True(result.Succeeded);
        Assert.Empty(TrustTableLoader.TryLoadFile(_userPath)!.Whitelist);
    }

    [Fact]
    public void Delete_LastUserRule_IsAllowedAndLeavesEmptyTable()
    {
        var editor = CreateEditor();
        Assert.True(editor.Add(new TrustRule(TrustRuleType.Name, "OnlyOne"), isBlacklist: false).Succeeded);

        var result = editor.Delete(new TrustRule(TrustRuleType.Name, "OnlyOne"), isBlacklist: false);

        Assert.True(result.Succeeded);
        var user = TrustTableLoader.TryLoadFile(_userPath)!;
        Assert.Empty(user.Whitelist);
        Assert.Empty(user.Blacklist);
        // 空用户层是合法状态，文件仍然存在（而不是被删掉）。
        Assert.True(File.Exists(_userPath));
        Assert.True(editor.UserTableExists);
    }

    [Fact]
    public void Delete_BuiltInRule_IsRejectedAsReadOnly()
    {
        var builtInRule = BuiltInTrustTable.Create().Blacklist[0];
        var editor = CreateEditor();

        var result = editor.Delete(builtInRule, isBlacklist: true);

        Assert.Equal(TrustEditStatus.ReadOnly, result.Status);
        Assert.Contains("内置层", result.Message);
        Assert.False(File.Exists(_userPath));
    }

    [Fact]
    public void Delete_AppDirectoryRule_IsRejectedAsReadOnly()
    {
        WriteAppTable(3, null, """{ "type": "namePattern", "value": "*apponly*" }""");
        var editor = CreateEditor();

        var result = editor.Delete(new TrustRule(TrustRuleType.NamePattern, "*apponly*"), isBlacklist: true);

        Assert.Equal(TrustEditStatus.ReadOnly, result.Status);
        Assert.Contains("程序目录层", result.Message);
    }

    [Fact]
    public void Delete_UnknownRule_ReturnsNotFound()
    {
        var result = CreateEditor().Delete(new TrustRule(TrustRuleType.Name, "NeverExisted"), isBlacklist: false);

        Assert.Equal(TrustEditStatus.NotFound, result.Status);
    }

    // ───────── 快照与来源层 ─────────

    [Fact]
    public void Snapshot_MarksBuiltInReadOnlyAndUserEditable()
    {
        var editor = CreateEditor();
        Assert.True(editor.Add(new TrustRule(TrustRuleType.Name, "UserOwned"), isBlacklist: false).Succeeded);

        var snapshot = editor.Snapshot();

        var userEntry = snapshot.Entries.Single(e => e.Rule.Value == "UserOwned");
        Assert.Equal(TrustRuleLayer.User, userEntry.Layer);
        Assert.False(userEntry.IsReadOnly);

        var builtInRule = BuiltInTrustTable.Create().Whitelist[0];
        var builtInEntry = snapshot.Entries.Single(e => e.Rule.Type == builtInRule.Type && e.Rule.Value == builtInRule.Value);
        Assert.Equal(TrustRuleLayer.BuiltIn, builtInEntry.Layer);
        Assert.True(builtInEntry.IsReadOnly);
    }

    // ───────── 导入 ─────────

    [Fact]
    public void Import_CountsAddedSkippedAndInvalidSeparately()
    {
        var duplicate = BuiltInTrustTable.Create().Whitelist[0];
        var duplicateType = TrustRuleTypeOption.From(duplicate.Type).WireName;
        var json = $$"""
        {
          "version": 1,
          "whitelist": [
            { "type": "name", "value": "ImportedNew" },
            { "type": "bogusType", "value": "whatever" },
            { "type": "{{duplicateType}}", "value": "{{duplicate.Value}}" }
          ],
          "blacklist": []
        }
        """;
        var editor = CreateEditor();

        var result = editor.Import(json);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Invalid);
        Assert.Equal(1, result.Skipped);
        Assert.Contains("新增 1 条", result.Message);
        Assert.Contains("跳过重复 1 条", result.Message);
        Assert.Contains("无效 1 条", result.Message);
        Assert.Contains(TrustTableLoader.TryLoadFile(_userPath)!.Whitelist, r => r.Value == "ImportedNew");
    }

    [Fact]
    public void Import_EmptyValue_IsCountedAsInvalid()
    {
        var json = """{ "whitelist": [ { "type": "name", "value": "   " } ] }""";

        var result = CreateEditor().Import(json);

        Assert.Equal(1, result.Invalid);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Skipped);
        Assert.False(File.Exists(_userPath));
    }

    [Fact]
    public void Import_MalformedJson_ReturnsInvalidWithoutTouchingUserTable()
    {
        var result = CreateEditor().Import("{ this is not json");

        Assert.Equal(TrustEditStatus.Invalid, result.Status);
        Assert.Contains("不是合法的 JSON", result.Message);
        Assert.False(File.Exists(_userPath));
    }

    [Fact]
    public void Import_EmptyContent_ReturnsInvalid()
    {
        var result = CreateEditor().Import("   ");

        Assert.Equal(TrustEditStatus.Invalid, result.Status);
        Assert.Contains("为空", result.Message);
    }

    [Fact]
    public void Import_BumpsVersionAboveMergedVersion()
    {
        var editor = CreateEditor();
        var before = TrustTableLoader.Load(_appPath, _userPath).Version;
        var json = """{ "whitelist": [ { "type": "name", "value": "ImportedVersioned" } ] }""";

        var result = editor.Import(json);

        Assert.True(result.Succeeded);
        Assert.True(TrustTableLoader.TryLoadFile(_userPath)!.Version > before);
    }

    [Fact]
    public void Import_MergedExport_DoesNotCopyBuiltInOrAppDirectoryRules()
    {
        WriteAppTable(3, """{ "type": "name", "value": "AppOnlyProcess" }""", null);
        var editor = CreateEditor();
        Assert.True(editor.Add(new TrustRule(TrustRuleType.Name, "UserOnlyProcess"), isBlacklist: false).Succeeded);

        var mergedJson = editor.ExportMergedTable();

        // 清空用户层，模拟「换台机器重新导入合并表」。
        File.Delete(_userPath);
        Assert.False(File.Exists(_userPath));

        var result = editor.Import(mergedJson);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Invalid);

        var builtIn = BuiltInTrustTable.Create();
        var expectedSkipped = builtIn.Whitelist.Count + builtIn.Blacklist.Count + 1; // +1 = 程序目录层那条
        Assert.Equal(expectedSkipped, result.Skipped);

        var user = TrustTableLoader.TryLoadFile(_userPath)!;
        var userValues = user.Whitelist.Concat(user.Blacklist).Select(r => r.Value).ToList();
        Assert.Equal(["UserOnlyProcess"], userValues);

        // 内置层与程序目录层的规则绝不能被复制进用户层。
        foreach (var rule in builtIn.Whitelist.Concat(builtIn.Blacklist))
        {
            Assert.DoesNotContain(userValues, v => string.Equals(v, rule.Value, StringComparison.OrdinalIgnoreCase));
        }

        Assert.DoesNotContain(userValues, v => v == "AppOnlyProcess");
    }

    // ───────── 导出与往返 ─────────

    [Fact]
    public void ExportUserLayer_ThenImport_RoundTripsIdenticalRules()
    {
        var editor = CreateEditor();
        Assert.True(editor.Add(new TrustRule(TrustRuleType.Name, "RoundTripName", "备注一"), isBlacklist: false).Succeeded);
        Assert.True(editor.Add(new TrustRule(TrustRuleType.Path, @"C:\RoundTrip\"), isBlacklist: false).Succeeded);
        Assert.True(editor.Add(new TrustRule(TrustRuleType.NamePattern, "*round*"), isBlacklist: true).Succeeded);

        var exported = editor.ExportUserLayer();
        var before = TrustTableLoader.Parse(exported);

        File.Delete(_userPath);
        var imported = editor.Import(exported);
        Assert.True(imported.Succeeded);

        var after = TrustTableLoader.Parse(editor.ExportUserLayer());

        Assert.Equal(
            before.Whitelist.Select(r => r.ToString()).ToList(),
            after.Whitelist.Select(r => r.ToString()).ToList());
        Assert.Equal(
            before.Blacklist.Select(r => r.ToString()).ToList(),
            after.Blacklist.Select(r => r.ToString()).ToList());
        Assert.Equal(3, after.Whitelist.Count + after.Blacklist.Count);
    }

    [Fact]
    public void ExportUserLayer_WhenFileMissing_ReturnsEmptyTableJson()
    {
        var exported = CreateEditor().ExportUserLayer();
        var table = TrustTableLoader.Parse(exported);

        Assert.Empty(table.Whitelist);
        Assert.Empty(table.Blacklist);
    }

    [Fact]
    public void ExportMergedTable_ContainsBuiltInAndUserRules()
    {
        var editor = CreateEditor();
        Assert.True(editor.Add(new TrustRule(TrustRuleType.Name, "MergedUserRule"), isBlacklist: false).Succeeded);

        var merged = TrustTableLoader.Parse(editor.ExportMergedTable());
        var builtIn = BuiltInTrustTable.Create();

        Assert.Contains(merged.Whitelist, r => r.Value == "MergedUserRule");
        Assert.Contains(merged.Whitelist, r => builtIn.Whitelist.Any(b => b.Type == r.Type && b.Value == r.Value));
        Assert.True(merged.Whitelist.Count > 1);
    }

    [Fact]
    public void ExportUserLayer_WritesReadableChineseNote()
    {
        var editor = CreateEditor();
        Assert.True(editor.Add(new TrustRule(TrustRuleType.Name, "CnNote", "中文备注"), isBlacklist: false).Succeeded);

        // 中文不该被转义成 \uXXXX 形式，用户要能直接读表。
        Assert.Contains("中文备注", editor.ExportUserLayer());
    }

    // ───────── 辅助 ─────────

    private TrustTableEditor CreateEditor() => new(_userPath, _appPath, () => FixedNow);

    private void WriteUserTable(int version, string? whitelistRule, string? blacklistRule)
    {
        var whitelist = whitelistRule is null ? "[]" : "[" + whitelistRule + "]";
        var blacklist = blacklistRule is null ? "[]" : "[" + blacklistRule + "]";
        Directory.CreateDirectory(Path.GetDirectoryName(_userPath)!);
        File.WriteAllText(
            _userPath,
            $$"""
            {
              "version": {{version}},
              "updated": "2026-10-01",
              "whitelist": {{whitelist}},
              "blacklist": {{blacklist}}
            }
            """);
    }

    private void WriteAppTable(int version, string? whitelistRule, string? blacklistRule = null)
    {
        var whitelist = whitelistRule is null ? "[]" : "[" + whitelistRule + "]";
        var blacklist = blacklistRule is null ? "[]" : "[" + blacklistRule + "]";
        Directory.CreateDirectory(Path.GetDirectoryName(_appPath)!);
        File.WriteAllText(
            _appPath,
            $$"""
            {
              "version": {{version}},
              "updated": "2026-10-02",
              "whitelist": {{whitelist}},
              "blacklist": {{blacklist}}
            }
            """);
    }
}
