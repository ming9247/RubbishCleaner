using System.ComponentModel;
using RubbishCleaner.App.Infrastructure;

namespace RubbishCleaner.App.Tests;

public class SortStateTests
{
    [Fact]
    public void Default_IsScoreDescending()
    {
        var sort = new SortState();

        Assert.Equal(SortColumn.Score, sort.Column);
        Assert.Equal(ListSortDirection.Descending, sort.Direction);
    }

    [Fact]
    public void Toggle_SameColumn_FlipsDirection()
    {
        var sort = new SortState();

        sort.Toggle(SortColumn.Score);
        Assert.Equal(ListSortDirection.Ascending, sort.Direction);

        sort.Toggle(SortColumn.Score);
        Assert.Equal(ListSortDirection.Descending, sort.Direction);
    }

    [Theory]
    [InlineData(SortColumn.Level, ListSortDirection.Descending)]
    [InlineData(SortColumn.Trust, ListSortDirection.Descending)]
    [InlineData(SortColumn.Name, ListSortDirection.Ascending)]
    [InlineData(SortColumn.Publisher, ListSortDirection.Ascending)]
    [InlineData(SortColumn.StartupSource, ListSortDirection.Ascending)]
    public void Toggle_NewColumn_UsesNaturalDefaultDirection(SortColumn column, ListSortDirection expected)
    {
        var sort = new SortState();

        sort.Toggle(column);

        Assert.Equal(column, sort.Column);
        Assert.Equal(expected, sort.Direction);
    }

    [Fact]
    public void Toggle_DefaultActiveColumn_FlipsInsteadOfResetting()
    {
        // Score 是默认激活列，所以第一次点击它是"翻转"，不是"切到新列"。
        // 这是标准表头行为：点已激活的列 = 换方向。
        var sort = new SortState();

        sort.Toggle(SortColumn.Score);

        Assert.Equal(SortColumn.Score, sort.Column);
        Assert.Equal(ListSortDirection.Ascending, sort.Direction);
    }

    [Fact]
    public void Toggle_SwitchingColumn_ResetsToNaturalDirection()
    {
        var sort = new SortState();
        sort.Toggle(SortColumn.Name);   // Name 升序
        sort.Toggle(SortColumn.Name);   // 再点一次变降序

        sort.Toggle(SortColumn.Score);  // 切到新列，应回到该列默认（降序）

        Assert.Equal(SortColumn.Score, sort.Column);
        Assert.Equal(ListSortDirection.Descending, sort.Direction);
    }

    [Fact]
    public void IndicatorFor_OnlyShowsOnActiveColumn()
    {
        var sort = new SortState(); // 默认 Score 降序

        Assert.Equal("▼", sort.IndicatorFor(SortColumn.Score));
        Assert.Equal(string.Empty, sort.IndicatorFor(SortColumn.Name));
    }

    [Fact]
    public void IndicatorFor_ReflectsDirection()
    {
        var sort = new SortState();
        sort.Toggle(SortColumn.Score); // 翻转为升序

        Assert.Equal("▲", sort.IndicatorFor(SortColumn.Score));
    }

    [Fact]
    public void Toggle_RaisesPropertyChanged_SoUiCanRefresh()
    {
        var sort = new SortState();
        var changed = new List<string?>();
        sort.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        sort.Toggle(SortColumn.Name);

        Assert.Contains(nameof(SortState.Column), changed);
        Assert.Contains(nameof(SortState.Direction), changed);
    }
}
