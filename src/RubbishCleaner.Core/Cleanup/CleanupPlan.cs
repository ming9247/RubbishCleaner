using Microsoft.Win32;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Cleanup;

/// <summary>一条待清理的启动项。</summary>
public sealed record CleanupTarget(
    string ProfileName,
    string EntryName,
    StartupSourceKind Source,
    RegistryAnchor Anchor,
    string? ExecutablePath,
    string? RawCommand);

/// <summary>
/// 清理计划。
///
/// 生成计划与执行删除分离，是为了让"删什么"这件事<b>先能被看到、被拒绝、被测试</b>，
/// 而不是一步到位就写注册表。
/// </summary>
public sealed class CleanupPlan
{
    public IReadOnlyList<CleanupTarget> Targets { get; init; } = [];

    /// <summary>被拒绝清理的项及原因（例如不是 Run 类、目标文件其实还在）。</summary>
    public IReadOnlyList<(string EntryName, string Reason)> Skipped { get; init; } = [];

    public bool IsEmpty => Targets.Count == 0;

    public int Count => Targets.Count;
}
