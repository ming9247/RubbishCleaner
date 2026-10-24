using RubbishCleaner.Core.Models;

namespace RubbishCleaner.Core.Uninstall;

/// <summary>卸载前的安全检查结果。</summary>
public sealed record UninstallPrecheck(
    bool CanProceed,
    string? BlockReason,
    bool UninstallerExists,
    string? ExecutablePath,
    IReadOnlyList<string>? Warnings = null)
{
    public static UninstallPrecheck Allow(string? exePath, IReadOnlyList<string>? warnings = null)
        => new(true, null, true, exePath, warnings ?? []);

    public static UninstallPrecheck Block(string reason, string? exePath = null)
        => new(false, reason, false, exePath, []);
}

/// <summary>
/// 卸载前的安全检查。
///
/// 这套检查独立于执行器，是为了让"能不能卸"成为一个<b>可测试、可展示</b>的判断：
/// UI 可以在用户点按钮之前就把问题指出来，而不是等卸载失败了才报错。
///
/// 检查项：
/// 1. 有没有卸载命令；
/// 2. 卸载程序文件还在不在（用户手动删过目录的情况很常见）；
/// 3. 卸载程序是否位于一个"看起来合理"的位置（防注册表被恶意篡改指向系统程序）；
/// 4. 目标程序是否正在运行（正在运行时卸载通常会失败或残留）。
/// </summary>
public static class UninstallPrechecker
{
    /// <summary>绝对不允许作为卸载程序执行的系统关键程序。</summary>
    private static readonly string[] ForbiddenExecutables =
    [
        "cmd.exe", "powershell.exe", "pwsh.exe", "rundll32.exe", "regsvr32.exe",
        "net.exe", "net1.exe", "sc.exe", "reg.exe", "wscript.exe", "cscript.exe",
        "mshta.exe", "certutil.exe", "bitsadmin.exe",
    ];

    private static bool IsForbidden(string fileName)
        => ForbiddenExecutables.Any(f => string.Equals(f, fileName, StringComparison.OrdinalIgnoreCase));

    private static bool IsRundll32(string fileName)
        => string.Equals(fileName, "rundll32.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// rundll32 的合法例外判定。
    ///
    /// <b>背景</b>：实测 NVIDIA 全家桶（7 个组件）的官方卸载方式就是
    /// <c>RunDll32.EXE "…\NVI2.DLL",UninstallPackage Display.Driver</c>，
    /// 一开始被"禁止系统工具"规则误杀。
    ///
    /// <b>判定依据</b>：rundll32 本身不危险，危险的是"加载任意 DLL"。
    /// 因此只在同时满足以下条件时放行：
    /// 1. DLL 路径是绝对路径；
    /// 2. DLL 文件真实存在；
    /// 3. DLL <b>不在系统目录</b>（系统目录下的 DLL 不该由第三方软件当卸载器）。
    ///
    /// 返回放行的 DLL 路径；不满足则返回 null（走拒绝分支）。
    /// </summary>
    private static string? TryAllowRundll32(string command)
    {
        var dll = ExtractRundll32Target(command);
        if (string.IsNullOrWhiteSpace(dll) || !Path.IsPathRooted(dll))
        {
            return null;
        }

        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (dll.StartsWith(systemRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;   // 系统目录下的 DLL 不允许当卸载器
        }

        try
        {
            return File.Exists(dll) ? dll : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从 rundll32 命令行中取出目标 DLL 路径。
    ///
    /// 命令行形态：<c>"C:\Windows\System32\rundll32.exe" "C:\App\NVI2.DLL",EntryPoint arg</c>
    ///
    /// 踩过的坑：直接用 <c>IndexOf(".exe")</c> 定位再切剩余部分，
    /// 会切到 <c>.exe"  "C:\App\NVI2.DLL"...</c> ——开头那个引号是 exe 路径的收尾引号，
    /// 按"引号包裹"解析会立刻匹配到空串。必须先把 exe 部分整体剥掉。
    /// </summary>
    private static string? ExtractRundll32Target(string command)
    {
        // 复用现有的 exe 提取逻辑，得到干净的可执行文件部分
        var exe = RegistryTextSanitizer.TryExtractExecutable(command);
        if (string.IsNullOrWhiteSpace(exe))
        {
            return null;
        }

        var text = command.Trim();
        var index = text.IndexOf(exe, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        // 跳过 exe 本身（若是引号包裹，连收尾引号一起跳过）
        var rest = text[(index + exe.Length)..].Trim();
        rest = rest.TrimStart('"').Trim();

        if (rest.Length == 0)
        {
            return null;
        }

        // 引号包裹的 DLL 路径
        if (rest[0] == '"')
        {
            var closing = rest.IndexOf('"', 1);
            return closing > 1 ? rest[1..closing] : null;
        }

        // 无引号：取到第一个逗号或空白
        var comma = rest.IndexOf(',');
        var space = rest.IndexOf(' ');
        var end = comma < 0 ? space : space < 0 ? comma : Math.Min(comma, space);
        return end < 0 ? rest : rest[..end];
    }
    /// <summary>对单个软件做卸载前检查。</summary>
    public static UninstallPrecheck Check(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (string.IsNullOrWhiteSpace(app.UninstallCommand))
        {
            return UninstallPrecheck.Block("该软件没有提供卸载命令，只能手动删除安装目录");
        }

        var exe = RegistryTextSanitizer.TryExtractExecutable(app.UninstallCommand);

        // MSI 卸载没有独立 exe，交给 Windows Installer 处理，属于安全路径
        if (exe is null)
        {
            return app.IsMsi
                ? UninstallPrecheck.Allow(null, ["将通过 Windows Installer 卸载"])
                : UninstallPrecheck.Block("无法从卸载命令中解析出可执行文件");
        }

        // 注意检查顺序：安全拦截必须<b>先于</b>路径校验。
        // 否则 "cmd.exe /c ..." 这类会先报"不是绝对路径"，
        // 掩盖掉真正的原因（指向系统工具），用户看到的提示就不准确了。
        var fileName = Path.GetFileName(exe);

        // 防护 1：注册表被篡改指向系统工具时拒绝执行。
        // 但 rundll32 是合法例外——见 TryAllowRundll32。
        if (IsForbidden(fileName))
        {
            var rundll32Dll = IsRundll32(fileName) ? TryAllowRundll32(app.UninstallCommand) : null;
            if (rundll32Dll is null)
            {
                return UninstallPrecheck.Block(
                    $"卸载命令指向系统工具「{fileName}」，出于安全考虑不执行。原始命令：{app.UninstallCommand}",
                    exe);
            }

            var dllWarnings = new List<string>();
            if (app.HasCorruptedText)
            {
                dllWarnings.Add("该条目的注册表信息存在异常字符，卸载命令可能不完整");
            }

            dllWarnings.Add($"通过 rundll32 加载 {Path.GetFileName(rundll32Dll)} 执行卸载");

            var dllRunning = TryGetRunningProcesses(app);
            if (dllRunning.Count > 0)
            {
                dllWarnings.Add($"检测到 {dllRunning.Count} 个相关进程正在运行，卸载前建议先退出");
            }

            return new UninstallPrecheck(true, null, true, exe, dllWarnings);
        }

        if (!Path.IsPathRooted(exe))
        {
            return UninstallPrecheck.Block($"卸载程序路径不是绝对路径：{exe}", exe);
        }

        if (!File.Exists(exe))
        {
            return UninstallPrecheck.Block(
                $"卸载程序已不存在：{exe}。该软件可能已被手动删除，建议使用「清理残留」。",
                exe);
        }

        var warnings = new List<string>();

        if (app.HasCorruptedText)
        {
            warnings.Add("该条目的注册表信息存在异常字符，卸载命令可能不完整");
        }

        var running = TryGetRunningProcesses(app);
        if (running.Count > 0)
        {
            warnings.Add($"检测到 {running.Count} 个相关进程正在运行，卸载前建议先退出，否则可能失败或留下残留");
        }

        return UninstallPrecheck.Allow(exe, warnings);
    }

    /// <summary>
    /// 找出与软件相关的运行中进程。
    /// 匹配依据：安装目录下的进程，或进程名包含软件名的主要部分。
    /// 刻意保守——宁可能少提示，也不要误报一堆无关进程吓到用户。
    /// </summary>
    private static IReadOnlyList<string> TryGetRunningProcesses(InstalledApp app)
    {
        var hits = new List<string>();

        // 优先按安装目录匹配（最准确）
        if (!string.IsNullOrWhiteSpace(app.InstallLocation) && Path.IsPathRooted(app.InstallLocation))
        {
            var location = app.InstallLocation.TrimEnd(Path.DirectorySeparatorChar);
            foreach (var process in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    var path = Common.Win32.ProcessHelper.TryGetExecutablePath(process.Id);
                    if (path is not null && path.StartsWith(location, StringComparison.OrdinalIgnoreCase))
                    {
                        hits.Add(process.ProcessName);
                    }
                }
                catch
                {
                    // 进程可能在枚举与访问之间退出
                }
                finally
                {
                    process.Dispose();
                }
            }

            return hits;
        }

        // 没有安装目录时，退回到"进程名包含软件名"的保守匹配。
        // 只对长度足够的名字启用，避免 "QQ" 这类短名匹配到一堆无关进程。
        var nameKey = NormalizeForMatching(app.DisplayName);
        if (nameKey.Length < 4)
        {
            return hits;
        }

        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (NormalizeForMatching(process.ProcessName).Contains(nameKey, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add(process.ProcessName);
                }
            }
            catch
            {
                // 忽略
            }
            finally
            {
                process.Dispose();
            }
        }

        return hits;
    }

    /// <summary>去掉版本号、空格与非字母数字，便于模糊匹配。</summary>
    private static string NormalizeForMatching(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
