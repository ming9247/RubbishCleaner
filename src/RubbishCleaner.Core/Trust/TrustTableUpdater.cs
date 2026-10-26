using System.Text.Json;
using RubbishCleaner.Common.Net;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Trust;

/// <summary>
/// 特征库在线更新器(v0.7)。
/// 
/// <para>从GitHub仓库下载最新的 trust-table.json，
/// 保存到用户目录 %ProgramData%\RubbishCleaner\trust-table.json。
/// 三层加载机制不变：内置表 → 程序目录表 → 用户表(网络更新会写到这里)。</para>
/// 
/// <para><b>版本号语义</b>：只有当远程版本号 &gt; 本地版本号时才更新。
/// 这保证用户即使手工编辑了本地表并抬高版本号，也不会被自动覆盖。</para>
/// 
/// <para><b>安全性</b>：下载后校验JSON格式有效性，无效不写入。
/// 采用先写临时文件、校验后再替换的两阶段提交模式。</para>
/// </summary>
public sealed class TrustTableUpdater : IDisposable
{
    private readonly HttpDownloader _downloader;
    private readonly string _targetPath;
    private readonly Func<string, string, TrustTable>? _parser;

    /// <summary>GitHub raw文件URL(默认从main分支读取)</summary>
    public const string DefaultGitHubRawUrl = 
        "https://raw.githubusercontent.com/ming9247/RubbishCleaner/main/src/RubbishCleaner.App/Resources/trust-table.json";

    /// <summary>
    /// 生产构造：真实下载器、真实用户目录。
    /// </summary>
    public TrustTableUpdater(string? targetPath = null)
        : this(new HttpDownloader(), targetPath, null)
    {
    }

    /// <summary>
    /// 测试构造：可注入下载器和解析器。
    /// </summary>
    public TrustTableUpdater(
        HttpDownloader downloader,
        string? targetPath,
        Func<string, string, TrustTable>? parser)
    {
        _downloader = downloader;
        _targetPath = targetPath ?? TrustTableLoader.UserTablePath;
        _parser = parser ?? ((json, source) => TrustTableLoader.Parse(json, source));
    }

    /// <summary>
    /// 检查更新：比较本地版本与远程版本。
    /// </summary>
    /// <param name="remoteUrl">远程trust-table.json的URL</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>更新检查结果</returns>
    public async Task<UpdateCheckResult> CheckUpdateAsync(
        string remoteUrl = DefaultGitHubRawUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 读取当前本地版本：取三层合并后的有效版本(内置表 → 程序目录表 → 用户表)，
            // 避免干净机器上用户表缺失时把"当前版本"算成 0(界面显示"当前 v0")。
            // 写入目标仍只写用户表(ADR-008：用户表只增不覆盖内置表)。
            var currentVersion = TrustTableLoader
                .Load(TrustTableLoader.DefaultAppTablePath(), _targetPath)
                .Version;

            // 下载远程版本信息(完整文件,后续更新时复用)
            var tempPath = Path.GetTempFileName();
            try
            {
                var downloadResult = await _downloader.DownloadFileAsync(
                    remoteUrl,
                    tempPath,
                    progress: null,
                    cancellationToken);

                if (!downloadResult.Success)
                {
                    return new UpdateCheckResult
                    {
                        Available = false,
                        ErrorMessage = downloadResult.ErrorMessage
                    };
                }

                // 解析远程版本号
                var remoteJson = await File.ReadAllTextAsync(tempPath, cancellationToken);
                var remoteTable = _parser!(remoteJson, "remote");

                if (remoteTable == TrustTable.Empty)
                {
                    return new UpdateCheckResult
                    {
                        Available = false,
                        ErrorMessage = "远程文件格式无效"
                    };
                }

                var hasUpdate = remoteTable.Version > currentVersion;

                return new UpdateCheckResult
                {
                    Available = hasUpdate,
                    CurrentVersion = currentVersion,
                    RemoteVersion = remoteTable.Version,
                    RemoteUpdatedDate = remoteTable.Updated,
                    CachedRemoteContent = hasUpdate ? remoteJson : null
                };
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult
            {
                Available = false,
                ErrorMessage = $"检查更新失败: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// 执行更新：下载并保存远程特征库。
    /// </summary>
    /// <param name="remoteUrl">远程trust-table.json的URL</param>
    /// <param name="progress">进度回调</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>更新结果</returns>
    public async Task<UpdateResult> UpdateAsync(
        string remoteUrl = DefaultGitHubRawUrl,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            progress?.Report("正在检查更新...");

            var checkResult = await CheckUpdateAsync(remoteUrl, cancellationToken);

            if (!string.IsNullOrEmpty(checkResult.ErrorMessage))
            {
                return new UpdateResult
                {
                    Success = false,
                    Message = checkResult.ErrorMessage
                };
            }

            if (!checkResult.Available)
            {
                return new UpdateResult
                {
                    Success = false,
                    Message = $"已是最新版本(v{checkResult.CurrentVersion})"
                };
            }

            progress?.Report($"发现新版本: v{checkResult.RemoteVersion}");

            string remoteJson;
            
            if (checkResult.CachedRemoteContent != null)
            {
                // 复用检查时下载的内容
                remoteJson = checkResult.CachedRemoteContent;
            }
            else
            {
                // 重新下载
                progress?.Report("正在下载特征库...");
                var tempPath = Path.GetTempFileName();
                try
                {
                    var downloadProgress = new Progress<(long, long?)>(bytesProgress =>
                {
                    var (downloaded, total) = bytesProgress;
                    if (total.HasValue)
                    {
                        var percent = (int)(downloaded * 100 / total.Value);
                        progress?.Report($"下载中... {percent}%");
                    }
                });

                var downloadResult = await _downloader.DownloadFileAsync(
                        remoteUrl,
                        tempPath,
                        downloadProgress,
                        cancellationToken);

                    if (!downloadResult.Success)
                    {
                        return new UpdateResult
                        {
                            Success = false,
                            Message = downloadResult.ErrorMessage ?? "下载失败"
                        };
                    }

                    remoteJson = await File.ReadAllTextAsync(tempPath, cancellationToken);
                }
                finally
                {
                    if (File.Exists(tempPath))
                    {
                        try { File.Delete(tempPath); } catch { }
                    }
                }
            }

            // 校验JSON有效性
            progress?.Report("正在校验文件...");
            var remoteTable = _parser!(remoteJson, "remote");
            if (remoteTable == TrustTable.Empty)
            {
                return new UpdateResult
                {
                    Success = false,
                    Message = "下载的文件格式无效"
                };
            }

            // 保存到用户目录
            progress?.Report("正在保存...");
            var directory = Path.GetDirectoryName(_targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 原子替换：先写同目录临时文件，落盘成功后再用 File.Move 覆盖目标文件。
            // 直接覆盖写(_targetPath)时若进程中途被杀会留下截断的 trust-table.json。
            var stagingPath = _targetPath + ".tmp";
            try
            {
                await File.WriteAllTextAsync(stagingPath, remoteJson, cancellationToken);
                File.Move(stagingPath, _targetPath, overwrite: true);
            }
            finally
            {
                // 任何失败路径都不留临时文件
                if (File.Exists(stagingPath))
                {
                    try { File.Delete(stagingPath); } catch { }
                }
            }

            progress?.Report($"更新完成: v{checkResult.CurrentVersion} → v{checkResult.RemoteVersion}");

            return new UpdateResult
            {
                Success = true,
                Message = $"特征库已更新到 v{checkResult.RemoteVersion}",
                OldVersion = checkResult.CurrentVersion,
                NewVersion = checkResult.RemoteVersion,
                FilePath = _targetPath
            };
        }
        catch (Exception ex)
        {
            return new UpdateResult
            {
                Success = false,
                Message = $"更新失败: {ex.Message}"
            };
        }
    }

    public void Dispose()
    {
        _downloader.Dispose();
    }
}

/// <summary>
/// 更新检查结果。
/// </summary>
public sealed record UpdateCheckResult
{
    /// <summary>是否有可用更新</summary>
    public required bool Available { get; init; }

    /// <summary>当前本地版本号</summary>
    public int CurrentVersion { get; init; }

    /// <summary>远程版本号</summary>
    public int RemoteVersion { get; init; }

    /// <summary>远程更新日期</summary>
    public string? RemoteUpdatedDate { get; init; }

    /// <summary>失败时的错误消息</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>内部缓存:检查时已下载的远程内容(避免重复下载)</summary>
    internal string? CachedRemoteContent { get; init; }
}

/// <summary>
/// 更新执行结果。
/// </summary>
public sealed record UpdateResult
{
    /// <summary>是否成功</summary>
    public required bool Success { get; init; }

    /// <summary>结果消息</summary>
    public required string Message { get; init; }

    /// <summary>成功时:更新前的版本号</summary>
    public int OldVersion { get; init; }

    /// <summary>成功时:更新后的版本号</summary>
    public int NewVersion { get; init; }

    /// <summary>成功时:保存的文件路径</summary>
    public string? FilePath { get; init; }
}
