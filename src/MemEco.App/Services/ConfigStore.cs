using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MemEco.Core.Models;

namespace MemEco.App.Services;

/// <summary>%APPDATA%\MemEco\config.json の読み書き。壊れていたら既定値。</summary>
public sealed class ConfigStore
{
    private readonly AppLog _log;

    public ConfigStore(AppLog log) => _log = log;

    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MemEco");

    public static string ConfigPath => Path.Combine(DirectoryPath, "config.json");

    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return new AppConfig();
            }

            string json = File.ReadAllText(ConfigPath);
            AppConfig? config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (config == null)
            {
                _log.Warn("config.json が空でした。既定値を使います。");
                return new AppConfig();
            }

            Normalize(config);
            _log.Info($"設定を読み込みました: {ConfigPath}");
            return config;
        }
        catch (Exception ex)
        {
            _log.Error("設定の読み込みに失敗しました。既定値を使います。", ex);
            TryBackupBrokenFile();
            return new AppConfig();
        }
    }

    public bool Save(AppConfig config)
    {
        try
        {
            Normalize(config);
            Directory.CreateDirectory(DirectoryPath);
            string json = JsonSerializer.Serialize(config, JsonOptions);
            string temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, json, new System.Text.UTF8Encoding(false));
            File.Move(temp, ConfigPath, true);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("設定の保存に失敗しました。", ex);
            return false;
        }
    }

    /// <summary>範囲外の値を安全な範囲に丸める。</summary>
    public static void Normalize(AppConfig config)
    {
        config.Exclusions ??= new List<string>();
        config.Exclusions.RemoveAll(string.IsNullOrWhiteSpace);

        config.WarningAvailablePercent = Math.Clamp(config.WarningAvailablePercent, 5, 60);
        config.CriticalAvailablePercent = Math.Clamp(config.CriticalAvailablePercent, 1, config.WarningAvailablePercent - 1);
        config.SampleIntervalMs = Math.Clamp(config.SampleIntervalMs, 1000, 10000);
        config.EcoIdleMinutes = Math.Clamp(config.EcoIdleMinutes, 1, 120);
        config.SuspendIdleMinutes = Math.Clamp(config.SuspendIdleMinutes, 1, 240);
        config.MaxSuspendMinutes = Math.Clamp(config.MaxSuspendMinutes, 1, 240);
        config.PurgeCooldownSeconds = Math.Clamp(config.PurgeCooldownSeconds, 10, 3600);
        config.MinProcessMemoryMb = Math.Clamp(config.MinProcessMemoryMb, 16, 4096);

        if (!Enum.IsDefined(config.Preset))
        {
            config.Preset = AutoPreset.Balanced;
        }
    }

    private void TryBackupBrokenFile()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                File.Copy(ConfigPath, ConfigPath + ".broken", true);
                _log.Warn($"壊れた設定を退避しました: {ConfigPath}.broken");
            }
        }
        catch
        {
            // ignore
        }
    }
}

/// <summary>%APPDATA%\MemEco\state.json（初回起動フラグなど UI 側の状態）。</summary>
public sealed class AppState
{
    public bool FirstRunShown { get; set; }

    [JsonIgnore]
    public static string StatePath => Path.Combine(ConfigStore.DirectoryPath, "state.json");

    public static AppState Load()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return new AppState();
            }

            string json = File.ReadAllText(StatePath);
            return JsonSerializer.Deserialize<AppState>(json, ConfigStore.JsonOptions) ?? new AppState();
        }
        catch
        {
            return new AppState();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.DirectoryPath);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(this, ConfigStore.JsonOptions),
                new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // ignore
        }
    }
}
