using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Scoring;

namespace RubbishCleaner.Core.Tests;

public class NameSimilarityTests
{
    [Theory]
    [InlineData("svch0st", "svchost", 1)]
    [InlineData("scvhost", "svchost", 1)]
    [InlineData("svchost", "svchost", 0)]
    [InlineData("", "svchost", 7)]
    [InlineData("lsass", "lsass", 0)]
    public void Levenshtein_ComputesExpectedDistance(string a, string b, int expected)
        => Assert.Equal(expected, NameSimilarity.Levenshtein(a, b));

    [Theory]
    [InlineData("svch0st", true)]
    [InlineData("scvhost", true)]
    [InlineData("lsas", true)]
    [InlineData("chrome", false)]
    [InlineData("notepad", false)]
    [InlineData("", false)]
    public void LooksMasquerading_DetectsTyposquatting(string name, bool expected)
    {
        Assert.Equal(expected, NameSimilarity.LooksMasquerading(name, out _));
    }

    [Fact]
    public void LooksMasquerading_ExactSystemName_IsNotMasquerading()
    {
        // 名字完全一致属于正常系统进程，不能判为伪装
        Assert.False(NameSimilarity.LooksMasquerading("svchost", out _));
    }

    [Fact]
    public void LooksMasquerading_ReportsMatchedSystemName()
    {
        _ = NameSimilarity.LooksMasquerading("svch0st.exe", out var matched);

        Assert.Equal("svchost", matched);
    }
}

public class RiskScorerTests
{
    private static SoftwareProfile CreateProfile(
        bool running = true,
        bool hasVisibleWindow = false,
        bool signed = false,
        bool hasDesktop = false,
        bool hasStartMenu = false,
        int startupEntryCount = 1,
        TrustVerdict trust = TrustVerdict.Unknown,
        string processName = "app",
        string? path = @"C:\Apps\app.exe")
    {
        var facts = new ProcessFacts(
            ProcessId: running ? 100 : 0,
            ProcessName: processName,
            ExecutablePath: path,
            IsRunning: running,
            HasVisibleWindow: hasVisibleWindow,
            HasTaskbarButton: hasVisibleWindow,
            SignatureValid: signed,
            IsMicrosoftSigned: false,
            SignerSubject: signed ? "CN=Test, O=Test Vendor" : null,
            Publisher: signed ? "Test Vendor" : null,
            FileDescription: "Test");

        return new SoftwareProfile
        {
            Key = "key",
            DisplayName = processName,
            ExecutablePath = path,
            StartupEntries = Enumerable.Range(0, startupEntryCount)
                .Select(i => new StartupEntry
                {
                    Id = $"id{i}",
                    Source = StartupSourceKind.RegistryRunHkcu,
                    Name = $"entry{i}",
                    Location = @"HKCU\Run",
                    ExecutablePath = path,
                    TargetExistsOverride = true,
                })
                .ToList(),
            LiveProcess = facts,
            HasDesktopShortcut = hasDesktop,
            HasStartMenuEntry = hasStartMenu,
            Trust = trust,
        };
    }

    [Fact]
    public void Score_NoFeaturesHit_IsZeroAndNormal()
    {
        var scorer = new RiskScorer();
        // 无自启动项、有可见窗口、有签名、有桌面与开始菜单入口
        var profile = CreateProfile(
            hasVisibleWindow: true, signed: true, hasDesktop: true, hasStartMenu: true,
            startupEntryCount: 0, processName: "notepad");

        var score = scorer.Score(profile);

        Assert.Equal(0, score.Total);
        Assert.Equal(RiskLevel.Normal, score.Level);
    }

    [Fact]
    public void Score_AllHeuristicFeaturesHit_IsCappedAt100()
    {
        var scorer = new RiskScorer();
        // 自启动30 + 无窗口25 + 无桌面15 + 无开始菜单15 + 未签名10 + 伪装10 = 105 → 封顶 100
        var profile = CreateProfile(processName: "svch0st");

        var score = scorer.Score(profile);

        Assert.Equal(100, score.Total);
        Assert.Equal(RiskLevel.HighRisk, score.Level);
    }

    [Theory]
    [InlineData(40, RiskLevel.Normal)]
    [InlineData(41, RiskLevel.Suspicious)]
    [InlineData(70, RiskLevel.Suspicious)]
    [InlineData(71, RiskLevel.HighRisk)]
    public void Classify_UsesDocumentedThresholds(int total, RiskLevel expected)
        => Assert.Equal(expected, new RiskScorer().Classify(total));

    [Fact]
    public void Score_WhiteListSuppressesLevelButKeepsRawTotal()
    {
        var scorer = new RiskScorer();
        var profile = CreateProfile(trust: TrustVerdict.Safe, processName: "svch0st");

        var score = scorer.Score(profile);

        Assert.Equal(RiskLevel.Normal, score.Level);
        Assert.True(score.Suppressed);
        Assert.True(score.Total > 0); // 原始分数保留，便于用户理解
    }

    [Fact]
    public void Score_BlackListForcesHighRisk()
    {
        var scorer = new RiskScorer();
        // 即使特征很少（仅自启动 + 有签名 + 有入口），黑名单也应强制高风险
        var profile = CreateProfile(
            hasVisibleWindow: true, signed: true, hasDesktop: true, hasStartMenu: true,
            trust: TrustVerdict.Malicious, processName: "notepad");

        var score = scorer.Score(profile);

        Assert.Equal(RiskLevel.HighRisk, score.Level);
    }

    [Fact]
    public void Score_NotRunning_DoesNotCountNoVisibleWindow()
    {
        var scorer = new RiskScorer();
        var profile = CreateProfile(running: false, signed: true, hasDesktop: true, hasStartMenu: true);

        var score = scorer.Score(profile);

        // 未运行时无法证明"无界面"，该项应标记为"不适用"且不计分
        var item = score.Items.Single(i => i.RuleId == RiskScorer.RuleNoVisibleWindow);
        Assert.False(item.Hit);
        Assert.True(item.NotApplicable);
        Assert.Equal(0, item.Contribution);
    }

    [Fact]
    public void Score_NotApplicableRules_DoNotContributeWeight()
    {
        // 回归测试：曾经 NotApplicable 规则仍会按权重计分，导致纯服务程序被误判为高风险
        var profile = CreateProfile(running: true, hasVisibleWindow: false, signed: true, processName: "nvcontainer");
        profile = new SoftwareProfile
        {
            Key = profile.Key,
            DisplayName = profile.DisplayName,
            ExecutablePath = profile.ExecutablePath,
            StartupEntries =
            [
                new StartupEntry
                {
                    Id = "svc",
                    Source = StartupSourceKind.WindowsService,
                    Name = "NvContainerLocalSystem",
                    Location = @"HKLM\SYSTEM\CurrentControlSet\Services",
                    ExecutablePath = @"C:\Program Files\NVIDIA\nvcontainer.exe",
                    TargetExistsOverride = true,
                },
            ],
            LiveProcess = profile.LiveProcess,
            HasDesktopShortcut = false,
            HasStartMenuEntry = false,
        };

        var score = new RiskScorer().Score(profile);

        // 纯服务：只有 AUTOSTART 计分，无窗口/无图标/无开始菜单均为"不适用"
        Assert.True(profile.IsServiceOnly);
        Assert.Equal(30, score.Total);
        Assert.Equal(RiskLevel.Normal, score.Level);
    }

    [Fact]
    public void Score_ServiceWithAutostartPlusUnsigned_StaysNormal()
    {
        // 未签名的第三方服务：30 + 10 = 40，仍属"正常"，不应被推送为高风险
        var profile = new SoftwareProfile
        {
            Key = "svc",
            DisplayName = "some-service",
            ExecutablePath = @"C:\Program Files\Vendor\svc.exe",
            StartupEntries =
            [
                new StartupEntry
                {
                    Id = "svc",
                    Source = StartupSourceKind.WindowsService,
                    Name = "SomeService",
                    Location = @"HKLM\SYSTEM\CurrentControlSet\Services",
                    ExecutablePath = @"C:\Program Files\Vendor\svc.exe",
                    TargetExistsOverride = true,
                },
            ],
            LiveProcess = new ProcessFacts(
                1, "svc", @"C:\Program Files\Vendor\svc.exe", true, false, false, false, false, null, null, null),
        };

        var score = new RiskScorer().Score(profile);

        Assert.Equal(40, score.Total);
        Assert.Equal(RiskLevel.Normal, score.Level);
    }

    [Fact]
    public void Score_KeepsAllRulesForTransparency()
    {
        var score = new RiskScorer().Score(CreateProfile());

        // 全部 8 条规则都保留，便于 UI 展示"命中 x/y"
        Assert.Equal(8, score.Items.Count);
        Assert.Contains(score.Items, i => i.RuleId == RiskScorer.RuleNoTray && !i.Hit && i.NotApplicable);
        Assert.Contains(score.Items, i => i.RuleId == RiskScorer.RuleOrphanStartup);
    }

    // ---------------- 失效启动项（残留） ----------------

    [Fact]
    public void Score_OrphanedEntry_DoesNotCountProgramRules()
    {
        // 目标文件已不存在的启动项：不能套用"未签名/无窗口/无图标"这些针对活程序规则，
        // 否则会把一条该清理的残留推成假的高风险。
        var profile = new SoftwareProfile
        {
            Key = "ghost",
            DisplayName = "ghost",
            ExecutablePath = @"D:\Old\Ghost\ghost.exe",
            StartupEntries =
            [
                new StartupEntry
                {
                    Id = "ghost",
                    Source = StartupSourceKind.RegistryRunHkcu,
                    Name = "Ghost",
                    Location = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
                    ExecutablePath = @"D:\Old\Ghost\ghost.exe",
                    TargetExistsOverride = false,
                },
            ],
            LiveProcess = ProcessFacts.NotRunning("ghost", @"D:\Old\Ghost\ghost.exe"),
        };

        var score = new RiskScorer().Score(profile);

        Assert.True(profile.IsOrphaned);
        // 只剩 AUTOSTART 计分，其余全部"不适用"
        Assert.Equal(30, score.Total);
        Assert.Equal(RiskLevel.Normal, score.Level);

        var orphanRule = score.Items.Single(i => i.RuleId == RiskScorer.RuleOrphanStartup);
        Assert.True(orphanRule.Hit);
        Assert.Equal(0, orphanRule.Contribution);   // 残留不计分，只做标记
    }

    [Fact]
    public void Score_PartiallyOrphaned_StillEvaluatesLiveProgram()
    {
        // 一个启动点失效、另一个有效：说明程序还在，应按活程序正常评分
        var profile = new SoftwareProfile
        {
            Key = "mixed",
            DisplayName = "mixed",
            ExecutablePath = @"C:\Apps\mixed.exe",
            StartupEntries =
            [
                new StartupEntry
                {
                    Id = "old", Source = StartupSourceKind.RegistryRunHkcu, Name = "Mixed(old)",
                    Location = @"HKCU\Run", ExecutablePath = @"D:\Old\mixed.exe",
                    TargetExistsOverride = false,
                },
                new StartupEntry
                {
                    Id = "new", Source = StartupSourceKind.RegistryRunHkcu, Name = "Mixed",
                    Location = @"HKCU\Run", ExecutablePath = @"C:\Apps\mixed.exe",
                    TargetExistsOverride = true,
                },
            ],
            LiveProcess = new ProcessFacts(1, "mixed", @"C:\Apps\mixed.exe", true, false, false, false, false, null, null, null),
        };

        var score = new RiskScorer().Score(profile);

        Assert.False(profile.IsOrphaned);
        Assert.Single(profile.OrphanEntries);
        Assert.True(score.Total > 30);   // 未签名 + 无窗口等都参与计分
    }

    [Theory]
    [InlineData(null, false)]                              // 无法解析路径 → 不判失效
    [InlineData("ghost.exe", false)]                        // 相对路径 → 不判失效
    [InlineData(@"\\nonexistent-host\share\a.exe", true)]   // UNC 绝对路径且不存在 → 判失效
    public void TargetExists_RelativeOrUnparseablePath_IsNotOrphan(string? path, bool expectOrphan)
    {
        var entry = new StartupEntry
        {
            Id = "x", Source = StartupSourceKind.ScheduledTask, Name = "x",
            Location = @"C:\Windows\System32\Tasks", ExecutablePath = path,
        };

        Assert.Equal(expectOrphan, entry.IsOrphan);
    }
}
