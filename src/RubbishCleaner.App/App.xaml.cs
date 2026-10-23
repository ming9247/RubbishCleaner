using System.IO;
using System.Windows;
using System.Windows.Threading;
using RubbishCleaner.Common.Logging;

namespace RubbishCleaner.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Configure(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RubbishCleaner",
            "logs"));

        Log.Info($"启动 v{typeof(App).Assembly.GetName().Version}，提权={Common.Win32.PrivilegeHelper.IsElevated}");

        // 三层全局兜底：任何未处理异常都要留痕并提示，绝不静默崩溃。
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var window = new Views.MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI 线程未处理异常", e.Exception);
        MessageBox.Show(
            $"程序遇到一个错误：{e.Exception.Message}\n\n详细信息已写入日志：\n{Log.Directory}",
            "RubbishCleaner",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        // 标记已处理，避免用户数据（扫描结果）因一次界面异常全部丢失。
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => Log.Error("AppDomain 未处理异常", e.ExceptionObject as Exception);

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("未观察的任务异常", e.Exception);
        e.SetObserved();
    }
}
