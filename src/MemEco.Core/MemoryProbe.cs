using System.Diagnostics;
using System.Runtime.InteropServices;
using MemEco.Core.Interop;
using MemEco.Core.Models;

namespace MemEco.Core;

/// <summary>
/// 物理メモリ / コミット / キャッシュ / メモリ圧縮ストアの計測と、macOS の
/// 「メモリプレッシャー」に相当する 0..1 スコアの算出（DESIGN §5.1）。
/// </summary>
public sealed class MemoryProbe
{
    private const string CompressionProcessName = "Memory Compression";

    /// <summary>圧縮ストア量の取得はプロセス列挙を伴うため 5 秒キャッシュする。</summary>
    private static readonly TimeSpan CompressionCacheDuration = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();

    private ulong _compressionBytes;
    private DateTime _compressionSampledUtc = DateTime.MinValue;
    private MemorySnapshot? _last;

    /// <summary>1 サンプル取得。失敗時は直近値（初回のみ Empty）を返し、例外を出さない。</summary>
    public MemorySnapshot Sample()
    {
        try
        {
            var status = new NativeMethods.MEMORYSTATUSEX
            {
                dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>(),
            };

            if (!NativeMethods.GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
            {
                return LastSnapshot();
            }

            var perf = new NativeMethods.PERFORMANCE_INFORMATION
            {
                cb = (uint)Marshal.SizeOf<NativeMethods.PERFORMANCE_INFORMATION>(),
            };
            bool hasPerf = NativeMethods.GetPerformanceInfo(ref perf, perf.cb);

            ulong totalPhys = status.ullTotalPhys;
            ulong availablePhys = status.ullAvailPhys;
            ulong commitBytes;
            ulong commitLimitBytes;
            ulong systemCacheBytes;
            ulong pageFileUsageBytes;

            if (hasPerf && perf.CommitLimit != 0)
            {
                // PERFORMANCE_INFORMATION の数値は「ページ数」。PageSize バイト/ページを掛ける。
                ulong pageSize = perf.PageSize == 0 ? 4096UL : perf.PageSize;
                ulong physicalTotalBytes = perf.PhysicalTotal * pageSize;

                commitBytes = perf.CommitTotal * pageSize;
                commitLimitBytes = perf.CommitLimit * pageSize;
                systemCacheBytes = perf.SystemCache * pageSize;

                // ページファイル使用量は Win32 に直接の口がないため、
                // 「コミットチャージのうち物理メモリを超えた分」を下限値として使う。
                pageFileUsageBytes = commitBytes > physicalTotalBytes ? commitBytes - physicalTotalBytes : 0;
            }
            else
            {
                // GetPerformanceInfo が使えない環境のフォールバック。
                commitLimitBytes = status.ullTotalPageFile;
                commitBytes = status.ullTotalPageFile > status.ullAvailPageFile
                    ? status.ullTotalPageFile - status.ullAvailPageFile
                    : 0;
                systemCacheBytes = 0;
                pageFileUsageBytes = commitBytes > totalPhys ? commitBytes - totalPhys : 0;
            }

            ulong compressionBytes = GetCompressionBytes();
            double score = ComputePressureScore(totalPhys, availablePhys, commitBytes, commitLimitBytes, compressionBytes);

            var snapshot = new MemorySnapshot
            {
                Timestamp = DateTime.UtcNow,
                TotalPhysBytes = totalPhys,
                AvailablePhysBytes = availablePhys,
                CommitBytes = commitBytes,
                CommitLimitBytes = commitLimitBytes,
                SystemCacheBytes = systemCacheBytes,
                CompressionBytes = compressionBytes,
                PageFileUsageBytes = pageFileUsageBytes,
                PressureScore = score,
                Pressure = ToLevel(score),
            };

            lock (_gate)
            {
                _last = snapshot;
            }

            return snapshot;
        }
        catch
        {
            return LastSnapshot();
        }
    }

    private MemorySnapshot LastSnapshot()
    {
        lock (_gate)
        {
            return _last ?? MemorySnapshot.Empty;
        }
    }

    /// <summary>DESIGN §5.1 の式をそのまま実装した圧迫度スコア。</summary>
    internal static double ComputePressureScore(ulong totalPhys, ulong availablePhys,
        ulong commitBytes, ulong commitLimitBytes, ulong compressionBytes)
    {
        if (totalPhys == 0) return 0;

        double availRatio = (double)availablePhys / totalPhys;
        double commitRatio = commitLimitBytes == 0 ? 0 : (double)commitBytes / commitLimitBytes;

        double availScore = Clamp01((0.28 - availRatio) / 0.22);
        double commitScore = Clamp01((commitRatio - 0.70) / 0.25);
        double compScore = Clamp01(((double)compressionBytes / totalPhys - 0.10) / 0.20);

        return Clamp01(Math.Max(availScore, commitScore) + 0.35 * compScore);
    }

    internal static MemoryPressureLevel ToLevel(double score) => score switch
    {
        < 0.45 => MemoryPressureLevel.Normal,
        < 0.75 => MemoryPressureLevel.Warning,
        _ => MemoryPressureLevel.Critical,
    };

    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

    /// <summary>"Memory Compression" のワーキングセットを圧縮ストア量の近似として使う（5 秒キャッシュ）。</summary>
    private ulong GetCompressionBytes()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (_compressionSampledUtc != DateTime.MinValue
                && now - _compressionSampledUtc < CompressionCacheDuration)
            {
                return _compressionBytes;
            }

            _compressionSampledUtc = now;
            ulong measured = QueryCompressionWorkingSetBytes();
            if (measured > 0 || _compressionBytes == 0)
            {
                _compressionBytes = measured;
            }

            return _compressionBytes;
        }
    }

    private static ulong QueryCompressionWorkingSetBytes()
    {
        // "Memory Compression" は保護プロセスのため OpenProcess が通らない環境がある。
        // .NET の Process.WorkingSet64（NtQuerySystemInformation 経由のスナップショット）なら読める。
        try
        {
            using var process = FindCompressionProcess();
            if (process is null) return 0;

            long workingSet = process.WorkingSet64;
            return workingSet > 0 ? (ulong)workingSet : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static Process? FindCompressionProcess()
    {
        try
        {
            var processes = Process.GetProcessesByName(CompressionProcessName);
            if (processes.Length == 0) return null;

            var chosen = processes[0];
            for (int i = 1; i < processes.Length; i++)
            {
                processes[i].Dispose();
            }

            return chosen;
        }
        catch
        {
            return null;
        }
    }
}
