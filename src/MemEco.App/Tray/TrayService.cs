using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MemEco.App.Presentation;
using MemEco.App.ViewModels;
using MemEco.Core.Models;
using Application = System.Windows.Application;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;
using WinForms = System.Windows.Forms;

namespace MemEco.App.Tray;

/// <summary>タスクトレイ常駐。左クリックでパネル、右クリックで WPF スタイルのメニュー。</summary>
public sealed class TrayService : IDisposable
{
    private readonly App _app;
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly DispatcherTimer _tooltipTimer;
    private readonly Dictionary<MemoryPressureLevel, Icon> _largeIcons = new();
    private readonly Dictionary<MemoryPressureLevel, Icon> _smallIcons = new();
    private readonly ContextMenu _menu;
    private readonly MenuItem _autoItem;
    private MemoryPressureLevel _level = MemoryPressureLevel.Normal;
    private EngineSnapshot? _snapshot;
    private bool _disposed;

    public TrayService(App app, PanelViewModel viewModel)
    {
        _app = app;

        _autoItem = CreateItem("全自動メモリ管理", () => _app.ToggleAutoManageFromTray(), checkable: true);
        _menu = new ContextMenu();
        _menu.Items.Add(CreateItem("パネルを開く", () => _app.ShowPanel()));
        _menu.Items.Add(CreateItem("今すぐ解放", () => _app.ReclaimNowFromUi()));
        _menu.Items.Add(CreateSeparator());
        _menu.Items.Add(_autoItem);
        _menu.Items.Add(CreateItem("設定…", () => _app.OpenSettings()));
        _menu.Items.Add(CreateSeparator());
        _menu.Items.Add(CreateItem("終了", () => _app.ExitApp()));

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = GetIcon(MemoryPressureLevel.Normal, large: true),
            Text = "MemEco — 計測中…",
            Visible = true,
        };
        _notifyIcon.MouseClick += OnMouseClick;

        _tooltipTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _tooltipTimer.Tick += (_, _) => UpdateTooltip();
        _tooltipTimer.Start();
    }

    private static MenuItem CreateItem(string header, Action action, bool checkable = false)
    {
        var item = new MenuItem { Header = header };
        if (Application.Current?.Resources["EcoMenuItem"] is Style style)
        {
            item.Style = style;
        }
        if (checkable)
        {
            item.IsCheckable = true;
        }
        item.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch
            {
                // メニュー操作の失敗でプロセスを落とさない
            }
        };
        return item;
    }

    private static Separator CreateSeparator()
    {
        var separator = new Separator();
        if (Application.Current?.Resources["EcoMenuSeparator"] is Style style)
        {
            separator.Style = style;
        }
        return separator;
    }

    private void OnMouseClick(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button == WinForms.MouseButtons.Left)
        {
            _app.TogglePanelFromTray();
        }
        else if (e.Button == WinForms.MouseButtons.Right)
        {
            ShowMenu();
        }
    }

    private void ShowMenu()
    {
        _menu.IsOpen = false;
        _autoItem.IsChecked = _app.Config.AutoManage;
        _autoItem.Header = _app.Config.AutoManage ? "全自動メモリ管理: ON" : "全自動メモリ管理: OFF";

        _app.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _menu.Placement = PlacementMode.MousePoint;
                _menu.IsOpen = true;
            }
            catch
            {
                // ignore
            }
        }), DispatcherPriority.Normal);
    }

    /// <summary>エンジン更新のたびに呼ぶ（アイコン色とツールチップを更新）。</summary>
    public void Update(EngineSnapshot? snapshot, AppConfig config)
    {
        if (_disposed)
        {
            return;
        }

        _snapshot = snapshot;
        if (snapshot != null && snapshot.Memory.TotalPhysBytes > 0)
        {
            MemoryPressureLevel level = snapshot.Memory.Pressure;
            if (level != _level)
            {
                _level = level;
                try
                {
                    _notifyIcon.Icon = GetIcon(level, large: true);
                }
                catch
                {
                    // ignore
                }
            }
        }

        UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            string text;
            EngineSnapshot? snapshot = _snapshot;
            if (snapshot != null && snapshot.Memory.TotalPhysBytes > 0)
            {
                string word = snapshot.Memory.Pressure switch
                {
                    MemoryPressureLevel.Warning => "注意",
                    MemoryPressureLevel.Critical => "危険",
                    _ => "余裕あり",
                };
                text = $"MemEco — 空き {Format.Gb(snapshot.Memory.AvailablePhysBytes)}・{word}";
            }
            else
            {
                text = "MemEco — 計測中…";
            }

            if (text.Length > 120)
            {
                text = text[..120];
            }

            _notifyIcon.Text = text;
        }
        catch
        {
            // ignore
        }
    }

    private Icon GetIcon(MemoryPressureLevel level, bool large)
    {
        Dictionary<MemoryPressureLevel, Icon> cache = large ? _largeIcons : _smallIcons;
        if (cache.TryGetValue(level, out Icon? icon) && icon != null)
        {
            return icon;
        }

        icon = TrayIconFactory.Create(level, large ? 32 : 16);
        cache[level] = icon;
        return icon;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            _tooltipTimer.Stop();
        }
        catch
        {
            // ignore
        }

        try
        {
            _menu.IsOpen = false;
        }
        catch
        {
            // ignore
        }

        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        catch
        {
            // ignore
        }

        foreach (Icon icon in _largeIcons.Values.Concat(_smallIcons.Values))
        {
            try
            {
                icon.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        _largeIcons.Clear();
        _smallIcons.Clear();
    }
}
