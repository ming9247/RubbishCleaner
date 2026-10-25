using Microsoft.Win32;

namespace RubbishCleaner.Core.BackupHistory;

/// <summary>
/// 备份历史（v0.6）——让「可还原」从"去文件夹里双击 .reg"变成界面里的一键回滚。
///
/// <para>备份文件的真实形态（v0.4.1 起）：\`orphan-{键名}-{yyyyMMddHHmmss}.reg\`，
/// UTF-16LE+BOM，段头是<b>物理路径</b>（32 位视图写 WOW6432Node，ADR-049）。
/// 历史条目只从<b>文件名与文件内容</b>反推，不做任何数据库——
/// 备份目录本身已经是事实的单一来源，再建索引迟早会失同步。</para>
///
/// <para><b>回滚的安全设计</b>：回滚就是 \`reg import\`，会把键<b>原样写回</b>。
/// 危险点不在「写回」（内容是当初删掉前的原样），而在「写回之前目标键又被谁创建过」——
/// 直接 import 会把新键的内容覆盖掉。所以回滚前必须重读目标键：若已存在，拒绝并提示人工核对。</para>
/// </summary>

/// <summary>一条备份历史条目（由备份目录里的 .reg 文件解析而来）。</summary>
public sealed record BackupHistoryEntry
{
    /// <summary>备份文件完整路径。</summary>
    public required string FilePath { get; init; }

    /// <summary>文件名（供 UI 直接展示）。</summary>
    public required string FileName { get; init; }

    /// <summary>备份的原键完整路径（从 .reg 段头解析，物理路径形式）。</summary>
    public required string KeyPath { get; init; }

    /// <summary>被清理时展示的名称（从 .reg 的 DisplayName 值解析，可能为空）。</summary>
    public string? DisplayName { get; init; }

    /// <summary>备份时间（取自文件名的时间戳；解析失败回退文件修改时间）。</summary>
    public required DateTimeOffset BackedUpAt { get; init; }

    /// <summary>文件大小（字节）。</summary>
    public required long FileSizeBytes { get; init; }

    /// <summary>回滚前检查的结论；null 表示当前可以回滚。</summary>
    public string? RollbackBlockReason { get; init; }

    /// <summary>当前是否允许回滚（键不存在且文件完好）。</summary>
    public bool CanRollback => RollbackBlockReason is null;
}

/// <summary>备份历史扫描结果。</summary>
public sealed record BackupHistoryResult
{
    public IReadOnlyList<BackupHistoryEntry> Entries { get; init; } = [];

    /// <summary>扫描的备份目录；不存在时 Entries 为空且 DegradedReason 说明。</summary>
    public string Directory { get; init; } = "";

    public string? DegradedReason { get; init; }

    public bool IsDegraded => !string.IsNullOrWhiteSpace(DegradedReason);

    public TimeSpan Duration { get; init; }
}

/// <summary>回滚结果。</summary>
public sealed record BackupRollbackResult
{
    public required BackupHistoryEntry Entry { get; init; }

    public required bool Success { get; init; }

    public required string Message { get; init; }

    /// <summary>回滚是写注册表操作，成功与否都要留痕；这里带回所用的时间戳文件名。</summary>
    public string? LogHint { get; init; }
}

/// <summary>备份清理结果(v0.7)。</summary>
public sealed record BackupCleanupResult
{
    /// <summary>是否成功</summary>
    public required bool Success { get; init; }

    /// <summary>结果消息</summary>
    public required string Message { get; init; }

    /// <summary>清理前的备份总数</summary>
    public int TotalBackups { get; init; }

    /// <summary>保留的备份数</summary>
    public int KeptBackups { get; init; }

    /// <summary>移除的备份数</summary>
    public int RemovedBackups { get; init; }

    /// <summary>失败的文件列表（如果有）</summary>
    public IReadOnlyList<string>? FailedFiles { get; init; }

    /// <summary>耗时</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>备份历史扫描与回滚（v0.6）。</summary>
public interface IBackupHistoryStore
{
    /// <summary>扫描备份目录，解析每个 .reg 的段头与 DisplayName。只读。</summary>
    Task<BackupHistoryResult> ListAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 把一个备份原样导入回注册表。
    ///
    /// <para><b>前置检查</b>：目标键当前已存在时拒绝（防止覆盖新创建的键）；</para>
    /// <para><b>执行方式</b>：\`reg import\`——与用户双击 .reg 完全同路径，不自己写注册表 API；</para>
    /// <para><b>回滚完成后必须复核</b>：键确实回来了（与 v0.4 的删除后复核同一约定）。</para>
    /// </summary>
    Task<BackupRollbackResult> RollbackAsync(
        BackupHistoryEntry entry,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 清理旧备份文件（v0.7）：按备份时间保留最近 keepCount 个，
    /// 其余移入回收站（永不硬删除）。
    /// </summary>
    /// <param name="keepCount">保留数量（必须≥1）</param>
    /// <param name="progress">进度回调</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>清理结果</returns>
    Task<BackupCleanupResult> CleanOldBackupsAsync(
        int keepCount = 30,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}