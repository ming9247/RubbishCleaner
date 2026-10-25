using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using RubbishCleaner.App.Infrastructure;
using RubbishCleaner.Common.Logging;
using RubbishCleaner.Core.BackupHistory;

namespace RubbishCleaner.App.ViewModels;

/// <summary>备份历史列表中的一行（v0.6）。</summary>
public sealed class BackupHistoryItemViewModel(BackupHistoryEntry entry)
{
    public BackupHistoryEntry Entry { get; } = entry;

    /// <summary>展示名：备份里的 DisplayName，退回文件名。</summary>
    public string DisplayName => Entry.DisplayName ?? Entry.FileName;

    /// <summary>完整键路径（物理路径形态，与 .reg 段头一致）。</summary>
    public string KeyPath => Entry.KeyPath;

    public string TimeText => Entry.BackedUpAt.ToString("yyyy-MM-dd HH:mm:ss");

    public string SizeText => Entry.FileSizeBytes >= 1024
        ? $"{Entry.FileSizeBytes / 1024.0:F1} KB"
        : $"{Entry.FileSizeBytes} B";

    /// <summary>可回滚（绿色标记）。</summary>
    public bool CanRollback => Entry.CanRollback;

    /// <summary>不能回滚的原因；null 表示可回滚。</summary>
    public string? RollbackBlockReason => Entry.RollbackBlockReason;

    /// <summary>状态列的展示文案。</summary>
    public string StateText => CanRollback ? "可回滚" : (RollbackBlockReason ?? "不可回滚");
}

/// <summary>
/// 备份历史页（v0.6）——把 v0.4 起「可还原」的承诺从"去文件夹双击 .reg"
/// 变成界面里的一键回滚。
///
/// <para><b>这一页是 v0.6 唯一的写入口</b>：回滚（reg import 把删除前的键写回）。
/// 因此回滚按钮带二次确认，且有一条反射测试锁死「除 Rollback 外不得出现
/// Clean / Delete / Fix / Repair 类命令」。</para>
///
/// <para>回滚为什么安全：内容是删除前的原样（.reg 原样导入），且 Store 会先检查
/// 目标键是否已被重建——被重建时拒绝回滚而不是覆盖。</para>
/// </summary>
public sealed class BackupHistoryViewModel : ObservableObject
{
    private readonly IBackupHistoryStore _store;

    /// <summary>正在扫描或回滚（两者共用 IsBusy，防并发打乱状态）。</summary>
    private bool _isBusy;
    private string _statusText = "还没有读取备份历史";
    private string? _errorText;
    private BackupHistoryItemViewModel? _selected;
    private BackupRollbackResult? _lastRollback;

    public BackupHistoryViewModel(IBackupHistoryStore? store = null)
    {
        _store = store ?? new BackupHistoryStore();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy)
        {
            OnError = ex =>
            {
                Log.Error("备份历史读取失败", ex);
                ErrorText = $"读取失败：{ex.Message}";
                IsBusy = false;
            },
        };

        RollbackCommand = new AsyncRelayCommand(
            () => RollbackWithConfirmAsync(Selected!),
            () => Selected is { CanRollback: true } && !IsBusy)
        {
            OnError = ex =>
            {
                Log.Error("备份回滚失败", ex);
                ErrorText = $"回滚失败：{ex.Message}";
                IsBusy = false;
            },
        };
    }

    /// <summary>
    /// 回滚前的二次确认。抽成委托是为了让测试能用假确认替换掉 MessageBox——
    /// 否则任何回滚测试都会卡在模态弹窗上（与 MainViewModel.ConfirmWithMessageBox 同一手法）。
    /// 默认实现要求用户显式点「确定」，取消即放弃。
    /// </summary>
    public Func<BackupHistoryItemViewModel, bool> ConfirmRollback { get; set; } = item =>
        System.Windows.MessageBox.Show(
            $"将把已删除的注册表键原样写回：\n\n{item.KeyPath}\n\n" +
            "执行前会检查目标键是否已被重建：若已重建会拒绝回滚。\n确定继续吗？",
            "确认回滚", System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.Cancel)
        == System.Windows.MessageBoxResult.OK;

    private async Task RollbackWithConfirmAsync(BackupHistoryItemViewModel item)
    {
        if (!ConfirmRollback(item))
        {
            StatusText = "已取消回滚";
            return;
        }

        await RollbackAsync(item.Entry, confirmed: true);
    }

    public ObservableCollection<BackupHistoryItemViewModel> Entries { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand RollbackCommand { get; }

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

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public BackupHistoryItemViewModel? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    /// <summary>最近一次回滚的结果（UI 展示用；null 表示还没有回滚过）。</summary>
    public BackupRollbackResult? LastRollback
    {
        get => _lastRollback;
        private set => SetProperty(ref _lastRollback, value);
    }

    /// <summary>页头摘要：检查了多少个备份文件、几条可回滚。</summary>
    public string SummaryText
    {
        get
        {
            if (Entries.Count == 0)
            {
                return IsBusy ? "正在读取备份…" : "还没有备份文件（清理孤儿条目时会自动生成）";
            }

            return $"共 {Entries.Count} 个备份，其中 {Entries.Count(e => e.CanRollback)} 个可回滚。" +
                   "回滚会把已删除的注册表键原样写回。";
        }
    }

    /// <summary>回滚是这一页唯一的写操作，UI 上必须常驻说明（与 v0.5 的只读承诺并列）。</summary>
    public string RollbackNotice =>
        "回滚 = 把已删除的注册表键原样写回（reg import）。执行前会检查目标键是否已被重建：若已重建会拒绝，避免覆盖新内容。";

    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorText = null;
        StatusText = "正在读取备份历史…";
        Entries.Clear();
        OnPropertyChanged(nameof(SummaryText));

        try
        {
            var result = await _store.ListAsync().ConfigureAwait(true);

            foreach (var entry in result.Entries)
            {
                Entries.Add(new BackupHistoryItemViewModel(entry));
            }

            StatusText = result.IsDegraded
                ? $"读取到 {result.Entries.Count} 个备份（{result.DegradedReason}）"
                : $"读取到 {result.Entries.Count} 个备份";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(SummaryText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// 回滚一个备份。<paramref name="confirmed"/> 必须由 UI 的二次确认传入——
    /// 本方法不自己弹确认框（弹窗是视图职责，ViewModel 只认"用户已确认"这个事实）。
    /// </summary>
    public async Task RollbackAsync(BackupHistoryEntry entry, bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!confirmed)
        {
            throw new InvalidOperationException("回滚必须经过用户确认（confirmed=true）");
        }

        if (IsBusy || !entry.CanRollback)
        {
            return;
        }

        IsBusy = true;
        ErrorText = null;
        StatusText = $"正在回滚 {entry.FileName}…";

        try
        {
            var result = await _store.RollbackAsync(entry).ConfigureAwait(true);
            LastRollback = result;

            if (result.Success)
            {
                Log.Info($"备份回滚成功：{entry.KeyPath}（{entry.FileName}）");
            }
            else
            {
                Log.Warn($"备份回滚未完成：{result.Message}");
            }

            // 回滚改变了键的存在性 → 整表重新检查（CanRollback 状态会变）。
            // 两个时序约束（v0.6 开发中实测踩过，有测试锁死）：
            // 1. 必须先解除 IsBusy 再刷新：RefreshAsync 有防重入（IsBusy 时直接 return），
            //    否则刷新被静默吞掉；
            // 2. 回滚结论必须写在刷新之后：刷新会把 StatusText 覆盖成"读取到 N 个备份"，
            //    用户就看不到回滚结果了——与 v0.3 缺陷 26（清理结论被扫描完成覆盖）同源。
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
            StatusText = result.Message;
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task RollbackAsync(BackupHistoryItemViewModel item)
    {
        // 二次确认在视图层做（弹窗），这里只处理"已确认"的调用链；
        // 占位实现由 Command 委托转发——见 XAML 的事件接线。
        await RollbackAsync(item.Entry, confirmed: true);
    }
}