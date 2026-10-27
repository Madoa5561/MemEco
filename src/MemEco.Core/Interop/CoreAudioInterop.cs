using System.Runtime.InteropServices;

namespace MemEco.Core.Interop;

/// <summary>
/// CoreAudio (IAudioSessionManager2) で「今、音を鳴らしているプロセス」を列挙する。
/// 失敗しても例外は出さず、空集合を返すだけ（保護判定を安全側に倒すため、音声判定は
/// 取れないときは「音声なし」= 触ってよい方向に倒れる点に注意）。
/// </summary>
internal static class CoreAudioInterop
{
    internal static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    internal static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    internal static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    internal const int eRender = 0;
    internal const int eConsole = 0;
    internal const int DEVICE_STATE_ACTIVE = 0x1;
    internal const uint CLSCTX_ALL = 0x17;
    internal const int AudioSessionStateActive = 1;
    internal const uint COINIT_MULTITHREADED = 0x0;
    internal const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
}

/// <summary>1 本の音声セッション（診断にも使う）。</summary>
internal readonly record struct AudioSessionInfo(uint ProcessId, int State, int ProcessIdResult, bool HasControl2)
{
    internal bool IsActive => State == CoreAudioInterop.AudioSessionStateActive;
}

/// <summary>音声セッションから「今、音を鳴らしているプロセス」を求める。</summary>
internal static class AudioSessionProbe
{
    /// <summary>音声セッションを持つプロセス ID の集合。失敗時は空集合。</summary>
    internal static HashSet<uint> GetAudioProcessIds()
    {
        var result = new HashSet<uint>();
        foreach (var session in QueryAudioSessions().Sessions)
        {
            if (session.IsActive && session.ProcessId != 0) result.Add(session.ProcessId);
        }

        return result;
    }

    /// <summary>
    /// アクティブなレンダー エンドポイント数と全セッション（状態・PID）を返す。
    /// 例外は出さず、失敗時は (0, 空リスト)。
    /// </summary>
    internal static (int DeviceCount, List<AudioSessionInfo> Sessions) QueryAudioSessions()
    {
        var sessions = new List<AudioSessionInfo>();
        int deviceCount = 0;
        var comObjects = new List<object>();
        bool mustUninitialize = false;

        try
        {
            // RPC_E_CHANGED_MODE は「既に別アパートメントで初期化済み」。その場合はそのまま使う。
            int hrInit = NativeMethods.CoInitializeEx(IntPtr.Zero, CoreAudioInterop.COINIT_MULTITHREADED);
            if (hrInit >= 0)
            {
                mustUninitialize = true;
            }
            else if (hrInit != CoreAudioInterop.RPC_E_CHANGED_MODE)
            {
                return (deviceCount, sessions);
            }

            var enumerator = CreateEnumerator();
            if (enumerator is null) return (deviceCount, sessions);
            comObjects.Add(enumerator);

            if (enumerator.EnumAudioEndpoints(CoreAudioInterop.eRender, CoreAudioInterop.DEVICE_STATE_ACTIVE,
                    out var devices) < 0 || devices is null)
            {
                return (deviceCount, sessions);
            }
            comObjects.Add(devices);

            if (devices.GetCount(out int count) < 0)
            {
                deviceCount = 0;
                return (deviceCount, sessions);
            }

            deviceCount = count;

            for (int d = 0; d < deviceCount; d++)
            {
                try
                {
                    if (devices.Item(d, out var device) < 0 || device is null) continue;
                    comObjects.Add(device);

                    var iid = CoreAudioInterop.IID_IAudioSessionManager2;
                    if (device.Activate(ref iid, CoreAudioInterop.CLSCTX_ALL, IntPtr.Zero, out object? raw) < 0
                        || raw is not IAudioSessionManager2 manager)
                    {
                        continue;
                    }
                    comObjects.Add(raw);

                    if (manager.GetSessionEnumerator(out var audioSessions) < 0 || audioSessions is null) continue;
                    comObjects.Add(audioSessions);

                    if (audioSessions.GetCount(out int sessionCount) < 0) continue;
                    for (int s = 0; s < sessionCount; s++)
                    {
                        try
                        {
                            if (audioSessions.GetSession(s, out var control) < 0 || control is null) continue;
                            comObjects.Add(control);

                            if (control.GetState(out int state) < 0) continue;

                            uint pid = 0;
                            int pidResult = -1;
                            bool hasControl2 = control is IAudioSessionControl2;
                            if (control is IAudioSessionControl2 control2)
                            {
                                pidResult = control2.GetProcessId(out pid);
                            }

                            sessions.Add(new AudioSessionInfo(pid, state, pidResult, hasControl2));
                        }
                        catch
                        {
                            // セッション単位の失敗は無視
                        }
                    }
                }
                catch
                {
                    // デバイス単位の失敗は無視
                }
            }
        }
        catch
        {
            sessions.Clear();
        }
        finally
        {
            for (int i = comObjects.Count - 1; i >= 0; i--)
            {
                try { Marshal.FinalReleaseComObject(comObjects[i]); }
                catch { /* 解放失敗は無視 */ }
            }

            if (mustUninitialize)
            {
                try { NativeMethods.CoUninitialize(); }
                catch { /* 無視 */ }
            }
        }

        return (deviceCount, sessions);
    }

    private static IMMDeviceEnumerator? CreateEnumerator()
    {
        IntPtr unknown = IntPtr.Zero;
        try
        {
            var clsid = CoreAudioInterop.CLSID_MMDeviceEnumerator;
            var iid = CoreAudioInterop.IID_IMMDeviceEnumerator;
            if (NativeMethods.CoCreateInstance(ref clsid, IntPtr.Zero, CoreAudioInterop.CLSCTX_ALL,
                    ref iid, out unknown) < 0 || unknown == IntPtr.Zero)
            {
                return null;
            }

            return Marshal.GetObjectForIUnknown(unknown) as IMMDeviceEnumerator;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (unknown != IntPtr.Zero) Marshal.Release(unknown);
        }
    }
}

// ------------------------------------------------------------------ COM インターフェイス定義

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection? devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int Item(int index, out IMMDevice? device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object? ppInterface);
    [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IntPtr properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? id);
    [PreserveSig] int GetState(out int state);
}

[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(IntPtr audioSessionGuid, uint streamFlags,
        [MarshalAs(UnmanagedType.IUnknown)] out object? sessionControl);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr audioSessionGuid, uint streamFlags,
        [MarshalAs(UnmanagedType.IUnknown)] out object? simpleAudioVolume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator? sessionEnumerator);
    [PreserveSig] int RegisterSessionNotification(IntPtr sessionNotification);
    [PreserveSig] int UnregisterSessionNotification(IntPtr sessionNotification);
    [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId,
        IntPtr duckNotification);
    [PreserveSig] int UnregisterDuckNotification(IntPtr duckNotification);
}

[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int sessionCount);
    [PreserveSig] int GetSession(int sessionCount, out IAudioSessionControl? session);
}

[ComImport]
[Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string? displayName);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string? iconPath);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
    [PreserveSig] int SetGroupingParam(IntPtr groupingParam, IntPtr eventContext);
    [PreserveSig] int GetGroupingParam(out IntPtr groupingParam);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr newNotifications);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr newNotifications);
}

[ComImport]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    // IAudioSessionControl から継承した 9 メソッド。COM の vtable 順（基底が先）を保つため
    // あえて継承せずに全部並べる（スロットずれ事故を防ぐ）。
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string? displayName);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string? iconPath);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
    [PreserveSig] int SetGroupingParam(IntPtr groupingParam, IntPtr eventContext);
    [PreserveSig] int GetGroupingParam(out IntPtr groupingParam);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr newNotifications);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr newNotifications);

    // IAudioSessionControl2 固有。
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string? sessionIdentifier);
    [PreserveSig] int GetSessionInstanceIdentifier(
        [MarshalAs(UnmanagedType.LPWStr)] out string? sessionInstanceIdentifier);
    [PreserveSig] int GetProcessId(out uint processId);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}
