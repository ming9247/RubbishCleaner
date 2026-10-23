namespace RubbishCleaner.Core.Models;

/// <summary>运行期采集到的客观进程事实。</summary>
/// <param name="ProcessId">进程 ID；未运行时为 0。</param>
/// <param name="ProcessName">不含扩展名的进程名。</param>
/// <param name="ExecutablePath">完整路径。</param>
/// <param name="IsRunning">当前是否在运行。</param>
/// <param name="HasVisibleWindow">是否存在可见顶层窗口。</param>
/// <param name="HasTaskbarButton">是否在任务栏有按钮。</param>
/// <param name="SignatureValid">Authenticode 签名是否有效。</param>
/// <param name="IsMicrosoftSigned">是否为微软签名。</param>
/// <param name="SignerSubject">签名主体。</param>
/// <param name="Publisher">可读发行商名称。</param>
/// <param name="FileDescription">文件版本信息中的描述。</param>
public sealed record ProcessFacts(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    bool IsRunning,
    bool HasVisibleWindow,
    bool HasTaskbarButton,
    bool SignatureValid,
    bool IsMicrosoftSigned,
    string? SignerSubject,
    string? Publisher,
    string? FileDescription)
{
    /// <summary>未运行时的占位事实。</summary>
    public static ProcessFacts NotRunning(string processName, string? executablePath) => new(
        ProcessId: 0,
        ProcessName: processName,
        ExecutablePath: executablePath,
        IsRunning: false,
        HasVisibleWindow: false,
        HasTaskbarButton: false,
        SignatureValid: false,
        IsMicrosoftSigned: false,
        SignerSubject: null,
        Publisher: null,
        FileDescription: null);
}
