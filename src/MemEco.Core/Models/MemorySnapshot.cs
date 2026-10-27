namespace MemEco.Core.Models;

/// <summary>macOS のメモリプレッシャーに相当する 3 段階。</summary>
public enum MemoryPressureLevel
{
    Normal = 0,
    Warning = 1,
    Critical = 2,
}

/// <summary>物理メモリ・コミット・キャッシュの 1 サンプル。</summary>
public sealed record MemorySnapshot
{
    public DateTime Timestamp { get; init; }

    /// <summary>搭載物理メモリ。</summary>
    public ulong TotalPhysBytes { get; init; }

    /// <summary>空き + スタンバイ（再利用可能）を合わせた実質空き。</summary>
    public ulong AvailablePhysBytes { get; init; }

    /// <summary>コミットチャージ（ページファイル含む）。</summary>
    public ulong CommitBytes { get; init; }

    public ulong CommitLimitBytes { get; init; }

    /// <summary>スタンバイ/キャッシュとして保持されている量（概算）。</summary>
    public ulong SystemCacheBytes { get; init; }

    /// <summary>Windows のメモリ圧縮ストア（"Memory Compression" プロセスのワーキングセット）。</summary>
    public ulong CompressionBytes { get; init; }

    /// <summary>ページファイル使用量。</summary>
    public ulong PageFileUsageBytes { get; init; }

    /// <summary>0.0 = 余裕あり / 1.0 = 危険。</summary>
    public double PressureScore { get; init; }

    public MemoryPressureLevel Pressure { get; init; }

    public ulong UsedPhysBytes => TotalPhysBytes > AvailablePhysBytes
        ? TotalPhysBytes - AvailablePhysBytes
        : 0;

    public double UsedRatio => TotalPhysBytes == 0 ? 0 : (double)UsedPhysBytes / TotalPhysBytes;

    public double AvailableRatio => TotalPhysBytes == 0 ? 0 : (double)AvailablePhysBytes / TotalPhysBytes;

    public double CommitRatio => CommitLimitBytes == 0 ? 0 : (double)CommitBytes / CommitLimitBytes;

    public static MemorySnapshot Empty { get; } = new() { Timestamp = DateTime.UtcNow };
}
