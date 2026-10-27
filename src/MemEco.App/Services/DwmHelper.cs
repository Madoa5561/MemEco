using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MemEco.App.Services;

/// <summary>Windows 11 の DWM 属性（角丸・ダークモード・バックドロップ）を適用する。</summary>
public static class DwmHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    private const int CornerRound = 2;
    private const int BackdropTransientWindow = 3; // Acrylic
    private const int BackdropMainWindow = 2;      // Mica

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>DWM が使えない環境では失敗するだけで、自前の背景描画で見た目は成立する。</summary>
    public static void ApplyModernWindow(Window window)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            int dark = 1;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            int round = CornerRound;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref round, sizeof(int));

            int backdrop = BackdropTransientWindow;
            if (DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) != 0)
            {
                backdrop = BackdropMainWindow;
                DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
            }
        }
        catch
        {
            // DWM 非対応環境では何もしない
        }
    }
}
