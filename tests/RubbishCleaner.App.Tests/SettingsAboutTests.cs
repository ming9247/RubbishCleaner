using System.IO;
using RubbishCleaner.App.Infrastructure;
using RubbishCleaner.App.ViewModels;
using RubbishCleaner.Core.BackupHistory;

namespace RubbishCleaner.App.Tests;

/// <summary>
/// 「关于软件」卡片（v0.9.1）的测试。
///
/// 三条约定：
/// ① 赞助按钮只做一件事——用系统浏览器打开爱发电主页，不弹窗、不写盘、不依赖联网开关；
/// ② 赞助地址来自 <see cref="AppLinks"/> 这一个常量（换地址只改一处）；
/// ③ 打开失败不能让界面崩，必须落到可复制的 ErrorText 上。
///
/// 打开浏览器的动作通过构造参数注入，测试里不真的启动浏览器。
/// </summary>
public class SettingsAboutTests
{
    private sealed class NoopStore : IBackupHistoryStore
    {
        public Task<BackupHistoryResult> ListAsync(
            IProgress<string>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new BackupHistoryResult { Entries = [], Directory = @"C:\backup" });

        public Task<BackupRollbackResult> RollbackAsync(
            BackupHistoryEntry entry, IProgress<string>? progress = null, CancellationToken ct = default)
            => throw new NotSupportedException("关于页测试不回滚");

        public Task<BackupCleanupResult> CleanOldBackupsAsync(
            int keepCount = 30, IProgress<string>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new BackupCleanupResult { Success = true, Message = "未使用" });
    }

    private static AppSettings TempSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rc-about-test-{Guid.NewGuid():N}.json");
        return new AppSettings { SettingsPathOverride = path };
    }

    private static SettingsViewModel CreateVm(Action<string> openUrl, AppSettings? settings = null)
        => new(backupStore: new NoopStore(), settings: settings ?? TempSettings(), openUrl: openUrl);

    [Fact]
    public void SponsorUrl_IsAfdianCreatorPage()
    {
        var vm = CreateVm(_ => { });

        Assert.Equal(AppLinks.AfdianUrl, vm.SponsorUrl);
        Assert.StartsWith("https://afdian.com/a/", vm.SponsorUrl, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(vm.RepositoryUrl));
    }

    [Fact]
    public void OpenSponsorCommand_OpensAfdianUrlExactlyOnce()
    {
        var opened = new List<string>();
        var vm = CreateVm(url => opened.Add(url));
        Assert.Null(vm.SponsorStatusText);

        vm.OpenSponsorCommand.Execute(null);

        Assert.Equal([AppLinks.AfdianUrl], opened);
        Assert.Null(vm.ErrorText);
        Assert.Contains(AppLinks.AfdianUrl, vm.SponsorStatusText);
    }

    [Fact]
    public void OpenSponsor_WorksEvenWhenNetworkingIsOff()
    {
        // 赞助是外链，不经过 TrustTableUpdater，也不受「启用联网」开关约束。
        var opened = new List<string>();
        var vm = CreateVm(url => opened.Add(url));
        Assert.False(vm.EnableNetworking);

        vm.OpenSponsor();

        Assert.Single(opened);
        Assert.False(vm.EnableNetworking);
    }

    [Fact]
    public void OpenSponsor_WhenBrowserFails_SetsCopyableErrorWithoutThrowing()
    {
        var vm = CreateVm(_ => throw new InvalidOperationException("没有默认浏览器"));

        var ex = Record.Exception(vm.OpenSponsor);

        Assert.Null(ex);
        Assert.Null(vm.SponsorStatusText);
        Assert.Contains(AppLinks.AfdianUrl, vm.ErrorText);
    }

    [Fact]
    public void OpenSponsor_DoesNotWriteSettingsFile()
    {
        var settings = TempSettings();
        var vm = CreateVm(_ => { }, settings);

        vm.OpenSponsor();

        // 赞助动作不改任何设置，也不应该顺手把 settings.json 写出来。
        Assert.False(File.Exists(settings.SettingsPathOverride));
    }

    [Fact]
    public void AppVersionText_ComesFromTheAssembly()
    {
        var expected = "v" + typeof(RubbishCleaner.App.App).Assembly.GetName().Version!.ToString(3);
        var vm = CreateVm(_ => { });

        Assert.Equal(expected, vm.AppVersionText);
        Assert.NotEqual("v0.6.0", vm.AppVersionText);
    }
}
