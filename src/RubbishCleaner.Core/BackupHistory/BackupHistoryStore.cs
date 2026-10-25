using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RubbishCleaner.Core.BackupHistory;

/// <summary>
/// 备份历史的扫描与回滚（v0.6）。
///
/// <para><b>数据来源只有一个</b>：备份目录里的 .reg 文件（v0.4.1 起由
/// <c>RegistryKeyBackupWriter</c> 写出，文件名 <c>orphan-{键名}-{yyyyMMddHHmmss}.reg</c>、
/// UTF-16LE+BOM、段头是物理路径）。刻意不建索引数据库——备份目录本身已是事实的
/// 单一来源，再建索引迟早失同步。</para>
///
/// <para><b>回滚的三层防线</b>（契约注释里的承诺，一条都不能少）：</para>
/// <list type="number">
/// <item><b>前置检查</b>：目标键当前已存在 → 拒绝。备份是「删除前的原样」，
/// 但从备份到回滚之间，用户或系统可能新建了同名键——直接 import 会把它覆盖掉；</item>
/// <item><b>执行</b>：<c>reg import</c>——与用户双击 .reg 完全同路径。
/// 不用 RegistryKey API 自己写回：那等于第二套写注册表的实现，编码/子键/值类型
/// 每一处都要重新踩 v0.4.1 已经踩过的坑（UTF-16LE、WOW6432Node、递归子键）；</item>
/// <item><b>复核</b>：import 后键必须真的回来。这与 v0.4「删除后复核键确实不存在」
/// 是同一约定——写操作不以退出码为准，以注册表实际状态为准。</item>
/// </list>
/// </summary>
public sealed class BackupHistoryStore : IBackupHistoryStore
{
    /// <summary>备份文件名的时间戳格式（RegistryKeyBackupWriter 写出的形态）。</summary>
    private const string TimestampFormat = "yyyyMMddHHmmss";

    /// <summary>reg.exe 单次调用的超时。import 是本地文件解析，正常毫秒级。</summary>
    private const int ImportTimeoutMs = 20000;

    /// <summary>.reg 段头行（物理路径）。用中括号锚定，避免匹配到注释或值行。</summary>
    private static readonly Regex SectionHeaderPattern = new(
        @"^\s*\[(.+?)\]\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>DisplayName 值行。v0.4 写出的形态：\`"DisplayName"="值"\`。</summary>
    private static readonly Regex DisplayNamePattern = new(
        @"^""DisplayName""=""(.*)""\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>孤儿条目键名 → .reg 文件名前缀（RegistryKeyBackupWriter 的命名规则）。</summary>
    private const string FileNamePrefix = "orphan-";

    private readonly string _directory;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, int> _importRunner;
    private readonly Func<string, (RegistryView View, string SubKeyPath)?> _keyPathMapper;
    /// <summary>回滚<b>前</b>的目标键存在性检查（防线：键已存在则拒绝，防覆盖新键）。</summary>
    private readonly Func<RegistryView, string, bool> _preRollbackKeyExists;

    /// <summary>回滚<b>后</b>的复核（防线：键必须真的回来，不以退出码为准）。
    /// 两个必须是独立依赖——同一个委托在语义上自相矛盾（先要求不存在、再要求存在），
    /// 测试也无法同时构造两条防线。v0.6 开发中实测踩过（第一版两者共用一个委托，
    /// SuccessPath 与 TargetKeyExists 两条测试互斥失败）。</summary>
    private readonly Func<RegistryView, string, bool> _postImportKeyExists;

    /// <summary>生产构造：真实目录、真实 reg.exe、真实注册表。</summary>
    public BackupHistoryStore(string? directory = null)
        : this(
            directory ?? OrphanDefaultBackupDirectory(),
            fileExists: null,
            importRunner: null,
            keyPathMapper: null,
            preRollbackKeyExists: null,
            postImportKeyExists: null)
    {
    }

    /// <summary>测试构造：全部依赖可注入，绝不真 import。
    /// 「回滚前检查」与「回滚后复核」是两个独立依赖（语义见字段注释）。</summary>
    public BackupHistoryStore(
        string directory,
        Func<string, bool>? fileExists,
        Func<string, int>? importRunner,
        Func<string, (RegistryView View, string SubKeyPath)?>? keyPathMapper,
        Func<RegistryView, string, bool>? preRollbackKeyExists,
        Func<RegistryView, string, bool>? postImportKeyExists)
    {
        _directory = directory;
        _fileExists = fileExists ?? File.Exists;
        _importRunner = importRunner ?? RunRegImport;
        _keyPathMapper = keyPathMapper ?? MapPhysicalKeyPath;
        _preRollbackKeyExists = preRollbackKeyExists ?? DefaultKeyExists;
        _postImportKeyExists = postImportKeyExists ?? DefaultKeyExists;
    }

    private static bool DefaultKeyExists(RegistryView view, string path)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            return baseKey.OpenSubKey(path) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>生产默认目录与 OrphanCleaner 同源（复用，不复制逻辑）。</summary>
    internal static string OrphanDefaultBackupDirectory()
        => RegistryCleanup.OrphanCleaner.DefaultBackupDirectory;

    /// <inheritdoc />
    public Task<BackupHistoryResult> ListAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => List(progress, cancellationToken), cancellationToken);

    private BackupHistoryResult List(IProgress<string>? progress, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        progress?.Report("正在读取备份目录…");

        if (!Directory.Exists(_directory))
        {
            return new BackupHistoryResult
            {
                Entries = [],
                Directory = _directory,
                DegradedReason = $"备份目录不存在：{_directory}",
                Duration = watch.Elapsed,
            };
        }

        var entries = new List<BackupHistoryEntry>();
        var unreadable = 0;

        foreach (var file in Directory.EnumerateFiles(_directory, "*" + ".reg"))
        {
            ct.ThrowIfCancellationRequested();

            var entry = Parse(file);
            if (entry is null)
            {
                // 非 orphan- 前缀或段头解析不出来的文件：不计入也不吞掉，降级说明里给数
                unreadable++;
                continue;
            }

            entries.Add(entry);
        }

        progress?.Report($"读取到 {entries.Count} 个备份");

        return new BackupHistoryResult
        {
            Entries = entries.OrderByDescending(e => e.BackedUpAt).ToList(),
            Directory = _directory,
            DegradedReason = unreadable > 0 ? $"{unreadable} 个文件无法识别（已跳过）" : null,
            Duration = watch.Elapsed,
        };
    }

    /// <summary>解析一个 .reg 备份文件；不符合孤儿备份形态返回 null。</summary>
    private BackupHistoryEntry? Parse(string filePath)
    {
        try
        {
            var fileName = Path.GetFileName(filePath);
            if (!fileName.StartsWith(FileNamePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // 编码必须是 UTF-16LE（RegistryKeyBackupWriter 的既有行为）；
            // 用 ReadAllText 让 .NET 按 BOM 自动判，读坏了内容会是乱码、段头匹配不上，自然降级
            var text = File.ReadAllText(filePath, Encoding.Unicode);
            var header = SectionHeaderPattern.Match(text);
            if (!header.Success)
            {
                return null;
            }

            var keyPath = header.Groups[1].Value.Trim();

            var display = DisplayNamePattern.Match(text);
            var displayName = display.Success ? display.Groups[1].Value : null;

            return new BackupHistoryEntry
            {
                FilePath = filePath,
                FileName = fileName,
                KeyPath = keyPath,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
                BackedUpAt = ParseTimestampFromFileName(fileName) ?? File.GetLastWriteTime(filePath),
                FileSizeBytes = new FileInfo(filePath).Length,
                RollbackBlockReason = ComputeRollbackBlockReason(filePath, keyPath, _preRollbackKeyExists),
            };
        }
        catch (Exception ex)
        {
            Common.Logging.Log.Warn($"备份文件解析失败：{filePath}（{ex.Message}）");
            return null;
        }
    }

    /// <summary>从文件名取时间戳；解析不出返回 null（调用方回退到文件修改时间）。</summary>
    private static DateTimeOffset? ParseTimestampFromFileName(string fileName)
    {
        // orphan-{键名}-{yyyyMMddHHmmss}.reg —— 键名本身可能含连字符，
        // 所以从右边找 14 位纯数字段，而不是按连字符切
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var parts = stem.Split('-');
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            var part = parts[i];
            if (part.Length != 14 || !part.All(char.IsDigit))
            {
                continue;
            }

            if (DateTimeOffset.TryParseExact(
                    part, TimestampFormat, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal,
                    out var value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>回滚前检查：文件还在吗？目标键是不是已经被谁建回来了？</summary>
    private string? ComputeRollbackBlockReason(
        string filePath, string keyPath, Func<RegistryView, string, bool> keyExists)
    {
        if (!_fileExists(filePath))
        {
            return "备份文件已不存在";
        }

        var mapped = _keyPathMapper(keyPath);
        if (mapped is null)
        {
            return "无法从段头解析出注册表位置";
        }

        if (keyExists(mapped.Value.View, mapped.Value.SubKeyPath))
        {
            // 这正是回滚最危险的场景：删掉之后的原键位被新建过。
            // 此时 import 会覆盖新内容，必须交给人核对。
            return "目标键当前已存在（可能是被重新创建的），自动回滚会覆盖它";
        }

        return null;
    }

    /// <inheritdoc />
    public Task<BackupRollbackResult> RollbackAsync(
        BackupHistoryEntry entry,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Rollback(entry, progress, cancellationToken), cancellationToken);

    private BackupRollbackResult Rollback(BackupHistoryEntry entry, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report($"正在回滚 {entry.FileName}…");

        // 防线 1：回滚前的状态必须重新检查——List 到 Rollback 之间状态可能变化
        if (!_fileExists(entry.FilePath))
        {
            return new BackupRollbackResult
            {
                Entry = entry, Success = false, Message = "备份文件已不存在，无法回滚",
            };
        }

        var mapped = _keyPathMapper(entry.KeyPath);
        if (mapped is null)
        {
            return new BackupRollbackResult
            {
                Entry = entry, Success = false, Message = $"无法从段头解析注册表位置：{entry.KeyPath}",
            };
        }

        if (_preRollbackKeyExists(mapped.Value.View, mapped.Value.SubKeyPath))
        {
            return new BackupRollbackResult
            {
                Entry = entry,
                Success = false,
                Message = $"目标键已存在（{entry.KeyPath}），为避免覆盖未执行回滚；请人工核对该键内容",
            };
        }

        // 防线 2：reg import——与用户双击 .reg 完全同路径
        progress?.Report("正在导入注册表…");
        var exitCode = _importRunner(entry.FilePath);
        if (exitCode != 0)
        {
            return new BackupRollbackResult
            {
                Entry = entry,
                Success = false,
                Message = $"reg import 失败（退出码 {exitCode}）",
                LogHint = entry.FileName,
            };
        }

        ct.ThrowIfCancellationRequested();

        // 防线 3：复核键确实回来了
        if (!_postImportKeyExists(mapped.Value.View, mapped.Value.SubKeyPath))
        {
            return new BackupRollbackResult
            {
                Entry = entry,
                Success = false,
                Message = $"reg import 退出码为 0，但复核发现键仍未恢复（{entry.KeyPath}）",
                LogHint = entry.FileName,
            };
        }

        progress?.Report("回滚完成");
        return new BackupRollbackResult
        {
            Entry = entry,
            Success = true,
            Message = $"已把 {entry.KeyPath} 原样写回（备份：{entry.FileName}）",
            LogHint = entry.FileName,
        };
    }

    private static int RunRegImport(string filePath)
    {
        try
        {
            var psi = new ProcessStartInfo("reg.exe", $"import \"{filePath}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return -1;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(ImportTimeoutMs);
            return process.HasExited ? process.ExitCode : -2;
        }
        catch
        {
            return -3;
        }
    }

    /// <summary>
    /// 把 .reg 段头的物理路径映射回 (视图, 子键路径)。
    ///
    /// <para>ADR-049 的逆运算：32 位视图的备份段头写的是
    /// <c>HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\...</c>（物理路径），
    /// 查键时要映射回 <c>RegistryView.Registry32</c> + 去 WOW6432Node 的原路径。
    /// HKCU 不做重定向，保持原样（ADR-049 的 HKCU 依据）。</para>
    /// </summary>
    internal static (RegistryView View, string SubKeyPath)? MapPhysicalKeyPath(string regSectionPath)
    {
        if (string.IsNullOrWhiteSpace(regSectionPath))
        {
            return null;
        }

        var trimmed = regSectionPath.Trim().TrimEnd('\\');
        const string Hklm = @"HKEY_LOCAL_MACHINE\";
        const string Hkcu = @"HKEY_CURRENT_USER\";
        const string Wow = @"SOFTWARE\WOW6432Node\";
        const string Software = @"SOFTWARE\";

        if (trimmed.StartsWith(Hklm, StringComparison.OrdinalIgnoreCase))
        {
            var sub = trimmed[Hklm.Length..];
            // WOW6432Node 只在 SOFTWARE 子树下才是重定向（system32\drivers 形态的 Wll 不在此列）
            if (sub.StartsWith(Wow, StringComparison.OrdinalIgnoreCase))
            {
                return (RegistryView.Registry32, Software + sub[Wow.Length..]);
            }

            return (RegistryView.Registry64, sub);
        }

        if (trimmed.StartsWith(Hkcu, StringComparison.OrdinalIgnoreCase))
        {
            return (RegistryView.Registry64, trimmed[Hkcu.Length..]);
        }

        // HKEY_USERS 等其他 hive：v0.4 的备份不会出现，出现即是异常数据，保守不处理
        return null;
    }
}