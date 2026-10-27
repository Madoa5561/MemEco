# MemEco 設計書 / チーム共有仕様

> macOS のメモリ管理体験（メモリプレッシャー・App Nap・メモリ圧縮・purge）を
> Windows 11 に持ち込む常駐アプリ。**全自動**で、軽く、美しいこと。

- プロダクト名: **MemEco**（メモリエコ）
- 対象: Windows 11（Build 22000+ / 検証機 26200）
- ランタイム: .NET 9（フレームワーク依存 / 単一 exe）
- UI: WPF（WinForms はトレイアイコンのみ）
- 外部 NuGet 依存: **なし**（オフラインビルド可）

---

## 1. macOS との対応表

| macOS | 本アプリの実装 |
|---|---|
| メモリプレッシャー表示 | `GlobalMemoryStatusEx` + `GetPerformanceInfo` + 圧縮ストア量から 0..1 スコアを算出 |
| App Nap（背面アプリの省電力化） | Windows 11 の **EcoQoS**（`SetProcessInformation` / ProcessPowerThrottling）+ 優先度 `BELOW_NORMAL` |
| メモリ圧縮 | Windows 標準の Memory Compression を監視し、指標として表示（OS 機能を再実装はしない） |
| purge / メモリ解放 | ワーキングセットのトリム（`EmptyWorkingSet`）+ スタンバイリスト解放（`NtSetSystemInformation` / 要管理者） |
| スワップ | ページファイル使用量・コミットチャージを監視 |
| 低メモリ時の自動退避 | 圧迫度 Critical 時のみ、条件を満たす背面プロセスを **一時停止**（App Nap Plus / 既定 OFF） |

## 2. アーキテクチャ

```
MemEco.exe (WPF, tray resident)
 ├─ MemEco.App  … UI / Tray / Config / Autostart / CLI / Preview
 └─ MemEco.Core … 監視・判断・操作（UI 非依存）
      ├─ MemoryProbe        : メモリ計測・プレッシャー算出
      ├─ ProcessProbe       : プロセス走査（前面/可視/CPU/親子/音声）
      ├─ ProcessSafetyPolicy: 触ってはいけないプロセスの判定
      ├─ ProcessController  : EcoQoS / 優先度 / Trim / Suspend / Purge
      └─ EcoEngine          : tick ループと状態機械・統計
```

イベント駆動: エンジンは 2 秒ごとに `EngineSnapshot` を発火。UI は Dispatcher で受けて描画。
エンジンは UI スレッドを一切知らない。

## 3. モジュール構成

| モジュール | 主なファイル |
|---|---|
| ソリューション / 共有モデル | `MemEco.sln`, `src/**/*.csproj`, `src/MemEco.Core/Models/*`, `IEcoEngine.cs`, `Diagnostics/CoreSelfTest.cs`, `app.manifest`, `assets/icon.ico` |
| エンジン（OS 連携） | `src/MemEco.Core/Interop/*`, `MemoryProbe.cs`, `ProcessProbe.cs`, `ProcessSafetyPolicy.cs`, `ProcessController.cs`, `EcoEngine.cs` |
| UI（WPF 常駐） | `src/MemEco.App/**`（`App.xaml(.cs)`, `Theme/*`, `Views/*`, `Tray/*`, `Services/*`, `Diagnostics/PreviewRenderer.cs`） |
| ビルド / 配布 / 検証 | `scripts/*`, `tools/verify.ps1`, `README.md`, `docs/USAGE.md`, `.github/workflows/build.yml` |

> `MemEco.Core` の公開シグネチャ（`IEcoEngine` / `Models`）は UI との契約です。変更する場合は UI 側も合わせて更新してください。

## 4. Core の必須 API（エンジン実装 / 変更禁止）

```csharp
public sealed class MemoryProbe { public MemorySnapshot Sample(); }

public sealed class ProcessProbe {
    public IReadOnlyList<ProcessSnapshot> Scan();   // 前面・可視・CPU・親 PID を含む
}

public sealed class ProcessSafetyPolicy {
    public bool IsProtected(ProcessSnapshot p);     // 触ってはいけない判定
    public string? Describe(ProcessSnapshot p);     // 除外理由（デバッグ）
}

public sealed class ProcessController {
    public bool CanPurgeStandby { get; }
    public bool CanUseEcoQos { get; }
    public bool CanSuspend { get; }
    public bool ApplyEco(int pid);                  // EcoQoS + BelowNormal
    public bool ClearEco(int pid);                  // EcoQoS 解除 + 元の優先度へ
    public ulong Trim(int pid, out ulong before);   // EmptyWorkingSet、解放量を返す
    public bool Suspend(int pid);
    public bool Resume(int pid);
    public bool PurgeStandby();                     // 要管理者 / スタンバイリスト解放
    public bool IsFullScreenAppRunning();           // SHQueryUserNotificationState
    public Task<string> SelfTestAsync(CancellationToken ct = default);
}

public sealed class EcoEngine : IEcoEngine {
    public EcoEngine(AppConfig config, Action<string>? activityLog = null);
    public static bool IsRunningElevated();
    // IEcoEngine のメンバー + 内部で tick ループ
}
```

補足: `ProcessSnapshot` には `ParentPid` を追加してよい（engine が Model を編集する場合は Lead に申告）。

## 5. エンジン仕様

### 5.1 圧迫度スコア
```
availRatio  = AvailPhys / TotalPhys
commitRatio = Commit / CommitLimit
availScore  = clamp01((0.28 - availRatio) / 0.22)
commitScore = clamp01((commitRatio - 0.70) / 0.25)
compScore   = clamp01((CompressionBytes / TotalPhys - 0.10) / 0.20)
score       = clamp01(max(availScore, commitScore) + 0.35 * compScore)
Level       = score < 0.45 ? Normal : score < 0.75 ? Warning : Critical
```

### 5.2 tick（既定 2000ms）
1. `MemoryProbe.Sample()`
2. 2 tick に 1 回 `ProcessProbe.Scan()` + 全画面検出
3. 状態機械を更新（下記）
4. `SnapshotReady` を発火

### 5.3 状態機械（PID ごと）
- `None → Eco`: 前面でない / 可視ウィンドウなし / CPU < 1.5% / アイドル `EcoIdleMinutes` 以上 / WS ≥ `MinProcessMemoryMb` / 保護対象でない
- `Eco → Trimmed`: 圧迫度 Warning 以上 かつ `EnableWorkingSetTrim`。同一 PID は 60 秒に 1 回まで
- `Trimmed/Eco → Suspended`: 圧迫度 Critical かつ `EnableSuspend` かつ アイドル `SuspendIdleMinutes` 以上。**子プロセスを持つものは対象外**
- `* → None`: 前面化 / 可視化 / 音声再生 / 設定 OFF / 保護対象化。このとき必ず `ClearEco` + `Resume`
- `Suspended` は `MaxSuspendMinutes` で強制復帰。`StopAsync` 時は全員復帰
- 保護対象・システム・昇格済み・自プロセス・親プロセス・起動 60 秒未満・除外リストは**一切触らない**

### 5.4 スタンバイ解放
`EnableStandbyPurge` かつ管理者かつ Level ≥ Warning かつ `PurgeCooldownSeconds` 経過時のみ。Critical でも同条件。失敗しても例外を投げない。

### 5.5 手動回収
`ReclaimNowAsync`: 保護対象以外を全走査 → Trim（WS 50MB 以上）→ `Full` かつ管理者なら Purge。
解放量を実測して `ReclaimResult` に返す。例外は握って `Failed`。

### 5.6 統計
`TotalReclaimedBytes`, `ReclaimedTodayBytes`（日付でリセット）, `ReclaimOperations`, `PurgeOperations`,
`EcoManagedCount`, `SuspendedCount`, `TrimmedCount`, `LastReclaimUtc`, `LastPurgeUtc`。
`Activity` イベントは日本語 1 行（例: `3 個のアプリから 412 MB を解放しました`）。

### 5.7 安全性（最重要）
- 例外でループを止めない（全操作を try/catch、ログへ）
- 権限のないプロセスは静かにスキップ
- 全画面ゲーム/プレゼン中は新しい操作をしない（`IsProtectionSuspended`）
- 停止したプロセスは必ず復帰する（`DisposeAsync` / `StopAsync` / 最大時間 / 前面化）
- メモリ操作は「解放できた量」を実測して統計に反映（見かけの数字を作らない）

## 6. UI 仕様（ui 担当）

### 6.1 常駐
- `NotifyIcon`（System.Windows.Forms）でトレイ常駐。左クリックでパネル表示/非表示、右クリックで WPF 風 ContextMenu
- トレイアイコンは実行時に System.Drawing で描画（圧迫度で色が変わる: 緑 / 琥珀 / 赤）
- 二重起動禁止（名前付き Mutex + EventWaitHandle。2 個目は既存に「表示」を通知して終了）
- 既定は非表示起動（トレイのみ）。初回起動のみパネルを表示
- 終了メニューは明示操作のみ（×ボタンはトレイへ格納）

### 6.2 メインパネル（440 x 680 目安、画面右下に配置）
1. ヘッダー: ロゴ + 「MemEco」 + 設定⚙ + 閉じる
2. **メモリプレッシャーリング**（カスタム描画）: スコアを円弧で表示、中央に「余裕あり / 注意 / 危険」、下に数値%
3. **メモリ内訳バー**: 使用中 / キャッシュ(スタンバイ) / 空き の積み上げ + 凡例。コミットバーも併記
4. **空きメモリのスパークライン**（直近 60 サンプル、グラデーション塗り）
5. 大きなトグル: 「全自動メモリ管理」ON/OFF（ピル型アニメーション）
6. プリセット選択（セグメント）: やさしい / 標準 / 積極的
7. アクション: 「今すぐ解放」（プライマリ）/「スタンバイも解放」（管理者時のみ有効）
8. 実績: 本日解放 / セッション合計 / エコ管理中 / 一時停止中
9. エコ管理中のアプリ一覧（最大 5 件 + スクロール、名前・状態バッジ・解放量）
10. フッター: モード表示（管理者モード / 標準モード）、バージョン

### 6.3 設定ウィンドウ（720 x 640、スクロール）
- 自動管理: ON/OFF、プリセット、しきい値スライダー
- 機能: EcoQoS、トリム、スタンバイ解放、一時停止（危険表示付き）
- 除外アプリ: プロセスピッカー（実行中プロセスから追加）＋テキスト追加/削除
- 起動: Windows と同時に起動（HKCU Run）、管理者モードで起動（タスクスケジューラ / UAC 1 回）
- 情報: バージョン、設定保存先、ログを開く、設定リセット

### 6.4 見た目
- ダーク基調。背景 `#0B111C` → `#111A2B` の斜めグラデーション、カード `#141E30` + 1px `#243149`
- アクセント: ミント `#4ADE80` → シアン `#22D3EE`。警告 `#FBBF24`、危険 `#F87171`
- 文字: `#E9EFF9` / 補助 `#8FA3BF`。フォント `Segoe UI Variable Text`, `Yu Gothic UI`
- 角丸 18px、DWM の Mica/Acrylic 背景（`DwmSetWindowAttribute` 38=システムバックドロップ, 33=角丸）+ `WindowChrome` で枠なし
- 影・グローは控えめ、アニメーション 200–300ms、60fps を維持
- 日本語 UI（英語混在の技術用語は可）

### 6.5 設定・状態
- 設定: `%APPDATA%\MemEco\config.json`（`AppConfig` を System.Text.Json で保存、破損時は既定値）
- ログ: `%APPDATA%\MemEco\logs\app.log`（簡易ローリング、1MB）
- 起動時に設定を読み、エンジンへ `ApplyConfig`

## 7. CLI 契約

| 引数 | 動作 |
|---|---|
| （なし）/ `--autostart` | トレイ常駐。ウィンドウは出さない（初回のみ表示） |
| `--show` | 起動してパネル表示 |
| `--selftest <path>` | `CoreSelfTest.RunAsync` の結果を UTF-8 で書き出して終了（exit 0/1） |
| `--preview <path>` | エンジンを ~2 秒動かし、メインパネルを PNG で書き出して終了 |
| `--reclaim <path>` | 単発起動で今すぐ解放を実行し、`ReclaimResult` を JSON で書き出して終了（検証用） |

## 8. ビルド / 実行

```powershell
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
& $dotnet build .\MemEco.sln -c Release
& $dotnet run --project .\src\MemEco.App -c Release -- --selftest .\selftest.txt
```

- 出力: `src\MemEco.App\bin\Release\net9.0-windows\MemEco.exe`
- NuGet は使わない（`PackageReference` 禁止）

## 9. 受け入れ基準

1. `dotnet build` が通る（エラー 0）
2. `--selftest` が完了し、管理者判定・メモリ値・プロセス走査・API 可否が出力される
3. `--preview` が空でない 440x680 の PNG を生成（UI が崩れていない）
4. 起動して 10 秒後も CPU 1% 未満・ワーキングセット 80MB 未満
5. ダミーのメモリ消費プロセス（300MB）を Trim で実際に減らせる（標準ユーザー権限で可）
6. 一時停止したプロセスが必ず復帰する（手動停止 / 終了 / 前面化）
7. 設定が `%APPDATA%\MemEco\config.json` に保存され、再起動で復元される
