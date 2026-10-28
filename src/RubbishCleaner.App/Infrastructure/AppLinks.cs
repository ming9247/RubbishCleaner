namespace RubbishCleaner.App.Infrastructure;

/// <summary>
/// 应用内所有对外链接的单一出处（v0.9.1）。
///
/// <para>为什么不直接写在 XAML 里：① 换地址只改这一处；② XAML 里写 <c>https://…</c> 容易被误当成资源键，
/// 也会让「哪个链接是赞助页」散落在视图里难以审计。</para>
///
/// <para><b>赞助是纯外链</b>：应用内不做任何支付、不读取任何账号信息，点击只是用系统浏览器打开
/// 爱发电官方页面（<c>UseShellExecute</c>）。程序不会自动跳转、不弹窗、不统计点击。</para>
/// </summary>
public static class AppLinks
{
    /// <summary>作者的爱发电主页——设置页「赞助作者」按钮打开的就是它。</summary>
    public const string AfdianUrl = "https://afdian.com/a/coco5280";

    /// <summary>项目主页（关于卡片里展示）。</summary>
    public const string RepositoryUrl = "https://github.com/ming9247/RubbishCleaner";

    /// <summary>Releases 页（关于卡片里展示，用户从这里下载新版）。</summary>
    public const string ReleasesUrl = "https://github.com/ming9247/RubbishCleaner/releases/latest";

    /// <summary>问题反馈（Issue）页。</summary>
    public const string IssuesUrl = "https://github.com/ming9247/RubbishCleaner/issues";
}
