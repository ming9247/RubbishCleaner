using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace RubbishCleaner.App.Tests;

/// <summary>
/// 主窗口布局的静态回归测试（v0.9.2）。
///
/// <para>起因：v0.9.1 真机截图暴露两个布局缺陷——</para>
/// <list type="number">
/// <item>「已安装软件」的 Grid 只声明了 2 行，底部操作条却用 <c>Grid.Row="2"</c>；
/// WPF 会把越界行夹到**最后一行**（已用 `Grid.Measure/Arrange` 实测：子元素落在 row 1 的顶部并占满剩余高度），
/// 于是那条白色 Card 直接盖住软件列表 → 用户看到「一片空白，被白界面拦住」。</item>
/// <item>「设置」页叠了 4 张 Auto 高度卡片却没有滚动容器，窗口不够高时「关于软件」整块看不到。</item>
/// </list>
///
/// <para>两条都不是绑定/逻辑问题，ViewModel 单测永远发现不了（ADR-056 同一个坑）。
/// 这里直接解析 XAML 做结构断言：不启动窗口、不需要管理员、秒级完成。</para>
/// </summary>
public class XamlLayoutTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static XDocument LoadMainWindow()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Xaml", "MainWindow.xaml");
        Assert.True(File.Exists(path), $"找不到 MainWindow.xaml：{path}");
        return XDocument.Load(path);
    }

    private static int CountDefinitions(XElement grid, string containerName)
        => grid.Elements().FirstOrDefault(e => e.Name.LocalName == containerName)
              ?.Elements().Count(e => e.Name.LocalName is "RowDefinition" or "ColumnDefinition") ?? 0;

    private static int GridIndex(XElement element, string suffix)
    {
        var raw = element.Attributes()
            .FirstOrDefault(a => a.Name.LocalName == "Grid." + suffix)?.Value;
        return raw is null ? 0 : int.Parse(raw);
    }

    /// <summary>
    /// 任何 Grid 的子元素，其 <c>Grid.Row/Column</c>（含 Span）都必须落在已声明的行/列内。
    /// 这是上面第 1 个缺陷的通用守卫：以后再加一行忘了声明定义，会立刻红。
    /// </summary>
    [Fact]
    public void EveryGridChild_StaysWithinDeclaredRowsAndColumns()
    {
        var doc = LoadMainWindow();
        var problems = new List<string>();

        foreach (var grid in doc.Descendants().Where(e => e.Name.LocalName == "Grid"))
        {
            var rows = CountDefinitions(grid, "Grid.RowDefinitions");
            var cols = CountDefinitions(grid, "Grid.ColumnDefinitions");

            foreach (var child in grid.Elements())
            {
                // 定义容器与资源不是「被摆放的元素」。
                if (child.Name.LocalName is "Grid.RowDefinitions" or "Grid.ColumnDefinitions"
                    or "Grid.Resources" or "Grid.Children")
                {
                    continue;
                }

                var row = GridIndex(child, "Row");
                var rowSpan = Math.Max(1, GridIndex(child, "RowSpan"));
                var col = GridIndex(child, "Column");
                var colSpan = Math.Max(1, GridIndex(child, "ColumnSpan"));

                if (rows == 0 ? row > 0 : row + rowSpan > rows)
                {
                    problems.Add($"{child.Name.LocalName}: Grid.Row={row} RowSpan={rowSpan} 但只声明了 {rows} 行");
                }

                if (cols == 0 ? col > 0 : col + colSpan > cols)
                {
                    problems.Add($"{child.Name.LocalName}: Grid.Column={col} ColumnSpan={colSpan} 但只声明了 {cols} 列");
                }
            }
        }

        Assert.True(problems.Count == 0,
            "MainWindow.xaml 存在越界的 Grid 行/列：" + Environment.NewLine
            + string.Join(Environment.NewLine, problems));
    }

    /// <summary>「已安装软件」页签必须给底部操作条留出第三行（否则它会盖住列表）。</summary>
    [Fact]
    public void InstalledAppsTab_HasRoomForBottomActionBar()
    {
        var doc = LoadMainWindow();
        var tab = doc.Descendants()
            .Single(e => e.Name.LocalName == "TabItem"
                         && (string?)e.Attribute("Header") == "已安装软件");
        var grid = tab.Elements().Single(e => e.Name.LocalName == "Grid");

        Assert.True(CountDefinitions(grid, "Grid.RowDefinitions") >= 3,
            "「已安装软件」的 Grid 至少要 3 行：工具条 / 列表 / 底部操作条。");
        Assert.Contains(grid.Elements(),
            e => e.Name.LocalName == "Border" && GridIndex(e, "Row") == 2);
    }

    /// <summary>
    /// 「设置」页签的内容必须放在 ScrollViewer 里：4 张 Auto 高度卡片叠起来会超过窗口高度，
    /// 没有滚动容器时最后一张「关于软件」直接看不见。
    /// </summary>
    [Fact]
    public void SettingsTab_ContentIsScrollable()
    {
        var doc = LoadMainWindow();
        var tab = doc.Descendants()
            .Single(e => e.Name.LocalName == "TabItem"
                         && (string?)e.Attribute("Header") == "设置");

        var scroll = tab.Elements().FirstOrDefault(e => e.Name.LocalName == "ScrollViewer");
        Assert.NotNull(scroll);
        Assert.Equal("Auto", (string?)scroll!.Attribute("VerticalScrollBarVisibility"));

        // 内容仍然只有一个 Grid（滚动容器不能把布局拆散）。
        Assert.Single(scroll.Elements().Where(e => e.Name.LocalName == "Grid"));
    }

    /// <summary>
    /// 反向保护：列表类页签**不能**套 ScrollViewer——内层 ListBox 会拿到无限高度，
    /// 失去自身滚动与虚拟化。这条断言防止「为了修设置页，把所有页签都包一层」。
    /// </summary>
    [Theory]
    [InlineData("启动项检测")]
    [InlineData("已安装软件")]
    [InlineData("残留扫描")]
    [InlineData("备份历史")]
    public void ListBackedTabs_AreNotWrappedInScrollViewer(string header)
    {
        var doc = LoadMainWindow();
        var tab = doc.Descendants()
            .Single(e => e.Name.LocalName == "TabItem" && (string?)e.Attribute("Header") == header);

        Assert.DoesNotContain(tab.Elements(), e => e.Name.LocalName == "ScrollViewer");
    }
}
