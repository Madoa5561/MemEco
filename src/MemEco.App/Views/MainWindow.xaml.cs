using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using MemEco.App.Services;
using MemEco.App.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Rect = System.Windows.Rect;

namespace MemEco.App.Views;

/// <summary>メインパネルのホストウィンドウ（枠なし・画面右下）。</summary>
public partial class MainWindow : Window
{
    private readonly App _app;

    public MainWindow(App app, PanelViewModel viewModel)
    {
        InitializeComponent();
        _app = app;
        DataContext = viewModel;
        SourceInitialized += (_, _) =>
        {
            DwmHelper.ApplyModernWindow(this);
            PositionNearTray();
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>×/Esc で破棄してよいか（App が HidePanel のときだけ true）。</summary>
    public bool AllowClose { get; set; }

    private void PositionNearTray()
    {
        Rect area = SystemParameters.WorkArea;
        double left = area.Right - Width - 12;
        double top = area.Bottom - Height - 12;
        Left = Math.Max(area.Left + 8, left);
        Top = Math.Max(area.Top + 8, top);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _app.HidePanel();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            _app.HidePanel();
            return;
        }

        base.OnClosing(e);
    }
}
