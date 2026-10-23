using System.Windows;
using RubbishCleaner.App.ViewModels;

namespace RubbishCleaner.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        FitToWorkArea();
        AlignHeaderWithList();
    }

    /// <summary>
    /// 让表头的右侧留白等于垂直滚动条宽度，使表头与列表内容逐列对齐。
    ///
    /// 为什么需要：表头是独立于 ListBox 的 Grid，而 ListBox 的垂直滚动条会占掉
    /// 行内容的宽度。表头若不预留同样的宽度，从第一列起就会逐渐错位（实测踩过）。
    /// 用 <see cref="SystemParameters.VerticalScrollBarWidth"/> 而不是写死数值，
    /// 才能适配不同 DPI 与主题。
    /// </summary>
    private void AlignHeaderWithList()
    {
        try
        {
            var scrollBarWidth = SystemParameters.VerticalScrollBarWidth;
            HeaderBorder.Padding = new Thickness(10, 6, 10 + scrollBarWidth, 6);
        }
        catch
        {
            // 对齐失败只影响观感，不应阻止窗口显示。
        }
    }

    /// <summary>允许在设计器或测试中注入视图模型。</summary>
    public MainWindow(MainViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>
    /// 让窗口适配当前屏幕工作区。
    ///
    /// 为什么需要：XAML 里写死的 Width=1180 在 125%/150% DPI 缩放的笔记本上
    /// （例如 1920×1080 @150%，逻辑工作区只有 1280×680）会超出屏幕，
    /// 导致窗口边缘被裁掉、底部状态栏看不见。实测已踩到。
    /// </summary>
    private void FitToWorkArea()
    {
        try
        {
            var workArea = SystemParameters.WorkArea;

            // 留出一点边距，避免贴边显得拥挤
            const double margin = 24;
            var maxWidth = workArea.Width - margin;
            var maxHeight = workArea.Height - margin;

            if (Width > maxWidth)
            {
                Width = Math.Max(MinWidth, maxWidth);
            }

            if (Height > maxHeight)
            {
                Height = Math.Max(MinHeight, maxHeight);
            }

            // 缩小后重新居中，否则可能仍偏出屏幕
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
            Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
        }
        catch
        {
            // 尺寸适配失败不应阻止窗口显示，退回 XAML 的默认值。
        }
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // 打开即扫描：工具定位是"用完即走"，不让用户多点一次。
        await _viewModel.ScanAsync();
    }
}
