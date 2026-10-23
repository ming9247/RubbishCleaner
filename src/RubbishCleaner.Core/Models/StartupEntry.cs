namespace RubbishCleaner.Core.Models;

/// <summary>
/// 一个具体的自启动点位（注册表值 / 快捷方式 / 服务 / 计划任务）。
/// </summary>
public sealed class StartupEntry
{
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

    /// <summary>分组用的键：优先可执行文件路径（小写），否则退回名称。</summary>
    public string GroupKey => !string.IsNullOrWhiteSpace(ExecutablePath)
        ? ExecutablePath!.ToLowerInvariant()
        : $"{Source}:{Name}".ToLowerInvariant();

    public override string ToString() => $"[{Source}] {Name} -> {ExecutablePath ?? RawCommand}";
}
