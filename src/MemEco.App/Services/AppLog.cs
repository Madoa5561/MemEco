using System.IO;
using System.Text;

namespace MemEco.App.Services;

/// <summary>%APPDATA%\MemEco\logs\app.log への簡易ロガー（1MB でローテーション）。</summary>
public sealed class AppLog
{
    private const long MaxBytes = 1024 * 1024;

    private readonly string? _path;
    private readonly object _gate = new();

    private AppLog(string? path) => _path = path;

    /// <summary>ファイルに書かないロガー（プレビュー等の単発モード用）。</summary>
    public static AppLog Disabled { get; } = new(null);

    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MemEco", "logs");

    public static string FilePath => Path.Combine(DirectoryPath, "app.log");

    public static AppLog Create(string? path = null) => new(path ?? FilePath);

    public string? Path_ => _path;

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex}");

    private void Write(string level, string message)
    {
        if (_path == null)
        {
            return;
        }

        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                RollIfNeeded();

                File.AppendAllText(_path, line, new UTF8Encoding(false));
            }
            catch
            {
                // ログ失敗でアプリは止めない
            }
        }
    }

    private void RollIfNeeded()
    {
        if (_path == null || !File.Exists(_path))
        {
            return;
        }

        var info = new FileInfo(_path);
        if (info.Length < MaxBytes)
        {
            return;
        }

        string old = _path + ".1";
        try
        {
            if (File.Exists(old))
            {
                File.Delete(old);
            }
            File.Move(_path, old);
        }
        catch
        {
            try
            {
                File.WriteAllText(_path, string.Empty);
            }
            catch
            {
                // ignore
            }
        }
    }
}
