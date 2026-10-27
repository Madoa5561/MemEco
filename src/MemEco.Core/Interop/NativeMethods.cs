using System.Runtime.InteropServices;
using System.Text;

namespace MemEco.Core.Interop;

/// <summary>
/// Win32 / NT API の宣言（Core 内部専用）。
/// このファイルはネイティブ宣言・構造体・定数だけを置く。ロジックは各 Pro/Controller 側。
/// ハンドルは呼び出し側が必ず CloseHandle すること。
/// </summary>
internal static class NativeMethods
{
    // ---------------------------------------------------------------- プロセス アクセス権

    internal const uint PROCESS_SET_QUOTA = 0x0100;
    internal const uint PROCESS_SET_INFORMATION = 0x0200;
    internal const uint PROCESS_QUERY_INFORMATION = 0x0400;
    internal const uint PROCESS_SUSPEND_RESUME = 0x0800;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>EcoQoS / 優先度 / Trim / Suspend を 1 ハンドルでまかなう基本セット (0x0F00)。</summary>
    internal const uint PROCESS_FULL_CONTROL_SET =
        PROCESS_QUERY_INFORMATION | PROCESS_SET_INFORMATION | PROCESS_SET_QUOTA | PROCESS_SUSPEND_RESUME;

    /// <summary>Trim (EmptyWorkingSet) に必要なアクセス権。</summary>
    internal const uint PROCESS_TRIM_ACCESS = PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA;

    // ---------------------------------------------------------------- 優先度クラス

    internal const uint IDLE_PRIORITY_CLASS = 0x00000040;
    internal const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
    internal const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    internal const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
    internal const uint HIGH_PRIORITY_CLASS = 0x00000080;
    internal const uint REALTIME_PRIORITY_CLASS = 0x00000100;

    // ---------------------------------------------------------------- EcoQoS (ProcessPowerThrottling)

    internal const int ProcessPowerThrottling = 4;
    internal const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    // ---------------------------------------------------------------- システム メモリ リスト

    internal const int SystemMemoryListInformation = 80;
    internal const int MemoryFlushModifiedList = 3;
    internal const int MemoryPurgeStandbyList = 4;

    // ---------------------------------------------------------------- ウィンドウ属性

    internal const int DWMWA_CLOAKED = 14;

    // ---------------------------------------------------------------- Shell

    internal const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    internal const int QUNS_PRESENTATION_MODE = 4;

    // ---------------------------------------------------------------- トークン / 特権

    internal const uint TOKEN_QUERY = 0x0008;
    internal const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    internal const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    internal const int ERROR_NOT_ALL_ASSIGNED = 1300;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_INVALID_PARAMETER = 87;

    // ================================================================ 構造体

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;

        public readonly ulong ToUInt64() => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // ================================================================ kernel32

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(IntPtr hProcess,
        out FILETIME lpCreationTime, out FILETIME lpExitTime,
        out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetPriorityClass(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessInformation(IntPtr hProcess, int processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation, uint processInformationSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags,
        StringBuilder lpExeName, ref uint lpdwSize);

    // ================================================================ psapi

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetPerformanceInfo(ref PERFORMANCE_INFORMATION pPerformanceInformation, uint cb);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessMemoryInfo(IntPtr hProcess,
        ref PROCESS_MEMORY_COUNTERS_EX counters, uint cb);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyWorkingSet(IntPtr hProcess);

    // ================================================================ ntdll

    [DllImport("ntdll.dll")]
    internal static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    internal static extern int NtResumeProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        out PROCESS_BASIC_INFORMATION processInformation, uint processInformationLength, out uint returnLength);

    [DllImport("ntdll.dll")]
    internal static extern int NtSetSystemInformation(int systemInformationClass,
        ref int systemInformation, int systemInformationLength);

    // ================================================================ advapi32（特権）

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AdjustTokenPrivileges(IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, uint bufferLength,
        IntPtr previousState, IntPtr returnLength);

    // ================================================================ user32（ウィンドウ）

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    // ================================================================ dwmapi

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute,
        out int pvAttribute, int cbAttribute);

    // ================================================================ shell32

    [DllImport("shell32.dll")]
    internal static extern int SHQueryUserNotificationState(out int pquns);

    // ================================================================ ole32（CoreAudio 用）

    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    internal static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
        ref Guid riid, out IntPtr ppv);
}
