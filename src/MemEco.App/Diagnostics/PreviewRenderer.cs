using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MemEco.App.ViewModels;
using Color = System.Windows.Media.Color;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace MemEco.App.Diagnostics;

/// <summary>
/// --preview 用のオフスクリーン描画。ウィンドウや DPI に依存せず、常に 440x680 ピクセルで出力する。
/// </summary>
public static class PreviewRenderer
{
    public const int PanelWidth = 440;
    public const int PanelHeight = 680;

    public static void RenderPanel(PanelViewModel viewModel, string path)
    {
        var root = new Grid
        {
            Width = PanelWidth,
            Height = PanelHeight,
            Background = new SolidColorBrush(Color.FromRgb(0x07, 0x0C, 0x15)),
        };

        var panel = new Views.MainPanel
        {
            Width = PanelWidth,
            Height = PanelHeight,
            DataContext = viewModel,
        };

        root.Children.Add(panel);
        RenderVisual(root, path, PanelWidth, PanelHeight);
    }

    /// <summary>設定画面のプレビュー（720x640）。</summary>
    public static void RenderSettings(string path)
    {
        var root = new Grid
        {
            Width = 720,
            Height = 640,
            Background = new SolidColorBrush(Color.FromRgb(0x07, 0x0C, 0x15)),
        };

        root.Children.Add(new Views.SettingsPanel { Width = 720, Height = 640 });
        RenderVisual(root, path, 720, 640);
    }

    /// <summary>指定サイズでレイアウトして PNG に書き出す（96 DPI 固定）。</summary>
    public static void RenderVisual(Visual visual, string path, int width, int height)
    {
        var element = visual as FrameworkElement;
        if (element != null)
        {
            element.Measure(new Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
