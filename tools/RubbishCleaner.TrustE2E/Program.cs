using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using RubbishCleaner.App.Infrastructure;
using RubbishCleaner.App.ViewModels;
using RubbishCleaner.Common.Logging;
using RubbishCleaner.Common.Net;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Trust;
using RubbishCleaner.TrustE2E;

// ============================================================================
// TrustTableUpdater 在线更新全链路实测探针（v0.8）
//
// 这不是单元测试：走真实 HTTP + 真实写盘，只把"写盘目标"和"设置/日志目录"
// 重定向到临时目录，避免污染用户机器。其中 [g] 段故意用生产构造函数
// TrustTableUpdater() 跑一次真实默认路径 %ProgramData%\RubbishCleaner\trust-table.json，
// 跑完把机器状态恢复原样。
//
// 用法：dotnet run --project tools/RubbishCleaner.TrustE2E
// 退出码：0 全通过 / 2 有失败项
// ============================================================================

Console.OutputEncoding = Encoding.UTF8;

// 诊断模式：--tls-diag [url]，用于把 TLS 失败挖到最内层原因
if (args.Length > 0 && string.Equals(args[0], "--tls-diag", StringComparison.OrdinalIgnoreCase))
{
    var diagUrl = args.Length > 1 ? args[1] : TrustTableUpdater.DefaultGitHubRawUrl;
    return await RubbishCleaner.TrustE2E.TlsDiagnostics.RunAsync(diagUrl);
}

var failures = new List<string>();

void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine((ok ? "  OK   " : "  FAIL ") + name + (detail is null ? "" : "  " + detail));
    if (!ok)
    {
        failures.Add(name + (detail is null ? "" : " -> " + detail));
    }
}

void Note(string text) => Console.WriteLine("  · " + text);

var root = Path.Combine(Path.GetTempPath(), "rc-truste2e-" + Guid.NewGuid().ToString("N"));
var logDir = Path.Combine(root, "logs");
var tempTarget = Path.Combine(root, "trust-table.json");
var tempSettings = Path.Combine(root, "settings.json");
var noAppTablePath = Path.Combine(root, "no-app-table.json");
Directory.CreateDirectory(root);
Log.Configure(logDir);

const string Url = TrustTableUpdater.DefaultGitHubRawUrl;
const int ExpectedRemoteVersion = 2;
const int ExpectedWhitelist = 19;
const int ExpectedBlacklist = 7;
const string ExpectedUpdated = "2026-10-24";

var builtin = BuiltInTrustTable.Create();

Console.WriteLine("=== TrustTableUpdater 在线更新全链路实测 ===");
Console.WriteLine($"时间      : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
Console.WriteLine($"系统      : {Environment.OSVersion} / .NET {Environment.Version}");
Console.WriteLine($"探针目录  : {root}");
Console.WriteLine($"远程地址  : {Url}");
Console.WriteLine($"用户层路径: {TrustTableLoader.UserTablePath}");
Console.WriteLine($"内置表    : v{builtin.Version} updated={builtin.Updated} 白={builtin.Whitelist.Count} 黑={builtin.Blacklist.Count}");
Console.WriteLine();

// ---------------------------------------------------------------------------
// [0] 端点基线：独立 HttpClient 只读测量，作为后续下载数据的对照
// ---------------------------------------------------------------------------
Console.WriteLine("[0] 端点基线测量（独立 HttpClient，不写盘）");
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    http.DefaultRequestHeaders.Add("User-Agent", "RubbishCleaner-e2e-probe/0.8");
    var sw = Stopwatch.StartNew();
    try
    {
        using var resp = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead);
        var body = await resp.Content.ReadAsByteArrayAsync();
        sw.Stop();
        Console.WriteLine($"  HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} | Content-Length={resp.Content.Headers.ContentLength?.ToString() ?? "null"} | 实收={body.Length} B | 耗时={sw.ElapsedMilliseconds} ms");
        Check("0.1 端点返回 200", (int)resp.StatusCode == 200, $"{(int)resp.StatusCode}");
        Check("0.2 响应体非空", body.Length > 0, $"{body.Length} B");
    }
    catch (Exception ex)
    {
        sw.Stop();
        Console.WriteLine($"  请求异常（{sw.ElapsedMilliseconds} ms）: {ex.GetType().Name}: {ex.Message}");
        Check("0.1 端点返回 200", false, $"{ex.GetType().Name}: {ex.Message}");
        Check("0.2 响应体非空", false, "请求失败，无响应体");
    }
}

// ---------------------------------------------------------------------------
// [a] 检查更新：本地无用户层文件（版本 0） vs 远程 v2
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("[a] 检查更新（本地用户层缺失 → 期望发现新版本）");
UpdateCheckResult? checkA = null;
{
    using var updater = new TrustTableUpdater(new HttpDownloader(), tempTarget, null);
    var sw = Stopwatch.StartNew();
    checkA = await updater.CheckUpdateAsync(Url);
    sw.Stop();
    Console.WriteLine($"  Available={checkA.Available} Current={checkA.CurrentVersion} Remote={checkA.RemoteVersion} Updated={checkA.RemoteUpdatedDate ?? "null"} Error={checkA.ErrorMessage ?? "null"} 耗时={sw.ElapsedMilliseconds} ms");
    Check("a.1 Available=true", checkA.Available);
    Check("a.2 CurrentVersion=0（无用户层文件）", checkA.CurrentVersion == 0, $"实际 {checkA.CurrentVersion}");
    Check("a.3 RemoteVersion=2", checkA.RemoteVersion == ExpectedRemoteVersion, $"实际 {checkA.RemoteVersion}");
    Check("a.4 RemoteUpdatedDate=2026-10-24", checkA.RemoteUpdatedDate == ExpectedUpdated, checkA.RemoteUpdatedDate ?? "null");
    Check("a.5 ErrorMessage 为空", checkA.ErrorMessage is null, checkA.ErrorMessage ?? "null");
    Check("a.6 仅检查不落盘", !File.Exists(tempTarget), File.Exists(tempTarget) ? "目标文件被创建" : "");
}

// ---------------------------------------------------------------------------
// [b] 执行更新：真实下载 + 真实写盘到临时目标
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("[b] 执行更新（真实下载 + 真实写盘）");
UpdateResult? updateB = null;
{
    using var updater = new TrustTableUpdater(new HttpDownloader(), tempTarget, null);
    var progress = new SyncProgress<string>(s => Note(s));
    var sw = Stopwatch.StartNew();
    updateB = await updater.UpdateAsync(Url, progress);
    sw.Stop();
    Console.WriteLine($"  Success={updateB.Success} Message={updateB.Message} {updateB.OldVersion}→{updateB.NewVersion} File={updateB.FilePath ?? "null"} 耗时={sw.ElapsedMilliseconds} ms");
    Check("b.1 Success=true", updateB.Success, updateB.Message);
    Check("b.2 版本 0 → 2", updateB.OldVersion == 0 && updateB.NewVersion == ExpectedRemoteVersion, $"{updateB.OldVersion}→{updateB.NewVersion}");
    Check("b.3 FilePath 指向探针临时目标", string.Equals(updateB.FilePath, tempTarget, StringComparison.OrdinalIgnoreCase), updateB.FilePath ?? "null");
    Check("b.4 目标文件已生成", File.Exists(tempTarget));
    if (File.Exists(tempTarget))
    {
        Note($"落盘: {tempTarget} | {new FileInfo(tempTarget).Length} B");
    }
}

// ---------------------------------------------------------------------------
// [c] 复验：重新从磁盘读回，校验 JSON 合法性与条目数
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("[c] 复验写盘结果（重新读文件）");
{
    var exists = File.Exists(tempTarget);
    Check("c.1 文件存在", exists);
    if (exists)
    {
        var json = await File.ReadAllTextAsync(tempTarget);
        var bytes = new FileInfo(tempTarget).Length;
        Console.WriteLine($"  文件 {tempTarget} | {bytes} B | {json.Length} 字符");

        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (Exception ex)
        {
            Check("c.2 JSON 可解析", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        Check("c.2 JSON 可解析", doc is not null);
        doc?.Dispose();

        var table = TrustTableLoader.Parse(json, "e2e-user");
        Console.WriteLine($"  Version={table.Version} Updated={table.Updated ?? "null"} Source={table.Source} 白={table.Whitelist.Count} 黑={table.Blacklist.Count}");
        Check("c.3 Version=2", table.Version == ExpectedRemoteVersion, $"实际 {table.Version}");
        Check("c.4 白名单 19 条", table.Whitelist.Count == ExpectedWhitelist, $"实际 {table.Whitelist.Count}");
        Check("c.5 黑名单 7 条", table.Blacklist.Count == ExpectedBlacklist, $"实际 {table.Blacklist.Count}");
        Check("c.6 Updated=2026-10-24", table.Updated == ExpectedUpdated, table.Updated ?? "null");

        var whiteTypes = table.Whitelist.GroupBy(r => r.Type).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal)
            .Select(g => $"{g.Key}×{g.Count()}");
        var blackTypes = table.Blacklist.GroupBy(r => r.Type).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal)
            .Select(g => $"{g.Key}×{g.Count()}");
        Note($"白名单类型分布: {string.Join(", ", whiteTypes)}");
        Note($"黑名单类型分布: {string.Join(", ", blackTypes)}");

        // 合并视角（探针上下文没有 app 层，app 层文件不存在）
        var mergedNoApp = TrustTableLoader.Load(noAppTablePath, tempTarget);
        Console.WriteLine($"  合并(无 app 层): v{mergedNoApp.Version} 白={mergedNoApp.Whitelist.Count} 黑={mergedNoApp.Blacklist.Count} Source={mergedNoApp.Source}");
        Check("c.7 合并后版本=2（max）", mergedNoApp.Version == ExpectedRemoteVersion, $"{mergedNoApp.Version}");
        Check("c.8 合并后白名单 19（与内置 9 条去重）", mergedNoApp.Whitelist.Count == ExpectedWhitelist, $"{mergedNoApp.Whitelist.Count}");
        Check("c.9 合并后黑名单 7（与内置 6 条去重）", mergedNoApp.Blacklist.Count == ExpectedBlacklist, $"{mergedNoApp.Blacklist.Count}");

        // 生产形态：app 层指向仓库 Resources\trust-table.json
        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            Note("未找到仓库根（RubbishCleaner.sln），跳过生产形态合并视角");
        }
        else
        {
            var appTablePath = Path.Combine(repoRoot, "src", "RubbishCleaner.App", "Resources", "trust-table.json");
            if (File.Exists(appTablePath))
            {
                var mergedProd = TrustTableLoader.Load(appTablePath, tempTarget);
                Console.WriteLine($"  合并(生产形态): v{mergedProd.Version} 白={mergedProd.Whitelist.Count} 黑={mergedProd.Blacklist.Count} Source={mergedProd.Source}");
                Check("c.10 生产形态合并版本=2", mergedProd.Version == ExpectedRemoteVersion, $"{mergedProd.Version}");
            }
            else
            {
                Note($"app 层文件不存在，跳过生产形态：{appTablePath}");
            }
        }
    }
}

// ---------------------------------------------------------------------------
// [d] 再次检查更新：应判定已是最新
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("[d] 再次检查更新（期望已是最新）");
{
    using var updater = new TrustTableUpdater(new HttpDownloader(), tempTarget, null);
    var sw = Stopwatch.StartNew();
    var result = await updater.CheckUpdateAsync(Url);
    sw.Stop();
    Console.WriteLine($"  Available={result.Available} Current={result.CurrentVersion} Remote={result.RemoteVersion} Error={result.ErrorMessage ?? "null"} 耗时={sw.ElapsedMilliseconds} ms");
    Check("d.1 Available=false", !result.Available);
    Check("d.2 CurrentVersion=2", result.CurrentVersion == ExpectedRemoteVersion, $"实际 {result.CurrentVersion}");
    Check("d.3 RemoteVersion=2", result.RemoteVersion == ExpectedRemoteVersion, $"实际 {result.RemoteVersion}");
    Check("d.4 ErrorMessage 为空", result.ErrorMessage is null, result.ErrorMessage ?? "null");
}
{
    using var updater = new TrustTableUpdater(new HttpDownloader(), tempTarget, null);
    var sw = Stopwatch.StartNew();
    var result = await updater.UpdateAsync(Url);
    sw.Stop();
    Console.WriteLine($"  UpdateAsync（无更新）: Success={result.Success} Message={result.Message} 耗时={sw.ElapsedMilliseconds} ms");
    Check("d.5 无更新时 Success=false", !result.Success);
    Check("d.6 文案含「已是最新版本」", result.Message.Contains("已是最新", StringComparison.Ordinal), result.Message);
}

// ---------------------------------------------------------------------------
// [e] 回滚：删掉用户层文件后回落内置表
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("[e] 回滚：删除用户层文件后重读（期望回落内置表）");
{
    File.Delete(tempTarget);
    Check("e.1 用户层文件已删除", !File.Exists(tempTarget));

    var fallback = TrustTableLoader.Load(noAppTablePath, tempTarget);
    Console.WriteLine($"  回滚后: v{fallback.Version} Updated={fallback.Updated ?? "null"} Source={fallback.Source} 白={fallback.Whitelist.Count} 黑={fallback.Blacklist.Count}");
    Check("e.2 版本回落 2 → 1", fallback.Version == builtin.Version, $"{fallback.Version}");
    Check("e.3 Updated 回落 2026-10-23", fallback.Updated == builtin.Updated, fallback.Updated ?? "null");
    Check("e.4 Source=builtin", fallback.Source == "builtin", fallback.Source);
    Check("e.5 白名单回落 9 条", fallback.Whitelist.Count == builtin.Whitelist.Count, $"{fallback.Whitelist.Count}");
    Check("e.6 黑名单回落 6 条", fallback.Blacklist.Count == builtin.Blacklist.Count, $"{fallback.Blacklist.Count}");

    using var updater = new TrustTableUpdater(new HttpDownloader(), tempTarget, null);
    var result = await updater.CheckUpdateAsync(Url);
    Console.WriteLine($"  回滚后检查: Available={result.Available} Current={result.CurrentVersion} Remote={result.RemoteVersion}");
    Check("e.7 回滚后重新变为可更新", result.Available && result.RemoteVersion == ExpectedRemoteVersion, $"{result.Available}/{result.CurrentVersion}→{result.RemoteVersion}");
    // 注意：updater 的 CurrentVersion 只读用户层文件（TryLoadFile(_targetPath)），
    // 不是合并后的表版本；用户层被删后它就是 0，而不是内置表/合并表的 1 或 2。
    Note($"回滚后 CurrentVersion={result.CurrentVersion}（updater 只比对用户层文件，不读合并表）");
}

// ---------------------------------------------------------------------------
// [f] 联网守卫：EnableNetworking=false 时直调公开方法必须被拦住
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("[f] 联网守卫：EnableNetworking=false 直调（不点按钮）");
{
    var counting = new CountingDownloader();
    var settings = AppSettings.Load(tempSettings);
    Note($"AppSettings.Load(临时路径) → EnableNetworking={settings.EnableNetworking} KeepBackupCount={settings.KeepBackupCount}");

    var canUseNetwork = true;
    var commandCanExecute = true;
    var statusBefore = "";
    var statusAfterCheck = "";
    var statusAfterUpdate = "";
    var isBusyAfter = true;
    Exception? guardException = null;

    // SettingsViewModel 会触碰 WPF 类型，放到 STA 线程里构造，避免线程模型干扰
    var thread = new Thread(() =>
    {
        try
        {
            var vm = new SettingsViewModel(
                () => new TrustTableUpdater(counting, tempTarget, null),
                new ThrowingBackupStore(),
                settings);

            canUseNetwork = vm.CanUseNetwork;
            commandCanExecute = vm.CheckUpdateCommand.CanExecute(null);
            statusBefore = vm.UpdateStatusText;

            vm.CheckUpdateAsync().GetAwaiter().GetResult();
            statusAfterCheck = vm.UpdateStatusText;

            vm.UpdateNowAsync().GetAwaiter().GetResult();
            statusAfterUpdate = vm.UpdateStatusText;
            isBusyAfter = vm.IsBusy;
        }
        catch (Exception ex)
        {
            guardException = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();

    Console.WriteLine($"  CanUseNetwork={canUseNetwork} CheckUpdateCommand.CanExecute={commandCanExecute}");
    Console.WriteLine($"  状态文案: 初始「{statusBefore}」/ Check 后「{statusAfterCheck}」/ UpdateNow 后「{statusAfterUpdate}」");
    Console.WriteLine($"  CountingDownloader.CallCount={counting.CallCount} IsBusy={isBusyAfter}");
    Check("f.1 无异常", guardException is null, guardException is null ? "" : $"{guardException.GetType().Name}: {guardException.Message}");
    Check("f.2 CanUseNetwork=false", !canUseNetwork);
    Check("f.3 CheckUpdateCommand 不可执行", !commandCanExecute);
    Check("f.4 直调 CheckUpdateAsync 状态文案未变", statusAfterCheck == statusBefore, $"「{statusAfterCheck}」");
    Check("f.5 直调 UpdateNowAsync 状态文案未变", statusAfterUpdate == statusBefore, $"「{statusAfterUpdate}」");
    Check("f.6 一次网络请求都没发出（CallCount=0）", counting.CallCount == 0, $"实际 {counting.CallCount}");
    Check("f.7 IsBusy 未被置起", !isBusyAfter);
    Check("f.8 未写真实设置文件", !File.Exists(tempSettings), File.Exists(tempSettings) ? "临时设置文件被写入" : "");
}

// ---------------------------------------------------------------------------
// [g] 真实默认路径：生产构造函数跑一遍 %ProgramData%\RubbishCleaner\trust-table.json
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("[g] 真实默认路径（生产构造函数 TrustTableUpdater()）");
{
    var realPath = TrustTableLoader.UserTablePath;
    var realDir = Path.GetDirectoryName(realPath)!;
    var dirExistedBefore = Directory.Exists(realDir);
    var fileExistedBefore = File.Exists(realPath);
    Console.WriteLine($"  默认路径: {realPath}");
    Console.WriteLine($"  运行前: 目录存在={dirExistedBefore} 文件存在={fileExistedBefore}");

    string? savedOriginal = null;
    if (fileExistedBefore)
    {
        var original = await File.ReadAllTextAsync(realPath);
        var t = TrustTableLoader.Parse(original, "before");
        Console.WriteLine($"  运行前内容: {original.Length} 字符 v{t.Version} 白={t.Whitelist.Count} 黑={t.Blacklist.Count}");
        savedOriginal = Path.Combine(root, "programdata-original-trust-table.json");
        await File.WriteAllTextAsync(savedOriginal, original);
        File.Delete(realPath);
        Note("原文件已备份到探针目录并从默认路径移除（为了走到真实写入分支），测试后恢复");
    }

    try
    {
        using (var updater = new TrustTableUpdater())
        {
            var sw = Stopwatch.StartNew();
            var check = await updater.CheckUpdateAsync();
            sw.Stop();
            Console.WriteLine($"  CheckUpdateAsync: Available={check.Available} Current={check.CurrentVersion} Remote={check.RemoteVersion} Error={check.ErrorMessage ?? "null"} 耗时={sw.ElapsedMilliseconds} ms");
            Check("g.1 默认路径检查无错误", check.ErrorMessage is null, check.ErrorMessage ?? "null");
            Check("g.2 默认路径发现新版本", check.Available && check.RemoteVersion == ExpectedRemoteVersion, $"{check.Available}/{check.RemoteVersion}");

            var progress = new SyncProgress<string>(s => Note(s));
            var sw2 = Stopwatch.StartNew();
            var update = await updater.UpdateAsync(progress: progress);
            sw2.Stop();
            Console.WriteLine($"  UpdateAsync: Success={update.Success} Message={update.Message} {update.OldVersion}→{update.NewVersion} File={update.FilePath ?? "null"} 耗时={sw2.ElapsedMilliseconds} ms");
            Check("g.3 默认路径更新成功", update.Success, update.Message);
            Check("g.4 FilePath == 默认路径", string.Equals(update.FilePath, realPath, StringComparison.OrdinalIgnoreCase), update.FilePath ?? "null");
        }

        Check("g.5 默认路径文件已生成", File.Exists(realPath));
        if (File.Exists(realPath))
        {
            var json = await File.ReadAllTextAsync(realPath);
            var t = TrustTableLoader.Parse(json, "programdata");
            Console.WriteLine($"  写入结果: {new FileInfo(realPath).Length} B v{t.Version} Updated={t.Updated ?? "null"} 白={t.Whitelist.Count} 黑={t.Blacklist.Count}");
            Check("g.6 默认路径文件 v2", t.Version == ExpectedRemoteVersion, $"{t.Version}");
            Check("g.7 默认路径白名单 19", t.Whitelist.Count == ExpectedWhitelist, $"{t.Whitelist.Count}");
            Check("g.8 默认路径黑名单 7", t.Blacklist.Count == ExpectedBlacklist, $"{t.Blacklist.Count}");
            Check("g.9 默认路径 Updated=2026-10-24", t.Updated == ExpectedUpdated, t.Updated ?? "null");
        }
    }
    catch (Exception ex)
    {
        Check("g.3 默认路径更新成功", false, $"{ex.GetType().Name}: {ex.Message}");
    }
    finally
    {
        // 还原机器状态：删掉本轮生成的文件；原本存在的则放回备份。
        if (File.Exists(realPath))
        {
            try
            {
                File.Delete(realPath);
                Note("已删除本轮在默认路径生成的文件");
            }
            catch (Exception ex)
            {
                Note($"删除默认路径文件失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (savedOriginal is not null && File.Exists(savedOriginal))
        {
            try
            {
                File.Copy(savedOriginal, realPath, overwrite: true);
                Note("已恢复运行前的默认路径文件");
            }
            catch (Exception ex)
            {
                Note($"恢复默认路径文件失败: {ex.GetType().Name}: {ex.Message}（备份仍在 {savedOriginal}）");
            }
        }

        if (!dirExistedBefore && Directory.Exists(realDir))
        {
            try
            {
                var leftovers = Directory.GetFileSystemEntries(realDir);
                if (leftovers.Length == 0)
                {
                    Directory.Delete(realDir);
                    Note("已删除本轮创建的 %ProgramData%\\RubbishCleaner 目录（原本不存在）");
                }
                else
                {
                    Note($"目录保留（内有 {leftovers.Length} 项非本轮文件）: {string.Join(", ", leftovers)}");
                }
            }
            catch (Exception ex)
            {
                Note($"清理目录失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"  收尾后: 目录存在={Directory.Exists(realDir)} 文件存在={File.Exists(realPath)}");
    }
}

// ---------------------------------------------------------------------------
// 汇总
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== 汇总 ===");
if (failures.Count == 0)
{
    Console.WriteLine("  全部通过");
}
else
{
    Console.WriteLine($"  失败 {failures.Count} 项：");
    foreach (var f in failures)
    {
        Console.WriteLine("    - " + f);
    }
}

Console.WriteLine($"  日志目录: {logDir}（探针日志，未写用户真实日志目录）");
Console.WriteLine($"  探针目录: {root}");

if (failures.Count == 0)
{
    try
    {
        Directory.Delete(root, recursive: true);
    }
    catch
    {
        // 临时目录清理失败不影响结论
    }
}
else
{
    Console.WriteLine("  有失败项，保留探针目录供排查");
}

return failures.Count == 0 ? 0 : 2;

static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "RubbishCleaner.sln")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    return null;
}
