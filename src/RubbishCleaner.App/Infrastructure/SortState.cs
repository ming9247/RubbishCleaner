using System.ComponentModel;

namespace RubbishCleaner.App.Infrastructure;

/// <summary>列表可排序的列。</summary>
public enum SortColumn
{
    Level,
    Name,
    StartupSource,
    Publisher,
    Score,
    Trust,
}

/// <summary>
/// 排序状态：记住列与方向，供 ViewModel 应用到集合。
///
/// 为什么在 ViewModel 层排序而不在 XAML 里做：
/// 排序键包含"风险等级"这类派生值（由分数计算得出），
/// 用 <c>CollectionViewSource</c> 需要在 XAML 里写比较器或转换器，可读性差且难测试。
/// </summary>
public sealed class SortState : ObservableObject
{
    private SortColumn _column = SortColumn.Score;
    private ListSortDirection _direction = ListSortDirection.Descending;

    /// <summary>当前排序列。</summary>
    public SortColumn Column
    {
        get => _column;
        private set => SetProperty(ref _column, value);
    }

    /// <summary>当前排序方向。</summary>
    public ListSortDirection Direction
    {
        get => _direction;
        private set => SetProperty(ref _direction, value);
    }

    /// <summary>
    /// 点击表头：同一列则切换方向，不同列则切到该列并使用该列的"自然默认方向"。
    /// 分数与等级默认降序（先看危险），名称与发行商默认升序（便于查找）。
    /// </summary>
    public void Toggle(SortColumn column)
    {
        if (_column == column)
        {
            Direction = _direction == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }
        else
        {
            Column = column;
            Direction = column is SortColumn.Score or SortColumn.Level or SortColumn.Trust
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }
    }

    /// <summary>表头箭头提示，例如 "分数 ▼"。</summary>
    public string IndicatorFor(SortColumn column)
    {
        if (_column != column)
        {
            return string.Empty;
        }

        return _direction == ListSortDirection.Ascending ? "▲" : "▼";
    }
}
