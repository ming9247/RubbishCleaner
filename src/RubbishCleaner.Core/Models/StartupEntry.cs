namespace RubbishCleaner.Core.Models;

/// <summary>
/// 一个具体的自启动点位（注册表值 / 快捷方式 / 服务 / 计划任务）。
/// </summary>
public sealed class StartupEntry
{
    private bool? _targetExists;

    /// <summary>稳定标识，便于 UI 去重与用户反馈定位。</summary>
    public required string Id { get; init; }

    public required StartupSourceKind Source { get; init; }

    /// <summary>值名 / 快捷方式名 / 服务名 / 任务名。</summary>
    public required string Name { get; init; }

    /// <summary>注册表全路径或文件夹路径。</summary>
    public required string Location { get; init; }

    public StartupScope Scope { get; init; } = StartupScope.Machine;

    /// <summary>原始命令行（已展开环境变量）。</summary>
    public string? RawCommand { get; init; }

    /// <summary>解析出的可执行文件绝对路径；解析失败为 null。</summary>
    public string? ExecutablePath { get; init; }

    public string? Arguments { get; init; }

    public bool IsSigned { get; init; }

    public bool IsMicrosoft { get; init; }

    public string? Publisher { get; init; }

    /// <summary>该条记录是否被成功读取（快捷方式解析失败时为 false）。</summary>
    public bool IsReadable { get; init; } = true;

    public string? ReadError { get; init; }

    /// <summary>
    /// 显式覆盖"目标文件是否存在"的判定结果。
    ///
    /// 仅供测试与离线导入使用：正常扫描时保持为 null，让 <see cref="TargetExists"/> 去查磁盘。
    /// 之所以暴露出来，是因为评分、排序等大量单元测试都需要"这个程序存在"这一前提，
    /// 而它们测试的对象与文件系统无关——若靠造假文件，测试会变慢且脆弱。
    /// </summary>
    public bool? TargetExistsOverride { get; init; }

    /// <summary>
    /// 目标文件是否存在（惰性求值并缓存，避免 UI 反复触发 IO）。
    ///
    /// 判定为 false 需要同时满足两个条件，缺一不可：
    /// 1. <see cref="ExecutablePath"/> 是<b>绝对路径</b>；
    /// 2. <see cref="File.Exists(string)"/> 返回 false。
    ///
    /// 为什么要限制绝对路径：计划任务与服务的命令行常写成裸文件名
    /// （如 <c>BthUdTask.exe</c>），实际由系统按 System32 解析。
    /// 若把这类相对路径也判为"不存在"，会把大量正规系统任务误报成残留（实测踩过）。
    /// </summary>
    public bool TargetExists
    {
        get
        {
            _targetExists ??= TargetExistsOverride ?? ComputeTargetExists();
            return _targetExists.Value;
        }
    }

    /// <summary>是否为"失效启动项"：指向的文件已不存在，属于卸载/升级残留。</summary>
    public bool IsOrphan => !TargetExists;

    /// <summary>分组用的键：优先可执行文件路径（小写），否则退回名称。</summary>
    public string GroupKey => !string.IsNullOrWhiteSpace(ExecutablePath)
        ? ExecutablePath!.ToLowerInvariant()
        : $"{Source}:{Name}".ToLowerInvariant();

    public override string ToString() => $"[{Source}] {Name} -> {ExecutablePath ?? RawCommand}";

    private bool ComputeTargetExists()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath))
        {
            // 无法解析出路径（例如 shell: 协议引用），不能断言"失效"。
            return true;
        }

        if (!Path.IsPathRooted(ExecutablePath))
        {
            // 相对路径（裸文件名）：由系统按工作目录/System32 解析，不判失效。
            return true;
        }

        try
        {
            return File.Exists(ExecutablePath);
        }
        catch
        {
            // 非法路径字符等情况，保守地认为"存在"，避免误报残留。
            return true;
        }
    }
}
