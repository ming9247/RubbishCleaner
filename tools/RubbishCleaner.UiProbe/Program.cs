using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RubbishCleaner.App.Views;

namespace RubbishCleaner.UiProbe;

/// <summary>
/// 真实 WPF 布局探针（v0.9.2）。
///
/// <para>为什么需要它：v0.9.1 的两个缺陷（已安装软件列表被底部操作条盖住、设置页滚不到底）
/// 都是「ViewModel 全绿但界面不对」，单元测试和 XAML 静态断言只能守结构，守不住几何。
/// 这个探针加载真实的 App 资源与 MainWindow，用 WPF 自己的 Measure/Arrange 量出坐标，
/// 断言「不重叠」「能滚到底」。不需要管理员、不显示窗口。</para>
///
/// <para>用法：<c>dotnet run --project tools/RubbishCleaner.UiProbe -c Debug</c>，
/// 退出码 0 = 全部通过。</para>
/// </summary>
public static class Program
{
    private static int _failed;

    [STAThread]
    public static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // 重要：XAML 里的相对 pack URI（如 Icon="/app.ico"）默认按**入口程序集**解析。
        // 探针的入口程序集是 UiProbe，找不到 App 里的资源。.NET Core 的 Application 在类型初始化时
        // 就把 ResourceAssembly 设成入口程序集了，公开属性 settable 只有一次 → 这里直接改私有静态字段。
        ForceResourceAssembly(typeof(RubbishCleaner.App.App).Assembly);

        var app = new RubbishCleaner.App.App
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
        app.InitializeComponent();   // 必须：MainWindow 的 StaticResource 全在 App.xaml 合并字典里

        var window = new MainWindow();
        var tabs = (TabControl)window.FindName("MainTabs")!;

        CheckInstalledAppsTab(tabs);
        CheckSettingsTab(tabs);
        CheckMultiSelect(tabs);

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? "==> UI 探针全部通过" : $"==> UI 探针失败 {_failed} 项");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>把 WPF 的资源程序集指向 RubbishCleaner.App（探针不在 App 程序集里，相对 pack URI 会解析错）。</summary>
    private static void ForceResourceAssembly(System.Reflection.Assembly assembly)
    {
        var type = typeof(Application);
        var fields = type.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(System.Reflection.Assembly))
            .ToList();
        foreach (var f in fields)
        {
            f.SetValue(null, assembly);
        }
        if (fields.Count == 0)
        {
            throw new InvalidOperationException("找不到 WPF 的资源程序集字段，无法把 pack URI 指到 App 程序集");
        }
        Console.WriteLine("  · 资源程序集已指向 " + assembly.GetName().Name + "（字段：" + string.Join(",", fields.Select(f => f.Name)) + "）");
    }

    // ───────── 已安装软件：列表不能被底部操作条盖住 ─────────

    private static void CheckInstalledAppsTab(TabControl tabs)
    {
        Console.WriteLine("[1] 已安装软件页签");
        var content = TabContent(tabs, "已安装软件");

        foreach (var height in new double[] { 700, 520, 420 })
        {
            Layout(content, 1200, height);

            var list = Descendants(content)
                .OfType<Border>()
                .FirstOrDefault(b => Grid.GetRow(b) == 1 && b.Child is Grid);
            var bottom = Descendants(content)
                .OfType<Border>()
                .FirstOrDefault(b => Grid.GetRow(b) == 2);

            if (list is null || bottom is null)
            {
                Fail($"高度 {height}: 找不到列表卡（row 1）或底部操作条（row 2）");
                continue;
            }

            var listBottom = list.TranslatePoint(new Point(0, list.ActualHeight), content).Y;
            var barTop = bottom.TranslatePoint(new Point(0, 0), content).Y;

            Pass($"{height}px：列表高 {list.ActualHeight:F0}px，底部条从 y={barTop:F0} 开始（列表底 y={listBottom:F0}）");
            Check(listBottom <= barTop + 0.5,
                $"{height}px：底部操作条不得压住列表（列表底 {listBottom:F0} > 条顶 {barTop:F0}）");
            Check(list.ActualHeight >= 100,
                $"{height}px：列表必须有可见高度（实际 {list.ActualHeight:F0}px）");
        }
        Console.WriteLine();
    }

    // ───────── 已安装软件：多选与卸载按钮（v0.9.3） ─────────

    private static void CheckMultiSelect(TabControl tabs)
    {
        Console.WriteLine("[3] 已安装软件的多选支持");
        var content = TabContent(tabs, "已安装软件");
        Layout(content, 1200, 700);

        var list = Descendants(content).OfType<ListBox>().FirstOrDefault();
        if (list is null)
        {
            Fail("找不到软件列表 ListBox");
            Console.WriteLine();
            return;
        }

        Check(list.SelectionMode == SelectionMode.Extended,
            $"ListBox.SelectionMode 必须是 Extended（实际 {list.SelectionMode}）——否则 Ctrl/Shift 多选无效");
        Check(list.SelectionMode != SelectionMode.Single, "不能是默认的 Single");

        var button = Descendants(content)
            .OfType<Button>()
            .FirstOrDefault(b => (b.Content as string)?.Contains("卸载选中") == true);
        Check(button is not null && button.Command is not null,
            "「卸载选中的软件」按钮存在且绑定了命令（多选时文案会变成「卸载选中的 N 个软件」）");
        Console.WriteLine();
    }

    // ───────── 设置页：必须能滚到底，且「关于软件」看得见 ─────────

    private static void CheckSettingsTab(TabControl tabs)
    {
        Console.WriteLine("[2] 设置页签");
        var content = TabContent(tabs, "设置");

        var scroll = content as ScrollViewer ?? Descendants(content).OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is null)
        {
            Fail("设置页内容不在 ScrollViewer 里（窗口不够高时「关于软件」会被裁掉）");
            Console.WriteLine();
            return;
        }
        Pass($"设置页有 ScrollViewer，VerticalScrollBarVisibility={scroll.VerticalScrollBarVisibility}");

        const double shortHeight = 460;
        Layout(content, 1200, shortHeight);
        Pass($"窗口高 {shortHeight}px 时 ScrollableHeight={scroll.ScrollableHeight:F0}px、ViewportHeight={scroll.ViewportHeight:F0}px");
        Check(scroll.ScrollableHeight > 1, "窗口不够高时必须可滚动（ScrollableHeight > 0）");

        // 滚到底，最后一张卡（关于软件）必须完整落在视口内。
        scroll.ScrollToEnd();
        Layout(content, 1200, shortHeight);
        var about = Descendants(scroll)
            .OfType<Border>()
            .FirstOrDefault(b => Grid.GetRow(b) == 3 && b.Child is StackPanel);
        if (about is null)
        {
            Fail("找不到「关于软件」卡片（Grid.Row=3 的 Border）");
        }
        else
        {
            var top = about.TranslatePoint(new Point(0, 0), scroll).Y;
            var bottom = about.TranslatePoint(new Point(0, about.ActualHeight), scroll).Y;
            Pass($"滚到底后「关于软件」y={top:F0}..{bottom:F0}，视口高 {scroll.ViewportHeight:F0}");
            Check(top >= -1 && bottom <= scroll.ViewportHeight + 1,
                $"滚到底后「关于软件」应完整可见（y={top:F0}..{bottom:F0}，视口 {scroll.ViewportHeight:F0}）");
        }

        // 窗口够高时不该出现多余滚动条。
        scroll.ScrollToTop();
        Layout(content, 1200, 1200);
        Check(scroll.ScrollableHeight <= 1, $"窗口够高时不该有多余滚动（ScrollableHeight={scroll.ScrollableHeight:F0}）");
        Console.WriteLine();
    }

    // ───────── helpers ─────────

    private static FrameworkElement TabContent(TabControl tabs, string header)
    {
        foreach (var item in tabs.Items)
        {
            if (item is TabItem tab && (string?)tab.Header == header)
            {
                if (tab.Content is not FrameworkElement fe)
                {
                    throw new InvalidOperationException($"页签「{header}」没有内容");
                }
                return fe;
            }
        }
        throw new InvalidOperationException($"找不到页签「{header}」");
    }

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var grand in Descendants(child))
            {
                yield return grand;
            }
        }
    }

    private static void Check(bool condition, string failureMessage)
    {
        if (condition)
        {
            Console.WriteLine("  OK   " + failureMessage);
            return;
        }
        _failed++;
        Console.WriteLine("  FAIL " + failureMessage);
    }

    private static void Pass(string message) => Console.WriteLine("  · " + message);

    private static void Fail(string message)
    {
        _failed++;
        Console.WriteLine("  FAIL " + message);
    }
}
