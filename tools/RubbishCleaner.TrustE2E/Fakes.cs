using RubbishCleaner.Common.Net;
using RubbishCleaner.Core.BackupHistory;

namespace RubbishCleaner.TrustE2E;

/// <summary>
/// 计数下载器：继承真实的 HttpDownloader，只统计「下载方法被调用几次」。
/// 用于证明联网守卫在直调场景下真的没有发起任何请求（计数器必须保持 0）。
/// </summary>
internal sealed class CountingDownloader : HttpDownloader
{
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public override Task<DownloadResult> DownloadFileAsync(
        string url,
        string destinationPath,
        IProgress<(long Downloaded, long? Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
        return base.DownloadFileAsync(url, destinationPath, progress, cancellationToken);
    }
}

/// <summary>
/// 备份历史桩：本探针只验证更新链路，备份相关方法永不被调用；
/// 一旦被调用即抛异常，避免误用真实 BackupHistoryStore 触碰用户备份目录。
/// </summary>
internal sealed class ThrowingBackupStore : IBackupHistoryStore
{
    public Task<BackupHistoryResult> ListAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("探针不涉及备份列表");

    public Task<BackupRollbackResult> RollbackAsync(BackupHistoryEntry entry, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("探针不涉及备份回滚");

    public Task<BackupCleanupResult> CleanOldBackupsAsync(int keepCount = 30, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("探针不涉及备份清理");
}

/// <summary>
/// 同步进度收集器：Progress&lt;T&gt; 在没有同步上下文时会把回调丢到线程池，
/// 探针需要在 UpdateAsync 返回时就能读到完整进度序列，所以用同步实现。
/// </summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _onReport;

    public SyncProgress(Action<T> onReport) => _onReport = onReport;

    public void Report(T value) => _onReport(value);
}
