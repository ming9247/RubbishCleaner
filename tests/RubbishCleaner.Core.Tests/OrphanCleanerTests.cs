using Microsoft.Win32;
using RubbishCleaner.Core.RegistryCleanup;

namespace RubbishCleaner.Core.Tests;

/// <summary>
/// 孤儿条目清理器的闸门测试（v0.4）。
///
/// 全部注入假实现，<b>绝不真删注册表</b>；
/// 断言重点是"任何一道闸门不通过就不删"。
/// </summary>
public class OrphanCleanerTests
{
    private const string ValidGuid = "{09CC399B-909F-47A2-BC3E-0009F440BFB9}";
    private const string Root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private sealed class Harness
    {
        public List<string> Deleted { get; } = [];

        public Dictionary<string, uint> States { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Enumerated { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> ExistingKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string BackupDir { get; set; } = Path.Combine(Path.GetTempPath(), "rc-orphan-" + Guid.NewGuid().ToString("N")[..8]);

        public bool DeleteThrows { get; set; }

        public bool KeySurvivesDelete { get; set; }

        public OrphanCleaner Build() => new(
            BackupDir,
            code => States.TryGetValue(code, out var s) ? s : 5u,
            () => Enumerated,
            (hive, view, path) =>
            {
                if (DeleteThrows)
                {
                    throw new UnauthorizedAccessException("模拟拒绝访问");
                }

                Deleted.Add(path);
                if (!KeySurvivesDelete)
                {
                    ExistingKeys.Remove(path);
                }
            },
            (hive, view, path) => ExistingKeys.Contains(path));

        public void Cleanup()
        {
            try
            {
                if (Directory.Exists(BackupDir))
                {
                    Directory.Delete(BackupDir, true);
                }
            }
            catch
            {
            }
        }
    }

    private static OrphanUninstallEntry Entry(
        string guid = ValidGuid,

        // 默认用 ERROR_UNKNOWN_PRODUCT：真机上"从未装过的产品"就是返回这个
        uint state = 4294967295u,
        string root = Root,
        string name = "测试孤儿")
        => new()
        {
            ProductCode = guid,
            DisplayName = name,
            Hive = RegistryHive.LocalMachine,
            View = RegistryView.Registry64,
            UninstallRootPath = root,
            MsiState = state,
            Evidence = ["测试证据"],
        };

    private static Harness Prepare(OrphanUninstallEntry entry, uint liveState = 1)
    {
        var h = new Harness();
        h.States[entry.ProductCode] = liveState;
        h.ExistingKeys.Add(@$"{entry.UninstallRootPath}\{entry.ProductCode}");
        return h;
    }

    [Fact]
    public void BuildPlan_ValidOrphan_IsAccepted()
    {
        var h = Prepare(Entry());
        using var scope = new Scope(h);

        var plan = h.Build().BuildPlan([Entry()]);

        Assert.Equal(1, plan.Count);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void BuildPlan_NonAbsentState_IsRejected()
    {
        // 这是 CC Switch 的形态：产品仍然 INSTALLED（即使文件被清掉了）
        var plan = new Harness().Build().BuildPlan([Entry(state: 5)]);

        Assert.True(plan.IsEmpty);
        Assert.Contains("ABSENT", plan.Skipped[0].Reason);
    }

    [Fact]
    public void BuildPlan_NonGuidProductCode_IsRejected()
    {
        var plan = new Harness().Build().BuildPlan([Entry(guid: "SomeSoftwareName")]);

        Assert.True(plan.IsEmpty);
        Assert.Contains("GUID", plan.Skipped[0].Reason);
    }

    [Fact]
    public void BuildPlan_NonStandardUninstallRoot_IsRejected()
    {
        var plan = new Harness().Build().BuildPlan(
            [Entry(root: @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run")]);

        Assert.True(plan.IsEmpty);
        Assert.Contains("Uninstall", plan.Skipped[0].Reason);
    }

    [Fact]
    public void BuildPlan_EmptyProductCode_IsRejectedNotTreatedAsRoot()
    {
        // 闸门 2 显式化后的核心用例：产品码为空时，拼接结果会退化成 Uninstall 根键本身。
        // 修复前没有这层判断，只能靠闸门 1（GUID 规则）间接拦住；
        // 现在必须在闸门 2 就被明确拒绝，且理由说清楚是"根键"。
        var plan = new Harness().Build().BuildPlan([Entry(guid: "")]);

        Assert.True(plan.IsEmpty);
        Assert.Contains("根键", plan.Skipped[0].Reason);
    }

    [Fact]
    public void BuildPlan_WhitespaceProductCode_IsRejected()
    {
        var plan = new Harness().Build().BuildPlan([Entry(guid: "   ")]);

        Assert.True(plan.IsEmpty);
        Assert.Contains("根键", plan.Skipped[0].Reason);
    }

    [Fact]
    public void BuildPlan_TrailingBackslashRoot_DoesNotDegenerateIntoRoot()
    {
        // UninstallRootPath 带尾反斜杠时，直接字符串拼接会得到 "...\Uninstall"（多一个分隔符），
        // 闸门 3 会因路径不等而拒绝——但绝不能出现"被当成合法路径"的情况。
        var plan = new Harness().Build().BuildPlan([Entry(root: Root + @"\")]);

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void BuildPlan_IsCaseInsensitiveOnUninstallRoot()
    {
        // 注册表路径不区分大小写；之前的 != 字符串比较在大小写不同时会误拒。
        var plan = new Harness().Build().BuildPlan(
            [Entry(root: @"software\microsoft\windows\currentversion\uninstall")]);

        Assert.Equal(1, plan.Count);
    }

    [Fact]
    public void Execute_HappyPath_BacksUpThenDeletes()
    {
        var h = Prepare(Entry());
        using var scope = new Scope(h);
        var cleaner = h.Build();
        var plan = cleaner.BuildPlan([Entry()]);

        var results = cleaner.Execute(plan);

        Assert.Single(results);
        Assert.True(results[0].Success);
        Assert.NotNull(results[0].BackupPath);
        Assert.True(File.Exists(results[0].BackupPath));
        Assert.Single(h.Deleted);
    }

    [Fact]
    public void Execute_StateChangedToInstalled_IsSkipped()
    {
        // 扫描时是 ABSENT，点确认之前用户把软件重新装上了 → 必须跳过
        var h = Prepare(Entry(), liveState: 1);
        using var scope = new Scope(h);
        var cleaner = h.Build();
        var plan = cleaner.BuildPlan([Entry()]);

        h.States[ValidGuid] = 5;   // 模拟"扫描后被重新安装"

        var results = cleaner.Execute(plan);

        Assert.False(results[0].Success);
        Assert.Contains("状态已变化", results[0].Message);
        Assert.Empty(h.Deleted);
    }

    [Fact]
    public void Execute_AppearedInEnumeration_IsSkipped()
    {
        var h = Prepare(Entry());
        using var scope = new Scope(h);
        var cleaner = h.Build();
        var plan = cleaner.BuildPlan([Entry()]);

        h.Enumerated.Add(ValidGuid);   // 模拟"枚举里突然出现了"

        var results = cleaner.Execute(plan);

        Assert.False(results[0].Success);
        Assert.Contains("判定不可靠", results[0].Message);
        Assert.Empty(h.Deleted);
    }

    [Fact]
    public void Execute_KeyAlreadyGone_IsSkipped()
    {
        var h = Prepare(Entry());
        using var scope = new Scope(h);
        var cleaner = h.Build();
        var plan = cleaner.BuildPlan([Entry()]);

        h.ExistingKeys.Clear();

        var results = cleaner.Execute(plan);

        Assert.False(results[0].Success);
        Assert.Contains("已不存在", results[0].Message);
    }

    [Fact]
    public void Execute_BackupFails_DoesNotDelete()
    {
        var h = Prepare(Entry());
        using var scope = new Scope(h);

        // 用一个非法目录让备份失败
        h.BackupDir = @"Z:\\NoSuchDrive\\backups";

        var cleaner = h.Build();
        var plan = cleaner.BuildPlan([Entry()]);

        var results = cleaner.Execute(plan);

        Assert.False(results[0].Success);
        Assert.Contains("备份失败", results[0].Message);
        Assert.Empty(h.Deleted);   // 最关键：备份失败绝不删
    }

    [Fact]
    public void Execute_DeleteThrows_ReportsFailureWithBackupPath()
    {
        var h = Prepare(Entry());
        using var scope = new Scope(h);
        h.DeleteThrows = true;

        var cleaner = h.Build();
        var plan = cleaner.BuildPlan([Entry()]);
        var results = cleaner.Execute(plan);

        Assert.False(results[0].Success);
        Assert.Contains("删除失败", results[0].Message);
        Assert.NotNull(results[0].BackupPath);
    }

    [Fact]
    public void Execute_KeySurvivesDelete_IsReportedAsFailure()
    {
        var h = Prepare(Entry());
        using var scope = new Scope(h);
        h.KeySurvivesDelete = true;

        var cleaner = h.Build();
        var plan = cleaner.BuildPlan([Entry()]);
        var results = cleaner.Execute(plan);

        Assert.False(results[0].Success);
        Assert.Contains("复核仍存在", results[0].Message);
    }

    [Fact]
    public void Execute_EmptyPlan_DoesNothing()
    {
        var h = new Harness();
        var results = h.Build().Execute(new OrphanCleanupPlan());

        Assert.Empty(results);
        Assert.Empty(h.Deleted);
    }

    [Fact]
    public void Execute_OneFailureDoesNotStopOthers()
    {
        var h = new Harness();
        using var scope = new Scope(h);
        var second = Entry(guid: "{11111111-2222-3333-4444-555555555555}", name: "第二个");

        h.States[ValidGuid] = 1;
        h.ExistingKeys.Add(@$"{Root}\{ValidGuid}");
        // 第二个故意不让它存在键 → 跳过
        h.States[second.ProductCode] = 1;

        var cleaner = h.Build();
        var plan = cleaner.BuildPlan([Entry(), second]);

        var results = cleaner.Execute(plan);

        Assert.Equal(2, results.Count);
        Assert.True(results[0].Success);
        Assert.False(results[1].Success);
    }

    /// <summary>
    /// 自动清理临时备份目录。
    ///
    /// 之前这个类是空实现（Dispose 不做事），而 Harness.Cleanup() 从未被调用，
    /// 结果每跑一次测试就往 %TEMP% 里漏一批 <c>rc-orphan-*</c> 目录。
    /// 现在把 Harness 交给它，Dispose 时真正删掉。
    /// </summary>
    private sealed class Scope : IDisposable
    {
        private readonly Harness? _harness;

        public Scope(Harness? harness = null) => _harness = harness;

        public void Dispose() => _harness?.Cleanup();
    }
}
