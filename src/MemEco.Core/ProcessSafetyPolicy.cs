using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MemEco.Core.Interop;
using MemEco.Core.Models;

namespace MemEco.Core;

/// <summary>
/// 「触ってはいけないプロセス」の判定（DESIGN §5.3 / §5.7）。
/// 迷ったら保護する（安全側）方針。理由文字列は UI / デバッグ表示用の日本語。
/// </summary>
public sealed class ProcessSafetyPolicy
{
    private readonly List<string> _exclusions;

    public ProcessSafetyPolicy() : this(new AppConfig())
    {
    }

    public ProcessSafetyPolicy(AppConfig config)
    {
        var exclusions = config.Exclusions ?? new List<string>();
        _exclusions = new List<string>(exclusions.Count);
        foreach (var item in exclusions)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            _exclusions.Add(item.Trim());
        }
    }

    /// <summary>絶対に触らない既知のシステム プロセス（拡張子なし・大文字小文字無視）。</summary>
    private static readonly HashSet<string> SystemProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "memory compression", "secure system",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "fontdrvhost", "dwm", "conhost", "sihost", "explorer",
        "searchhost", "startmenuexperiencehost", "shellexperiencehost", "textinputhost",
        "runtimebroker", "ctfmon", "taskhostw", "dllhost", "applicationframehost",
        "lockapp", "logonui", "userinit", "wudfhost", "spoolsv", "audiodg",
        "smartscreen", "securityhealthservice", "securityhealthsystray", "lsm",
        "wmiprvse", "searchindexer", "msmpeng", "sppsvc", "trustedinstaller",
    };

    /// <summary>自アプリと、開発環境（DeepSeek Harness）自身。誤って止めない。</summary>
    private static readonly HashSet<string> SelfProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "memeco", "deepseek harness", "deepseek-harness", "deepseek",
    };

    private static readonly string[] SystemFolderMarkers =
    {
        @"\System32\", @"\SysWOW64\", @"\Sysnative\", @"\WinSxS\", @"\servicing\",
    };

    internal const int RecentStartGraceSeconds = 60;

    private static readonly int SelfPid = Environment.ProcessId;
    private static readonly int SelfParentPid = QueryParentPid(SelfPid);

    /// <summary>pid → 起動時刻キャッシュ（ProcessProbe が実測値を流し込む）。</summary>
    private static readonly ConcurrentDictionary<int, StartTimeEntry> StartTimes = new();

    private readonly record struct StartTimeEntry(DateTime StartUtc, DateTime CachedAtUtc);

    // ---------------------------------------------------------------- 公開 API

    /// <summary>触ってはいけないプロセスか。</summary>
    public bool IsProtected(ProcessSnapshot snapshot) => ComputeReason(snapshot) is not null;

    /// <summary>除外理由（保護対象でなければ null）。</summary>
    public string? Describe(ProcessSnapshot snapshot) => ComputeReason(snapshot);

    /// <summary>
    /// 保護理由を 1 回だけ評価して返す。null なら操作してよい。
    /// （IsProtected と Describe の二重評価を避けるための内部入り口）
    /// </summary>
    internal string? ComputeReason(ProcessSnapshot p)
    {
        if (p.Pid <= 4) return "システム プロセスです";
        if (p.Pid == SelfPid) return "MemEco 自身です";
        if (SelfParentPid > 0 && p.Pid == SelfParentPid) return "MemEco の親プロセスです";

        var bareName = GetBareName(p.Name);
        if (bareName.Length > 0 && SystemProcessNames.Contains(bareName)) return "システムの中核プロセスです";
        if (bareName.Length > 0 && SelfProcessNames.Contains(bareName)) return "MemEco 関連のプロセスです";

        if (p.IsSystem || IsSystemFolderPath(p.ExecutablePath)) return "Windows のシステム ファイルです";
        if (p.IsElevated) return "管理者権限で動作しています";
        if (p.IsForeground) return "現在アクティブなウィンドウです";
        if (p.HasVisibleWindow) return "ウィンドウを表示しています";
        if (p.HasAudio) return "音声を再生しています";

        var excluded = MatchExclusion(bareName, p.ExecutablePath);
        if (excluded is not null) return $"除外リスト（{excluded}）に含まれています";

        if (StartedWithin(p.Pid, RecentStartGraceSeconds)) return "起動したばかりです（60 秒未満）";

        return null;
    }

    // ---------------------------------------------------------------- 判定パーツ

    internal static bool IsKnownSystemName(string? name) => GetBareName(name) is { Length: > 0 } bare && SystemProcessNames.Contains(bare);

    internal static bool IsSystemFolderPath(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath)) return false;
        foreach (var marker in SystemFolderMarkers)
        {
            if (executablePath.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    internal static string GetBareName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private string? MatchExclusion(string bareName, string? executablePath)
    {
        if (_exclusions.Count == 0) return null;

        foreach (var exclusion in _exclusions)
        {
            if (bareName.Length > 0
                && (bareName.Equals(exclusion, StringComparison.OrdinalIgnoreCase)
                    || bareName.Contains(exclusion, StringComparison.OrdinalIgnoreCase)
                    || (bareName + ".exe").Equals(exclusion, StringComparison.OrdinalIgnoreCase)))
            {
                return exclusion;
            }

            if (!string.IsNullOrEmpty(executablePath)
                && executablePath.Contains(exclusion, StringComparison.OrdinalIgnoreCase))
            {
                return exclusion;
            }
        }

        return null;
    }

    /// <summary>起動から grace 秒以内か（起動時刻が不明なら false = 判定しない）。</summary>
    private static bool StartedWithin(int pid, int seconds)
    {
        var startUtc = GetStartTimeUtc(pid);
        if (startUtc is null) return false;
        var elapsed = DateTime.UtcNow - startUtc.Value;
        return elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromSeconds(seconds);
    }

    private static DateTime? GetStartTimeUtc(int pid)
    {
        var now = DateTime.UtcNow;
        if (StartTimes.TryGetValue(pid, out var cached) && now - cached.CachedAtUtc < TimeSpan.FromMinutes(10))
        {
            return cached.StartUtc;
        }

        try
        {
            IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return null;

            try
            {
                if (!NativeMethods.GetProcessTimes(handle, out var created, out _, out _, out _)) return null;
                var startUtc = FileTimeToUtc(created.ToUInt64());
                if (startUtc is null) return null;

                RememberStartTime(pid, created.ToUInt64());
                return startUtc;
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>ProcessProbe から起動時刻（FILETIME）を受け取りキャッシュする。</summary>
    internal static void RememberStartTime(int pid, ulong creationFileTime)
    {
        var startUtc = FileTimeToUtc(creationFileTime);
        if (startUtc is null) return;

        StartTimes[pid] = new StartTimeEntry(startUtc.Value, DateTime.UtcNow);

        // 長時間稼働で辞書が膨らみすぎないよう、古いエントリを時々掃除する。
        if (StartTimes.Count > 8192)
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(5);
            foreach (var pair in StartTimes)
            {
                if (pair.Value.CachedAtUtc < cutoff) StartTimes.TryRemove(pair.Key, out _);
            }
        }
    }

    private static DateTime? FileTimeToUtc(ulong fileTime)
    {
        if (fileTime == 0 || fileTime > long.MaxValue) return null;
        try
        {
            return DateTime.FromFileTimeUtc((long)fileTime);
        }
        catch
        {
            return null;
        }
    }

    private static int QueryParentPid(int pid)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return 0;

            int status = NativeMethods.NtQueryInformationProcess(handle, 0,
                out var info, (uint)Marshal.SizeOf<NativeMethods.PROCESS_BASIC_INFORMATION>(), out _);
            if (status != 0) return 0;
            return (int)info.InheritedFromUniqueProcessId.ToInt64();
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (handle != IntPtr.Zero) NativeMethods.CloseHandle(handle);
        }
    }
}
