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
    }

    /// <summary>允许在设计器或测试中注入视图模型。</summary>
    public MainWindow(MainViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // 打开即扫描：工具定位是"用完即走"，不让用户多点一次。
        await _viewModel.ScanAsync();
    }
}
