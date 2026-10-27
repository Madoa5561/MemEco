using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MemEco.App.ViewModels;
using MemEco.Core.Models;
using Application = System.Windows.Application;
using RadioButton = System.Windows.Controls.RadioButton;
using Rect = System.Windows.Rect;
using UserControl = System.Windows.Controls.UserControl;

namespace MemEco.App.Views;

/// <summary>メインパネル本体（440x680）。プレビューではウィンドウなしで単体レンダリングする。</summary>
public partial class MainPanel : UserControl
{
    public MainPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyRoundedClip();
        SizeChanged += (_, _) => ApplyRoundedClip();
    }

    private static App? Host => Application.Current as App;

    private void ApplyRoundedClip()
    {
        if (RootBorder.ActualWidth <= 1 || RootBorder.ActualHeight <= 1)
        {
            return;
        }

        RootBorder.Clip = new RectangleGeometry(
            new Rect(0, 0, RootBorder.ActualWidth, RootBorder.ActualHeight), 18, 18);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        Window? window = Window.GetWindow(this);
        if (window == null)
        {
            return;
        }

        try
        {
            window.DragMove();
        }
        catch
        {
            // ドラッグできない状況は無視
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => Host?.OpenSettings();

    private void Close_Click(object sender, RoutedEventArgs e) => Host?.HidePanel();

    private void Reclaim_Click(object sender, RoutedEventArgs e) => Host?.ReclaimNowFromUi();

    private void Purge_Click(object sender, RoutedEventArgs e) => Host?.PurgeStandbyFromUi();

    private void Preset_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton radio || radio.Tag is not string tag)
        {
            return;
        }

        if (!Enum.TryParse(tag, ignoreCase: true, out AutoPreset preset))
        {
            return;
        }

        (DataContext as PanelViewModel)?.SetPresetFromUser(preset);
    }
}
