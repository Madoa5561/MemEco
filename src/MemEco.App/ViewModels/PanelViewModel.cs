using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MemEco.App.Presentation;
using MemEco.Core.Models;

namespace MemEco.App.ViewModels;

/// <summary>パネルの 1 行分（エコ対象アプリ）。</summary>
public sealed class ProcessRow
{
    public required string Name { get; init; }
    public int Pid { get; init; }
    public string StateKey { get; init; } = "none";
    public string Badge { get; init; } = "";
    public string ReclaimedText { get; init; } = "";
    public string Detail { get; init; } = "";
}

/// <summary>メインパネルの表示状態。UI スレッド専用。</summary>
public sealed class PanelViewModel : INotifyPropertyChanged
{
    private const int HistoryLength = 60;
    private readonly double[] _history = new double[HistoryLength];
    private int _historyCount;

    private bool _hasData;
    private double _score;
    private MemoryPressureLevel _level = MemoryPressureLevel.Normal;
    private string _statusWord = "計測中…";
    private string _statusCaption = "メモリプレッシャー";
    private string _levelKey = "nodata";
    private string _availableText = "—";
    private string _totalText = "—";
    private string _totalCaptionText = "搭載 —";
    private string _availableCaption = "利用可能メモリ";
    private double _usedRatio;
    private double _cacheRatio;
    private string _usedPercentText = "—";
    private string _usedLegend = "使用中 —";
    private string _cacheLegend = "キャッシュ —";
    private string _freeLegend = "空き —";
    private double _commitRatio;
    private string _commitPercentText = "—";
    private string _commitText = "コミット —";
    private bool _autoManage = true;
    private AutoPreset _preset = AutoPreset.Balanced;
    private bool _isAdmin;
    private string _modeText = "標準モード";
    private string _todayReclaimedText = "—";
    private string _sessionReclaimedText = "—";
    private string _ecoCountText = "0";
    private string _suspendCountText = "0";
    private string _statusLine = "監視を準備しています";
    private bool _isBusy;
    private bool _isProtectionSuspended;
    private string _versionText = "v1.0.0";
    private double[]? _historySnapshot;
    private string? _notice;
    private string? _lastActivity;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>ユーザー操作（パネル上のトグル）で発生する。</summary>
    public event Action<bool>? AutoManageChanged;

    public event Action<AutoPreset>? PresetChanged;

    public bool HasData
    {
        get => _hasData;
        private set
        {
            if (Set(ref _hasData, value))
            {
                OnPropertyChanged(nameof(HasNoData));
            }
        }
    }

    public bool HasNoData => !_hasData;

    public double Score
    {
        get => _score;
        private set => Set(ref _score, value);
    }

    public MemoryPressureLevel Level
    {
        get => _level;
        private set => Set(ref _level, value);
    }

    public string LevelKey
    {
        get => _levelKey;
        private set => Set(ref _levelKey, value);
    }

    public string StatusWord
    {
        get => _statusWord;
        private set => Set(ref _statusWord, value);
    }

    public string StatusCaption
    {
        get => _statusCaption;
        private set => Set(ref _statusCaption, value);
    }

    public string AvailableText
    {
        get => _availableText;
        private set => Set(ref _availableText, value);
    }

    public string TotalText
    {
        get => _totalText;
        private set => Set(ref _totalText, value);
    }

    public string TotalCaptionText
    {
        get => _totalCaptionText;
        private set => Set(ref _totalCaptionText, value);
    }

    public string AvailableCaption
    {
        get => _availableCaption;
        private set => Set(ref _availableCaption, value);
    }

    public double UsedRatio
    {
        get => _usedRatio;
        private set => Set(ref _usedRatio, value);
    }

    public double CacheRatio
    {
        get => _cacheRatio;
        private set => Set(ref _cacheRatio, value);
    }

    public string UsedPercentText
    {
        get => _usedPercentText;
        private set => Set(ref _usedPercentText, value);
    }

    public string UsedLegend
    {
        get => _usedLegend;
        private set => Set(ref _usedLegend, value);
    }

    public string CacheLegend
    {
        get => _cacheLegend;
        private set => Set(ref _cacheLegend, value);
    }

    public string FreeLegend
    {
        get => _freeLegend;
        private set => Set(ref _freeLegend, value);
    }

    public double CommitRatio
    {
        get => _commitRatio;
        private set => Set(ref _commitRatio, value);
    }

    public string CommitPercentText
    {
        get => _commitPercentText;
        private set => Set(ref _commitPercentText, value);
    }

    public string CommitText
    {
        get => _commitText;
        private set => Set(ref _commitText, value);
    }

    public double[]? History
    {
        get => _historySnapshot;
        private set => Set(ref _historySnapshot, value);
    }

    public bool AutoManage
    {
        get => _autoManage;
        set
        {
            if (Set(ref _autoManage, value))
            {
                AutoManageChanged?.Invoke(value);
            }
        }
    }

    public AutoPreset Preset
    {
        get => _preset;
        private set => Set(ref _preset, value);
    }

    public bool IsAdmin
    {
        get => _isAdmin;
        private set
        {
            if (Set(ref _isAdmin, value))
            {
                OnPropertyChanged(nameof(CanPurgeStandby));
            }
        }
    }

    public bool CanPurgeStandby => _isAdmin;

    public string ModeText
    {
        get => _modeText;
        private set => Set(ref _modeText, value);
    }

    public string TodayReclaimedText
    {
        get => _todayReclaimedText;
        private set => Set(ref _todayReclaimedText, value);
    }

    public string SessionReclaimedText
    {
        get => _sessionReclaimedText;
        private set => Set(ref _sessionReclaimedText, value);
    }

    public string EcoCountText
    {
        get => _ecoCountText;
        private set => Set(ref _ecoCountText, value);
    }

    public string SuspendCountText
    {
        get => _suspendCountText;
        private set => Set(ref _suspendCountText, value);
    }

    public string StatusLine
    {
        get => _statusLine;
        private set => Set(ref _statusLine, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                OnPropertyChanged(nameof(ReclaimButtonText));
            }
        }
    }

    public bool IsNotBusy => !_isBusy;

    public string ReclaimButtonText => _isBusy ? "解放中…" : "今すぐ解放";

    public bool IsProtectionSuspended
    {
        get => _isProtectionSuspended;
        private set => Set(ref _isProtectionSuspended, value);
    }

    public string VersionText
    {
        get => _versionText;
        private set => Set(ref _versionText, value);
    }

    public ObservableCollection<ProcessRow> Processes { get; } = new();

    public bool HasProcesses => Processes.Count > 0;

    public string ProcessEmptyText => "まだエコ対象のアプリはありません";

    public string ManagedCountText => Processes.Count == 0 ? "" : $"{Processes.Count} 件";

    public void SetVersion(string version) => VersionText = version;

    public void SetAdmin(bool isAdmin)
    {
        IsAdmin = isAdmin;
        ModeText = isAdmin ? "管理者モード" : "標準モード";
    }

    /// <summary>設定側の変更を UI に反映する（ユーザー操作コールバックは発火しない）。</summary>
    public void SetConfigState(AppConfig config)
    {
        Set(ref _autoManage, config.AutoManage, nameof(AutoManage));
        Preset = config.Preset;
    }

    /// <summary>エンジンからのスナップショットを反映する。</summary>
    public void ApplySnapshot(EngineSnapshot snapshot)
    {
        ApplyMemory(snapshot.Memory);

        TodayReclaimedText = Format.Freed(snapshot.Stats.ReclaimedTodayBytes);
        SessionReclaimedText = Format.Freed(snapshot.Stats.TotalReclaimedBytes);
        EcoCountText = snapshot.Stats.EcoManagedCount.ToString();
        SuspendCountText = snapshot.Stats.SuspendedCount.ToString();
        NoteProtectionSuspended(snapshot.IsProtectionSuspended);

        UpdateProcesses(snapshot.ManagedProcesses);
        if (_lastActivity == null && !string.IsNullOrWhiteSpace(snapshot.Status))
        {
            SetStatusLine(snapshot.Status);
        }
    }

    /// <summary>プレビュー用: 統計とプロセスだけを反映する（履歴は間引かない）。</summary>
    public void ApplySnapshotStats(EngineSnapshot snapshot)
    {
        TodayReclaimedText = Format.Freed(snapshot.Stats.ReclaimedTodayBytes);
        SessionReclaimedText = Format.Freed(snapshot.Stats.TotalReclaimedBytes);
        EcoCountText = snapshot.Stats.EcoManagedCount.ToString();
        SuspendCountText = snapshot.Stats.SuspendedCount.ToString();
        UpdateProcesses(snapshot.ManagedProcesses);
        NoteProtectionSuspended(snapshot.IsProtectionSuspended);
    }

    /// <summary>メモリ値だけを反映する（プレビューの高解像度サンプリングにも使う）。</summary>
    public void ApplyMemory(MemorySnapshot memory)
    {
        if (memory == null || memory.TotalPhysBytes == 0)
        {
            HasData = false;
            LevelKey = "nodata";
            StatusWord = "計測中…";
            StatusCaption = "メモリプレッシャー";
            AvailableText = "—";
            TotalText = "—";
            TotalCaptionText = "搭載 —";
            UsedPercentText = "—";
            UsedLegend = "使用中 —";
            CacheLegend = "キャッシュ —";
            FreeLegend = "未使用 —";
            CommitPercentText = "—";
            CommitText = "—";
            Score = 0;
            UsedRatio = 0;
            CacheRatio = 0;
            CommitRatio = 0;
            return;
        }

        HasData = true;
        double total = memory.TotalPhysBytes;
        double available = memory.AvailablePhysBytes;
        double used = memory.UsedPhysBytes;
        double cache = Math.Min(memory.SystemCacheBytes, available);
        double free = Math.Max(0, available - cache);

        // 0 除算・範囲外を防ぐ
        UsedRatio = total > 0 ? Math.Clamp(used / total, 0, 1) : 0;
        CacheRatio = total > 0 ? Math.Clamp(cache / total, 0, 1) : 0;
        Score = Math.Clamp(memory.PressureScore * 100.0, 0, 100);
        Level = memory.Pressure;
        LevelKey = memory.Pressure switch
        {
            MemoryPressureLevel.Warning => "warning",
            MemoryPressureLevel.Critical => "critical",
            _ => "normal",
        };
        StatusWord = memory.Pressure switch
        {
            MemoryPressureLevel.Warning => "注意",
            MemoryPressureLevel.Critical => "危険",
            _ => "余裕あり",
        };
        StatusCaption = "メモリプレッシャー";
        AvailableText = Format.Gb(memory.AvailablePhysBytes);
        TotalText = Format.Gb(memory.TotalPhysBytes);
        TotalCaptionText = $"搭載 {Format.Gb(memory.TotalPhysBytes)}";
        AvailableCaption = "利用可能メモリ";
        UsedPercentText = $"使用率 {Format.Percent(UsedRatio)}";
        UsedLegend = $"使用中 {Format.Bytes((ulong)used)}";
        CacheLegend = $"キャッシュ {Format.Bytes((ulong)cache)}";
        FreeLegend = $"未使用 {Format.Bytes((ulong)free)}";

        // コミット情報が明らかに不正な値（Core 側の計測不備）のときは数字を作らず「—」にする
        bool commitValid = memory.CommitLimitBytes >= 64UL * 1024 * 1024
                           && memory.CommitBytes <= memory.CommitLimitBytes * 4;
        if (commitValid)
        {
            CommitRatio = Math.Clamp(memory.CommitRatio, 0, 1);
            CommitPercentText = Format.Percent(CommitRatio);
            CommitText = $"{Format.Bytes(memory.CommitBytes)} / {Format.Bytes(memory.CommitLimitBytes)}";
        }
        else
        {
            CommitRatio = 0;
            CommitPercentText = "—";
            CommitText = "コミット情報を取得できません";
        }

        PushHistory(available / (1024d * 1024d * 1024d));
    }

    public void PushHistory(double availableGb)
    {
        if (availableGb <= 0)
        {
            return;
        }

        if (_historyCount < HistoryLength)
        {
            _history[_historyCount++] = availableGb;
        }
        else
        {
            Array.Copy(_history, 1, _history, 0, HistoryLength - 1);
            _history[HistoryLength - 1] = availableGb;
        }

        History = _history.Take(_historyCount).ToArray();
    }

    public void ClearHistory()
    {
        _historyCount = 0;
        History = null;
    }

    private void UpdateProcesses(IReadOnlyList<ProcessSnapshot> processes)
    {
        if (processes == null)
        {
            return;
        }

        var rows = processes
            .Where(p => p.State != EcoState.None)
            .OrderByDescending(p => p.ReclaimedBytes)
            .ThenByDescending(p => p.WorkingSetBytes)
            .Take(12)
            .Select(p => new ProcessRow
            {
                Name = CleanName(p.Name),
                Pid = p.Pid,
                StateKey = p.State switch
                {
                    EcoState.Trimmed => "trim",
                    EcoState.Suspended => "suspend",
                    EcoState.Eco => "eco",
                    _ => "none",
                },
                Badge = p.State switch
                {
                    EcoState.Trimmed => "トリム",
                    EcoState.Suspended => "停止",
                    EcoState.Eco => "エコ",
                    _ => "—",
                },
                ReclaimedText = p.ReclaimedBytes > 0 ? Format.Freed(p.ReclaimedBytes) : "",
                Detail = $"PID {p.Pid}" + (p.WorkingSetBytes > 0 ? $" ・ {Format.Freed(p.WorkingSetBytes)}" : ""),
            })
            .ToList();

        bool changed = rows.Count != Processes.Count;
        if (!changed)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (Processes[i].Name != rows[i].Name || Processes[i].Badge != rows[i].Badge
                    || Processes[i].ReclaimedText != rows[i].ReclaimedText)
                {
                    changed = true;
                    break;
                }
            }
        }

        if (!changed)
        {
            return;
        }

        Processes.Clear();
        foreach (ProcessRow row in rows)
        {
            Processes.Add(row);
        }
        OnPropertyChanged(nameof(HasProcesses));
        OnPropertyChanged(nameof(ManagedCountText));
    }

    private static string CleanName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "不明なアプリ";
        }
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    public void SetBusy(bool busy) => IsBusy = busy;

    /// <summary>エンジンの Activity イベント（日本語 1 行）を表示する。</summary>
    public void SetActivity(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }
        _lastActivity = message;
        SetStatusLine(message);
    }

    /// <summary>操作結果などの一時メッセージ。数秒後に直前のアクティビティへ戻る。</summary>
    public void ShowNotice(string? message, TimeSpan? duration = null)
    {
        _notice = message;
        SetStatusLine(message ?? _lastActivity ?? "監視しています");
        if (message == null)
        {
            return;
        }

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = duration ?? TimeSpan.FromSeconds(6),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_notice == message)
            {
                _notice = null;
                SetStatusLine(_lastActivity ?? "監視しています");
            }
        };
        timer.Start();
    }

    private void SetStatusLine(string text)
    {
        string prefix = _isProtectionSuspended ? "全画面表示中: 操作を保留 ・ " : "";
        StatusLine = prefix + text;
    }

    public void NoteProtectionSuspended(bool suspended)
    {
        IsProtectionSuspended = suspended;
        SetStatusLine(_notice ?? _lastActivity ?? "監視しています");
    }

    public void SetPresetFromUser(AutoPreset preset)
    {
        if (_preset == preset)
        {
            return;
        }
        Preset = preset;
        PresetChanged?.Invoke(preset);
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName ?? ""));
}
