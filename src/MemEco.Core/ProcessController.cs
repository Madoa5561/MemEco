using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MemEco.Core.Interop;

namespace MemEco.Core;

/// <summary>
/// プロセス操作の窓口（DESIGN §4 / §5）。
/// EcoQoS・優先度・ワーキングセット トリム・一時停止・スタンバイ解放。
/// すべて bool を返し、例外は外に出さない（失敗は静かに false）。
/// </summary>
public sealed class ProcessController
{
    private const string ProfilePrivilege = "SeProfileSingleProcessPrivilege";
    private const string QuotaPrivilege = "SeIncreaseQuotaPrivilege";

    private readonly object _gate = new();

    /// <summary>pid → 元の優先度クラス（Eco 解除時に復元する）。</summary>
    private readonly ConcurrentDictionary<int, uint> _originalPriority = new();

    private int _purgeCapability = -1;
    private static readonly bool SuspendApiAvailable = ProbeSuspendApi();

    // ---------------------------------------------------------------- 可否

    /// <summary>スタンバイリスト解放は「管理者 かつ 特権が実際に割り当てられた」場合のみ可。</summary>
    public bool CanPurgeStandby => GetPurgeCapability();

    /// <summary>EcoQoS は Windows 10 2004 (19041) 以降。</summary>
    public bool CanUseEcoQos => Environment.OSVersion.Version.Build >= 19041;

    /// <summary>NtSuspendProcess / NtResumeProcess が ntdll から解決できるか。</summary>
    public bool CanSuspend => SuspendApiAvailable;

    private bool GetPurgeCapability()
    {
        lock (_gate)
        {
            if (_purgeCapability >= 0) return _purgeCapability == 1;

            bool capable = false;
            try
            {
                capable = EcoEngine.IsRunningElevated() && TryEnablePrivilege(ProfilePrivilege);
            }
            catch
            {
                capable = false;
            }

            _purgeCapability = capable ? 1 : 0;
            return capable;
        }
    }

    // ---------------------------------------------------------------- EcoQoS

    /// <summary>EcoQoS（効率モード）ON + 優先度 BELOW_NORMAL。元の優先度を覚えておく。</summary>
    public bool ApplyEco(int pid)
    {
        if (pid <= 4) return false;

        IntPtr handle = OpenForControl(pid);
        if (handle == IntPtr.Zero) return false;

        bool anySuccess = false;
        try
        {
            try
            {
                if (_originalPriority.Count > 4096) _originalPriority.Clear();
                uint current = NativeMethods.GetPriorityClass(handle);
                if (current != 0) _originalPriority.TryAdd(pid, current);
                if (NativeMethods.SetPriorityClass(handle, NativeMethods.BELOW_NORMAL_PRIORITY_CLASS))
                {
                    anySuccess = true;
                }
            }
            catch
            {
                // 優先度変更の失敗は無視（EcoQoS だけでも価値がある）
            }

            try
            {
                var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
                {
                    Version = 1,
                    ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                };

                if (NativeMethods.SetProcessInformation(handle, NativeMethods.ProcessPowerThrottling,
                        ref state, (uint)Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>()))
                {
                    anySuccess = true;
                }
            }
            catch
            {
                // EcoQoS 非対応環境では優先度だけ適用
            }

            if (!anySuccess)
            {
                _originalPriority.TryRemove(pid, out _);
            }

            return anySuccess;
        }
        catch
        {
            return false;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>EcoQoS OFF + 元の優先度へ戻す。</summary>
    public bool ClearEco(int pid)
    {
        if (pid <= 4)
        {
            _originalPriority.TryRemove(pid, out _);
            return false;
        }

        IntPtr handle = OpenForControl(pid);
        if (handle == IntPtr.Zero)
        {
            _originalPriority.TryRemove(pid, out _);
            return false;
        }

        bool anySuccess = false;
        try
        {
            try
            {
                var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
                {
                    Version = 1,
                    ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = 0,
                };

                if (NativeMethods.SetProcessInformation(handle, NativeMethods.ProcessPowerThrottling,
                        ref state, (uint)Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>()))
                {
                    anySuccess = true;
                }
            }
            catch
            {
                // 無視
            }

            if (_originalPriority.TryRemove(pid, out uint original))
            {
                try
                {
                    if (NativeMethods.SetPriorityClass(handle, original)) anySuccess = true;
                }
                catch
                {
                    // 無視
                }
            }

            return anySuccess;
        }
        catch
        {
            return false;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    // ---------------------------------------------------------------- ワーキングセット トリム

    /// <summary>
    /// EmptyWorkingSet を実行し、解放できた実測バイト数を返す。before には実行前の WS。
    /// </summary>
    public ulong Trim(int pid, out ulong before)
    {
        before = 0;
        if (pid <= 4) return 0;

        IntPtr handle = OpenForTrim(pid);
        if (handle == IntPtr.Zero) return 0;

        try
        {
            before = QueryWorkingSet(handle);
            if (!NativeMethods.EmptyWorkingSet(handle)) return 0;

            ulong after = QueryWorkingSet(handle);
            return before > after ? before - after : 0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    // ---------------------------------------------------------------- 一時停止 / 復帰

    /// <summary>NtSuspendProcess。成功したら true。</summary>
    public bool Suspend(int pid)
    {
        if (pid <= 4 || !SuspendApiAvailable) return false;

        IntPtr handle = OpenForSuspend(pid);
        if (handle == IntPtr.Zero) return false;

        try
        {
            return NativeMethods.NtSuspendProcess(handle) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>NtResumeProcess。停止していないプロセスに対しても安全（無害）。</summary>
    public bool Resume(int pid)
    {
        if (pid <= 4 || !SuspendApiAvailable) return false;

        IntPtr handle = OpenForSuspend(pid);
        if (handle == IntPtr.Zero) return false;

        try
        {
            return NativeMethods.NtResumeProcess(handle) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    // ---------------------------------------------------------------- スタンバイリスト解放

    /// <summary>スタンバイリスト解放（要管理者）。失敗しても例外を投げず false。</summary>
    public bool PurgeStandby() => PurgeStandby(out _);

    /// <summary>
    /// スタンバイリスト解放 + 解放量の実測（システムキャッシュの差分）。
    /// 管理者でない / 特権がない / NTSTATUS 失敗 のいずれでも静かに false。
    /// </summary>
    internal bool PurgeStandby(out ulong standbyFreedBytes)
    {
        standbyFreedBytes = 0;

        try
        {
            if (!CanPurgeStandby) return false;
            if (!TryEnablePrivilege(ProfilePrivilege)) return false;
            TryEnablePrivilege(QuotaPrivilege);

            ulong cacheBefore = QuerySystemCacheBytes();

            int command = NativeMethods.MemoryPurgeStandbyList;
            int status = NativeMethods.NtSetSystemInformation(NativeMethods.SystemMemoryListInformation,
                ref command, sizeof(int));
            if (status != 0) return false;

            // 実測: スタンバイ（システムキャッシュ）が実際に減るまで最大 400ms 観測する。
            ulong cacheAfter = cacheBefore;
            long deadline = Environment.TickCount64 + 400;
            do
            {
                Thread.Sleep(40);
                cacheAfter = QuerySystemCacheBytes();
                if (cacheAfter < cacheBefore) break;
            }
            while (Environment.TickCount64 < deadline);

            standbyFreedBytes = cacheBefore > cacheAfter ? cacheBefore - cacheAfter : 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 全画面検出

    /// <summary>D3D 全画面ゲーム（QUNS_RUNNING_D3D_FULL_SCREEN=3）またはプレゼン（4）を検出。</summary>
    public bool IsFullScreenAppRunning()
    {
        try
        {
            int hr = NativeMethods.SHQueryUserNotificationState(out int state);
            if (hr != 0) return false;

            return state is NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN or NativeMethods.QUNS_PRESENTATION_MODE;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 自己テスト

    /// <summary>自プロセスで EcoQoS 適用→復元・自己トリムを実行し、日本語 1 行で結果を返す。</summary>
    public async Task<string> SelfTestAsync(CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(RunSelfTest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return "自己テストはキャンセルされました";
        }
        catch (Exception ex)
        {
            return $"自己テストで例外が出ました: {ex.Message}";
        }
    }

    private string RunSelfTest()
    {
        int pid = Environment.ProcessId;

        try
        {
            uint priorityBefore = QueryPriorityClass(pid);
            bool applied = ApplyEco(pid);
            uint priorityManaged = QueryPriorityClass(pid);
            bool restored = ClearEco(pid);
            if (!restored)
            {
                // 念のため二度試す（EcoQoS を残したまま終了しない）
                restored = ClearEco(pid);
            }

            uint priorityAfter = QueryPriorityClass(pid);

            ulong workingSetBefore = QueryWorkingSetByPid(pid);
            ulong freed = Trim(pid, out ulong trimBefore);
            ulong workingSetAfter = trimBefore > freed ? trimBefore - freed : 0;

            string ecoText = applied
                ? $"EcoQoS 適用→復元 {(restored ? "OK" : "NG")}（優先度 {PriorityName(priorityBefore)}→{PriorityName(priorityManaged)}→{PriorityName(priorityAfter)}）"
                : $"EcoQoS 非適用（優先度 {PriorityName(priorityBefore)} のまま／この環境では設定不可）";

            string trimText = $"自己トリム {(freed / 1024d / 1024d):F0} MB 解放（WS {(workingSetBefore / 1024d / 1024d):F0} MB → {(workingSetAfter / 1024d / 1024d):F0} MB）";

            return $"OK: {ecoText} / {trimText}";
        }
        catch (Exception ex)
        {
            return $"自己テスト失敗: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- 内部ヘルパ

    private static IntPtr OpenForControl(int pid)
    {
        // Lead 実測: EventInformation 系は PROCESS_SET_INFORMATION が必須。
        // 基本 0x0F00、拒否されたら権限を落として再試行する。
        Span<uint> levels = stackalloc uint[]
        {
            NativeMethods.PROCESS_FULL_CONTROL_SET,
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_SET_INFORMATION,
            NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_INFORMATION,
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
        };

        return OpenFirstAvailable(pid, levels);
    }

    private static IntPtr OpenForTrim(int pid)
    {
        Span<uint> levels = stackalloc uint[]
        {
            NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_QUOTA,
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_SET_QUOTA,
        };

        return OpenFirstAvailable(pid, levels);
    }

    private static IntPtr OpenForSuspend(int pid)
    {
        Span<uint> levels = stackalloc uint[]
        {
            NativeMethods.PROCESS_SUSPEND_RESUME | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            NativeMethods.PROCESS_SUSPEND_RESUME,
        };

        return OpenFirstAvailable(pid, levels);
    }

    private static IntPtr OpenFirstAvailable(int pid, ReadOnlySpan<uint> accessLevels)
    {
        foreach (uint access in accessLevels)
        {
            IntPtr handle = NativeMethods.OpenProcess(access, false, pid);
            if (handle != IntPtr.Zero) return handle;

            int error = Marshal.GetLastWin32Error();
            if (error == NativeMethods.ERROR_ACCESS_DENIED) return IntPtr.Zero;
            if (error == NativeMethods.ERROR_INVALID_PARAMETER) return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private static ulong QueryWorkingSet(IntPtr handle)
    {
        try
        {
            var counters = new NativeMethods.PROCESS_MEMORY_COUNTERS_EX
            {
                cb = (uint)Marshal.SizeOf<NativeMethods.PROCESS_MEMORY_COUNTERS_EX>(),
            };

            return NativeMethods.GetProcessMemoryInfo(handle, ref counters, counters.cb)
                ? counters.WorkingSetSize
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static ulong QueryWorkingSetByPid(int pid)
    {
        IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return 0;

        try
        {
            return QueryWorkingSet(handle);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static uint QueryPriorityClass(int pid)
    {
        IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return 0;

        try
        {
            return NativeMethods.GetPriorityClass(handle);
        }
        catch
        {
            return 0;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static ulong QuerySystemCacheBytes()
    {
        try
        {
            var perf = new NativeMethods.PERFORMANCE_INFORMATION
            {
                cb = (uint)Marshal.SizeOf<NativeMethods.PERFORMANCE_INFORMATION>(),
            };

            if (!NativeMethods.GetPerformanceInfo(ref perf, perf.cb)) return 0;
            ulong pageSize = perf.PageSize == 0 ? 4096UL : perf.PageSize;
            return perf.SystemCache * pageSize;
        }
        catch
        {
            return 0;
        }
    }

    private static string PriorityName(uint priorityClass) => priorityClass switch
    {
        NativeMethods.IDLE_PRIORITY_CLASS => "Idle",
        NativeMethods.BELOW_NORMAL_PRIORITY_CLASS => "BelowNormal",
        NativeMethods.NORMAL_PRIORITY_CLASS => "Normal",
        NativeMethods.ABOVE_NORMAL_PRIORITY_CLASS => "AboveNormal",
        NativeMethods.HIGH_PRIORITY_CLASS => "High",
        NativeMethods.REALTIME_PRIORITY_CLASS => "Realtime",
        0 => "不明",
        _ => $"0x{priorityClass:X}",
    };

    private static bool TryEnablePrivilege(string privilegeName)
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(),
                    NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY, out token))
            {
                return false;
            }

            if (!NativeMethods.LookupPrivilegeValue(null, privilegeName, out var luid)) return false;

            var state = new NativeMethods.TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new NativeMethods.LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = NativeMethods.SE_PRIVILEGE_ENABLED,
                },
            };

            if (!NativeMethods.AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero))
            {
                return false;
            }

            // 特権が実際に割り当てられたか（1300 = ERROR_NOT_ALL_ASSIGNED は失敗扱い）。
            return Marshal.GetLastWin32Error() != NativeMethods.ERROR_NOT_ALL_ASSIGNED;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero) NativeMethods.CloseHandle(token);
        }
    }

    private static bool ProbeSuspendApi()
    {
        try
        {
            if (!NativeLibrary.TryLoad("ntdll.dll", out IntPtr ntdll)) return false;

            try
            {
                return NativeLibrary.TryGetExport(ntdll, "NtSuspendProcess", out _)
                       && NativeLibrary.TryGetExport(ntdll, "NtResumeProcess", out _);
            }
            finally
            {
                NativeLibrary.Free(ntdll);
            }
        }
        catch
        {
            return false;
        }
    }
}
