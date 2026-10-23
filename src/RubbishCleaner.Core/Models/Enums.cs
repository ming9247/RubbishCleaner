namespace RubbishCleaner.Core.Models;

/// <summary>自启动点位类别。v0.2/v0.3 预留项已在枚举中占位，但当前不会产出。</summary>
public enum StartupSourceKind
{
    RegistryRunHkcu,
    RegistryRunHklm,
    RegistryRunWow64,
    StartupFolderUser,
    StartupFolderMachine,
    WindowsService,
    ScheduledTask,

    // v0.2 预留
    Winlogon,
    AppInitDlls,
    IfeoHijack,
}

/// <summary>作用域：整机 / 当前用户。</summary>
public enum StartupScope
{
    Machine,
    User,
}

/// <summary>风险等级。</summary>
public enum RiskLevel
{
    Normal,
    Suspicious,
    HighRisk,
}

/// <summary>证据严重程度。</summary>
public enum EvidenceSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>确认表判定结果。</summary>
public enum TrustVerdict
{
    Unknown,
    Safe,
    Malicious,
}

public static class StartupSourceKindExtensions
{
    /// <summary>面向用户的中文名称。</summary>
    public static string ToDisplayName(this StartupSourceKind kind) => kind switch
    {
        StartupSourceKind.RegistryRunHkcu => "注册表 Run（当前用户）",
        StartupSourceKind.RegistryRunHklm => "注册表 Run（本机）",
        StartupSourceKind.RegistryRunWow64 => "注册表 Run（32 位兼容）",
        StartupSourceKind.StartupFolderUser => "启动文件夹（当前用户）",
        StartupSourceKind.StartupFolderMachine => "启动文件夹（公共）",
        StartupSourceKind.WindowsService => "Windows 服务",
        StartupSourceKind.ScheduledTask => "计划任务",
        StartupSourceKind.Winlogon => "Winlogon 劫持",
        StartupSourceKind.AppInitDlls => "AppInit_DLLs",
        StartupSourceKind.IfeoHijack => "映像劫持（IFEO）",
        _ => kind.ToString(),
    };

    public static string ToDisplayName(this RiskLevel level) => level switch
    {
        RiskLevel.Normal => "正常",
        RiskLevel.Suspicious => "疑似",
        RiskLevel.HighRisk => "高风险",
        _ => level.ToString(),
    };
}
