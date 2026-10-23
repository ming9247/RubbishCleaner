using System.Xml.Linq;
using RubbishCleaner.Core.Abstractions;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Detection.Sources;

/// <summary>
/// 计划任务数据源：枚举 <c>%SystemRoot%\System32\Tasks</c> 下的 XML 定义，解析 &lt;Exec&gt; 动作。
/// 选择 XML 解析而非 TaskScheduler COM，是为了保持零第三方依赖（见 ADR-002）。
/// </summary>
public sealed class ScheduledTaskSource : IStartupSource
{
    public StartupSourceKind Kind => StartupSourceKind.ScheduledTask;

    public bool RequiresElevation => true;

    private readonly string _tasksRoot;

    public ScheduledTaskSource(string? tasksRoot = null)
        => _tasksRoot = tasksRoot
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");

    public Task<SourceResult> CollectAsync(ScanContext context)
    {
        if (!Directory.Exists(_tasksRoot))
        {
            return Task.FromResult(SourceResult.Empty(Kind));
        }

        var entries = new List<StartupEntry>();
        var unreadable = 0;

        try
        {
            var files = Directory.EnumerateFiles(_tasksRoot, "*", SearchOption.AllDirectories).ToList();
            foreach (var file in files)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                // 系统会生成一些非 XML 的临时文件，直接跳过。
                if (file.EndsWith(".job", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var entry = ParseTask(file);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    unreadable++;
                }
                catch (IOException)
                {
                    unreadable++;
                }
                catch (System.Xml.XmlException)
                {
                    // 非标准 XML（例如旧版 .job），忽略。
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(SourceResult.Failed(
                Kind,
                "无法读取计划任务目录，请以管理员身份运行以获得完整结果。"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(SourceResult.Failed(Kind, ex.Message));
        }

        var degraded = unreadable > 0;
        return Task.FromResult(new SourceResult(
            Kind,
            entries,
            degraded,
            degraded ? $"有 {unreadable} 个任务定义无法读取（通常需要管理员权限）" : null));
    }

    /// <summary>解析单个任务 XML；没有 Exec 动作（例如只有 COM 处理程序）时返回 null。</summary>
    internal StartupEntry? ParseTask(string filePath)
    {
        var doc = XDocument.Load(filePath);
        var root = doc.Root;
        if (root is null)
        {
            return null;
        }

        // 兼容带/不带命名空间两种情况
        var execs = root.Descendants()
            .Where(e => e.Name.LocalName == "Exec")
            .ToList();

        if (execs.Count == 0)
        {
            return null;
        }

        var command = execs
            .Select(e => e.Elements().FirstOrDefault(c => c.Name.LocalName == "Command")?.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var arguments = execs
            .Select(e => e.Elements().FirstOrDefault(c => c.Name.LocalName == "Arguments")?.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var resolved = CommandLineResolver.Resolve(command);

        // 任务名取相对于 Tasks 目录的路径，用反斜杠分隔（与任务计划程序一致）
        var relative = Path.GetRelativePath(_tasksRoot, filePath);
        var taskName = @"\" + relative.Replace(Path.DirectorySeparatorChar, '\\');
        var enabled = !string.Equals(
            root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Enabled")?.Value,
            "false",
            StringComparison.OrdinalIgnoreCase);

        return new StartupEntry
        {
            Id = EntryId.Create(Kind, _tasksRoot, taskName),
            Source = Kind,
            Name = taskName,
            Location = _tasksRoot,
            Scope = StartupScope.Machine,
            RawCommand = string.IsNullOrWhiteSpace(arguments) ? command : $"{command} {arguments}",
            ExecutablePath = resolved.ExecutablePath,
            Arguments = string.IsNullOrWhiteSpace(arguments) ? resolved.Arguments : arguments,
            IsReadable = true,
            ReadError = enabled ? resolved.Note : "任务已禁用",
        };
    }
}
