using RubbishCleaner.Core.Detection;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Cleanup;

/// <summary>
/// 手动处理该打开哪个 Windows 管理界面（v0.9.4）。
///
/// <para>为什么只在 UI 层"打开界面"而不代劳：删服务可能牵连依赖它的其它服务，
/// 删计划任务也可能是某个软件正常在用（我们只判定"指向的文件不存在"）。
/// 决定权与最终那一下右键，始终留在用户手里。</para>
/// </summary>
public enum ManualFixTargetKind
{
    /// <summary>打开「服务」管理单元（services.msc）。</summary>
    WindowsService,

    /// <summary>打开「任务计划程序」（taskschd.msc）。</summary>
    ScheduledTask,

    /// <summary>在资源管理器中定位启动文件夹里的快捷方式。</summary>
    StartupFolder,

    /// <summary>任务已不在任务计划程序里，只剩一份定义文件——在资源管理器中定位那份文件。</summary>
    TaskDefinitionFile,
}

/// <summary>
/// 不可自动清理的残留项及手动处理指引。
///
/// 为什么不代劳：服务与计划任务属于系统机制，
/// 删除一个服务可能影响依赖它的其他服务，甚至导致开机异常。
/// 这类操作交给用户手动执行，工具只负责"告诉他在哪、该敲什么命令、该打开哪个界面"。
/// </summary>
public sealed record ManualFixGuide(
    string EntryName,
    StartupSourceKind Source,
    string? ExecutablePath,
    string Title,
    string Explanation,
    string? Command,
    string? CommandHint,
    ManualFixTargetKind Target,
    string? TargetPath);

/// <summary>
/// 为不可自动清理的残留项生成手动处理指引。
///
/// 设计原则：<b>给可复制的命令，但绝不替用户执行</b>。
/// 用户能看到命令、理解后果、自己决定。
/// </summary>
public static class ManualFixAdvisor
{
    /// <summary>按启动项类型生成指引；无法给出建议时返回 null。</summary>
    public static ManualFixGuide? For(StartupEntry entry)
        => For(entry, isTaskRegistered: null);

    /// <param name="isTaskRegistered">
    /// 任务是否还注册在「任务计划程序」里；<c>null</c> 时走真实的注册表探测。
    /// 抽成参数是为了让"未注册 → 让用户删定义文件"这条分支可以在单测里确定性覆盖。
    /// </param>
    public static ManualFixGuide? For(StartupEntry entry, Func<string, bool?>? isTaskRegistered)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.Source switch
        {
            StartupSourceKind.WindowsService => ServiceGuide(entry),
            StartupSourceKind.ScheduledTask => TaskGuide(entry, isTaskRegistered),
            StartupSourceKind.StartupFolderUser or StartupSourceKind.StartupFolderMachine => ShortcutGuide(entry),
            _ => null,
        };
    }

    private static ManualFixGuide ServiceGuide(StartupEntry entry)
    {
        // 服务名通常是 entry.Name；若 ImagePath 里的文件名与之不同，两个都提示，让用户对照。
        var serviceName = entry.Name;
        var fileName = string.IsNullOrWhiteSpace(entry.ExecutablePath)
            ? null
            : Path.GetFileName(entry.ExecutablePath);

        var hint = fileName is not null && !string.Equals(
            Path.GetFileNameWithoutExtension(fileName), serviceName, StringComparison.OrdinalIgnoreCase)
            ? $"服务名是「{serviceName}」，但可执行文件叫「{fileName}」，请先确认是不是同一项。"
            : null;

        return new ManualFixGuide(
            entry.Name,
            entry.Source,
            entry.ExecutablePath,
            "残留的 Windows 服务",
            "服务的启动程序已不存在，但服务仍注册在系统里。删除服务需要管理员权限，"
            + "且可能影响依赖它的其他服务，因此本工具不会自动处理。"
            + "建议先确认没有其他程序依赖它，再手动删除。",
            $"sc.exe delete \"{serviceName}\"",
            hint,
            ManualFixTargetKind.WindowsService,
            serviceName);
    }

    private static ManualFixGuide TaskGuide(StartupEntry entry, Func<string, bool?>? isTaskRegistered)
    {
        var relative = entry.Name.TrimStart('\\');
        var definitionFile = Path.Combine(entry.Location, relative);
        var registered = (isTaskRegistered ?? ScheduledTaskRegistration.IsRegistered)(entry.Name);

        // 只剩定义文件：任务计划程序里根本没有它，schtasks 也会报「找不到该程序」。
        // 真机遇到的就是这一种（OneDrive 升级留下的孤儿定义文件），所以必须给"删文件"的指引。
        if (registered == false)
        {
            return new ManualFixGuide(
                entry.Name,
                entry.Source,
                entry.ExecutablePath,
                "残留的计划任务定义文件（任务计划程序里已不存在）",
                "这个任务已经不在「任务计划程序」里了，只剩一份定义文件（卸载/升级时没清干净）。"
                + "所以 schtasks /Delete 会报「找不到」，列表里也翻不到它——直接把下面这份文件删掉即可。",
                $"del /f /q \"{definitionFile}\"",
                $"文件位置：{definitionFile}",
                ManualFixTargetKind.TaskDefinitionFile,
                definitionFile);
        }

        // 已注册（或无权判断）：走 schtasks。无权判断时补一句"找不到就只剩定义文件"，
        // 免得又出现"命令报找不到、用户在 UI 里翻不到"的困惑。
        var hint = NormalizeTaskName(entry.Name) != entry.Name
            ? $"任务名已按计划任务程序的规则补全为「{NormalizeTaskName(entry.Name)}」。"
            : null;

        if (registered is null)
        {
            hint = (hint is null ? string.Empty : hint + " ")
                + $"若任务计划程序里找不到它，说明任务已注销、只剩定义文件，可直接删除：{definitionFile}";
        }

        return new ManualFixGuide(
            entry.Name,
            entry.Source,
            entry.ExecutablePath,
            "残留的计划任务",
            "计划任务指向的程序已不存在。可以在「任务计划程序」中手动删除："
            + "找到对应任务 → 右键 → 删除。",
            // schtasks 必须用完整任务名（以 \ 开头），这里直接给出来
            $"schtasks.exe /Delete /TN \"{NormalizeTaskName(entry.Name)}\" /F",
            hint,
            ManualFixTargetKind.ScheduledTask,
            NormalizeTaskName(entry.Name));
    }

    private static ManualFixGuide ShortcutGuide(StartupEntry entry)
        => new(
            entry.Name,
            entry.Source,
            entry.ExecutablePath,
            "残留的启动文件夹快捷方式",
            "启动文件夹里的这个快捷方式指向的程序已不存在。直接删除该快捷方式即可，"
            + "不需要管理员权限。",
            null,
            $"位置：{entry.Location}",
            ManualFixTargetKind.StartupFolder,
            Path.Combine(entry.Location, entry.Name));

    /// <summary>
    /// 计划任务名在注册表/文件系统里通常不带前导反斜杠，但 <c>schtasks</c> 要求带。
    /// </summary>
    private static string NormalizeTaskName(string name)
        => name.StartsWith('\\') ? name : "\\" + name;
}
