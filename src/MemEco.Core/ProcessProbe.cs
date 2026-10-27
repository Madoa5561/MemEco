using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MemEco.Core.Interop;
using MemEco.Core.Models;

namespace MemEco.Core;

/// <summary>
/// 実行中プロセスの走査（DESIGN §4 / §5.2）。
/// 前面・可視ウィンドウ・CPU 差分・親 PID・音声・保護判定を含む。
/// WMI は使わず、OpenProcess + ntdll/psapi のみ。250 プロセスで 40ms 未満を目標にする。
/// </summary>
public sealed class ProcessProbe
{
    private readonly object _gate = new();

    private readonly ProcessSafetyPolicy _defaultPolicy = new(new AppConfig());

    private Dictionary<int, CpuSample> _previousCpu = new();
    private long _previousTimestamp;

    private readonly record struct CpuSample(ulong KernelTime, ulong UserTime);

    private sealed class WindowInfo
    {
        public string? Title { get; set; }
    }

    /// <summary>全プロセスを 1 回走査する。個別プロセスの失敗はスキップし、例外を出さない。</summary>
    public IReadOnlyList<ProcessSnapshot> Scan()
    {
        try
        {
            // エンジン tick と手動回収が同時に走っても内部状態が壊れないように直列化する。
            lock (_gate)
            {
                return ScanCore();
            }
        }
        catch
        {
            return Array.Empty<ProcessSnapshot>();
        }
    }

    private IReadOnlyList<ProcessSnapshot> ScanCore()
    {
        var nowTimestamp = Stopwatch.GetTimestamp();
        double elapsedSeconds = _previousTimestamp == 0
            ? 0
            : (nowTimestamp - _previousTimestamp) / (double)Stopwatch.Frequency;
        int cpuCount = Math.Max(1, Environment.ProcessorCount);

        var windows = EnumerateWindows(out uint foregroundPid);
        var audioPids = AudioSessionProbe.GetAudioProcessIds();

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return Array.Empty<ProcessSnapshot>();
        }

        var results = new List<ProcessSnapshot>(processes.Length);
        var nextCpu = new Dictionary<int, CpuSample>(processes.Length);

        foreach (var process in processes)
        {
            try
            {
                int pid;
                string name;
                try
                {
                    pid = process.Id;
                    name = process.ProcessName;
                }
                catch
                {
                    continue;
                }

                if (pid <= 0)
                {
                    continue;
                }

                ulong workingSet = 0;
                ulong privateBytes = 0;
                ulong kernelTime = 0;
                ulong userTime = 0;
                string? executablePath = null;
                int parentPid = 0;
                bool isElevated = false;
                bool hasTimes = false;

                IntPtr handle = OpenForQuery(pid);
                if (handle == IntPtr.Zero)
                {
                    // 開けないプロセスは「昇格済み（＝触らない）」扱い。
                    isElevated = true;
                }
                else
                {
                    try
                    {
                        if (NativeMethods.GetProcessTimes(handle, out var created, out _,
                                out var kernel, out var user))
                        {
                            hasTimes = true;
                            kernelTime = kernel.ToUInt64();
                            userTime = user.ToUInt64();
                            ProcessSafetyPolicy.RememberStartTime(pid, created.ToUInt64());
                        }

                        var counters = new NativeMethods.PROCESS_MEMORY_COUNTERS_EX
                        {
                            cb = (uint)Marshal.SizeOf<NativeMethods.PROCESS_MEMORY_COUNTERS_EX>(),
                        };
                        if (NativeMethods.GetProcessMemoryInfo(handle, ref counters, counters.cb))
                        {
                            workingSet = counters.WorkingSetSize;
                            privateBytes = counters.PrivateUsage;
                        }

                        executablePath = QueryExecutablePath(handle);
                        parentPid = QueryParentPid(handle);
                    }
                    finally
                    {
                        NativeMethods.CloseHandle(handle);
                    }
                }

                double cpuPercent = 0;
                if (hasTimes)
                {
                    if (elapsedSeconds > 0.01 && _previousCpu.TryGetValue(pid, out var previous))
                    {
                        ulong delta = 0;
                        if (kernelTime >= previous.KernelTime) delta += kernelTime - previous.KernelTime;
                        if (userTime >= previous.UserTime) delta += userTime - previous.UserTime;

                        // 100ns 単位 / 経過実時間 / 論理 CPU 数 → 全体に対する %。
                        cpuPercent = delta / 10_000_000d / elapsedSeconds / cpuCount * 100d;
                        if (cpuPercent < 0) cpuPercent = 0;
                        double max = cpuCount * 100d;
                        if (cpuPercent > max) cpuPercent = max;
                    }

                    nextCpu[pid] = new CpuSample(kernelTime, userTime);
                }

                bool hasVisibleWindow = windows.TryGetValue(pid, out var window);
                bool isSystem = ProcessSafetyPolicy.IsKnownSystemName(name)
                                || ProcessSafetyPolicy.IsSystemFolderPath(executablePath);

                var snapshot = new ProcessSnapshot
                {
                    Pid = pid,
                    ParentPid = parentPid,
                    Name = name,
                    ExecutablePath = executablePath,
                    Title = window?.Title,
                    WorkingSetBytes = workingSet,
                    PrivateBytes = privateBytes,
                    CpuPercent = cpuPercent,
                    HasVisibleWindow = hasVisibleWindow,
                    IsForeground = foregroundPid != 0 && pid == (int)foregroundPid,
                    IsElevated = isElevated,
                    IsSystem = isSystem,
                    HasAudio = audioPids.Contains((uint)pid),
                };

                var reason = _defaultPolicy.ComputeReason(snapshot);
                if (reason is not null)
                {
                    snapshot = snapshot with { IsProtected = true, Note = reason };
                }

                results.Add(snapshot);
            }
            catch
            {
                // 走査中に終了したプロセス等はスキップ。
            }
            finally
            {
                process.Dispose();
            }
        }

        lock (_gate)
        {
            _previousCpu = nextCpu;
            _previousTimestamp = nowTimestamp;
        }

        return results;
    }

    // ---------------------------------------------------------------- ヘルパ

    private static IntPtr OpenForQuery(int pid)
    {
        IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle != IntPtr.Zero) return handle;

        if (Marshal.GetLastWin32Error() == NativeMethods.ERROR_ACCESS_DENIED) return IntPtr.Zero;

        return NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_INFORMATION, false, pid);
    }

    private static string? QueryExecutablePath(IntPtr handle)
    {
        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            if (NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size))
            {
                return size == 0 ? null : buffer.ToString(0, (int)size);
            }

            if (Marshal.GetLastWin32Error() != 122 /* ERROR_INSUFFICIENT_BUFFER */) return null;

            buffer = new StringBuilder(32768);
            size = (uint)buffer.Capacity;
            if (!NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size)) return null;
            return size == 0 ? null : buffer.ToString(0, (int)size);
        }
        catch
        {
            return null;
        }
    }

    private static int QueryParentPid(IntPtr handle)
    {
        try
        {
            int status = NativeMethods.NtQueryInformationProcess(handle, 0,
                out var info, (uint)Marshal.SizeOf<NativeMethods.PROCESS_BASIC_INFORMATION>(), out _);
            if (status != 0) return 0;

            long parent = info.InheritedFromUniqueProcessId.ToInt64();
            return parent is > 0 and <= int.MaxValue ? (int)parent : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>可視・非最小化・非クロークのトップレベル ウィンドウから PID → タイトルを作る。</summary>
    private static Dictionary<int, WindowInfo> EnumerateWindows(out uint foregroundPid)
    {
        var map = new Dictionary<int, WindowInfo>();
        foregroundPid = 0;

        try
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(foreground, out foregroundPid);
            }
        }
        catch
        {
            foregroundPid = 0;
        }

        try
        {
            NativeMethods.EnumWindows((hWnd, _) =>
            {
                try
                {
                    if (!NativeMethods.IsWindowVisible(hWnd)) return true;
                    if (NativeMethods.IsIconic(hWnd)) return true;
                    if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED,
                            out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                    {
                        return true;
                    }

                    NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == 0) return true;

                    string? title = null;
                    int length = NativeMethods.GetWindowTextLength(hWnd);
                    if (length > 0)
                    {
                        var buffer = new StringBuilder(Math.Min(length + 1, 512));
                        if (NativeMethods.GetWindowText(hWnd, buffer, buffer.Capacity) > 0)
                        {
                            title = buffer.ToString();
                        }
                    }

                    if (map.TryGetValue((int)pid, out var existing))
                    {
                        if (string.IsNullOrEmpty(existing.Title) && !string.IsNullOrEmpty(title))
                        {
                            existing.Title = title;
                        }
                    }
                    else
                    {
                        map[(int)pid] = new WindowInfo { Title = title };
                    }
                }
                catch
                {
                    // ウィンドウ単位の失敗は無視
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // EnumWindows 自体の失敗は無視
        }

        return map;
    }
}
