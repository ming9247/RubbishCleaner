using System;
using System.IO;
using System.Linq;
using RubbishCleaner.App.Infrastructure;
using RubbishCleaner.App.ViewModels;
using RubbishCleaner.Core.Scoring;

namespace RubbishCleaner.App.Tests;

/// <summary>
/// 首次运行引导与版本号的测试（v0.8）。
///
/// <para>三条约定：</para>
/// <list type="number">
/// <item>引导只在"没看过"时显示，看过之后不再自动弹，且标记必须落盘；</item>
/// <item>关掉引导不能顺手动到别的设置——这是主窗口与设置页共用同一个
/// <see cref="AppSettings"/> 实例的理由；</item>
/// <item>标题栏版本号必须来自程序集，不能再是硬编码字面量；引导文案里引用的
/// 权重与阈值必须与 <see cref="ScoringWeights.Default"/> 逐字一致。</item>
/// </list>
/// </summary>
public class OnboardingTests
{
    /// <summary>指向临时文件的设置实例，绝不碰开发机真实的 %ProgramData% 设置。</summary>
    private static AppSettings TempSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rc-onboarding-test-{Guid.NewGuid():N}.json");
        return new AppSettings { SettingsPathOverride = path };
    }

    [Fact]
    public void Onboarding_ShowsWhenNeverSeen()
    {
        var vm = new MainViewModel(TempSettings());

        Assert.True(vm.IsOnboardingVisible);
        Assert.NotEmpty(vm.OnboardingSteps);
    }

    [Fact]
    public void Dismiss_HidesGuide_AndPersistsTheFlag()
    {
        var settings = TempSettings();
        var vm = new MainViewModel(settings);

        vm.DismissOnboardingCommand.Execute(null);

        Assert.False(vm.IsOnboardingVisible);
        // 重新从磁盘读：标记必须真的落盘，否则下次启动又弹一遍。
        Assert.True(AppSettings.Load(settings.SettingsPathOverride!).HasSeenOnboarding);
    }

    [Fact]
    public void Dismiss_KeepsUnrelatedSettings()
    {
        var settings = TempSettings();
        settings.EnableNetworking = true;
        settings.KeepBackupCount = 7;
        settings.Save();

        var vm = new MainViewModel(AppSettings.Load(settings.SettingsPathOverride!));
        vm.DismissOnboardingCommand.Execute(null);

        var reloaded = AppSettings.Load(settings.SettingsPathOverride!);
        Assert.True(reloaded.HasSeenOnboarding);
        Assert.True(reloaded.EnableNetworking);
        Assert.Equal(7, reloaded.KeepBackupCount);
    }

    [Fact]
    public void Reset_BringsTheGuideBack()
    {
        var settings = TempSettings();
        var vm = new MainViewModel(settings);
        vm.DismissOnboardingCommand.Execute(null);

        vm.ResetOnboardingCommand.Execute(null);

        Assert.True(vm.IsOnboardingVisible);
        Assert.False(AppSettings.Load(settings.SettingsPathOverride!).HasSeenOnboarding);
    }

    [Fact]
    public void VersionText_ComesFromTheAssembly()
    {
        var vm = new MainViewModel(TempSettings());
        var expected = typeof(RubbishCleaner.App.App).Assembly.GetName().Version!.ToString(3);

        Assert.Equal($"v{expected}", vm.VersionText);
        // v0.8 之前这里硬编码 v0.6.0，发布到 0.8 就成了假信息。
        Assert.NotEqual("v0.6.0", vm.VersionText);
    }

    [Fact]
    public void GuideCopy_QuotesTheRealWeightsAndThresholds()
    {
        var d = ScoringWeights.Default;
        var copy = string.Join("\n", OnboardingContent.Steps.Select(s => s.Body));

        Assert.Contains($"存在自启动项 {d.Autostart} 分", copy);
        Assert.Contains($"没有可见窗口 {d.NoVisibleWindow} 分", copy);
        Assert.Contains($"没有桌面图标 {d.NoDesktopIcon} 分", copy);
        Assert.Contains($"没有开始菜单项 {d.NoStartMenu} 分", copy);
        Assert.Contains($"没有数字签名 {d.Unsigned} 分", copy);
        Assert.Contains($"程序名伪装成系统进程 {d.Masquerade} 分", copy);
        Assert.Contains($"{d.SuspiciousThreshold} 分及以上判为「疑似」", copy);
        Assert.Contains($"{d.HighRiskThreshold} 分及以上判为「高风险」", copy);
    }

    [Fact]
    public void GuideCopy_SendsStartupBackupToTheFolderThatActuallyHoldsIt()
    {
        var copy = string.Join("\n", OnboardingContent.Steps.Select(s => s.Body));

        // 启动项备份写在 %LocalAppData%\RubbishCleaner\backups（MainViewModel.BackupDirectory），
        // 而「备份历史」页只枚举 registry-backups 下的 orphan-*.reg——
        // 两者不是同一个目录，所以启动项备份的还原入口是「打开备份目录」。
        Assert.Contains("打开备份目录", copy);
        Assert.Contains("备份历史", copy);
    }

    [Fact]
    public void GuideCopy_DoesNotClaimEveryRuleIsNotApplicableWhenNotRunning()
    {
        var copy = string.Join("\n", OnboardingContent.Steps.Select(s => s.Body));

        // RiskScorer.cs:82 只有「没有可见窗口」带 !isRunning；
        // :86（无桌面图标）/ :90（无开始菜单）/ :97（无签名）只按 serviceOnly / orphan 判不适用。
        Assert.Contains("只有「没有可见窗口」这一项标记为「不适用」", copy);
    }

    [Fact]
    public void EveryStepHasTitleAndBody()
    {
        Assert.All(OnboardingContent.Steps, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Title));
            Assert.False(string.IsNullOrWhiteSpace(s.Body));
        });
    }
}
