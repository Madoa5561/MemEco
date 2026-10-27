using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using MemEco.App.Diagnostics;
using MemEco.App.Presentation;
using MemEco.App.Services;
using MemEco.App.Tray;
using MemEco.App.ViewModels;
using MemEco.App.Views;
using MemEco.Core;
using MemEco.Core.Diagnostics;
using MemEco.Core.Models;
using Application = System.Windows.Application;

namespace MemEco.App;

/// <summary>
/// 常駐アプリの本体。CLI 解釈・二重起動防止・エンジン接続・トレイ・パネル・設定を所有する。
/// </summary>
public partial class App : Application
{
    private SingleInstanceGuard? _guard;
    private AppLog _log = AppLog.Disabled;
    private ConfigStore? _configStore;
    private AppConfig _config = new();
    private AppState _state = new();
    private IEcoEngine? _engine;
    private TrayService? _tray;
    private MainWindow? _panel;
    private SettingsWindow? _settings;
    private PanelViewModel _vm = new();
    private bool _exiting;
    private bool _configDirty;

    internal event Action? ConfigChanged;

    internal AppConfig Config => _config;

    internal PanelViewModel ViewModel => _vm;

    internal bool IsSettingsOpen => _settings != null;

    internal string VersionString => "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        CliArgs cli = CliArgs.Parse(e.Args);
        switch (cli.Mode)
        {
            case CliMode.SelfTest:
                StartOneShot(() => RunSelfTestAsync(cli.OutputPath!), cli.OutputPath);
                break;

            case CliMode.Preview:
                StartOneShot(() => RunPreviewAsync(cli.OutputPath!), cli.OutputPath);
                break;

            case CliMode.Reclaim:
                StartOneShot(() => RunReclaimAsync(cli.OutputPath!), cli.OutputPath);
                break;

            case CliMode.SettingsPreview:
                StartOneShot(() => RunSettingsPreviewAsync(cli.OutputPath!), cli.OutputPath);
                break;

            default:
                StartResident(cli);
                break;
        }
    }

    // =====================================================================
    // 単発モード（--selftest / --preview / --reclaim）
    // =====================================================================

    private void StartOneShot(Func<Task<int>> work, string? outputPath)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            int code = 2;
            try
            {
                code = await work();
            }
            catch (Exception ex)
            {
                TryWriteErrorFile(outputPath, ex);
                code = 2;
            }

            Shutdown(code);
        }), DispatcherPriority.Normal);
    }

    /// <summary>自己診断。実プロセスを操作しないよう、自動管理を全て OFF にした設定を渡す。</summary>
    private async Task<int> RunSelfTestAsync(string path)
    {
        var config = new AppConfig
        {
            AutoManage = false,
            EnableEcoMode = false,
            EnableWorkingSetTrim = false,
            EnableSuspend = false,
            EnableStandbyPurge = false,
            ShowNotifications = false,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string report;
        try
        {
            report = await CoreSelfTest.RunAsync(config, cts.Token);
        }
        catch (Exception ex)
        {
            WriteTextFile(path, $"MemEco セルフテストが失敗しました{Environment.NewLine}{ex}{Environment.NewLine}");
            return 1;
        }

        WriteTextFile(path, report);
        return 0;
    }

    /// <summary>パネルを PNG に出力。プロセス操作を起こさないようエンジンは一時停止で回す。</summary>
    private async Task<int> RunPreviewAsync(string path)
    {
        var config = new AppConfig
        {
            AutoManage = false,
            EnableEcoMode = false,
            EnableWorkingSetTrim = false,
            EnableSuspend = false,
            EnableStandbyPurge = false,
            ShowNotifications = false,
        };

        var viewModel = new PanelViewModel();
        viewModel.SetVersion(VersionString);
        // 表示は既定の見た目（全自動 ON）。エンジン側は全機能 OFF・一時停止で動かすため実操作は行わない。
        viewModel.SetConfigState(new AppConfig());

        IEcoEngine? engine = null;
        try
        {
            engine = EcoEngineFactory.Create(config.Clone(), _ => { });
            engine.IsPaused = true;
            await engine.StartAsync();
            engine.IsPaused = true;

            viewModel.SetAdmin(engine.IsAdmin);

            var probe = new MemoryProbe();
            DateTime deadline = DateTime.UtcNow.AddSeconds(4);
            DateTime lastStats = DateTime.MinValue;
            int samples = 0;

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    MemorySnapshot sample = probe.Sample();
                    if (sample.TotalPhysBytes > 0)
                    {
                        viewModel.ApplyMemory(sample);
                        samples++;
                    }
                }
                catch
                {
                    // サンプリング失敗は無視
                }

                try
                {
                    EngineSnapshot latest = engine.Latest;
                    if (latest.Memory.TotalPhysBytes > 0 && latest.Timestamp > lastStats)
                    {
                        viewModel.ApplySnapshotStats(latest);
                        lastStats = latest.Timestamp;
                    }
                }
                catch
                {
                    // ignore
                }

                if (samples >= 60 && lastStats != DateTime.MinValue)
                {
                    break;
                }

                await Task.Delay(60);
            }

            // リングの 250ms アニメーションを落ち着かせてから撮影する
            await Task.Delay(300);

            PreviewRenderer.RenderPanel(viewModel, path);
            return 0;
        }
        finally
        {
            if (engine != null)
            {
                try
                {
                    Task stop = engine.StopAsync();
                    await Task.WhenAny(stop, Task.Delay(2000));
                }
                catch
                {
                    // ignore
                }

                try
                {
                    await engine.DisposeAsync();
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    /// <summary>今すぐ解放を 1 回だけ実行し、結果を JSON で書き出す（検証用）。</summary>
    private async Task<int> RunReclaimAsync(string path)
    {
        var config = new AppConfig
        {
            AutoManage = false,
            EnableEcoMode = false,
            EnableWorkingSetTrim = false,
            EnableSuspend = false,
            EnableStandbyPurge = false,
            ShowNotifications = false,
        };

        IEcoEngine? engine = null;
        ReclaimResult result;
        try
        {
            engine = EcoEngineFactory.Create(config.Clone(), _ => { });
            engine.IsPaused = false;
            await engine.StartAsync();
            await Task.Delay(1000);
            result = await engine.ReclaimNowAsync(ReclaimMode.Full);
        }
        catch (Exception ex)
        {
            result = ReclaimResult.Failed(ex.Message);
        }
        finally
        {
            if (engine != null)
            {
                try
                {
                    await engine.StopAsync();
                }
                catch
                {
                    // ignore
                }

                try
                {
                    await engine.DisposeAsync();
                }
                catch
                {
                    // ignore
                }
            }
        }

        var payload = new
        {
            success = result.Success,
            freedBytes = result.FreedBytes,
            freedMb = Math.Round(result.FreedBytes / 1048576d, 1),
            trimmedProcesses = result.TrimmedProcesses,
            standbyPurged = result.StandbyPurged,
            message = result.Message,
            isAdmin = EcoEngine.IsRunningElevated(),
            utc = DateTime.UtcNow.ToString("o"),
        };

        WriteTextFile(path, JsonSerializer.Serialize(payload, ConfigStore.JsonOptions));
        return result.Success ? 0 : 1;
    }

    /// <summary>設定画面を PNG に出力（開発用の確認モード）。設定・レジストリは変更しない。</summary>
    private async Task<int> RunSettingsPreviewAsync(string path)
    {
        await Task.Delay(120);
        PreviewRenderer.RenderSettings(path);
        return 0;
    }

    // =====================================================================
    // 常駐モード
    // =====================================================================
    private void StartResident(CliArgs cli)
    {
        _log = AppLog.Create();
        _log.Info($"MemEco 起動 mode={cli.Mode} hidden={cli.StartHidden}");

        _guard = new SingleInstanceGuard();
        if (!_guard.IsFirstInstance)
        {
            _log.Info("既存インスタンスを検出しました。パネル表示を通知して終了します。");
            _guard.SignalShow();
            _guard.Dispose();
            _guard = null;
            Shutdown(0);
            return;
        }

        _configStore = new ConfigStore(_log);
        bool configExists = File.Exists(ConfigStore.ConfigPath);
        _config = _configStore.Load();
        if (!configExists)
        {
            // 検証項目: 起動時に config.json が存在すること
            _configStore.Save(_config);
            _log.Info($"既定の設定ファイルを作成しました: {ConfigStore.ConfigPath}");
        }

        _state = AppState.Load();

        _vm = new PanelViewModel();
        _vm.SetVersion(VersionString);
        _vm.SetConfigState(_config);
        _vm.SetAdmin(EcoEngine.IsRunningElevated());
        _vm.AutoManageChanged += OnAutoManageChanged;
        _vm.PresetChanged += OnPresetChanged;

        _engine = EcoEngineFactory.Create(_config.Clone(), OnEngineActivityLog);
        _engine.SnapshotReady += OnEngineSnapshot;
        _engine.Activity += OnEngineActivity;
        _engine.IsPaused = false;

        _tray = new TrayService(this, _vm);
        _tray.Update(_engine.Latest, _config);

        _guard.Listen(() => Dispatcher.BeginInvoke(new Action(ShowPanel), DispatcherPriority.Normal));

        bool firstRun = !_state.FirstRunShown;
        bool shouldShow = cli.Mode == CliMode.Show || (firstRun && !cli.StartHidden);
        if (firstRun)
        {
            _state.FirstRunShown = true;
            _state.Save();
        }

        _ = InitializeResidentAsync(shouldShow);
    }

    private async Task InitializeResidentAsync(bool showPanel)
    {
        try
        {
            if (_engine != null)
            {
                await _engine.StartAsync();
            }
        }
        catch (Exception ex)
        {
            _log.Error("エンジンの開始に失敗しました", ex);
        }

        try
        {
            _vm.SetAdmin(_engine?.IsAdmin ?? EcoEngine.IsRunningElevated());
            _vm.ApplySnapshot(_engine?.Latest ?? new EngineSnapshot());
            _tray?.Update(_engine?.Latest, _config);

            if (showPanel)
            {
                // 最初のスナップショットが来るのを少しだけ待ってから表示する（空表示のちらつき防止）
                DateTime until = DateTime.UtcNow.AddSeconds(2.5);
                while (DateTime.UtcNow < until && (_engine?.Latest.Memory.TotalPhysBytes ?? 0) == 0)
                {
                    await Task.Delay(80);
                }

                _vm.ApplySnapshot(_engine?.Latest ?? new EngineSnapshot());
                ShowPanel();
            }
        }
        catch (Exception ex)
        {
            _log.Error("初期化に失敗しました", ex);
            if (showPanel)
            {
                ShowPanel();
            }
        }
    }

    private void OnEngineSnapshot(object? sender, EngineSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _vm.ApplySnapshot(snapshot);
                _tray?.Update(snapshot, _config);
            }
            catch (Exception ex)
            {
                _log.Error("スナップショットの反映に失敗しました", ex);
            }
        }), DispatcherPriority.Background);
    }

    private void OnEngineActivity(object? sender, string message)
    {
        Dispatcher.BeginInvoke(new Action(() => _vm.SetActivity(message)), DispatcherPriority.Background);
    }

    private void OnEngineActivityLog(string message) => _log.Info($"[activity] {message}");

    // =====================================================================
    // パネル / 設定
    // =====================================================================

    internal void ShowPanel()
    {
        if (_exiting)
        {
            return;
        }

        if (_panel != null)
        {
            _panel.Activate();
            return;
        }

        try
        {
            var panel = new MainWindow(this, _vm);
            _panel = panel;
            panel.Closed += (_, _) =>
            {
                if (ReferenceEquals(_panel, panel))
                {
                    _panel = null;
                }
            };
            panel.Show();
            panel.Activate();
            _log.Info("パネルを表示しました。");
        }
        catch (Exception ex)
        {
            _log.Error("パネルの表示に失敗しました", ex);
            _panel = null;
        }
    }

    internal void HidePanel()
    {
        MainWindow? panel = _panel;
        if (panel == null)
        {
            return;
        }

        _panel = null;
        panel.AllowClose = true;
        try
        {
            panel.Close();
        }
        catch (Exception ex)
        {
            _log.Error("パネルを閉じられませんでした", ex);
        }

        _log.Info("パネルをトレイに格納しました。");
    }

    internal void TogglePanelFromTray()
    {
        if (_panel != null)
        {
            HidePanel();
        }
        else
        {
            ShowPanel();
        }
    }

    internal void OpenSettings()
    {
        if (_settings != null)
        {
            _settings.Activate();
            return;
        }

        try
        {
            var window = new SettingsWindow(this);
            _settings = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_settings, window))
                {
                    _settings = null;
                }
            };
            if (_panel != null)
            {
                window.Owner = _panel;
            }
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            _log.Error("設定画面を開けませんでした", ex);
            _settings = null;
        }
    }

    /// <summary>設定画面からの変更を保存し、エンジンへ反映する。</summary>
    internal void ApplyConfigFromSettings() => SaveConfigFromSettings(announce: true);

    internal void SaveConfigFromSettings(bool announce)
    {
        SaveConfig();
        if (announce)
        {
            _vm.ShowNotice("設定を保存しました");
        }
    }

    /// <summary>設定を既定値へ戻す（設定画面のリセット操作）。</summary>
    internal void ResetConfig()
    {
        _config = new AppConfig();
        SaveConfig();
        _vm.SetConfigState(_config);
        _log.Info("設定をリセットしました。");
    }

    private void SaveConfig()
    {
        if (_configStore == null)
        {
            return;
        }

        if (_configStore.Save(_config))
        {
            _configDirty = false;
            _engine?.ApplyConfig(_config.Clone());
            _vm.SetConfigState(_config);
            _tray?.Update(_engine?.Latest, _config);
            ConfigChanged?.Invoke();
        }
    }

    private void OnAutoManageChanged(bool enabled)
    {
        _config.AutoManage = enabled;
        SaveConfig();
        _vm.ShowNotice(enabled ? "全自動メモリ管理を ON にしました" : "全自動メモリ管理を OFF にしました");
        _log.Info($"全自動管理: {(enabled ? "ON" : "OFF")}");
    }

    private void OnPresetChanged(AutoPreset preset)
    {
        _config.ApplyPreset(preset);
        SaveConfig();
        _vm.ShowNotice($"プリセットを「{PresetName(preset)}」に変更しました");
        _log.Info($"プリセット: {preset}");
    }

    internal static string PresetName(AutoPreset preset) => preset switch
    {
        AutoPreset.Gentle => "やさしい",
        AutoPreset.Aggressive => "積極的",
        _ => "標準",
    };

    internal void ToggleAutoManageFromTray() => _vm.AutoManage = !_vm.AutoManage;

    internal void ReclaimNowFromUi() => _ = ReclaimAsync(ReclaimMode.Auto);

    internal void PurgeStandbyFromUi() => _ = PurgeStandbyFromUiAsync();

    private async Task PurgeStandbyFromUiAsync()
    {
        if (_engine == null || _vm.IsBusy)
        {
            return;
        }

        _vm.SetBusy(true);
        _vm.ShowNotice("スタンバイリストを解放しています…");
        try
        {
            ReclaimResult result = await _engine.PurgeStandbyAsync();
            _vm.ShowNotice(DescribeResult(result));
        }
        catch (Exception ex)
        {
            _log.Error("スタンバイ解放に失敗しました", ex);
            _vm.ShowNotice("スタンバイ解放に失敗しました");
        }
        finally
        {
            _vm.SetBusy(false);
        }
    }

    internal async Task ReclaimAsync(ReclaimMode mode)
    {
        if (_engine == null || _vm.IsBusy)
        {
            return;
        }

        _vm.SetBusy(true);
        _vm.ShowNotice(mode == ReclaimMode.Full ? "メモリを解放しています…" : "メモリを解放しています…");
        try
        {
            ReclaimResult result = await _engine.ReclaimNowAsync(mode);
            _vm.ShowNotice(DescribeResult(result));
            _log.Info($"手動解放: success={result.Success} freed={result.FreedBytes} msg={result.Message}");
        }
        catch (Exception ex)
        {
            _log.Error("手動解放に失敗しました", ex);
            _vm.ShowNotice("解放処理でエラーが発生しました");
        }
        finally
        {
            _vm.SetBusy(false);
        }
    }

    /// <summary>結果はエンジンの戻り値をそのまま表示する（見かけの数字を作らない）。</summary>
    private static string DescribeResult(ReclaimResult result)
    {
        if (!result.Success)
        {
            return string.IsNullOrWhiteSpace(result.Message)
                ? "解放できませんでした"
                : $"解放できませんでした: {result.Message}";
        }

        if (result.FreedBytes == 0)
        {
            return "解放できるメモリがありませんでした";
        }

        return string.IsNullOrWhiteSpace(result.Message)
            ? $"{Format.Freed(result.FreedBytes)} を解放しました"
            : result.Message;
    }

    // =====================================================================
    // 終了 / 例外
    // =====================================================================

    internal async void ExitApp()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        _log.Info("終了要求を受け取りました。");

        try
        {
            _tray?.Dispose();
            _tray = null;
        }
        catch
        {
            // ignore
        }

        HidePanel();

        if (_settings != null)
        {
            try
            {
                _settings.Close();
            }
            catch
            {
                // ignore
            }
            _settings = null;
        }

        IEcoEngine? engine = _engine;
        _engine = null;
        if (engine != null)
        {
            try
            {
                Task stop = engine.StopAsync();
                await Task.WhenAny(stop, Task.Delay(2000));
            }
            catch (Exception ex)
            {
                _log.Error("エンジン停止に失敗しました", ex);
            }

            try
            {
                await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // ignore
            }
        }

        if (_configDirty)
        {
            _configStore?.Save(_config);
        }

        _guard?.Dispose();
        _guard = null;
        _log.Info("MemEco を終了します。");
        Shutdown(0);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log.Error("UI スレッドで未処理の例外が発生しました", e.Exception);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _log.Error("未処理の例外（AppDomain）", e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log.Error("未監視の Task 例外", e.Exception);
        e.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _tray?.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            _guard?.Dispose();
        }
        catch
        {
            // ignore
        }

        base.OnExit(e);
    }

    // =====================================================================
    // ファイル出力ヘルパー
    // =====================================================================

    private static void WriteTextFile(string path, string text)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static void TryWriteErrorFile(string? outputPath, Exception ex)
    {
        if (string.IsNullOrEmpty(outputPath))
        {
            return;
        }

        try
        {
            WriteTextFile(outputPath + ".error.txt", ex.ToString());
        }
        catch
        {
            // ignore
        }
    }
}
