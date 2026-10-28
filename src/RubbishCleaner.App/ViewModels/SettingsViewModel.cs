using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using RubbishCleaner.App.Infrastructure;
using RubbishCleaner.Common.Logging;
using RubbishCleaner.Core.BackupHistory;
using RubbishCleaner.Core.Models;
using RubbishCleaner.Core.Trust;

namespace RubbishCleaner.App.ViewModels;

/// <summary>
/// 设置页视图模型（v0.7）。
///
/// <para>三个职责，互不依赖：</para>
/// <list type="number">
/// <item>联网开关——默认关闭，关闭时所有联网按钮禁用（v0.7 核心约定）。</item>
/// <item>特征库在线更新——检查 + 立即更新，只写用户层 trust-table.json。</item>
/// <item>备份保留数量——超过 N 个的旧备份移入回收站（永不硬删除）。</item>
/// <item>特征库离线编辑——在应用内查看/新增/修改/删除用户层规则，并支持 JSON 导入导出
/// （内置层与程序目录层只读，删除只作用于用户层）。</item>
/// </list>
///
/// <para>为什么清理旧备份的按钮放在这里而不是备份历史页：备份历史页有反射测试
/// 锁死「除 Rollback 外不得出现 Clean / Delete / Fix / Repair 类命令」（v0.6 约定，
/// 那一页的写操作只允许回滚）。清理旧备份属于维护操作，归设置页。</para>
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly Func<TrustTableUpdater> _updaterFactory;
    private readonly IBackupHistoryStore _backupStore;
    private readonly AppSettings _settings;
    private readonly TrustTableEditor _trustEditor;
    private readonly Func<string?> _pickImportFilePath;
    private readonly Func<string, string?> _pickExportFilePath;

    private bool _enableNetworking;
    private string _keepBackupCountText = "30";
    private string _updateStatusText = "还没有检查过更新";
    private bool _isBusy;
    private string? _errorText;

    private TrustTableSnapshot _trustSnapshot = TrustTableSnapshot.Empty;
    private TrustRuleEntry? _selectedTrustEntry;
    private TrustRuleTypeOption _selectedRuleType = TrustRuleTypeOption.All[0];
    private string _ruleValueText = string.Empty;
    private string _ruleNoteText = string.Empty;
    private bool _ruleIsBlacklist;
    private string? _trustEditorStatusText;

    /// <param name="updaterFactory">在线更新器工厂，默认 <see cref="TrustTableUpdater"/>。</param>
    /// <param name="backupStore">备份历史存储，默认 <see cref="BackupHistoryStore"/>。</param>
    /// <param name="settings">应用设置，默认从磁盘加载。</param>
    /// <param name="trustEditor">特征库编辑器，默认写 <c>%ProgramData%\RubbishCleaner\trust-table.json</c>。</param>
    /// <param name="pickImportFilePath">选导入文件的委托；默认弹 <c>OpenFileDialog</c>，测试可注入。</param>
    /// <param name="pickExportFilePath">选导出保存路径的委托；默认弹 <c>SaveFileDialog</c>，测试可注入。</param>
    public SettingsViewModel(
        Func<TrustTableUpdater>? updaterFactory = null,
        IBackupHistoryStore? backupStore = null,
        AppSettings? settings = null,
        TrustTableEditor? trustEditor = null,
        Func<string?>? pickImportFilePath = null,
        Func<string, string?>? pickExportFilePath = null)
    {
        _updaterFactory = updaterFactory ?? (() => new TrustTableUpdater());
        _backupStore = backupStore ?? new BackupHistoryStore();
        _settings = settings ?? AppSettings.Load();
        _trustEditor = trustEditor ?? new TrustTableEditor();
        _pickImportFilePath = pickImportFilePath ?? DefaultPickImportFilePath;
        _pickExportFilePath = pickExportFilePath ?? DefaultPickExportFilePath;

        _enableNetworking = _settings.EnableNetworking;
        _keepBackupCountText = _settings.KeepBackupCount.ToString();

        CheckUpdateCommand = new AsyncRelayCommand(CheckUpdateAsync, () => CanUseNetwork && !IsBusy)
        {
            OnError = ex =>
            {
                Log.Error("特征库更新检查失败", ex);
                ErrorText = $"检查更新失败：{ex.Message}";
                IsBusy = false;
            },
        };

        UpdateNowCommand = new AsyncRelayCommand(UpdateNowAsync, () => CanUseNetwork && !IsBusy)
        {
            OnError = ex =>
            {
                Log.Error("特征库更新失败", ex);
                ErrorText = $"更新失败：{ex.Message}";
                IsBusy = false;
            },
        };

        CleanOldBackupsCommand = new AsyncRelayCommand(CleanOldBackupsAsync, () => !IsBusy)
        {
            OnError = ex =>
            {
                Log.Error("清理旧备份失败", ex);
                ErrorText = $"清理旧备份失败：{ex.Message}";
                IsBusy = false;
            },
        };

        AddTrustRuleCommand = new AsyncRelayCommand(AddTrustRuleAsync, () => !IsBusy)
        {
            OnError = ex =>
            {
                Log.Error("新增特征库规则失败", ex);
                ErrorText = $"新增规则失败：{ex.Message}";
                IsBusy = false;
            },
        };

        UpdateTrustRuleCommand = new AsyncRelayCommand(UpdateTrustRuleAsync, () => !IsBusy && CanEditSelectedEntry)
        {
            OnError = ex =>
            {
                Log.Error("修改特征库规则失败", ex);
                ErrorText = $"修改规则失败：{ex.Message}";
                IsBusy = false;
            },
        };

        DeleteTrustRuleCommand = new AsyncRelayCommand(DeleteTrustRuleAsync, () => !IsBusy && CanEditSelectedEntry)
        {
            OnError = ex =>
            {
                Log.Error("删除特征库规则失败", ex);
                ErrorText = $"删除规则失败：{ex.Message}";
                IsBusy = false;
            },
        };

        ImportTrustTableCommand = new AsyncRelayCommand(ImportTrustTableAsync, () => !IsBusy)
        {
            OnError = ex =>
            {
                Log.Error("导入特征库失败", ex);
                ErrorText = $"导入失败：{ex.Message}";
                IsBusy = false;
            },
        };

        ExportTrustTableCommand = new AsyncRelayCommand(
            () => ExportTrustTableAsync(mergedTable: false),
            () => !IsBusy)
        {
            OnError = ex =>
            {
                Log.Error("导出特征库失败", ex);
                ErrorText = $"导出失败：{ex.Message}";
                IsBusy = false;
            },
        };

        ExportMergedTrustTableCommand = new AsyncRelayCommand(
            () => ExportTrustTableAsync(mergedTable: true),
            () => !IsBusy)
        {
            OnError = ex =>
            {
                Log.Error("导出合并特征库失败", ex);
                ErrorText = $"导出合并表失败：{ex.Message}";
                IsBusy = false;
            },
        };

        ReloadTrustTable();
    }

    /// <summary>联网开关。关闭时更新按钮禁用，且不发起任何网络请求。</summary>
    public bool EnableNetworking
    {
        get => _enableNetworking;
        set
        {
            if (SetProperty(ref _enableNetworking, value))
            {
                _settings.EnableNetworking = value;
                SaveQuietly();
                OnPropertyChanged(nameof(NetworkNotice));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>开关旁的说明文字。</summary>
    public string NetworkNotice => EnableNetworking
        ? "已启用联网：仅「检查更新 / 立即更新」会访问 GitHub，其余功能完全离线。"
        : "联网已关闭（默认）：所有功能完全离线。启用后才能在线更新特征库。";

    /// <summary>备份保留数量（文本形态，便于 TextBox 编辑；执行时解析）。</summary>
    public string KeepBackupCountText
    {
        get => _keepBackupCountText;
        set => SetProperty(ref _keepBackupCountText, value);
    }

    /// <summary>更新检查 / 执行的状态文案。</summary>
    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set => SetProperty(ref _updateStatusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    /// <summary>当前是否允许联网（开关打开才允许）。</summary>
    public bool CanUseNetwork => EnableNetworking;

    public ICommand CheckUpdateCommand { get; }

    public ICommand UpdateNowCommand { get; }

    public ICommand CleanOldBackupsCommand { get; }

    /// <summary>
    /// 清理旧备份前的二次确认。与 MainViewModel.ConfirmHandler 同一手法：
    /// 默认 MessageBox，测试可替换。参数是"将要移入回收站的文件数"。
    /// </summary>
    public Func<int, bool> ConfirmCleanOldBackups { get; set; } = count =>
        System.Windows.MessageBox.Show(
            $"将把 {count} 个较早的备份移入回收站（不直接删除，可从回收站还原）。\n确定继续吗？",
            "确认清理旧备份", System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.Cancel)
        == System.Windows.MessageBoxResult.OK;

    // ───────── 特征库编辑（第四职责） ─────────

    /// <summary>特征库全部规则（三层合并后的视图，带来源层标注）。</summary>
    public ObservableCollection<TrustRuleEntry> TrustEntries { get; } = [];

    /// <summary>可选规则类型（下拉框数据源）。</summary>
    public IReadOnlyList<TrustRuleTypeOption> TrustRuleTypes => TrustRuleTypeOption.All;

    /// <summary>当前选中的规则；选中只读层规则时「删除 / 修改」按钮禁用。</summary>
    public TrustRuleEntry? SelectedTrustEntry
    {
        get => _selectedTrustEntry;
        set
        {
            if (SetProperty(ref _selectedTrustEntry, value))
            {
                if (value is not null)
                {
                    // 选中即回填到编辑区，用户改完点「保存修改」即可。
                    _selectedRuleType = TrustRuleTypeOption.From(value.Type);
                    OnPropertyChanged(nameof(SelectedRuleType));
                    RuleValueText = value.Value;
                    RuleNoteText = value.Note ?? string.Empty;
                    RuleIsBlacklist = value.IsBlacklist;
                }

                OnPropertyChanged(nameof(SelectionNotice));
                OnPropertyChanged(nameof(CanEditSelectedEntry));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>编辑区选中的规则类型。</summary>
    public TrustRuleTypeOption SelectedRuleType
    {
        get => _selectedRuleType;
        set => SetProperty(ref _selectedRuleType, value);
    }

    /// <summary>编辑区的规则值（path 类型必须以反斜杠结尾）。</summary>
    public string RuleValueText
    {
        get => _ruleValueText;
        set => SetProperty(ref _ruleValueText, value);
    }

    /// <summary>编辑区的备注（可空）。</summary>
    public string RuleNoteText
    {
        get => _ruleNoteText;
        set => SetProperty(ref _ruleNoteText, value);
    }

    /// <summary>编辑区：勾选为黑名单，否则白名单。</summary>
    public bool RuleIsBlacklist
    {
        get => _ruleIsBlacklist;
        set => SetProperty(ref _ruleIsBlacklist, value);
    }

    /// <summary>当前选中项是否可改删（只读层为 false）。</summary>
    public bool CanEditSelectedEntry => SelectedTrustEntry?.IsReadOnly == false;

    /// <summary>选中项的说明：只读层要告诉用户为什么改不了。</summary>
    public string SelectionNotice => SelectedTrustEntry switch
    {
        null => "选中一条规则后可修改或删除；内置层与程序目录层只读。",
        { IsReadOnly: true } entry => $"该规则来自{entry.LayerText}，只读：删除与修改只作用于用户层。",
        var entry => $"已选中用户层规则 {entry.Rule}，可修改或删除。",
    };

    /// <summary>特征库编辑的状态文案（成功提示）。</summary>
    public string? TrustEditorStatusText
    {
        get => _trustEditorStatusText;
        private set => SetProperty(ref _trustEditorStatusText, value);
    }

    /// <summary>用户层文件路径，界面上告知用户改动写到哪里。</summary>
    public string UserTablePath => _trustEditor.UserTablePath;

    /// <summary>用户层文件是否已存在（不存在时首次编辑会自动创建）。</summary>
    public bool UserTableExists => _trustEditor.UserTableExists;

    /// <summary>用户层当前规则条数（白 + 黑）。</summary>
    public int UserRuleCount => _trustSnapshot.UserLayer.Whitelist.Count + _trustSnapshot.UserLayer.Blacklist.Count;

    /// <summary>三层合并后的规则条数。</summary>
    public int MergedRuleCount => _trustSnapshot.Merged.Whitelist.Count + _trustSnapshot.Merged.Blacklist.Count;

    /// <summary>三层合并后的版本号（用户层保存时会提到它 + 1）。</summary>
    public int MergedVersion => _trustSnapshot.Merged.Version;

    public ICommand AddTrustRuleCommand { get; }

    public ICommand UpdateTrustRuleCommand { get; }

    public ICommand DeleteTrustRuleCommand { get; }

    public ICommand ImportTrustTableCommand { get; }

    public ICommand ExportTrustTableCommand { get; }

    /// <summary>导出三层合并后的全部规则（文件名会写明「合并表」，导入时只取用户层没有的部分）。</summary>
    public ICommand ExportMergedTrustTableCommand { get; }

    /// <summary>重新读取特征库并刷新列表（不改盘）。</summary>
    public void ReloadTrustTable()
    {
        try
        {
            _trustSnapshot = _trustEditor.Snapshot();
        }
        catch (Exception ex)
        {
            // 读盘失败不能拖垮设置页：保留上一次快照并提示。
            Log.Warn($"特征库读取失败：{ex.Message}");
            ErrorText = $"读取特征库失败：{ex.Message}";
            return;
        }

        var previous = SelectedTrustEntry;
        TrustEntries.Clear();
        foreach (var entry in _trustSnapshot.Entries)
        {
            TrustEntries.Add(entry);
        }

        // 尽量保持选中项（按规则本身重找），找不到就清空。
        SelectedTrustEntry = previous is null
            ? null
            : TrustEntries.FirstOrDefault(e =>
                e.IsBlacklist == previous.IsBlacklist && e.Rule == previous.Rule);

        OnPropertyChanged(nameof(UserRuleCount));
        OnPropertyChanged(nameof(MergedRuleCount));
        OnPropertyChanged(nameof(MergedVersion));
        OnPropertyChanged(nameof(UserTableExists));
    }

    /// <summary>新增规则到用户层。</summary>
    public Task AddTrustRuleAsync()
    {
        var rule = new TrustRule(SelectedRuleType.Type, RuleValueText, RuleNoteText);
        var result = _trustEditor.Add(rule, RuleIsBlacklist);
        ApplyEditResult(result, successMessage: result.Message);
        return Task.CompletedTask;
    }

    /// <summary>把选中规则改成编辑区的内容（白/黑名单可互转）。</summary>
    public Task UpdateTrustRuleAsync()
    {
        if (SelectedTrustEntry is not { } entry)
        {
            ErrorText = "请先在列表中选中要修改的规则。";
            return Task.CompletedTask;
        }

        var updated = new TrustRule(SelectedRuleType.Type, RuleValueText, RuleNoteText);
        var result = _trustEditor.Update(entry.Rule, entry.IsBlacklist, updated, RuleIsBlacklist);
        ApplyEditResult(result, successMessage: result.Message);
        return Task.CompletedTask;
    }

    /// <summary>删除选中规则（只作用于用户层）。</summary>
    public Task DeleteTrustRuleAsync()
    {
        if (SelectedTrustEntry is not { } entry)
        {
            ErrorText = "请先在列表中选中要删除的规则。";
            return Task.CompletedTask;
        }

        var result = _trustEditor.Delete(entry.Rule, entry.IsBlacklist);
        ApplyEditResult(result, successMessage: result.Message);
        return Task.CompletedTask;
    }

    /// <summary>从 JSON 文件导入规则（只追加用户层还没有的）。</summary>
    public Task ImportTrustTableAsync()
    {
        var path = _pickImportFilePath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask; // 用户取消，不算错误。
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"读取导入文件失败：{ex.Message}");
            ErrorText = $"读取导入文件失败：{ex.Message}";
            return Task.CompletedTask;
        }

        var result = _trustEditor.Import(json);
        if (result.Succeeded)
        {
            Log.Info($"特征库导入完成：新增 {result.Added}，跳过 {result.Skipped}，无效 {result.Invalid}");
        }

        ApplyEditResult(
            new TrustEditResult(result.Status, result.Message, result.Table),
            successMessage: result.Message);
        return Task.CompletedTask;
    }

    /// <summary>导出特征库。默认导出用户层（可与导入形成闭环），也可导出三层合并表。</summary>
    public Task ExportTrustTableAsync(bool mergedTable)
    {
        var defaultName = mergedTable
            ? $"trust-table-合并表-{DateTime.Now:yyyyMMdd}.json"
            : $"trust-table-用户层-{DateTime.Now:yyyyMMdd}.json";

        var path = _pickExportFilePath(defaultName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask; // 用户取消。
        }

        try
        {
            var json = mergedTable ? _trustEditor.ExportMergedTable() : _trustEditor.ExportUserLayer();
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Log.Warn($"导出特征库失败：{ex.Message}");
            ErrorText = $"导出失败：{ex.Message}";
            return Task.CompletedTask;
        }

        TrustEditorStatusText = mergedTable
            ? $"已导出全部规则（合并表，共 {MergedRuleCount} 条）到 {path}。注意：重新导入时只会写入用户层没有的规则。"
            : $"已导出用户层规则（共 {UserRuleCount} 条）到 {path}。";
        ErrorText = null;
        Log.Info($"特征库导出成功：{path}");
        return Task.CompletedTask;
    }

    /// <summary>把编辑结果反映到界面：成功刷列表 + 状态文案，失败写 ErrorText（不抛异常）。</summary>
    private void ApplyEditResult(TrustEditResult result, string successMessage)
    {
        if (result.Succeeded)
        {
            ReloadTrustTable();
            ErrorText = null;
            TrustEditorStatusText = successMessage;
            return;
        }

        // 重复 / 非法 / 只读都是「可解释的拒绝」，用同一处错误提示展示原因。
        ErrorText = result.Message;
    }

    private static string? DefaultPickImportFilePath()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入特征库（JSON）",
            Filter = "特征库文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? DefaultPickExportFilePath(string defaultName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出特征库（JSON）",
            Filter = "特征库文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            FileName = defaultName,
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>解析保留数量；非法输入返回 null 并写错误提示。</summary>
    private int? ParseKeepCount()
    {
        if (!int.TryParse(KeepBackupCountText.Trim(), out var count) || count < 1)
        {
            ErrorText = "备份保留数量必须是正整数。";
            return null;
        }

        // 持久化合法值。
        if (count != _settings.KeepBackupCount)
        {
            _settings.KeepBackupCount = count;
            SaveQuietly();
        }

        return count;
    }

    private void SaveQuietly()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            // 设置保存失败不阻断操作本身（例如 ProgramData 只读），记日志即可。
            Log.Warn($"设置保存失败：{ex.Message}");
        }
    }

    /// <summary>检查远程特征库是否有更新（只读下载到内存，不写文件）。</summary>
    public async Task CheckUpdateAsync()
    {
        if (!CanUseNetwork)
        {
            // 联网守卫不只靠按钮的 CanExecute：直调（测试/未来调用方）同样必须拦住。
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorText = null;
        UpdateStatusText = "正在检查更新…";
        try
        {
            using var updater = _updaterFactory();
            var result = await updater.CheckUpdateAsync().ConfigureAwait(true);
            if (result.Available)
            {
                UpdateStatusText = $"发现新版本：v{result.RemoteVersion}（当前 v{result.CurrentVersion}，更新日期 {result.RemoteUpdatedDate ?? "未知"}）。点击「立即更新」。";
            }
            else
            {
                UpdateStatusText = $"已是最新版本 v{result.CurrentVersion}，无需更新。";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 立即更新特征库：下载并写入用户层 trust-table.json（只在远程版本更新时写入，
    /// 符合 ADR-008 追加合并语义——内置表与程序目录表永远不会被覆盖）。
    /// </summary>
    public async Task UpdateNowAsync()
    {
        if (!CanUseNetwork)
        {
            // 同 CheckUpdateAsync：直调也必须拦住，守卫不依赖按钮状态。
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorText = null;
        UpdateStatusText = "正在下载特征库…";
        try
        {
            using var updater = _updaterFactory();
            var result = await updater.UpdateAsync().ConfigureAwait(true);

            if (result.Success)
            {
                Log.Info($"特征库已更新：v{result.OldVersion} → v{result.NewVersion}");
                UpdateStatusText = $"更新完成：v{result.OldVersion} → v{result.NewVersion}。重启应用后生效。";
            }
            else
            {
                Log.Warn($"特征库更新未完成：{result.Message}");
                UpdateStatusText = result.Message;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 清理旧备份：按备份时间保留最新的 N 个，其余移入回收站（永不硬删除）。
    /// </summary>
    public async Task CleanOldBackupsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var keepCount = ParseKeepCount();
        if (keepCount is null)
        {
            return;
        }

        IsBusy = true;
        ErrorText = null;
        UpdateStatusText = "正在统计备份…";
        try
        {
            // 先统计有多少个会被移走，让确认框有依据；没有多余备份就直接结束。
            var list = await _backupStore.ListAsync().ConfigureAwait(true);
            var extra = Math.Max(0, list.Entries.Count - keepCount.Value);
            if (extra == 0)
            {
                UpdateStatusText = $"备份共 {list.Entries.Count} 个，未超过保留数量 {keepCount.Value}，无需清理。";
                return;
            }

            if (!ConfirmCleanOldBackups(extra))
            {
                UpdateStatusText = "已取消清理。";
                return;
            }

            UpdateStatusText = $"正在清理，保留最新 {keepCount.Value} 个…";
            var result = await _backupStore.CleanOldBackupsAsync(keepCount.Value).ConfigureAwait(true);

            if (result.Success)
            {
                Log.Info($"旧备份清理完成：保留 {result.KeptBackups}，移入回收站 {result.RemovedBackups}");
                UpdateStatusText = $"清理完成：保留 {result.KeptBackups} 个，{result.RemovedBackups} 个已移入回收站。";
            }
            else
            {
                var failed = result.FailedFiles is { Count: > 0 }
                    ? "失败文件：" + string.Join("；", result.FailedFiles)
                    : string.Empty;
                Log.Warn($"旧备份清理未完成：{result.Message}");
                UpdateStatusText = $"清理未完成：{result.Message}{failed}";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
