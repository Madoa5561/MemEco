using MemEco.Core.Models;

namespace MemEco.Core;

/// <summary>
/// メモリエコ・エンジンの公開契約。UI はこのインターフェースだけに依存する。
/// </summary>
public interface IEcoEngine : IAsyncDisposable
{
    /// <summary>2 秒ごとの最新スナップショット通知（UI スレッドへの marshalling は呼び出し側で行う）。</summary>
    event EventHandler<EngineSnapshot>? SnapshotReady;

    /// <summary>人間向けの短いアクティビティログ（例: "3 個のプロセスから 412 MB を回収"）。</summary>
    event EventHandler<string>? Activity;

    /// <summary>最後に生成されたスナップショット。</summary>
    EngineSnapshot Latest { get; }

    AppConfig Config { get; }

    bool IsAdmin { get; }

    /// <summary>UI の一時停止スイッチ。true の間はサンプリングのみ継続し、操作は行わない。</summary>
    bool IsPaused { get; set; }

    Task StartAsync();

    Task StopAsync();

    /// <summary>設定を反映する（次 tick から）。</summary>
    void ApplyConfig(AppConfig config);

    /// <summary>「今すぐ解放」。</summary>
    Task<ReclaimResult> ReclaimNowAsync(ReclaimMode mode = ReclaimMode.Auto);

    /// <summary>スタンバイリストの解放（管理者のみ）。</summary>
    Task<ReclaimResult> PurgeStandbyAsync();
}

public static class EcoEngineFactory
{
    /// <summary>エンジンを生成する。activityLog が null の場合は内部で握りつぶす。</summary>
    public static IEcoEngine Create(AppConfig config, Action<string>? activityLog = null)
        => new EcoEngine(config, activityLog);
}
