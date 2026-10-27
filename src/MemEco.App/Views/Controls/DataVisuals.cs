using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MemEco.Core.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace MemEco.App.Views.Controls;

/// <summary>図形描画で共通に使う色・幾何ヘルパー。</summary>
internal static class VisualHelpers
{
    public static readonly Color Mint = Color.FromRgb(0x4A, 0xDE, 0x80);
    public static readonly Color Cyan = Color.FromRgb(0x22, 0xD3, 0xEE);
    public static readonly Color Amber = Color.FromRgb(0xFB, 0xBF, 0x24);
    public static readonly Color Red = Color.FromRgb(0xF8, 0x71, 0x71);
    public static readonly Color Track = Color.FromRgb(0x1D, 0x29, 0x41);
    public static readonly Color CardBg = Color.FromRgb(0x14, 0x1E, 0x30);

    public static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    public static Color Lerp(Color a, Color b, double t)
    {
        t = Clamp01(t);
        return Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    public static Point PointOnCircle(Point center, double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }

    public static StreamGeometry BuildArc(Point center, double radius, double startDeg, double sweepDeg)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            Point start = PointOnCircle(center, radius, startDeg);
            Point end = PointOnCircle(center, radius, startDeg + sweepDeg);
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, Math.Abs(sweepDeg) > 180,
                SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    public static Brush MakeGradient(Color from, Color to, Point? start = null, Point? end = null)
    {
        var brush = new LinearGradientBrush(from, to, start ?? new Point(0, 0), end ?? new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    public static Pen MakePen(Brush brush, double thickness, bool round = true)
    {
        var pen = new Pen(brush, thickness);
        if (round)
        {
            pen.StartLineCap = PenLineCap.Round;
            pen.EndLineCap = PenLineCap.Round;
            pen.LineJoin = PenLineJoin.Round;
        }
        return pen;
    }
}

/// <summary>メモリプレッシャーの円弧リング。Score 変化は 250ms で滑らかに追従する。</summary>
public sealed class PressureRing : FrameworkElement
{
    public static readonly DependencyProperty ScoreProperty = DependencyProperty.Register(
        nameof(Score), typeof(double), typeof(PressureRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, OnScoreChanged));

    public static readonly DependencyProperty AnimatedScoreProperty = DependencyProperty.Register(
        nameof(AnimatedScore), typeof(double), typeof(PressureRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(MemoryPressureLevel), typeof(PressureRing),
        new FrameworkPropertyMetadata(MemoryPressureLevel.Normal, FrameworkPropertyMetadataOptions.AffectsRender));

    private bool _hasValue;

    public double Score
    {
        get => (double)GetValue(ScoreProperty);
        set => SetValue(ScoreProperty, value);
    }

    public double AnimatedScore
    {
        get => (double)GetValue(AnimatedScoreProperty);
        set => SetValue(AnimatedScoreProperty, value);
    }

    public MemoryPressureLevel Level
    {
        get => (MemoryPressureLevel)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    private static void OnScoreChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ring = (PressureRing)d;
        double target = (double)e.NewValue;

        // オフスクリーン描画（--preview）や非表示中はアニメーションせず即時反映する
        bool canAnimate = ring._hasValue && PresentationSource.FromVisual(ring) != null;
        if (!canAnimate)
        {
            ring._hasValue = true;
            ring.BeginAnimation(AnimatedScoreProperty, null);
            ring.AnimatedScore = target;
            return;
        }

        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        ring.BeginAnimation(AnimatedScoreProperty, animation);
    }

    private (Color From, Color To) LevelColors() => Level switch
    {
        MemoryPressureLevel.Warning => (Color.FromRgb(0xFB, 0xBF, 0x24), Color.FromRgb(0xF5, 0x9E, 0x0B)),
        MemoryPressureLevel.Critical => (Color.FromRgb(0xF8, 0x71, 0x71), Color.FromRgb(0xEF, 0x44, 0x44)),
        _ => (VisualHelpers.Mint, VisualHelpers.Cyan),
    };

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        double size = Math.Min(w, h);
        if (size <= 4 || double.IsNaN(size))
        {
            return;
        }

        var center = new Point(w / 2, h / 2);
        double thickness = Math.Max(7, size * 0.082);
        double radius = size / 2 - thickness / 2 - 1;

        (Color from, Color to) = LevelColors();

        // 内側の淡いグロー
        var glow = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(26, from.R, from.G, from.B), 0.55));
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, from.R, from.G, from.B), 1.0));
        glow.Freeze();
        dc.DrawEllipse(glow, null, center, radius + thickness * 0.6, radius + thickness * 0.6);

        // トラック
        var trackBrush = new SolidColorBrush(Color.FromArgb(255, 0x1B, 0x27, 0x3D));
        trackBrush.Freeze();
        dc.DrawEllipse(null, VisualHelpers.MakePen(trackBrush, thickness), center, radius, radius);

        double sweep = VisualHelpers.Clamp01(AnimatedScore / 100.0) * 359.9;
        if (sweep < 1.0)
        {
            return;
        }

        var gradient = VisualHelpers.MakeGradient(from, to, new Point(0, 0), new Point(1, 1));
        var arcPen = VisualHelpers.MakePen(gradient, thickness);
        StreamGeometry arc = VisualHelpers.BuildArc(center, radius, -90, sweep);
        dc.DrawGeometry(null, arcPen, arc);

        // 終端の光点
        Point tip = VisualHelpers.PointOnCircle(center, radius, -90 + sweep);
        var haloBrush = new SolidColorBrush(Color.FromArgb(70, to.R, to.G, to.B));
        haloBrush.Freeze();
        var dotBrush = new SolidColorBrush(to);
        dotBrush.Freeze();
        var dotPen = new Pen(new SolidColorBrush(Color.FromArgb(230, 0x0B, 0x11, 0x1C)), 1.2);
        dotPen.Freeze();
        dc.DrawEllipse(haloBrush, null, tip, thickness * 0.95, thickness * 0.95);
        dc.DrawEllipse(dotBrush, dotPen, tip, thickness * 0.38, thickness * 0.38);
    }
}

/// <summary>空きメモリ推移のスパークライン（60 点、グラデーション塗り）。</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(double[]), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[]? Points
    {
        get => (double[]?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 4 || h <= 4)
        {
            return;
        }

        double[]? points = Points;
        if (points == null || points.Length < 2)
        {
            var hintPen = new Pen(new SolidColorBrush(Color.FromArgb(120, 0x1D, 0x29, 0x41)), 1.2)
            {
                DashStyle = new DashStyle(new double[] { 3, 4 }, 0),
            };
            hintPen.Freeze();
            double y = h * 0.62;
            dc.DrawLine(hintPen, new Point(0, y), new Point(w, y));
            return;
        }

        double min = points[0];
        double max = points[0];
        foreach (double v in points)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }

        double range = max - min;
        if (range < Math.Max(0.0001, max * 0.0005))
        {
            range = Math.Max(0.02, max * 0.01);
            min -= range / 2;
        }
        else
        {
            double pad = range * 0.18;
            min -= pad;
            max += pad;
            range = max - min;
        }

        double topPad = 3;
        double bottomPad = 2;
        double usable = h - topPad - bottomPad;
        var pts = new Point[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            double x = points.Length == 1 ? w / 2 : w * i / (points.Length - 1.0);
            double norm = (points[i] - min) / range;
            double y = topPad + usable * (1 - VisualHelpers.Clamp01(norm));
            pts[i] = new Point(x, y);
        }

        // 塗りつぶし用（閉じた図形）
        var fillGeometry = new StreamGeometry();
        using (StreamGeometryContext ctx = fillGeometry.Open())
        {
            ctx.BeginFigure(new Point(pts[0].X, h), true, true);
            ctx.LineTo(pts[0], false, false);
            for (int i = 1; i < pts.Length - 1; i++)
            {
                Point mid = new((pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2);
                ctx.QuadraticBezierTo(pts[i], mid, true, false);
            }
            ctx.LineTo(pts[^1], true, false);
            ctx.LineTo(new Point(pts[^1].X, h), false, false);
        }
        fillGeometry.Freeze();

        Color lineColor = (Stroke as SolidColorBrush)?.Color ?? VisualHelpers.Mint;
        var fill = new LinearGradientBrush(
            Color.FromArgb(64, lineColor.R, lineColor.G, lineColor.B),
            Color.FromArgb(0, lineColor.R, lineColor.G, lineColor.B),
            new Point(0, 0), new Point(0, 1));
        fill.Freeze();
        dc.DrawGeometry(fill, null, fillGeometry);

        // 線用（開いた図形）
        var lineGeometry = new StreamGeometry();
        using (StreamGeometryContext ctx = lineGeometry.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            for (int i = 1; i < pts.Length - 1; i++)
            {
                Point mid = new((pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2);
                ctx.QuadraticBezierTo(pts[i], mid, true, false);
            }
            ctx.LineTo(pts[^1], true, false);
        }
        lineGeometry.Freeze();

        Brush stroke = Stroke ?? new SolidColorBrush(VisualHelpers.Mint);
        dc.DrawGeometry(null, VisualHelpers.MakePen(stroke, 1.6), lineGeometry);

        // 現在値の点
        Point last = pts[^1];
        var halo = new SolidColorBrush(Color.FromArgb(70, lineColor.R, lineColor.G, lineColor.B));
        halo.Freeze();
        var dot = new SolidColorBrush(lineColor);
        dot.Freeze();
        dc.DrawEllipse(halo, null, last, 5.5, 5.5);
        dc.DrawEllipse(dot, new Pen(new SolidColorBrush(Color.FromArgb(255, 0x0B, 0x11, 0x1C)), 1.1), last, 2.4, 2.4);
    }
}

/// <summary>使用中 / キャッシュ / 空き の積み上げバー。</summary>
public sealed class StackedBar : FrameworkElement
{
    public static readonly DependencyProperty UsedRatioProperty = DependencyProperty.Register(
        nameof(UsedRatio), typeof(double), typeof(StackedBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CacheRatioProperty = DependencyProperty.Register(
        nameof(CacheRatio), typeof(double), typeof(StackedBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double UsedRatio
    {
        get => (double)GetValue(UsedRatioProperty);
        set => SetValue(UsedRatioProperty, value);
    }

    public double CacheRatio
    {
        get => (double)GetValue(CacheRatioProperty);
        set => SetValue(CacheRatioProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 2 || h <= 2)
        {
            return;
        }

        double r = h / 2;
        var track = new SolidColorBrush(Color.FromArgb(255, 0x1D, 0x29, 0x41));
        track.Freeze();
        dc.DrawRoundedRectangle(track, null, new Rect(0, 0, w, h), r, r);

        double used = VisualHelpers.Clamp01(UsedRatio) * w;
        double cache = VisualHelpers.Clamp01(CacheRatio) * w;
        if (used + cache > w)
        {
            double scale = w / (used + cache);
            used *= scale;
            cache *= scale;
        }

        const double gap = 2.5;
        if (used >= 2.5)
        {
            var usedBrush = VisualHelpers.MakeGradient(VisualHelpers.Mint, VisualHelpers.Cyan, new Point(0, 0), new Point(1, 0));
            double wUsed = Math.Max(2.5, used - (cache > 2.5 ? gap : 0));
            dc.DrawRoundedRectangle(usedBrush, null, new Rect(0, 0, wUsed, h), r, r);
        }

        if (cache >= 2.5)
        {
            var cacheBrush = new SolidColorBrush(Color.FromArgb(255, 0x33, 0x47, 0x66));
            cacheBrush.Freeze();
            double x = used + gap;
            double wCache = Math.Max(2.5, cache - gap);
            dc.DrawRoundedRectangle(cacheBrush, null, new Rect(x, 0, wCache, h), r, r);
        }
    }
}

/// <summary>単純な丸メーター（コミット使用率など）。</summary>
public sealed class MeterBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(MeterBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(MeterBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 2 || h <= 2)
        {
            return;
        }

        double r = h / 2;
        var track = new SolidColorBrush(Color.FromArgb(255, 0x1D, 0x29, 0x41));
        track.Freeze();
        dc.DrawRoundedRectangle(track, null, new Rect(0, 0, w, h), r, r);

        double value = VisualHelpers.Clamp01(Value) * w;
        if (value < 1.2)
        {
            return;
        }

        Brush fill = Fill ?? VisualHelpers.MakeGradient(VisualHelpers.Mint, VisualHelpers.Cyan, new Point(0, 0), new Point(1, 0));
        dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, Math.Max(h, value), h), r, r);
    }
}
