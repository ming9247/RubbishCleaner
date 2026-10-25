namespace RubbishCleaner.Core.Uninstall;

/// <summary>卸载执行结果。</summary>
public sealed record UninstallOutcome(
    bool Started,
    bool Completed,
    int? ExitCode,
    string Message,
    TimeSpan Elapsed);

/// <summary>
/// 调用软件自带的卸载程序。
///
/// <b>这是本项目风险最高的一段代码</b>——它要执行第三方提供的命令。
/// 因此设计上刻意保持"薄"：只负责"按用户确认的命令启动进程并等待"，
/// 所有判断（能不能卸、用哪个命令、要不要先结束进程）都留在上层，便于测试与审计。
///
/// 安全约束：
/// 1. <b>只执行注册表里已有的 UninstallString</b>，不接受任意命令；
/// 2. 启动前校验可执行文件确实存在（文件不存在时给出明确提示，而不是让 Shell 报错）；
/// 3. <b>不静默执行</b>：默认走标准卸载命令，带界面，让用户看到卸载程序在做什么；
/// 4. 有超时保护，避免挂死的卸载程序拖住整个工具。
/// </summary>
public sealed class UninstallExecutor
{
    /// <summary>默认等待上限：大型软件（如 Adobe）卸载可能需要十几分钟。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);

    private readonly TimeSpan _timeout;

    public UninstallExecutor(TimeSpan? timeout = null) => _timeout = timeout ?? DefaultTimeout;

    /// <summary>
    /// 执行卸载命令。
    /// </summary>
    /// <param name="command">完整的 UninstallString。</param>
    /// <param name="silent">是否优先使用静默模式（需要调用方传入 QuietUninstallString）。</param>
    public UninstallOutcome Execute(string? command, bool silent = false)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new UninstallOutcome(false, false, null, "没有可用的卸载命令", TimeSpan.Zero);
        }

        var executable = Models.RegistryTextSanitizer.TryExtractExecutable(command);
        if (executable is not null && Path.IsPathRooted(executable) && !File.Exists(executable))
        {
            // 卸载程序本身已不存在：这是最常见的情况（用户手动删过目录）
            return new UninstallOutcome(
                false, false, null,
                $"卸载程序不存在：{executable}。可以尝试「清理残留」，或手动删除安装目录。",
                TimeSpan.Zero);
        }

        var startedAt = DateTimeOffset.Now;
        try
        {
            var startInfo = BuildStartInfo(command, silent);

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return new UninstallOutcome(false, false, null, "无法启动卸载程序", DateTimeOffset.Now - startedAt);
            }

            var finished = process.WaitForExit((int)_timeout.TotalMilliseconds);
            var elapsed = DateTimeOffset.Now - startedAt;

            if (!finished)
            {
                return new UninstallOutcome(
                    true, false, null,
                    $"卸载程序超过 {_timeout.TotalMinutes:F0} 分钟未结束，已放弃等待（它可能仍在运行）",
                    elapsed);
            }

            return new UninstallOutcome(
                true, true, process.ExitCode,
                process.ExitCode == 0
                    ? "卸载程序已正常结束"
                    : $"卸载程序结束，退出码 {process.ExitCode}（部分软件用非 0 表示「已完成但需重启」）",
                elapsed);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 用户取消 UAC 时是 1223
            var message = ex.NativeErrorCode == 1223
                ? "已取消（UAC 被拒绝）"
                : $"启动卸载程序失败：{ex.Message}";
            return new UninstallOutcome(false, false, null, message, DateTimeOffset.Now - startedAt);
        }
        catch (Exception ex)
        {
            return new UninstallOutcome(false, false, null, $"执行卸载失败：{ex.Message}", DateTimeOffset.Now - startedAt);
        }
    }

    /// <param name="command">完整命令行（UninstallString 或 QuietUninstallString）。</param>
    private static System.Diagnostics.ProcessStartInfo BuildStartInfo(string command, bool silent)
    {
        // 静默模式由调用方直接传 QuietUninstallString，这里<b>不自行拼接静默参数</b>：
        // 各软件的静默参数五花八门（/S、/silent、/qn、--silent），猜错会导致卸载异常甚至卡死。
        // 因此 silent 参数只影响日志语义，命令内容完全照抄注册表里的原值。
        _ = silent;

        var (fileName, arguments) = SplitCommand(command);

        var isRooted = Path.IsPathRooted(fileName);

        return new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            // 用 ShellExecute 让卸载程序能自己请求提权（很多卸载器清单里要求管理员）
            UseShellExecute = true,
            // 只对绝对路径设置工作目录：MsiExec.exe 这类由系统解析的工具，
            // 强行设到别的目录反而会让它找不到同目录的依赖。
            WorkingDirectory = isRooted
                ? Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory
                : Environment.CurrentDirectory,
        };
    }

    /// <summary>
    /// 把一条完整命令行拆成「可执行文件」与「参数」两段。
    ///
    /// <b>为什么不能复用 TryExtractExecutable</b>：那个方法对
    /// MsiExec.exe /X{GUID} 返回 null（它的职责是"取出可校验的 exe 路径"，
    /// 而 MSI 没有独立可校验的 exe）。拿 null 做 fallback 会把<b>整条命令行</b>当成文件名，
    /// 于是 Process.Start 去找一个叫 "MsiExec.exe /X{...}" 的文件，必然失败。
    ///
    /// 这个缺陷在 v0.2 就存在，但因为当时"没有真正执行过一次卸载"（见 v0.2 工作日志），
    /// 一直到 v0.3.1 用真实软件（CC Switch，MSI）验证时才暴露——
    /// <b>影响本机 27 个 MSI 软件的全部卸载</b>。
    /// </summary>
    internal static (string FileName, string Arguments) SplitCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return (string.Empty, string.Empty);
        }

        var lead = command.TrimStart();

        // 形态 1：引号包裹的路径 —— "C:\Program Files\App\uninst.exe" /S
        if (lead.Length > 1 && lead[0] == '"')
        {
            var withoutLead = lead[1..];
            var closing = withoutLead.IndexOf('"');
            if (closing > 0)
            {
                return (withoutLead[..closing], withoutLead[(closing + 1)..].Trim());
            }
        }

        // 形态 2：含 .exe —— C:\App\uninst.exe /allusers 、MsiExec.exe /X{GUID}
        var exeIndex = lead.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex > 0)
        {
            return (lead[..(exeIndex + 4)], lead[(exeIndex + 4)..].Trim());
        }

        // 形态 3：没有 .exe 字样，按第一个空格切
        var space = lead.IndexOf(' ');
        return space > 0
            ? (lead[..space], lead[(space + 1)..].Trim())
            : (lead, string.Empty);
    }
}
