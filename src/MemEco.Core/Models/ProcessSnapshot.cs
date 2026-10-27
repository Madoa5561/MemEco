namespace MemEco.Core.Models;

/// <summary>エンジンが各プロセスに適用している状態。</summary>
public enum EcoState
{
    /// <summary>未管理（通常どおり）。</summary>
    None = 0,

    /// <summary>EcoQoS（Windows 11 の効率モード）+ 優先度低下を適用中。</summary>
    Eco = 1,

    /// <summary>Eco + ワーキングセットのトリムを実施済み。</summary>
    Trimmed = 2,

    /// <summary>一時停止中（App Nap Plus）。復帰は自動。</summary>
    Suspended = 3,
}

/// <summary>UI に渡す「メモリエコ対象」プロセスのスナップショット。</summary>
public sealed record ProcessSnapshot
{
    public int Pid { get; init; }

    /// <summary>親プロセス ID（取得できた場合）。子連れプロセスの安全判定に使う。</summary>
    public int ParentPid { get; init; }

    public string Name { get; init; } = "";
    public string? ExecutablePath { get; init; }
    public string? Title { get; init; }

    public ulong WorkingSetBytes { get; init; }
    public ulong PrivateBytes { get; init; }

    /// <summary>直近サンプル区間の CPU 使用率（全論理 CPU に対する %）。</summary>
    public double CpuPercent { get; init; }

    public bool HasVisibleWindow { get; init; }
    public bool IsForeground { get; init; }
    public bool IsElevated { get; init; }
    public bool IsSystem { get; init; }
    public bool IsProtected { get; init; }
    public bool HasAudio { get; init; }

    public EcoState State { get; init; }

    /// <summary>このプロセスから回収した累計バイト数（このセッション）。</summary>
    public ulong ReclaimedBytes { get; init; }

    public DateTime? LastActiveUtc { get; init; }
    public DateTime? ManagedSinceUtc { get; init; }

    /// <summary>管理対象外にした理由（デバッグ表示用）。</summary>
    public string? Note { get; init; }
}
