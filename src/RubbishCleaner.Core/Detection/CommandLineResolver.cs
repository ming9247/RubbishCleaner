using System.Diagnostics;

namespace RubbishCleaner.Core.Detection;

/// <summary>
/// 把自启动命令行解析为"可执行文件路径 + 参数"。
/// 安全约定：只做字符串处理与 <see cref="File.Exists(string)"/> 判断，<b>绝不执行</b>命令行。
/// </summary>
public static class CommandLineResolver
{
    private static readonly string[] ShellHosts =
    [
        "svchost.exe",
        "services.exe",
        "wininit.exe",
        "smss.exe",
    ];

    /// <summary>解析结果。<see cref="ExecutablePath"/> 为 null 表示无法确定归属文件。</summary>
    public readonly record struct Resolved(string? ExecutablePath, string? Arguments, string? Note);

    public static Resolved Resolve(string? rawCommand)
    {
        if (string.IsNullOrWhiteSpace(rawCommand))
        {
            return new Resolved(null, null, "空命令行");
        }

        var command = Environment.ExpandEnvironmentVariables(rawCommand).Trim();

        // 1) explorer.exe shell:startup 之类的协议引用，没有真实可执行文件。
        if (command.Contains("shell:", StringComparison.OrdinalIgnoreCase))
        {
            return new Resolved(null, null, "shell 协议引用");
        }

        var (head, tail) = SplitHead(command);
        if (string.IsNullOrEmpty(head))
        {
            return new Resolved(null, null, "无法切分命令行");
        }

        var headName = Path.GetFileName(head);

        // 2) svchost / services 等宿主进程：归属到宿主自身，避免把系统宿主判成可疑程序。
        if (ShellHosts.Any(h => string.Equals(h, headName, StringComparison.OrdinalIgnoreCase)))
        {
            return new Resolved(head, tail, "系统宿主进程");
        }

        // 3) rundll32 "C:\x\a.dll",Entry -> 归属到 dll
        if (string.Equals(headName, "rundll32.exe", StringComparison.OrdinalIgnoreCase))
        {
            var dll = FirstArgument(tail);
            return string.IsNullOrEmpty(dll)
                ? new Resolved(head, tail, "rundll32 无参数")
                : new Resolved(CleanToken(dll), tail, "rundll32 宿主");
        }

        // 4) cmd.exe /c "C:\x\a.exe" -arg -> 取 /c 之后的部分重新解析
        if (string.Equals(headName, "cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            var inner = StripCmdSwitches(tail);
            return string.IsNullOrEmpty(inner)
                ? new Resolved(head, tail, "cmd 无内联命令")
                : Resolve(inner);
        }

        // 5) 常规情况：head 即目标路径（可能是相对路径）
        var candidate = CleanToken(head);
        if (Path.IsPathRooted(candidate))
        {
            return new Resolved(candidate, tail, File.Exists(candidate) ? null : "目标文件不存在");
        }

        return new Resolved(candidate, tail, "相对路径");
    }

    /// <summary>
    /// 从无引号命令行中推断真实可执行文件路径：
    /// 从最长前缀开始向前回溯，找到第一个真实存在的文件（处理 "C:\Program Files\A B\a.exe" 这类含空格路径）。
    /// </summary>
    public static string? GuessExecutableFromUnquoted(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var command = commandLine.Trim();

        // 引号包裹：直接取引号内内容
        if (command[0] == '"')
        {
            var closing = command.IndexOf('"', 1);
            return closing > 1 ? command[1..closing] : null;
        }

        // 找到所有 ".exe" 结束位置，从右往左尝试
        var indices = new List<int>();
        var searchIndex = 0;
        while (true)
        {
            var found = command.IndexOf(".exe", searchIndex, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                break;
            }

            indices.Add(found + 4);
            searchIndex = found + 4;
        }

        for (var i = indices.Count - 1; i >= 0; i--)
        {
            var candidate = command[..indices[i]].Trim();
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // 非法路径字符，继续回溯。
            }
        }

        // 全部不存在时退回第一个空白前的 token
        var space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    /// <summary>切出命令行首段（尊重引号）与剩余参数。</summary>
    private static (string Head, string Tail) SplitHead(string command)
    {
        command = command.Trim();
        if (command.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        if (command[0] == '"')
        {
            var closing = command.IndexOf('"', 1);
            if (closing < 0)
            {
                return (command[1..], string.Empty);
            }

            var rest = command[(closing + 1)..].Trim();
            return (command[1..closing], rest);
        }

        // 无引号：优先尝试"含空格但仍存在"的路径，其次退回首个空白
        var guessed = GuessExecutableFromUnquoted(command);
        if (!string.IsNullOrEmpty(guessed))
        {
            var rest = command.Length > guessed.Length ? command[guessed.Length..].Trim() : string.Empty;
            return (guessed, rest);
        }

        var space = command.IndexOf(' ');
        return space < 0
            ? (command, string.Empty)
            : (command[..space], command[(space + 1)..].Trim());
    }

    private static string? FirstArgument(string tail)
    {
        if (string.IsNullOrWhiteSpace(tail))
        {
            return null;
        }

        tail = tail.Trim();
        if (tail[0] == '"')
        {
            var closing = tail.IndexOf('"', 1);
            return closing > 1 ? tail[1..closing] : null;
        }

        var comma = tail.IndexOf(',');
        var space = tail.IndexOf(' ');
        var end = comma < 0 ? space : space < 0 ? comma : Math.Min(comma, space);
        return end < 0 ? tail : tail[..end];
    }

    private static string? StripCmdSwitches(string tail)
    {
        if (string.IsNullOrWhiteSpace(tail))
        {
            return null;
        }

        var parts = tail.Trim().Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        return parts[0].Equals("/c", StringComparison.OrdinalIgnoreCase)
               || parts[0].Equals("/k", StringComparison.OrdinalIgnoreCase)
            ? parts[1]
            : null;
    }

    private static string CleanToken(string token)
    {
        var cleaned = token.Trim().Trim('"').Trim();
        try
        {
            return Environment.ExpandEnvironmentVariables(cleaned);
        }
        catch
        {
            return cleaned;
        }
    }
}
