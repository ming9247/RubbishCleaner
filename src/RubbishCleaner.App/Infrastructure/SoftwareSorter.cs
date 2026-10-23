using System.ComponentModel;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.App.Infrastructure;

/// <summary>
/// 按列排序软件列表。
///
/// 为什么单独抽出来：排序键包含"风险等级""确认表判定"这类派生值，
/// 放在 ViewModel 里只能靠点击 UI 验证；抽成纯函数后可以直接单元测试。
/// 只依赖 Core 的 <see cref="SoftwareProfile"/>，不引用任何 WPF 类型。
/// </summary>
public static class SoftwareSorter
{
    /// <summary>对列表排序；主键相同时按分数降序，保证结果稳定不抖动。</summary>
    public static IReadOnlyList<SoftwareProfile> Sort(
        IEnumerable<SoftwareProfile> source,
        SortColumn column,
        ListSortDirection direction)
    {
        ArgumentNullException.ThrowIfNull(source);

        return column switch
        {
            SortColumn.Level => Order(source, p => (int)p.Score.Level, direction),
            SortColumn.Name => Order(source, p => p.DisplayName, direction),
            SortColumn.StartupSource => Order(source, p => StartupKey(p), direction),
            SortColumn.Publisher => Order(source, p => p.Publisher ?? string.Empty, direction),
            SortColumn.Trust => Order(source, p => (int)p.Trust, direction),
            _ => Order(source, p => p.Score.Total, direction),
        };
    }

    /// <summary>取第一个启动项的类别名作为排序键，保证同一软件多启动点时结果稳定。</summary>
    private static string StartupKey(SoftwareProfile profile)
        => profile.StartupEntries.Count == 0
            ? string.Empty
            : profile.StartupEntries[0].Source.ToDisplayName();

    private static IReadOnlyList<SoftwareProfile> Order<TKey>(
        IEnumerable<SoftwareProfile> source,
        Func<SoftwareProfile, TKey> keySelector,
        ListSortDirection direction)
    {
        var comparer = Comparer<TKey>.Default;

        return (direction == ListSortDirection.Ascending
                ? source.OrderBy(keySelector, comparer)
                : source.OrderByDescending(keySelector, comparer))
            .ThenByDescending(p => p.Score.Total)   // 稳定次序：同主键时分数高的在前
            .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
