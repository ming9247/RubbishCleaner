using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using RubbishCleaner.App.Infrastructure;
using RubbishCleaner.Common.Logging;
using RubbishCleaner.Core.BackupHistory;
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

    private bool _enableNetworking;
    private string _keepBackupCountText = "30";
    private string _updateStatusText = "还没有检查过更新";
    private bool _isBusy;
    private string? _errorText;

    public SettingsViewModel(
        Func<TrustTableUpdater>? updaterFactory = null,
        IBackupHistoryStore? backupStore = null,
        AppSettings? settings = null)
    {
        _updaterFactory = updaterFactory ?? (() => new TrustTableUpdater());
        _backupStore = backupStore ?? new BackupHistoryStore();
        _settings = settings ?? AppSettings.Load();

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
