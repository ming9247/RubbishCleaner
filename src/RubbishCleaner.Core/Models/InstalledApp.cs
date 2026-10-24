using Microsoft.Win32;

namespace RubbishCleaner.Core.Models;

/// <summary>已安装软件条目（来自 Uninstall 注册表键）。</summary>
public sealed class InstalledApp
{
    /// <summary>稳定标识：来源视图 + 注册表子键名。</summary>
    public required string Id { get; init; }

    /// <summary>显示名称（已清洗，见 <see cref="RegistryTextSanitizer"/>）。</summary>
    public required string DisplayName { get; init; }

    public string? DisplayVersion { get; init; }

    public string? Publisher { get; init; }

    public string? InstallLocation { get; init; }

    /// <summary>注册表位置，用于"在注册表中定位"。</summary>
    public required string RegistryPath { get; init; }

    public AppScope Scope { get; init; } = AppScope.Machine;

    /// <summary>是否为 32 位（WOW6432Node）注册的软件。</summary>
    public bool Is32Bit { get; init; }

    /// <summary>标准卸载命令；为 null 表示该条目没有可用卸载方式。</summary>
    public string? UninstallCommand { get; init; }

    /// <summary>静默卸载命令（不是所有软件都提供）。</summary>
    public string? QuietUninstallCommand { get; init; }

    /// <summary>是否由 Windows Installer（MSI）管理。</summary>
    public bool IsMsi { get; init; }

    /// <summary>估算的安装大小（KB）；0 表示未知。</summary>
    public long EstimatedSizeKb { get; init; }

    /// <summary>注册表文本被污染的条目（例如被第三方安装器写坏）。</summary>
    public bool HasCorruptedText { get; init; }

    /// <summary>是否有可用的卸载命令。</summary>
    public bool CanUninstall => !string.IsNullOrWhiteSpace(UninstallCommand);

    /// <summary>能否静默卸载。</summary>
    public bool CanUninstallSilently => !string.IsNullOrWhiteSpace(QuietUninstallCommand);

    public override string ToString() => $"{DisplayName} {DisplayVersion}".Trim();
}

/// <summary>软件安装作用域。</summary>
public enum AppScope
{
    /// <summary>为所有用户安装（HKLM）。</summary>
    Machine,

    /// <summary>仅当前用户安装（HKCU）。</summary>
    User,
}
