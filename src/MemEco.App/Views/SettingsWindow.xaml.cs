using System.Windows;
using System.Windows.Input;
using MemEco.App.Services;

namespace MemEco.App.Views;

/// <summary>設定ウィンドウ（モーダル風）。</summary>
public partial class SettingsWindow : Window
{
    private readonly App _app;

    public SettingsWindow(App app)
    {
        InitializeComponent();
        _app = app;
        SourceInitialized += (_, _) => DwmHelper.ApplyModernWindow(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // ignore
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
