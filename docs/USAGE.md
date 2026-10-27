# MemEco クイックリファレンス

1 ページで MemEco の操作を確認できます。詳しくは [README.md](../README.md) /
[docs/DESIGN.md](DESIGN.md) を参照してください。

---

## 起動・終了

| やりたいこと | 操作 |
|---|---|
| 起動（パネルを表示） | スタートメニューの **MemEco**、または `MemEco.exe --show` |
| 起動（トレイのみ） | ログオン時に自動起動 / `MemEco.exe --autostart` |
| パネルの表示・非表示 | トレイアイコンを左クリック |
| 終了 | トレイアイコンを右クリック → 終了（× はトレイへ格納） |

> 二重起動はしません。2 個目は既存の MemEco にパネル表示を通知して終了します。

---

## トレイメニュー（右クリック）

| 項目 | 動作 |
|---|---|
| パネルを開く | メインパネルを表示 |
| 今すぐ解放 | その場でワーキングセットを整理 |
| 設定 | 設定画面を開く |
| 終了 | 常駐を終了（一時停止中のプロセスは復帰） |

トレイアイコンの色 = 圧迫度: **緑**（余裕あり） / **琥珀**（注意） / **赤**（危険）。

---

## パネルの見方

| 表示 | 意味 |
|---|---|
| メモリプレッシャーリング | 0〜100% の圧迫度。余裕あり / 注意 / 危険 |
| メモリ内訳バー | 使用中 / キャッシュ（スタンバイ）/ 空き、コミット量 |
| 空きメモリのスパークライン | 直近の空きメモリ推移 |
| 全自動メモリ管理 | 自動処理の ON/OFF（表示・記録は継続） |
| プリセット | やさしい / 標準 / 積極的 |
| 今すぐ解放 | 手動のメモリ回収 |
| スタンバイも解放 | 管理者モードのときだけ有効 |
| 実績 | 本日解放 / セッション合計 / エコ管理中 / 一時停止中 |
| エコ管理中のアプリ | 対象アプリと状態・解放量 |
| フッター | 管理者モード / 標準モード、バージョン |

### プリセット

| プリセット | しきい値（空き） | 一時停止 |
|---|---|---|
| やさしい | 注意 15% / 危険 8% | 使わない |
| 標準（既定） | 注意 20% / 危険 10% | 使わない |
| 積極的 | 注意 25% / 危険 15% | 使う（10 分アイドル） |

---

## 設定画面（⚙）

- 自動管理 ON/OFF、プリセット、しきい値スライダー
- 機能スイッチ: EcoQoS / トリム / スタンバイ解放 / 一時停止
- 除外アプリ: 実行中プロセスから選ぶ、または名前で追加
- 起動: Windows と同時に起動 / 管理者モードで起動
- 情報: バージョン、設定保存先、ログを開く、設定リセット

---

## CLI

```
MemEco.exe [--autostart] [--show] [--selftest <path>] [--preview <path>] [--reclaim <path>]
```

| 引数 | 動作 | 終了コード |
|---|---|---|
| （なし）/ `--autostart` | トレイ常駐。ウィンドウは出さない（初回のみ表示） | 常駐中は終了しない |
| `--show` | 起動してパネルを表示 | 常駐中は終了しない |
| `--selftest <path>` | 環境・API・メモリ・プロセス走査の結果を UTF-8 テキストで出力して終了 | 0 = 成功 / 1 = 失敗 |
| `--preview <path>` | エンジンを約 2 秒動かし、パネルを PNG で出力して終了 | 0 = 成功 |
| `--reclaim <path>` | 単発で「今すぐ解放」を実行し、結果を JSON で出力して終了 | 0 = 成功 |

例:

```powershell
$exe = ".\src\MemEco.App\bin\Release\net9.0-windows\MemEco.exe"
& $exe --selftest .\selftest.txt
& $exe --preview  .\preview.png
& $exe --reclaim  .\reclaim.json
```

`--reclaim` の JSON の主なキー: `Success` / `FreedBytes` / `TrimmedProcesses` /
`StandbyPurged` / `Message`。

---

## 設定ファイル

場所: `%APPDATA%\MemEco\config.json`

主なキー（実装により大文字小文字が異なる場合があります）:

| キー | 意味 | 既定 |
|---|---|---|
| `AutoManage` | 全自動メモリ管理 | `true` |
| `Preset` | `Gentle` / `Balanced` / `Aggressive` | `Balanced` |
| `EnableEcoMode` | EcoQoS | `true` |
| `EnableWorkingSetTrim` | ワーキングセットのトリム | `true` |
| `EnableSuspend` | 一時停止（App Nap Plus） | `false` |
| `EnableStandbyPurge` | スタンバイ解放（要管理者） | `true` |
| `WarningAvailablePercent` | 「注意」になる空き % | `20` |
| `CriticalAvailablePercent` | 「危険」になる空き % | `10` |
| `SampleIntervalMs` | サンプリング間隔（ms） | `2000` |
| `EcoIdleMinutes` | Eco 適用までのアイドル分数 | `2` |
| `SuspendIdleMinutes` | 一時停止までのアイドル分数 | `20` |
| `MaxSuspendMinutes` | 一時停止の最大継続分数 | `30` |
| `PurgeCooldownSeconds` | スタンバイ解放のクールダウン（秒） | `90` |
| `MinProcessMemoryMb` | 対象にする最小メモリ（MB） | `100` |
| `PauseWhenFullscreen` | 全画面時は停止 | `true` |
| `StartWithWindows` | Windows と同時に起動 | `true` |
| `LaunchMinimized` | 最小化（トレイのみ）で起動 | `true` |
| `ShowNotifications` | 通知を表示 | `true` |
| `Exclusions` | 除外するプロセス名の配列 | `[]` |
| `UseElevatedStartup` | 管理者モードで起動 | `false` |

壊れている場合は既定値で起動します。リセットは設定画面から、または
`config.json` を削除して再起動してください。

---

## ログ

場所: `%APPDATA%\MemEco\logs\app.log`（約 1 MB でローテーション）

設定画面の「ログを開く」からも開けます。起動失敗・操作失敗の原因はここを確認してください。

---

## 困ったとき

| 症状 | 確認 |
|---|---|
| スタンバイ解放が押せない | 管理者モードか（フッター表示）、`schtasks /Query /TN "MemEco"` |
| 自動起動しない | タスクの有無、`%APPDATA%\MemEco\logs\app.log` |
| 二重起動した | 仕様です（2 個目は通知して終了） |
| 解放してもすぐ戻る | アプリが再びメモリを触っています。正常な挙動です |
| 設定を初期化したい | 設定画面のリセット、または `config.json` 削除 |
