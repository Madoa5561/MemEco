using System.IO;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using System.ComponentModel;

namespace MemEco.App.Services;

/// <summary>スタートアップ登録（HKCU\Run）と管理者モード登録（タスクスケジューラ）。</summary>
public static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "MemEco";
    private const string TaskName = "MemEco";

    public static string ExecutablePath
    {
        get
        {
            try
            {
                string? path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    return path;
                }
                return Process.GetCurrentProcess().MainModule?.FileName ?? "MemEco.exe";
            }
            catch
            {
                return "MemEco.exe";
            }
        }
    }

    public static bool IsAdmin
    {
        get
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    // ---------- HKCU\Run ----------

    public static bool IsRunEntryEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(RunValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static (bool Ok, string Message) SetRunEntry(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (key == null)
            {
                return (false, "レジストリを開けませんでした。");
            }

            if (enabled)
            {
                key.SetValue(RunValueName, $"\"{ExecutablePath}\" --autostart", RegistryValueKind.String);
                return (true, "Windows と同時に起動するように設定しました。");
            }

            if (key.GetValue(RunValueName) != null)
            {
                key.DeleteValue(RunValueName, false);
            }
            return (true, "Windows と同時に起動する設定を解除しました。");
        }
        catch (Exception ex)
        {
            return (false, $"スタートアップ設定を変更できませんでした: {ex.Message}");
        }
    }

    // ---------- タスクスケジューラ（管理者モード） ----------

    public static bool IsTaskRegistered()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks", $"/Query /TN \"{TaskName}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using Process? process = Process.Start(psi);
            if (process == null)
            {
                return false;
            }
            process.WaitForExit(5000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>UAC を 1 回だけ出して「ログオン時に管理者で起動」を登録する。</summary>
    public static (bool Ok, bool Canceled, string Message) RegisterElevatedTask()
    {
        string arguments =
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{ExecutablePath}\\\" --autostart\" /SC ONLOGON /RL HIGHEST /F";
        return RunElevated(arguments, register: true);
    }

    public static (bool Ok, bool Canceled, string Message) UnregisterElevatedTask()
        => RunElevated($"/Delete /TN \"{TaskName}\" /F", register: false);

    private static (bool Ok, bool Canceled, string Message) RunElevated(string arguments, bool register)
    {
        var psi = new ProcessStartInfo("schtasks", arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false,
        };

        try
        {
            using Process? process = Process.Start(psi);
            if (process == null)
            {
                return (false, false, "タスクスケジューラを起動できませんでした。");
            }

            if (!process.WaitForExit(30000))
            {
                return (false, false, "タスクの登録がタイムアウトしました。");
            }

            if (process.ExitCode == 0)
            {
                return (true, false, register
                    ? "管理者モードでの自動起動を登録しました。"
                    : "管理者モードでの自動起動を解除しました。");
            }

            return (false, false, $"タスクの登録に失敗しました (code {process.ExitCode})。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, true, "UAC がキャンセルされました。");
        }
        catch (Exception ex)
        {
            return (false, false, $"タスクの登録に失敗しました: {ex.Message}");
        }
    }
}
