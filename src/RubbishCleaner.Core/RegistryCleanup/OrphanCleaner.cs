using Microsoft.Win32;

namespace RubbishCleaner.Core.RegistryCleanup;

/// <summary>一条待清理的注册表孤儿条目。</summary>
public sealed record OrphanCleanupTarget(
    string ProductCode,
    string DisplayName,
    string FullPath,
    RegistryHive Hive,
    RegistryView View,
    string UninstallRootPath,
    IReadOnlyList<string> Evidence);

/// <summary>清理计划。</summary>
public sealed class OrphanCleanupPlan
{
    public IReadOnlyList<OrphanCleanupTarget> Targets { get; init; } = [];

    public IReadOnlyList<(string Name, string Reason)> Skipped { get; init; } = [];

    public bool IsEmpty => Targets.Count == 0;

    public int Count => Targets.Count;
}

/// <summary>单条清理结果。</summary>
public sealed record OrphanCleanupResult(
    OrphanCleanupTarget Target,
    bool Success,
    string Message,
    string? BackupPath);

/// <summary>
/// 孤儿 Uninstall 条目的清理器（v0.4）。
///
/// <b>七道闸门</b>（比文件清理更严，因为注册表删错影响面更大）：
/// <list type="number">
/// <item>产品 GUID 必须是严格 <c>{8-4-4-4-12}</c> 格式</item>
/// <item>拒绝 <c>Uninstall</c> 根键本身</item>
/// <item>必须是 <c>...\Uninstall\{GUID}</c>，只差一层</item>
/// <item><b>执行前重新查一次 MSI 状态</b>：必须是 ABSENT（防止扫描到确认之间产品被装上）</item>
/// <item>再次确认不在已安装产品枚举里</item>
/// <item><b>备份必须成功且文件非空</b>，否则不删</item>
/// <item>删除后复核键确实不存在</item>
/// </list>
///
/// <b>为什么允许删键（而 v0.1.3 的 Run 清理只删值）</b>：
/// <c>Uninstall\{GUID}</c> 是"一个产品一个键"，键下所有值都属于这个产品，删键是安全的；
/// 而一个 Run 键下挂着多个软件的值，删键会误伤（ADR-019）。
/// </summary>
public sealed class OrphanCleaner
{
    private static readonly System.Text.RegularExpressions.Regex GuidPattern = new(
        @"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly string _backupDirectory;
    private readonly Func<string, uint> _stateQuery;
    private readonly Func<HashSet<string>> _enumeration;
    private readonly Action<RegistryHive, RegistryView, string> _deleteKey;
    private readonly Func<RegistryHive, RegistryView, string, bool> _keyExists;

    /// <summary>注入依赖，便于单测（不真删注册表）。</summary>
    public OrphanCleaner(
        string? backupDirectory = null,
        Func<string, uint>? stateQuery = null,
        Func<HashSet<string>>? enumeration = null,
        Action<RegistryHive, RegistryView, string>? deleteKey = null,
        Func<RegistryHive, RegistryView, string, bool>? keyExists = null)
    {
        _backupDirectory = backupDirectory ?? DefaultBackupDirectory;
        _stateQuery = stateQuery ?? MsiInterop.QueryProductState;
        _enumeration = enumeration ?? MsiInterop.EnumerateProducts;
        _deleteKey = deleteKey ?? DeleteKeyCore;
        _keyExists = keyExists ?? KeyExistsCore;
    }

    /// <summary>默认备份目录：<c>%LocalAppData%\RubbishCleaner\registry-backups\</c>。</summary>
    public static string DefaultBackupDirectory
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
            {
                local = Path.GetTempPath();
            }

            return Path.Combine(local, "RubbishCleaner", "registry-backups");
        }
    }

    /// <summary>由扫描结果生成清理计划（不含执行前复核）。</summary>
    public OrphanCleanupPlan BuildPlan(IEnumerable<OrphanUninstallEntry> entries)
    {
        var targets = new List<OrphanCleanupTarget>();
        var skipped = new List<(string, string)>();

        foreach (var entry in entries)
        {
            // 闸门 4：必须是 ABSENT（扫描时的判定，执行时还会再查一次）
            if (!MsiInterop.IsAbsent(entry.MsiState))
            {
                skipped.Add((entry.DisplayName, $"MSI 状态不是 ABSENT（{MsiInterop.DescribeState(entry.MsiState)}），拒绝清理"));
                continue;
            }

            // 闸门 1
            if (!GuidPattern.IsMatch(entry.ProductCode))
            {
                skipped.Add((entry.DisplayName, "产品码不是标准 GUID 格式"));
                continue;
            }

            // 闸门 2 + 3：路径必须是 ...\Uninstall\{GUID}
            var expected = $@"{entry.UninstallRootPath}\{entry.ProductCode}";
            if (entry.UninstallRootPath != UninstallRoot)
            {
                skipped.Add((entry.DisplayName, $"不在标准 Uninstall 路径下：{entry.UninstallRootPath}"));
                continue;
            }

            _ = expected;

            targets.Add(new OrphanCleanupTarget(
                entry.ProductCode,
                entry.DisplayName,
                entry.FullPath,
                entry.Hive,
                entry.View,
                entry.UninstallRootPath,
                entry.Evidence));
        }

        return new OrphanCleanupPlan { Targets = targets, Skipped = skipped };
    }

    /// <summary>
    /// 执行清理：逐条 —— 复核 → 备份 → 删键 → 再复核。
    ///
    /// 任何一步失败都<b>保持原状</b>并如实报告；绝不"绕过备份直接删"。
    /// </summary>
    public IReadOnlyList<OrphanCleanupResult> Execute(OrphanCleanupPlan plan, IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var results = new List<OrphanCleanupResult>(plan.Count);
        var installedProducts = _enumeration();

        foreach (var target in plan.Targets)
        {
            var subKeyPath = $@"{target.UninstallRootPath}\{target.ProductCode}";

            // 闸门 4（执行前复核）：状态必须是 ABSENT。
            // 扫描到用户点确认之间，产品可能被重新安装——此时删除会破坏正常软件。
            var state = _stateQuery(target.ProductCode);
            if (!MsiInterop.IsAbsent(state))
            {
                results.Add(new OrphanCleanupResult(
                    target, false,
                    $"跳过：MSI 状态已变化（当前 {MsiInterop.DescribeState(state)}），不再视为孤儿",
                    null));
                continue;
            }

            // 闸门 5：再次确认不在枚举里
            if (installedProducts.Contains(target.ProductCode))
            {
                results.Add(new OrphanCleanupResult(
                    target, false,
                    "跳过：该产品出现在已安装产品列表中，判定不可靠",
                    null));
                continue;
            }

            // 键必须存在，否则没什么可删
            if (!_keyExists(target.Hive, target.View, subKeyPath))
            {
                results.Add(new OrphanCleanupResult(target, false, "跳过：注册表键已不存在", null));
                continue;
            }

            // 闸门 6：备份。备份失败绝不删。
            var backup = RegistryKeyBackupWriter.Write(
                target.Hive,
                target.View,
                subKeyPath,
                _backupDirectory,
                out var backupError);

            if (backup is null)
            {
                results.Add(new OrphanCleanupResult(
                    target, false,
                    $"跳过：备份失败（{backupError ?? "未知原因"}），为安全起见不删除",
                    null));
                continue;
            }

            // 删键
            try
            {
                _deleteKey(target.Hive, target.View, subKeyPath);
            }
            catch (Exception ex)
            {
                results.Add(new OrphanCleanupResult(target, false, $"删除失败：{ex.Message}（备份：{backup}）", backup));
                continue;
            }

            // 闸门 7：复核键确实不存在
            if (_keyExists(target.Hive, target.View, subKeyPath))
            {
                results.Add(new OrphanCleanupResult(target, false, $"删除后复核仍存在（备份：{backup}）", backup));
                continue;
            }

            results.Add(new OrphanCleanupResult(
                target, true,
                $"已删除孤儿条目（备份：{Path.GetFileName(backup)}，可双击 .reg 还原）",
                backup));

            progress?.Report($"已清理：{target.DisplayName}");
        }

        return results;
    }

    private static bool KeyExistsCore(RegistryHive hive, RegistryView view, string subKeyPath)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKeyPath, writable: false);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteKeyCore(RegistryHive hive, RegistryView view, string subKeyPath)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        baseKey.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
    }
}
