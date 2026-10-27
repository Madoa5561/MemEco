using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MemEco.Core.Models;

namespace MemEco.App.Tray;

/// <summary>メモリチップ風のトレイアイコンを実行時に描画する。</summary>
internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static readonly Color Mint = Color.FromArgb(0x4A, 0xDE, 0x80);
    private static readonly Color Cyan = Color.FromArgb(0x22, 0xD3, 0xEE);
    private static readonly Color Amber = Color.FromArgb(0xFB, 0xBF, 0x24);
    private static readonly Color Red = Color.FromArgb(0xF8, 0x71, 0x71);
    private static readonly Color Ink = Color.FromArgb(0x06, 0x20, 0x18);
    private static readonly Color Dark = Color.FromArgb(0x0B, 0x11, 0x1C);

    public static Icon Create(MemoryPressureLevel level, int size)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            float s = size;
            float pad = s * 0.11f;
            var body = new RectangleF(pad, pad, s - pad * 2, s - pad * 2);
            using GraphicsPath bodyPath = RoundedRect(body, s * 0.24f);
            using (var gradient = new LinearGradientBrush(body, Mint, Cyan, 45f))
            {
                g.FillPath(gradient, bodyPath);
            }

            // チップの内部（暗い正方形）
            float inner = s * 0.30f;
            var innerRect = new RectangleF((s - inner) / 2, (s - inner) / 2, inner, inner);
            using (GraphicsPath innerPath = RoundedRect(innerRect, s * 0.07f))
            using (var ink = new SolidBrush(Ink))
            {
                g.FillPath(ink, innerPath);
            }

            // 圧迫度インジケーター
            float dot = s * 0.34f;
            float dx = s - dot - pad * 0.25f;
            float dy = s - dot - pad * 0.25f;
            var dotRect = new RectangleF(dx, dy, dot, dot);
            using (var dotBrush = new SolidBrush(LevelColor(level)))
            {
                g.FillEllipse(dotBrush, dotRect);
            }
            using (var ring = new Pen(Dark, Math.Max(1f, s * 0.045f)))
            {
                g.DrawEllipse(ring, dotRect);
            }
        }

        return FromBitmap(bitmap);
    }

    private static Color LevelColor(MemoryPressureLevel level) => level switch
    {
        MemoryPressureLevel.Warning => Amber,
        MemoryPressureLevel.Critical => Red,
        _ => Mint,
    };

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Icon FromBitmap(Bitmap bitmap)
    {
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
