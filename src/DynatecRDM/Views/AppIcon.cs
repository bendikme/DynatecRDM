using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace DynatecRDM.Views;

/// <summary>
/// The app icon - a blue rounded square with a screen and an arrow - drawn for one exact pixel
/// size. The .ico is made from these drawings and the windows show the same ones, so the icon is
/// identical in the tray, on the taskbar and in the app.
///
/// Every straight line sits on whole pixels, so nothing is smeared across two. The three small
/// sizes Windows uses most - 16, 20 and 24 - are placed by hand, pixel for pixel; larger sizes
/// are laid out in proportion with their line widths and edges rounded to whole pixels. Only the
/// square's corners and the arrowhead's diagonals are anti-aliased.
/// </summary>
public static class AppIconArt
{
    private static readonly Color Top = Color.FromRgb(0x2A, 0x94, 0xFF);
    private static readonly Color Bottom = Color.FromRgb(0x00, 0x4C, 0xCC);

    private static readonly Brush Glyph = Frozen(new SolidColorBrush(Colors.White));

    /// <summary>Draws the icon into the square from (0,0) to (<paramref name="px"/>, <paramref name="px"/>), in pixels.</summary>
    public static void Draw(DrawingContext dc, int px)
    {
        if (px <= 0) return;

        if (px < 16)
        {
            // Smaller than anything Windows asks for; scale the 16 down rather than draw mush.
            dc.PushTransform(new ScaleTransform(px / 16.0, px / 16.0));
            DrawSmall(dc, 16);
            dc.Pop();
        }
        else if (px <= 24)
        {
            // 17-19 and 21-23 use the hand-made size below them, centred.
            var art = px >= 24 ? 24 : px >= 20 ? 20 : 16;
            var offset = (px - art) / 2;
            dc.PushTransform(new TranslateTransform(offset, offset));
            DrawSmall(dc, art);
            dc.Pop();
        }
        else
        {
            DrawLarge(dc, px);
        }
    }

    // ------------------------------------------------------------------ 16, 20, 24

    private static void DrawSmall(DrawingContext dc, int size)
    {
        switch (size)
        {
            case 16:
                Square(dc, 16, inset: 1, radius: 3);
                // Screen, one pixel thick: columns 3-12, rows 3-9.
                Box(dc, 3, 3, 13, 10, 1);
                Fill(dc, 7, 10, 9, 12);          // stand
                Fill(dc, 5, 12, 11, 13);         // foot
                Fill(dc, 5, 6, 11, 7);           // arrow shaft, tip at column 10
                Fill(dc, 9, 5, 10, 6);           // arrowhead: a five-pixel chevron, or it reads as a plus
                Fill(dc, 9, 7, 10, 8);
                Fill(dc, 8, 4, 9, 5);
                Fill(dc, 8, 8, 9, 9);
                break;

            case 20:
                Square(dc, 20, inset: 1, radius: 4);
                // Screen, one pixel thick: columns 4-15, rows 4-12.
                Box(dc, 4, 4, 16, 13, 1);
                Fill(dc, 9, 13, 11, 15);         // stand
                Fill(dc, 6, 15, 14, 16);         // foot
                Fill(dc, 7, 8, 13, 9);           // shaft
                Fill(dc, 13, 8, 14, 9);          // tip
                Fill(dc, 12, 7, 13, 8);          // arrowhead
                Fill(dc, 12, 9, 13, 10);
                Fill(dc, 11, 6, 12, 7);
                Fill(dc, 11, 10, 12, 11);
                break;

            default:
                Square(dc, 24, inset: 1, radius: 5);
                // Screen, two pixels thick: columns 4-19, rows 4-15.
                Box(dc, 4, 4, 20, 16, 2);
                Fill(dc, 11, 16, 13, 18);        // stand
                Fill(dc, 8, 18, 16, 20);         // foot
                Fill(dc, 8, 9, 15, 11);          // shaft, rows 9-10
                // Arrowhead, two pixels thick, tip at columns 14-15.
                Fill(dc, 12, 7, 14, 8);
                Fill(dc, 13, 8, 15, 9);
                Fill(dc, 14, 9, 16, 11);
                Fill(dc, 13, 11, 15, 12);
                Fill(dc, 12, 12, 14, 13);
                break;
        }
    }

    // ------------------------------------------------------------------ 25 and up

    private static void DrawLarge(DrawingContext dc, int s)
    {
        var inset = (int)Math.Round(s * 0.04);
        Square(dc, s, inset, (s - (2 * inset)) * 0.24);

        // Line width in whole pixels, never thinner than two.
        var w = Math.Max(2, (int)Math.Round(s * 0.07));

        // Screen: symmetric left and right, whole-pixel edges.
        var left = (int)Math.Round(s * 0.2225);
        var right = s - left;
        var top = (int)Math.Round(s * 0.2325);
        var bottom = (int)Math.Round(s * 0.6475);
        Box(dc, left, top, right, bottom, w);

        // Stand, centred exactly: its width has the same parity as the icon.
        var standWidth = (s - w) % 2 == 0 ? w : w + 1;
        var standLeft = (s - standWidth) / 2;
        var footTop = (int)Math.Round(s * 0.72);
        Fill(dc, standLeft, bottom, standLeft + standWidth, footTop);

        // Foot.
        var footLeft = (int)Math.Round(s * 0.3625);
        Fill(dc, footLeft, footTop, s - footLeft, footTop + w);

        // Arrow through the middle of the screen: the shaft snapped, the head drawn as a chevron.
        var innerTop = top + w;
        var innerBottom = bottom - w;
        var shaftTop = (int)Math.Round(((innerTop + innerBottom) / 2.0) - (w / 2.0));
        var centreY = shaftTop + (w / 2.0);
        var shaftLeft = (int)Math.Round(s * 0.35);
        var tip = (int)Math.Round(s * 0.65);
        Fill(dc, shaftLeft, shaftTop, tip - (w / 2), shaftTop + w);

        var head = Math.Max(2, Math.Round(s * 0.10));
        var pen = new Pen(Glyph, w) { LineJoin = PenLineJoin.Miter, StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        pen.Freeze();
        var tipX = tip - (w / 2.0);
        var chevron = new StreamGeometry();
        using (var g = chevron.Open())
        {
            g.BeginFigure(new Point(tipX - head, centreY - head), false, false);
            g.LineTo(new Point(tipX, centreY), true, true);
            g.LineTo(new Point(tipX - head, centreY + head), true, true);
        }
        chevron.Freeze();
        dc.DrawGeometry(null, pen, chevron);
    }

    // ------------------------------------------------------------------ pieces

    private static void Square(DrawingContext dc, int size, int inset, double radius)
    {
        var fill = new LinearGradientBrush(Top, Bottom, new Point(0, 0), new Point(1, 1));
        fill.Freeze();
        var extent = size - (2 * inset);
        dc.DrawRoundedRectangle(fill, null, new Rect(inset, inset, extent, extent), radius, radius);
    }

    /// <summary>A rectangle outline <paramref name="t"/> pixels thick, inside x0..x1, y0..y1.</summary>
    private static void Box(DrawingContext dc, int x0, int y0, int x1, int y1, int t)
    {
        Fill(dc, x0, y0, x1, y0 + t);
        Fill(dc, x0, y1 - t, x1, y1);
        Fill(dc, x0, y0 + t, x0 + t, y1 - t);
        Fill(dc, x1 - t, y0 + t, x1, y1 - t);
    }

    private static void Fill(DrawingContext dc, double x0, double y0, double x1, double y1) =>
        dc.DrawRectangle(Glyph, null, new Rect(x0, y0, x1 - x0, y1 - y0));

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Shows the app icon at the element's size, drawn for the exact number of device pixels it
/// covers, so it is as sharp at 150% or 200% as the .ico is at its own sizes.
/// </summary>
public sealed class AppIcon : FrameworkElement
{
    public AppIcon()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var side = Math.Min(ActualWidth, ActualHeight);
        var px = (int)Math.Round(side * dpi.DpiScaleX);
        if (px <= 0) return;

        // Draw in device pixels, then hand WPF the result at the element's own scale.
        dc.PushTransform(new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY));
        AppIconArt.Draw(dc, px);
        dc.Pop();
    }
}
