using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Leftover;

/// <summary>一条待清理的残留目录。</summary>
public sealed record LeftoverCleanupTarget(
    string DirectoryPath,
    string DisplayName,
    LeftoverKind Kind,
    long TotalBytes,
    int FileCount,
    IReadOnlyList<string> Evidence);

/// <summary>
/// 删除（移入回收站）的结果。
///
/// 刻意在 Core 里定义，而不是直接复用 Common 的 <c>RecycleResult</c>：
/// Core 不应该知道"删除"是用回收站、还是别的什么机制实现的。
/// 谁提供删除能力由 App 层注入，这样 Core 的清理逻辑可以在纯内存里被测透。
/// </summary>
public sealed record LeftoverDeleteOutcome(bool Success, string Message)
{
    public static LeftoverDeleteOutcome Ok(string message = "已移入回收站") => new(true, message);

    public static LeftoverDeleteOutcome Fail(string message) => new(false, message);
}

/// <summary>
/// 残留清理计划。
///
/// 与 <see cref="Cleanup.CleanupPlan"/>（启动项清理）一样，"生成计划"与"执行删除"分离，
/// 目的是让"要删什么"先能被看到、被拒绝、被测试，而不是一步到位就动磁盘。
/// </summary>
public sealed class LeftoverCleanupPlan
{
    public IReadOnlyList<LeftoverCleanupTarget> Targets { get; init; } = [];

    /// <summary>被拒绝清理的项及原因。</summary>
    public IReadOnlyList<(string DirectoryPath, string Reason)> Skipped { get; init; } = [];

    public bool IsEmpty => Targets.Count == 0;

    public int Count => Targets.Count;

    public long TotalBytes => Targets.Sum(t => t.TotalBytes);
}

/// <summary>单条清理结果。</summary>
public sealed record LeftoverCleanupResult(
    LeftoverCleanupTarget Target,
    bool Success,
    string Message);

/// <summary>
/// 残留目录清理器。
///
/// <b>四条安全原则（这是本类的全部价值）</b>：
/// <list type="number">
/// <item>
/// <b>只移入回收站，绝不永久删除</b>。误判是必然会发生的，用户必须能自己还原。
/// 因此执行层只接受"移入回收站"这一种删除方式，代码里<b>不存在</b>永久删除的路径。
/// </item>
/// <item><b>只接受高置信度项</b>。低/中置信度（名字像但没硬证据）永远不进计划。</item>
/// <item>
/// <b>执行前重新校验</b>。从扫描到用户点确认之间，软件可能被重装、目录可能已被删除。
/// 此时必须跳过而不是硬删——与启动项清理的 <c>StartupCleaner.Execute</c> 是同一套思路。
/// </item>
/// <item><b>逐项独立执行、逐项报告</b>。一个失败不影响其余，也不把失败说成成功。</item>
/// </list>
/// </summary>
public sealed class LeftoverCleaner
{
    private readonly Func<string, LeftoverDeleteOutcome> _delete;
    private readonly Func<string, bool> _directoryExists;

    /// <summary>注入删除与存在性判定。生产环境由 App 层传入回收站实现，测试里传假实现。</summary>
    public LeftoverCleaner(Func<string, LeftoverDeleteOutcome> delete, Func<string, bool>? directoryExists = null)
    {
        _delete = delete ?? throw new ArgumentNullException(nameof(delete));
        _directoryExists = directoryExists ?? SafeDirectoryExists;
    }

    /// <summary>
    /// 由扫描结果生成清理计划。
    ///
    /// 拒绝条件：
    /// <list type="bullet">
    /// <item>非高置信度；</item>
    /// <item>未通过安全闸门（<see cref="LeftoverItem.BlockReason"/> 非空）；</item>
    /// <item>目录已不存在（没什么可删的，也避免回收站操作报错）。</item>
    /// </list>
    /// </summary>
    public LeftoverCleanupPlan BuildPlan(IEnumerable<LeftoverItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var targets = new List<LeftoverCleanupTarget>();
        var skipped = new List<(string, string)>();

        foreach (var item in items)
        {
            if (item.Confidence != LeftoverConfidence.High)
            {
                skipped.Add((item.DirectoryPath, $"置信度不足（{item.Confidence}），仅作提示"));
                continue;
            }

            if (item.BlockReason is not null)
            {
                skipped.Add((item.DirectoryPath, item.BlockReason));
                continue;
            }

            if (!_directoryExists(item.DirectoryPath))
            {
                skipped.Add((item.DirectoryPath, "目录已不存在"));
                continue;
            }

            targets.Add(new LeftoverCleanupTarget(
                item.DirectoryPath,
                item.DisplayName,
                item.Kind,
                item.TotalBytes,
                item.FileCount,
                item.Evidence));
        }

        return new LeftoverCleanupPlan { Targets = targets, Skipped = skipped };
    }

    /// <summary>
    /// 执行清理：逐个移入回收站。
    ///
    /// 注意这里<b>没有</b>"失败就永久删除"的兜底分支，也没有绕过回收站的选项——
    /// 这是刻意留下的空洞：清理残留永远不该是不可逆的。
    /// </summary>
    public IReadOnlyList<LeftoverCleanupResult> Execute(LeftoverCleanupPlan plan, IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var results = new List<LeftoverCleanupResult>(plan.Count);
        var completed = 0;

        foreach (var target in plan.Targets)
        {
            // 执行前重新校验：扫描到确认之间目录可能已被删除，也可能被换成了别的东西。
            if (!_directoryExists(target.DirectoryPath))
            {
                results.Add(new LeftoverCleanupResult(target, false, "目录已不存在，已跳过"));
                continue;
            }

            var outcome = _delete(target.DirectoryPath);
            results.Add(new LeftoverCleanupResult(target, outcome.Success, outcome.Message));

            completed++;
            progress?.Report($"已处理 {completed}/{plan.Count}：{target.DisplayName}");
        }

        return results;
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
