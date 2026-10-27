using System.Security.Principal;
using MemEco.Core.Models;

namespace MemEco.Core;

/// <summary>
/// 自動メモリ管理エンジン（DESIGN §5）。
/// 2 秒 tick・2 tick に 1 回スキャン・PID ごとの状態機械・統計・手動回収。
/// UI スレッドを一切知らず、例外でループを止めない。
/// </summary>
public sealed class EcoEngine : IEcoEngine
{
    private const int MaxManagedRows = 60;
    private const double CpuBusyThresholdPercent = 1.5;
    private const ulong MinTrimWorkingSetBytes = 50UL * 1024 * 1024;

    private static readonly TimeSpan TrimInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MinActivityInterval = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly object _activityGate = new();
    private readonly Action<string> _log;

    private readonly MemoryProbe _memoryProbe = new();
    private readonly ProcessProbe _processProbe = new();
    private readonly ProcessController _controller = new();
    private readonly bool _isAdmin = IsRunningElevated();

    private readonly Dictionary<int, TrackedProcess> _tracked = new();

    private AppConfig _config;
    private ProcessSafetyPolicy _policy;

    private IReadOnlyList<ProcessSnapshot> _lastScan = Array.Empty<ProcessSnapshot>();
    private Dictionary<int, ProcessSnapshot> _lastScanById = new();
    private DateTime? _lastScanUtc;
    private MemorySnapshot _memory = MemorySnapshot.Empty;
    private bool _protectionSuspended;

    private ulong _totalReclaimed;
    private ulong _reclaimedToday;
    private long _reclaimOperations;
    private long _purgeOperations;
    private DateTime _day = DateTime.Today;
    private DateTime? _lastReclaimUtc;
    private DateTime? _lastPurgeUtc;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;
    private volatile bool _paused;
    private volatile EngineSnapshot _latest = new();
    private DateTime _lastActivityUtc = DateTime.MinValue;

    public EcoEngine(AppConfig config, Action<string>? activityLog = null)
    {
        _config = config ?? new AppConfig();
        _policy = new ProcessSafetyPolicy(_config);
        _log = activityLog ?? (_ => { });
    }

    public event EventHandler<EngineSnapshot>? SnapshotReady;
    public event EventHandler<string>? Activity;

    public EngineSnapshot Latest => _latest;

    public AppConfig Config => _config;

    public bool IsAdmin => _isAdmin;

    public bool IsPaused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;

            if (value)
            {
                Say("自動管理を一時停止しました（管理中のプロセスを元に戻します）");
                _ = Task.Run(() =>
                {
                    try { ReleaseAllManaged("一時停止"); }
                    catch (Exception ex) { Log($"一時停止時の復帰でエラー: {ex.Message}"); }
                });
            }
            else
            {
                Say("自動管理を再開しました");
            }
        }
    }

    // ---------------------------------------------------------------- ライフサイクル

    public Task StartAsync()
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_loop is { IsCompleted: false }) return Task.CompletedTask;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => LoopAsync(token), token);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;

        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        if (cts is not null)
        {
            try { cts.Cancel(); }
            catch { /* 無視 */ }
        }

        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* 正常停止 */ }
            catch (Exception ex) { Log($"ループ停止時にエラー: {ex.Message}"); }
        }

        cts?.Dispose();

        ReleaseAllManaged("停止");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { await StopAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log($"破棄時にエラー: {ex.Message}"); }

        try { ReleaseAllManaged("終了"); }
        catch (Exception ex) { Log($"破棄時の復帰でエラー: {ex.Message}"); }
    }

    public void ApplyConfig(AppConfig config)
    {
        if (config is null) return;

        lock (_gate)
        {
            _config = config;
            _policy = new ProcessSafetyPolicy(config);
        }

        if (!config.AutoManage || !config.EnableEcoMode)
        {
            _ = Task.Run(() =>
            {
                try { ReleaseAllManaged("設定変更"); }
                catch (Exception ex) { Log($"設定変更時の復帰でエラー: {ex.Message}"); }
            });
        }
    }

    // ---------------------------------------------------------------- 手動回収

    public Task<ReclaimResult> ReclaimNowAsync(ReclaimMode mode = ReclaimMode.Auto) =>
        Task.Run(() => ReclaimCore(mode));

    public Task<ReclaimResult> PurgeStandbyAsync() => Task.Run(PurgeCore);

    private ReclaimResult ReclaimCore(ReclaimMode mode)
    {
        try
        {
            var config = _config;
            var policy = new ProcessSafetyPolicy(config);
            var scan = _processProbe.Scan();

            ulong totalFreed = 0;
            int trimmed = 0;

            foreach (var process in scan.OrderByDescending(p => p.WorkingSetBytes))
            {
                if (process.WorkingSetBytes < MinTrimWorkingSetBytes) continue;
                if (policy.ComputeReason(process) is not null) continue;

                ulong freed = _controller.Trim(process.Pid, out _);
                if (freed == 0) continue;

                totalFreed += freed;
                trimmed++;

                lock (_gate)
                {
                    if (_tracked.TryGetValue(process.Pid, out var tracked))
                    {
                        tracked.ReclaimedBytes += freed;
                    }
                }
            }

            bool wantPurge = mode == ReclaimMode.Full
                             || (mode == ReclaimMode.Auto && config.EnableStandbyPurge);
            bool purged = false;
            ulong standbyFreed = 0;
            bool purgeUnavailable = wantPurge && !_controller.CanPurgeStandby;

            if (wantPurge && !purgeUnavailable)
            {
                purged = _controller.PurgeStandby(out standbyFreed);
            }

            var now = DateTime.UtcNow;
            lock (_gate)
            {
                RolloverDay(now);
                _totalReclaimed += totalFreed;
                _reclaimedToday += totalFreed;

                if (trimmed > 0 || totalFreed > 0)
                {
                    _reclaimOperations++;
                    _lastReclaimUtc = now;
                }

                if (purged)
                {
                    _purgeOperations++;
                    _lastPurgeUtc = now;
                }
            }

            var message = BuildReclaimMessage(trimmed, totalFreed, purged, standbyFreed, purgeUnavailable);
            Say(message);

            return new ReclaimResult(true, totalFreed, trimmed, purged, message);
        }
        catch (Exception ex)
        {
            var message = $"メモリの解放に失敗しました: {ex.Message}";
            Log(message);
            return ReclaimResult.Failed(message);
        }
    }

    private ReclaimResult PurgeCore()
    {
        try
        {
            if (!_controller.CanPurgeStandby)
            {
                const string notAdmin = "スタンバイの解放には管理者権限が必要です";
                Log(notAdmin);
                return ReclaimResult.Failed(notAdmin);
            }

            if (!_controller.PurgeStandby(out ulong standbyFreed))
            {
                const string failed = "スタンバイの解放に失敗しました（管理者権限が必要です）";
                Log(failed);
                return ReclaimResult.Failed(failed);
            }

            lock (_gate)
            {
                _purgeOperations++;
                _lastPurgeUtc = DateTime.UtcNow;
            }

            var message = standbyFreed > 0
                ? $"スタンバイ 約 {FormatBytes(standbyFreed)} を解放しました（空きメモリには現れないため回収量には計上しません）"
                : "スタンバイ メモリを解放しました（解放量は OS が管理するため実測できません）";

            Say("スタンバイ メモリを解放しました");
            return new ReclaimResult(true, 0, 0, true, message);
        }
        catch (Exception ex)
        {
            var message = $"スタンバイの解放に失敗しました: {ex.Message}";
            Log(message);
            return ReclaimResult.Failed(message);
        }
    }

    private static string BuildReclaimMessage(int trimmed, ulong freed, bool purged, ulong standbyFreed,
        bool purgeUnavailable)
    {
        string head = trimmed > 0
            ? $"{trimmed} 個のアプリから {FormatBytes(freed)} を解放しました"
            : "解放できるメモリはほとんどありませんでした";

        if (purged)
        {
            head = standbyFreed > 0
                ? $"{head}／スタンバイ 約 {FormatBytes(standbyFreed)} も解放（回収量には計上しません）"
                : $"{head}／スタンバイ メモリを解放しました";
        }

        if (purgeUnavailable)
        {
            head = $"{head}（スタンバイ解放は管理者権限が必要です）";
        }

        return head;
    }

    // ---------------------------------------------------------------- tick ループ

    private async Task LoopAsync(CancellationToken ct)
    {
        Log("メモリ エコ エンジンを開始しました");

        int tick = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Tick(tick);
                }
                catch (Exception ex)
                {
                    Log($"tick でエラー: {ex.Message}");
                }

                tick++;

                int interval = _config.SampleIntervalMs;
                if (interval < 500) interval = 500;
                if (interval > 15000) interval = 15000;

                try
                {
                    await Task.Delay(interval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"エンジン ループが停止しました: {ex.Message}");
        }

        Log("メモリ エコ エンジンを停止しました");
    }

    private void Tick(int tick)
    {
        var now = DateTime.UtcNow;
        List<string> activities = new();
        EngineSnapshot snapshot;

        MemorySnapshot memory;
        try
        {
            memory = _memoryProbe.Sample();
        }
        catch (Exception ex)
        {
            Log($"メモリ計測でエラー: {ex.Message}");
            memory = _memory;
        }

        lock (_gate)
        {
            _memory = memory;

            if (tick % 2 == 0)
            {
                try
                {
                    var scan = _processProbe.Scan();
                    var byId = new Dictionary<int, ProcessSnapshot>(scan.Count);
                    var parents = new HashSet<int>();
                    foreach (var item in scan)
                    {
                        byId[item.Pid] = item;
                        if (item.ParentPid > 0) parents.Add(item.ParentPid);
                    }

                    _lastScan = scan;
                    _lastScanById = byId;
                    _lastScanUtc = now;

                    bool fullscreen = false;
                    try
                    {
                        fullscreen = _config.PauseWhenFullscreen && _controller.IsFullScreenAppRunning();
                    }
                    catch (Exception ex)
                    {
                        Log($"全画面検出でエラー: {ex.Message}");
                    }

                    if (fullscreen && !_protectionSuspended)
                    {
                        activities.Add("全画面アプリを検出したため操作を休止しています");
                    }

                    _protectionSuspended = fullscreen;
                }
                catch (Exception ex)
                {
                    Log($"プロセス走査でエラー: {ex.Message}");
                }
            }

            if (DateTime.Today != _day) RolloverDay(now);

            UpdateStates(now, memory, activities);
            snapshot = BuildSnapshot(now, memory);
        }

        Publish(snapshot, activities);
    }

    // ---------------------------------------------------------------- 状態機械

    private void UpdateStates(DateTime now, MemorySnapshot memory, List<string> activities)
    {
        var config = _config;
        bool autoEnabled = config.AutoManage && !_paused;
        bool settingsOn = autoEnabled && config.EnableEcoMode;

        var parents = new HashSet<int>();
        foreach (var p in _lastScan)
        {
            if (p.ParentPid > 0) parents.Add(p.ParentPid);
        }

        var seen = new HashSet<int>();
        int ecoAdded = 0;
        int trimmedCount = 0;
        ulong trimmedBytes = 0;

        foreach (var process in _lastScan)
        {
            seen.Add(process.Pid);

            if (!_tracked.TryGetValue(process.Pid, out var tracked))
            {
                tracked = new TrackedProcess
                {
                    Pid = process.Pid,
                    Name = process.Name,
                    LastActiveUtc = now,
                };
                _tracked[process.Pid] = tracked;
            }

            if (!string.IsNullOrEmpty(process.Name)) tracked.Name = process.Name;

            bool active = process.IsForeground || process.HasVisibleWindow || process.HasAudio;
            if (active || process.CpuPercent >= CpuBusyThresholdPercent)
            {
                tracked.LastActiveUtc = now;
            }

            var protectedReason = _policy.ComputeReason(process);
            bool protectedProcess = protectedReason is not null;

            // 前面 / 可視 / 音声 / 保護対象 / 設定 OFF / 一時停止 → 必ず元に戻す
            bool mustRelease = protectedProcess || active || !settingsOn
                               || (tracked.State == EcoState.Suspended && !config.EnableSuspend);

            if (mustRelease)
            {
                if (tracked.State != EcoState.None)
                {
                    string reason = protectedReason
                                    ?? (active ? "前面/可視/音声" : "設定オフ");
                    ReleaseTracked(tracked, reason, activities);
                }

                continue;
            }

            // 一時停止の最大時間チェック
            if (tracked.State == EcoState.Suspended)
            {
                var limit = TimeSpan.FromMinutes(Math.Max(1, config.MaxSuspendMinutes));
                if (tracked.SuspendedAtUtc is { } suspendedAt && now - suspendedAt >= limit)
                {
                    _controller.Resume(process.Pid);
                    tracked.State = EcoState.Trimmed;
                    tracked.SuspendedAtUtc = null;
                    tracked.LastActiveUtc = now;
                    activities.Add($"{tracked.Name} の一時停止を解除しました（最大時間経過）");
                }

                continue;
            }

            // 全画面中は新規操作をしない
            if (_protectionSuspended) continue;

            // None → Eco
            if (tracked.State == EcoState.None)
            {
                var idle = now - tracked.LastActiveUtc;
                bool idleEnough = idle >= TimeSpan.FromMinutes(Math.Max(0, config.EcoIdleMinutes));
                bool bigEnough = process.WorkingSetBytes
                                 >= (ulong)Math.Max(0, config.MinProcessMemoryMb) * 1024UL * 1024UL;

                if (!active && process.CpuPercent < CpuBusyThresholdPercent && idleEnough && bigEnough)
                {
                    if (_controller.ApplyEco(process.Pid))
                    {
                        tracked.State = EcoState.Eco;
                        tracked.ManagedSinceUtc = now;
                        tracked.LastTrimUtc = null;
                        ecoAdded++;
                    }
                }

                continue;
            }

            // Eco / Trimmed → Trimmed（Warning 以上・同一 PID は 60 秒に 1 回まで）
            if ((tracked.State == EcoState.Eco || tracked.State == EcoState.Trimmed)
                && config.EnableWorkingSetTrim
                && memory.Pressure >= MemoryPressureLevel.Warning)
            {
                bool trimDue = tracked.LastTrimUtc is not { } lastTrim || now - lastTrim >= TrimInterval;
                if (trimDue)
                {
                    ulong freed = _controller.Trim(process.Pid, out _);
                    tracked.LastTrimUtc = now;
                    tracked.State = EcoState.Trimmed;

                    if (freed > 0)
                    {
                        tracked.ReclaimedBytes += freed;
                        _totalReclaimed += freed;
                        _reclaimedToday += freed;
                        _reclaimOperations++;
                        _lastReclaimUtc = now;
                        trimmedBytes += freed;
                        trimmedCount++;
                    }
                }
            }

            // Eco / Trimmed → Suspended（Critical・子プロセス持ちは対象外）
            if ((tracked.State == EcoState.Eco || tracked.State == EcoState.Trimmed)
                && config.EnableSuspend
                && memory.Pressure == MemoryPressureLevel.Critical)
            {
                if (parents.Contains(process.Pid))
                {
                    // 子連れは安全のため絶対に停止しない
                }
                else if (now - tracked.LastActiveUtc >= TimeSpan.FromMinutes(Math.Max(0, config.SuspendIdleMinutes)))
                {
                    if (_controller.Suspend(process.Pid))
                    {
                        tracked.State = EcoState.Suspended;
                        tracked.SuspendedAtUtc = now;
                        activities.Add($"{tracked.Name} を一時停止しました（メモリ不足のため）");
                    }
                }
            }
        }

        // 消えたプロセスの後始末（停止中だったものは念のため復帰を試みる）
        List<int>? gone = null;
        foreach (var pair in _tracked)
        {
            if (seen.Contains(pair.Key)) continue;

            if (pair.Value.State == EcoState.Suspended)
            {
                try { _controller.Resume(pair.Key); }
                catch { /* 無視 */ }
            }

            gone ??= new List<int>();
            gone.Add(pair.Key);
        }

        if (gone is not null)
        {
            foreach (int pid in gone) _tracked.Remove(pid);
        }

        if (trimmedCount > 0)
        {
            activities.Add($"{trimmedCount} 個のアプリから {FormatBytes(trimmedBytes)} を解放しました");
        }

        if (ecoAdded > 0)
        {
            activities.Add($"{ecoAdded} 個のアプリをエコ管理に移行しました");
        }

        // スタンバイリスト解放（管理者時のみ・Warning 以上・クールダウン経過時）
        if (_isAdmin
            && settingsOn
            && config.EnableStandbyPurge
            && !_protectionSuspended
            && memory.Pressure >= MemoryPressureLevel.Warning)
        {
            var cooldown = TimeSpan.FromSeconds(Math.Max(0, config.PurgeCooldownSeconds));
            bool cooldownPassed = _lastPurgeUtc is not { } lastPurge || now - lastPurge >= cooldown;

            if (cooldownPassed && _controller.PurgeStandby())
            {
                _purgeOperations++;
                _lastPurgeUtc = now;
                activities.Add("スタンバイ メモリを解放しました");
            }
        }
    }

    private void ReleaseTracked(TrackedProcess tracked, string reason, List<string> activities)
    {
        bool wasSuspended = tracked.State == EcoState.Suspended;
        bool resumed = false;

        try
        {
            if (wasSuspended) resumed = _controller.Resume(tracked.Pid);
        }
        catch (Exception ex)
        {
            Log($"{tracked.Name} の復帰でエラー: {ex.Message}");
        }

        try
        {
            _controller.ClearEco(tracked.Pid);
        }
        catch (Exception ex)
        {
            Log($"{tracked.Name} の Eco 解除でエラー: {ex.Message}");
        }

        tracked.State = EcoState.None;
        tracked.ManagedSinceUtc = null;
        tracked.SuspendedAtUtc = null;
        tracked.LastTrimUtc = null;
        tracked.LastActiveUtc = DateTime.UtcNow;

        if (resumed)
        {
            activities.Add($"{tracked.Name} の一時停止を解除しました（{reason}）");
        }
    }

    /// <summary>管理下の全プロセスを Eco 解除・停止解除して元に戻す。</summary>
    private void ReleaseAllManaged(string reason)
    {
        List<TrackedProcess> items;

        lock (_gate)
        {
            items = new List<TrackedProcess>(_tracked.Values);
            _tracked.Clear();
        }

        if (items.Count == 0) return;

        int resumed = 0;
        int released = 0;
        foreach (var tracked in items)
        {
            if (tracked.State == EcoState.None) continue;

            try
            {
                if (tracked.State == EcoState.Suspended && _controller.Resume(tracked.Pid)) resumed++;
                _controller.ClearEco(tracked.Pid);
                released++;
            }
            catch (Exception ex)
            {
                Log($"{tracked.Name} の復帰でエラー: {ex.Message}");
            }
        }

        if (released > 0)
        {
            Log($"{released} 個のプロセスを通常状態に戻しました（{reason}）" +
                (resumed > 0 ? $"／一時停止解除 {resumed} 件" : string.Empty));
        }
    }

    // ---------------------------------------------------------------- スナップショット

    private EngineSnapshot BuildSnapshot(DateTime now, MemorySnapshot memory)
    {
        var managed = new List<ProcessSnapshot>(_tracked.Count);
        int ecoCount = 0;
        int trimmed = 0;
        int suspended = 0;

        foreach (var tracked in _tracked.Values)
        {
            if (tracked.State == EcoState.None) continue;

            _lastScanById.TryGetValue(tracked.Pid, out var live);
            var baseSnapshot = live ?? new ProcessSnapshot { Pid = tracked.Pid, Name = tracked.Name };

            managed.Add(baseSnapshot with
            {
                Name = string.IsNullOrEmpty(tracked.Name) ? baseSnapshot.Name : tracked.Name,
                State = tracked.State,
                ReclaimedBytes = tracked.ReclaimedBytes,
                LastActiveUtc = tracked.LastActiveUtc,
                ManagedSinceUtc = tracked.ManagedSinceUtc,
                IsProtected = false,
                Note = null,
            });

            switch (tracked.State)
            {
                case EcoState.Eco: ecoCount++; break;
                case EcoState.Trimmed: trimmed++; break;
                case EcoState.Suspended: suspended++; break;
            }
        }

        managed.Sort((a, b) => b.ReclaimedBytes.CompareTo(a.ReclaimedBytes));
        if (managed.Count > MaxManagedRows)
        {
            managed.RemoveRange(MaxManagedRows, managed.Count - MaxManagedRows);
        }

        var stats = new EngineStats
        {
            TotalReclaimedBytes = _totalReclaimed,
            ReclaimedTodayBytes = _reclaimedToday,
            ReclaimOperations = (int)Math.Min(int.MaxValue, _reclaimOperations),
            PurgeOperations = (int)Math.Min(int.MaxValue, _purgeOperations),
            EcoManagedCount = ecoCount + trimmed + suspended,
            SuspendedCount = suspended,
            TrimmedCount = trimmed,
            Day = _day,
            LastReclaimUtc = _lastReclaimUtc,
            LastPurgeUtc = _lastPurgeUtc,
        };

        return new EngineSnapshot
        {
            Timestamp = now,
            Memory = memory,
            ManagedProcesses = managed,
            Stats = stats,
            LastScanUtc = _lastScanUtc,
            IsAdmin = _isAdmin,
            IsPaused = _paused,
            IsProtectionSuspended = _protectionSuspended,
            Status = BuildStatus(),
        };
    }

    private string BuildStatus()
    {
        if (_disposed) return "停止中";
        if (_paused) return "一時停止中・サンプリングのみ継続";
        if (_protectionSuspended) return "全画面アプリを検出・操作を休止中";
        if (!_config.AutoManage) return "全自動メモリ管理はオフ";
        return _isAdmin ? "管理者モード・全自動管理中" : "標準モード・一部機能制限";
    }

    private void Publish(EngineSnapshot snapshot, List<string> activities)
    {
        _latest = snapshot;

        foreach (var activity in activities)
        {
            Say(activity);
        }

        try
        {
            SnapshotReady?.Invoke(this, snapshot);
        }
        catch (Exception ex)
        {
            Log($"SnapshotReady ハンドラでエラー: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- ログ / 小物

    private void Log(string message)
    {
        try
        {
            _log(message);
        }
        catch
        {
            // ログ出力の失敗でエンジンを止めない
        }
    }

    /// <summary>UI 向けアクティビティ（日本語 1 行）。5 秒に最大 1 行へ間引く。</summary>
    private void Say(string message)
    {
        var now = DateTime.UtcNow;

        lock (_activityGate)
        {
            if (now - _lastActivityUtc < MinActivityInterval) return;
            _lastActivityUtc = now;
        }

        Log(message);

        try
        {
            Activity?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            Log($"Activity ハンドラでエラー: {ex.Message}");
        }
    }

    private void RolloverDay(DateTime now)
    {
        var today = now.ToLocalTime().Date;
        if (today == _day) return;

        _day = today;
        _reclaimedToday = 0;
    }

    private static string FormatBytes(ulong bytes)
    {
        if (bytes >= 1024UL * 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d / 1024d:F1} GB";
        }

        return $"{bytes / 1024d / 1024d:F0} MB";
    }

    public static bool IsRunningElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 内部状態

    private sealed class TrackedProcess
    {
        public int Pid;
        public string Name = "";
        public EcoState State;
        public DateTime LastActiveUtc;
        public DateTime? ManagedSinceUtc;
        public DateTime? LastTrimUtc;
        public DateTime? SuspendedAtUtc;
        public ulong ReclaimedBytes;
    }
}
