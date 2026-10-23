using System.Text;

namespace RubbishCleaner.Common.Logging;

public enum LogLevel
{
    Info,
    Warn,
    Error,
}

/// <summary>
/// 极简文件日志：按天分文件，保留 7 天。
/// 刻意不引入第三方日志库，以保持"绿色单文件、零依赖"的发布目标。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly int RetentionDays = 7;
    private static string? _directory;
    private static bool _initialized;

    public static string Directory
    {
        get
        {
            EnsureInitialized();
            return _directory!;
        }
    }

    /// <summary>允许宿主（如 App 启动早期）自定义日志目录。</summary>
    public static void Configure(string directory)
    {
        lock (Gate)
        {
            _directory = directory;
            _initialized = true;
            try
            {
                System.IO.Directory.CreateDirectory(_directory);
                Cleanup();
            }
            catch
            {
                // 日志目录不可写时静默降级为不落盘，不能因此影响主流程。
            }
        }
    }

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warn, message);

    public static void Error(string message, Exception? exception = null)
        => Write(LogLevel.Error, exception is null ? message : $"{message} :: {exception.GetType().Name}: {exception.Message}");

    private static void Write(LogLevel level, string message)
    {
        try
        {
            EnsureInitialized();
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ")
                .Append("[T").Append(Environment.CurrentManagedThreadId).Append("] ")
                .Append(message)
                .AppendLine()
                .ToString();

            lock (Gate)
            {
                var path = Path.Combine(_directory!, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败绝不影响业务。
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            _directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RubbishCleaner",
                "logs");
            _initialized = true;
        }
    }

    private static void Cleanup()
    {
        var threshold = DateTime.Now.AddDays(-RetentionDays);
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory!, "app-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < threshold)
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // 忽略清理失败。
            }
        }
    }
}
