namespace MemEco.Core.Models;

/// <summary>このセッション / 本日の回収実績。</summary>
public sealed record EngineStats
{
    public ulong TotalReclaimedBytes { get; init; }
    public ulong ReclaimedTodayBytes { get; init; }
    public int ReclaimOperations { get; init; }
    public int PurgeOperations { get; init; }
    public int EcoManagedCount { get; init; }
    public int SuspendedCount { get; init; }
    public int TrimmedCount { get; init; }
    public DateTime Day { get; init; } = DateTime.Today;
    public DateTime? LastReclaimUtc { get; init; }
    public DateTime? LastPurgeUtc { get; init; }

    public static EngineStats Empty { get; } = new();
}

/// <summary>UI が 1 フレームで必要とする全データ。</summary>
public sealed record EngineSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public MemorySnapshot Memory { get; init; } = MemorySnapshot.Empty;
    public IReadOnlyList<ProcessSnapshot> ManagedProcesses { get; init; } = Array.Empty<ProcessSnapshot>();
    public EngineStats Stats { get; init; } = EngineStats.Empty;

    /// <summary>プロセス走査の最終実行時刻（負荷の目安）。</summary>
    public DateTime? LastScanUtc { get; init; }

    public bool IsAdmin { get; init; }
    public bool IsPaused { get; init; }

    /// <summary>全画面ゲーム等を検出して保護動作中か。</summary>
    public bool IsProtectionSuspended { get; init; }

    public string Status { get; init; } = "";
}

/// <summary>手動 / 自動のメモリ回収結果。</summary>
public sealed record ReclaimResult(
    bool Success,
    ulong FreedBytes,
    int TrimmedProcesses,
    bool StandbyPurged,
    string Message)
{
    public static ReclaimResult Failed(string message) => new(false, 0, 0, false, message);
}
