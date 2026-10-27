using System.Diagnostics;
using System.Text;
using MemEco.Core.Models;

namespace MemEco.Core.Diagnostics;

/// <summary>
/// ビルド / 実行環境の自己診断。`MemEco.exe --selftest out.txt` から利用する。
/// 管理者権限の有無・主要 API の可否・現在のメモリ状況を 1 つのレポートにまとめる。
/// </summary>
public static class CoreSelfTest
{
    public static async Task<string> RunAsync(AppConfig? config = null, CancellationToken ct = default)
    {
        config ??= new AppConfig();
        var sb = new StringBuilder();
        sb.AppendLine("MemEco セルフテスト");
        sb.AppendLine("===================");
        sb.AppendLine($"時刻: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"OS: {Environment.OSVersion.VersionString} (Build {Environment.OSVersion.Version.Build})");
        sb.AppendLine($"64bit: {Environment.Is64BitOperatingSystem} / プロセス: {Environment.Is64BitProcess}");
        sb.AppendLine($"管理者: {EcoEngine.IsRunningElevated()}");

        var probe = new MemoryProbe();
        var mem = probe.Sample();
        sb.AppendLine();
        sb.AppendLine("--- メモリ ---");
        sb.AppendLine($"合計: {Fmt(mem.TotalPhysBytes)} / 空き: {Fmt(mem.AvailablePhysBytes)} ({mem.AvailableRatio:P1})");
        sb.AppendLine($"コミット: {Fmt(mem.CommitBytes)} / {Fmt(mem.CommitLimitBytes)} ({mem.CommitRatio:P1})");
        sb.AppendLine($"キャッシュ: {Fmt(mem.SystemCacheBytes)} / 圧縮: {Fmt(mem.CompressionBytes)}");
        sb.AppendLine($"プレッシャー: {mem.PressureScore:P0} => {mem.Pressure}");
        sb.AppendLine();

        var controller = new ProcessController();
        sb.AppendLine("--- API 可否 ---");
        sb.AppendLine($"EmptyWorkingSet(自プロセス): {await controller.SelfTestAsync(ct)}");
        sb.AppendLine($"スタンバイ解放可否: {controller.CanPurgeStandby} (要管理者)");
        sb.AppendLine($"EcoQoS 可否: {controller.CanUseEcoQos}");
        sb.AppendLine($"一時停止 API 可否: {controller.CanSuspend}");
        sb.AppendLine();

        sb.AppendLine("--- プロセス走査 ---");
        var probe2 = new ProcessProbe();
        var sw = Stopwatch.StartNew();
        var procs = probe2.Scan();
        sw.Stop();
        sb.AppendLine($"検出: {procs.Count} プロセス / 所要 {sw.ElapsedMilliseconds} ms");
        var top = procs.OrderByDescending(p => p.WorkingSetBytes).Take(8);
        foreach (var p in top)
        {
            sb.AppendLine($"  {p.Name,-28} PID {p.Pid,6}  WS {Fmt(p.WorkingSetBytes),10}  前面={p.IsForeground} 可視={p.HasVisibleWindow} 保護={p.IsProtected}");
        }
        sb.AppendLine();

        sb.AppendLine("--- エンジン ---");
        var engine = EcoEngineFactory.Create(config, _ => { });
        try
        {
            await engine.StartAsync();
            var t0 = DateTime.UtcNow;
            while (DateTime.UtcNow - t0 < TimeSpan.FromSeconds(3)) await Task.Delay(100, ct);
            var snap = engine.Latest;
            sb.AppendLine($"状態: {snap.Status}");
            sb.AppendLine($"管理対象: {snap.ManagedProcesses.Count} / 回収済み: {Fmt(snap.Stats.TotalReclaimedBytes)}");
        }
        finally
        {
            await engine.StopAsync();
        }

        sb.AppendLine();
        sb.AppendLine("完了");
        return sb.ToString();

        static string Fmt(ulong b) => b >= 1024UL * 1024 * 1024
            ? $"{b / 1024d / 1024 / 1024:F2} GB"
            : $"{b / 1024d / 1024:F0} MB";
    }
}
