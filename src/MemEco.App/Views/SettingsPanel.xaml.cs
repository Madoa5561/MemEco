using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MemEco.App.Services;
using MemEco.Core.Models;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace MemEco.App.Views;

/// <summary>設定画面（720x640）。変更は即座に config.json へ保存し、エンジンへ反映する。</summary>
public partial class SettingsPanel : UserControl
{
    private sealed class Candidate
    {
        public string Name { get; init; } = "";
        public string Detail { get; init; } = "";
        public long Bytes { get; init; }
    }

    private readonly ObservableCollection<string> _exclusions = new();
    private readonly ObservableCollection<Candidate> _candidates = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _resetTimer;
    private readonly AppConfig _fallbackConfig = new();

    private bool _loading = true;
    private int _resetStage;

    public SettingsPanel()
    {
        InitializeComponent();
        ExclusionList.ItemsSource = _exclusions;
        CandidateList.ItemsSource = _candidates;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveConfig(announce: false);
        };

        _resetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _resetTimer.Tick += (_, _) =>
        {
            _resetTimer.Stop();
            _resetStage = 0;
            ResetHint.Text = "";
            ResetButton.Content = "設定をリセット";
        };

        Loaded += (_, _) => RefreshAll();
        Unloaded += (_, _) =>
        {
            _saveTimer.Stop();
            _resetTimer.Stop();
        };

        RefreshAll();
    }

    private static App? Host => Application.Current as App;

    private AppConfig Config => Host?.Config ?? _fallbackConfig;

    // =====================================================================
    // 表示の同期
    // =====================================================================

    private void RefreshAll()
    {
        _loading = true;
        try
        {
            AppConfig config = Config;
            VersionText.Text = Host?.VersionString ?? "v1.0.0";
            ConfigPathText.Text = ConfigStore.ConfigPath;
            LogPathText.Text = AppLog.FilePath;

            AutoManageToggle.IsChecked = config.AutoManage;
            PresetGentle.IsChecked = config.Preset == AutoPreset.Gentle;
            PresetBalanced.IsChecked = config.Preset == AutoPreset.Balanced;
            PresetAggressive.IsChecked = config.Preset == AutoPreset.Aggressive;

            WarningSlider.Value = config.WarningAvailablePercent;
            CriticalSlider.Value = config.CriticalAvailablePercent;
            UpdateThresholdLabels();

            EcoQosToggle.IsChecked = config.EnableEcoMode;
            TrimToggle.IsChecked = config.EnableWorkingSetTrim;
            StandbyToggle.IsChecked = config.EnableStandbyPurge;
            SuspendToggle.IsChecked = config.EnableSuspend;
            FullscreenToggle.IsChecked = config.PauseWhenFullscreen;

            bool admin = Host?.ViewModel.IsAdmin ?? AutostartService.IsAdmin;
            StandbyToggle.IsEnabled = admin;
            StandbyDesc.Text = admin
                ? "スタンバイメモリを解放します（管理者モード）"
                : "要 管理者モード（現在は標準モード）";

            StartWithWindowsToggle.IsChecked = AutostartService.IsRunEntryEnabled();
            ElevatedToggle.IsChecked = AutostartService.IsTaskRegistered();
            StartupStatus.Text = AutostartService.IsTaskRegistered()
                ? "管理者モードで自動起動する設定です（タスク: MemEco）"
                : "";

            _exclusions.Clear();
            foreach (string name in config.Exclusions
                         .Where(x => !string.IsNullOrWhiteSpace(x))
                         .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))
            {
                _exclusions.Add(name);
            }
            UpdateExclusionState();
            UpdateAutoNote();
        }
        finally
        {
            _loading = false;
        }
    }

    private void UpdateAutoNote()
    {
        AutoNote.Text = Config.AutoManage
            ? "空きメモリが少なくなると自動で解放します"
            : "OFF の間は監視のみ行い、メモリ操作はしません";
    }

    private void UpdateThresholdLabels()
    {
        // InitializeComponent 中（Min/Max 設定時）はまだ名前付き要素が無い
        if (WarningValueText == null || CriticalValueText == null || ThresholdHint == null
            || WarningSlider == null || CriticalSlider == null)
        {
            return;
        }

        WarningValueText.Text = $"{WarningSlider.Value:0}%";
        CriticalValueText.Text = $"{CriticalSlider.Value:0}%";
        bool invalid = CriticalSlider.Value >= WarningSlider.Value;
        ThresholdHint.Text = invalid
            ? "危険の値は警告より小さくしてください（自動で調整します）"
            : "危険の値は警告より小さくしてください";
        ThresholdHint.Foreground = invalid
            ? (System.Windows.Media.Brush)(Application.Current?.Resources["AmberBrush"] ?? System.Windows.Media.Brushes.Orange)
            : (System.Windows.Media.Brush)(Application.Current?.Resources["MutedTextBrush"] ?? System.Windows.Media.Brushes.Gray);
    }

    private void UpdateExclusionState()
    {
        ExclusionCountText.Text = $"{_exclusions.Count} 件";
        ExclusionEmpty.Visibility = _exclusions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveConfig(bool announce)
    {
        Host?.SaveConfigFromSettings(announce);
    }

    // =====================================================================
    // 自動管理 / プリセット / しきい値
    // =====================================================================

    private void AutoManage_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Config.AutoManage = AutoManageToggle.IsChecked == true;
        SaveConfig(announce: false);
        UpdateAutoNote();
    }

    private void Preset_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton radio || radio.Tag is not string tag)
        {
            return;
        }

        if (!Enum.TryParse(tag, ignoreCase: true, out AutoPreset preset))
        {
            return;
        }

        Config.ApplyPreset(preset);
        SaveConfig(announce: false);
        RefreshAll();
    }

    private void WarningSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateThresholdLabels();
        if (_loading)
        {
            return;
        }

        int warning = (int)Math.Round(WarningSlider.Value);
        if (Config.CriticalAvailablePercent >= warning)
        {
            Config.CriticalAvailablePercent = Math.Max(1, warning - 1);
            _loading = true;
            CriticalSlider.Value = Config.CriticalAvailablePercent;
            UpdateThresholdLabels();
            _loading = false;
        }

        Config.WarningAvailablePercent = warning;
        ScheduleSave();
    }

    private void CriticalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateThresholdLabels();
        if (_loading)
        {
            return;
        }

        int critical = (int)Math.Round(CriticalSlider.Value);
        int warning = (int)Math.Round(WarningSlider.Value);
        if (critical >= warning)
        {
            critical = Math.Max(1, warning - 1);
            _loading = true;
            CriticalSlider.Value = critical;
            UpdateThresholdLabels();
            _loading = false;
        }

        Config.CriticalAvailablePercent = critical;
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // =====================================================================
    // 機能スイッチ
    // =====================================================================

    private void Feature_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppConfig config = Config;
        config.EnableEcoMode = EcoQosToggle.IsChecked == true;
        config.EnableWorkingSetTrim = TrimToggle.IsChecked == true;
        config.EnableStandbyPurge = StandbyToggle.IsChecked == true;
        config.EnableSuspend = SuspendToggle.IsChecked == true;
        config.PauseWhenFullscreen = FullscreenToggle.IsChecked == true;
        SaveConfig(announce: false);
    }

    // =====================================================================
    // 除外アプリ
    // =====================================================================

    private void AddExclusion_Click(object sender, RoutedEventArgs e) => AddExclusion(ExclusionInput.Text);

    private void ExclusionInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        AddExclusion(ExclusionInput.Text);
    }

    private void AddExclusion(string? raw)
    {
        string name = (raw ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return;
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        AppConfig config = Config;
        if (config.Exclusions.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
        {
            StartupStatus.Text = $"「{name}」は既に除外されています。";
            return;
        }

        config.Exclusions.Add(name);
        _exclusions.Add(name);
        ExclusionInput.Text = string.Empty;
        UpdateExclusionState();
        SaveConfig(announce: false);
        StartupStatus.Text = $"「{name}」を除外に追加しました。";
    }

    private void RemoveExclusion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string name)
        {
            return;
        }

        Config.Exclusions.RemoveAll(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        _exclusions.Remove(name);
        UpdateExclusionState();
        SaveConfig(announce: false);
    }

    private void ToggleCandidates_Click(object sender, RoutedEventArgs e)
    {
        bool open = CandidatePanel.Visibility != Visibility.Visible;
        CandidatePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open && _candidates.Count == 0)
        {
            LoadCandidates();
        }
    }

    private void LoadCandidates()
    {
        _candidates.Clear();
        try
        {
            var items = new List<Candidate>();
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    string name = process.ProcessName;
                    if (string.IsNullOrWhiteSpace(name) || name.Equals("MemEco", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    long ws = process.WorkingSet64;
                    if (ws < 20L * 1024 * 1024)
                    {
                        continue;
                    }

                    items.Add(new Candidate
                    {
                        Name = name,
                        Detail = $"{ws / 1024d / 1024:F0} MB",
                        Bytes = ws,
                    });
                }
                catch
                {
                    // アクセスできないプロセスは無視
                }
                finally
                {
                    process.Dispose();
                }
            }

            foreach (Candidate candidate in items
                         .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                         .Select(g => g.First())
                         .OrderByDescending(x => x.Bytes)
                         .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                         .Take(40))
            {
                _candidates.Add(candidate);
            }
        }
        catch
        {
            // ignore
        }

        CandidateEmpty.Visibility = _candidates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name })
        {
            AddExclusion(name);
        }
    }

    // =====================================================================
    // 起動設定
    // =====================================================================

    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        bool enabled = StartWithWindowsToggle.IsChecked == true;
        (bool ok, string message) = AutostartService.SetRunEntry(enabled);
        StartupStatus.Text = message;
        if (!ok)
        {
            _loading = true;
            StartWithWindowsToggle.IsChecked = !enabled;
            _loading = false;
            return;
        }

        Config.StartWithWindows = enabled;
        SaveConfig(announce: false);
    }

    private void Elevated_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        bool enabled = ElevatedToggle.IsChecked == true;
        if (enabled)
        {
            (bool ok, bool canceled, string message) = AutostartService.RegisterElevatedTask();
            StartupStatus.Text = message;
            if (!ok)
            {
                _loading = true;
                ElevatedToggle.IsChecked = false;
                _loading = false;
                if (canceled)
                {
                    StartupStatus.Text = "UAC がキャンセルされました（管理者モードは登録されていません）";
                }
                return;
            }

            // 二重起動を避けるため、HKCU のスタートアップは解除する
            if (AutostartService.IsRunEntryEnabled())
            {
                AutostartService.SetRunEntry(false);
                _loading = true;
                StartWithWindowsToggle.IsChecked = false;
                _loading = false;
            }

            Config.UseElevatedStartup = true;
            Config.StartWithWindows = false;
            Config.LaunchMinimized = true;
            SaveConfig(announce: false);
        }
        else
        {
            (bool ok, bool canceled, string message) = AutostartService.UnregisterElevatedTask();
            StartupStatus.Text = message;
            if (!ok)
            {
                _loading = true;
                ElevatedToggle.IsChecked = true;
                _loading = false;
                if (canceled)
                {
                    StartupStatus.Text = "UAC がキャンセルされました（管理者モードのままです）";
                }
                return;
            }

            Config.UseElevatedStartup = false;
            SaveConfig(announce: false);
        }
    }

    // =====================================================================
    // 情報
    // =====================================================================

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(AppLog.DirectoryPath);

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(ConfigStore.DirectoryPath);

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // ignore
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_resetStage == 0)
        {
            _resetStage = 1;
            ResetHint.Text = "もう一度押すとすべての設定を初期状態に戻します";
            ResetButton.Content = "本当にリセット";
            _resetTimer.Stop();
            _resetTimer.Start();
            return;
        }

        _resetStage = 0;
        _resetTimer.Stop();
        ResetButton.Content = "設定をリセット";
        ResetHint.Text = "設定を初期状態に戻しました";
        Host?.ResetConfig();
        RefreshAll();
    }

    // =====================================================================
    // ウィンドウ操作
    // =====================================================================

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        try
        {
            Window.GetWindow(this)?.DragMove();
        }
        catch
        {
            // ignore
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
}
