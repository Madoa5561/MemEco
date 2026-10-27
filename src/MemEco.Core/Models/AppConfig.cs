using System.Text.Json.Serialization;

namespace MemEco.Core.Models;

/// <summary>自動管理の強さ。ユーザーが選ぶ 3 つのモード。</summary>
public enum AutoPreset
{
    /// <summary>やさしい: 明らかなメモリ不足時のみ動く。</summary>
    Gentle = 0,

    /// <summary>標準（おすすめ）: バランス型。既定値。</summary>
    Balanced = 1,

    /// <summary>積極的: 休止（サスペンド）も使う上級者向け。</summary>
    Aggressive = 2,
}

public enum ReclaimMode
{
    /// <summary>設定に従い自動で判断。</summary>
    Auto = 0,

    /// <summary>ワーキングセットのトリムのみ。</summary>
    TrimOnly = 1,

    /// <summary>トリム + スタンバイリスト解放（要管理者）。</summary>
    Full = 2,
}

/// <summary>%APPDATA%\MemEco\config.json に保存される設定。</summary>
public sealed class AppConfig
{
    public int Version { get; set; } = 1;

    /// <summary>全自動メモリ管理の ON/OFF。</summary>
    public bool AutoManage { get; set; } = true;

    public AutoPreset Preset { get; set; } = AutoPreset.Balanced;

    // ---- 機能スイッチ ----
    public bool EnableEcoMode { get; set; } = true;
    public bool EnableWorkingSetTrim { get; set; } = true;

    /// <summary>バックグラウンドアプリの一時停止（App Nap Plus）。既定は安全のため OFF。</summary>
    public bool EnableSuspend { get; set; }

    /// <summary>スタンバイリスト解放（要管理者権限）。</summary>
    public bool EnableStandbyPurge { get; set; } = true;

    // ---- しきい値 ----
    /// <summary>空きメモリがこの % を下回ると「注意」。</summary>
    public int WarningAvailablePercent { get; set; } = 20;

    /// <summary>空きメモリがこの % を下回ると「危険」。</summary>
    public int CriticalAvailablePercent { get; set; } = 10;

    // ---- タイミング ----
    /// <summary>メモリ監視のサンプリング間隔（ミリ秒）。</summary>
    public int SampleIntervalMs { get; set; } = 2000;

    /// <summary>バックグラウンドと判定してから Eco を適用するまでの分数。</summary>
    public int EcoIdleMinutes { get; set; } = 2;

    /// <summary>一時停止までのアイドル分数（EnableSuspend 時のみ）。</summary>
    public int SuspendIdleMinutes { get; set; } = 20;

    /// <summary>一時停止の最大継続分数（超えたら強制復帰）。</summary>
    public int MaxSuspendMinutes { get; set; } = 30;

    /// <summary>スタンバイ解放のクールダウン（秒）。</summary>
    public int PurgeCooldownSeconds { get; set; } = 90;

    /// <summary>Eco 対象にする最小メモリ（MB）。小さいプロセスは無視。</summary>
    public int MinProcessMemoryMb { get; set; } = 100;

    // ---- 挙動 ----
    public bool PauseWhenFullscreen { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool LaunchMinimized { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;

    /// <summary>除外するプロセス名（拡張子なし）またはパス断片。</summary>
    public List<string> Exclusions { get; set; } = new();

    /// <summary>管理者モード（タスクスケジューラ）を使うか。</summary>
    public bool UseElevatedStartup { get; set; }

    public AppConfig Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        return System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
    }

    /// <summary>プリセットに応じてしきい値・機能をまとめて設定する。</summary>
    public void ApplyPreset(AutoPreset preset)
    {
        Preset = preset;
        switch (preset)
        {
            case AutoPreset.Gentle:
                EnableEcoMode = true;
                EnableWorkingSetTrim = true;
                EnableSuspend = false;
                EnableStandbyPurge = true;
                WarningAvailablePercent = 15;
                CriticalAvailablePercent = 8;
                EcoIdleMinutes = 5;
                SuspendIdleMinutes = 30;
                MaxSuspendMinutes = 30;
                MinProcessMemoryMb = 150;
                break;

            case AutoPreset.Balanced:
                EnableEcoMode = true;
                EnableWorkingSetTrim = true;
                EnableSuspend = false;
                EnableStandbyPurge = true;
                WarningAvailablePercent = 20;
                CriticalAvailablePercent = 10;
                EcoIdleMinutes = 2;
                SuspendIdleMinutes = 20;
                MaxSuspendMinutes = 30;
                MinProcessMemoryMb = 100;
                break;

            case AutoPreset.Aggressive:
                EnableEcoMode = true;
                EnableWorkingSetTrim = true;
                EnableSuspend = true;
                EnableStandbyPurge = true;
                WarningAvailablePercent = 25;
                CriticalAvailablePercent = 15;
                EcoIdleMinutes = 1;
                SuspendIdleMinutes = 10;
                MaxSuspendMinutes = 20;
                MinProcessMemoryMb = 64;
                break;
        }
    }
}
