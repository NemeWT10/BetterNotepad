using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace BetterNotepad.Imaging;

public enum IconKind
{
    Undo, Redo, Crop, Pen, Highlighter, Line, Arrow, Rectangle, Ellipse, Text, Blur, Pixelate,
    RotateLeft, RotateRight, FlipHorizontal, FlipVertical, Resize, ZoomIn, ZoomOut, Fit, ActualSize,
    Play, Pause, Edit, Check, Close, Link, Info, BrokenImage,
}

/// <summary>Line icons (20×20 grid, 1.5 px stroke, round caps) drawn for this module, in the app's icon language.</summary>
internal static class Icons
{
    private static readonly Dictionary<IconKind, (Geometry? Stroke, Geometry? Fill)> Cache = new();

    public static (Geometry? Stroke, Geometry? Fill) Get(IconKind kind)
    {
        if (Cache.TryGetValue(kind, out var g)) return g;
        g = kind switch
        {
            IconKind.Undo => (P("M7.5 4.5 L4 8 L7.5 11.5 M4 8 H12 A4.25 4.25 0 0 1 12 16.5 H8.5"), null),
            IconKind.Redo => (P("M12.5 4.5 L16 8 L12.5 11.5 M16 8 H8 A4.25 4.25 0 0 0 8 16.5 H11.5"), null),
            IconKind.Crop => (P("M6 2.5 V14 H17.5 M2.5 6 H14 V17.5"), null),
            IconKind.Pen => (P("M13.6 3.6 A1.6 1.6 0 0 1 15.9 3.6 L16.4 4.1 A1.6 1.6 0 0 1 16.4 6.4 L7.2 15.6 L3.5 16.5 L4.4 12.8 Z M12 5.2 L14.8 8"), null),
            IconKind.Highlighter => (P("M12.2 3.3 L16.7 7.8 L10.4 14.1 L5.9 9.6 Z M5.9 9.6 L4.6 13.4 L6.6 15.4 L10.4 14.1 M3 17.5 H10"), null),
            IconKind.Line => (P("M4 16 L16 4"), null),
            IconKind.Arrow => (P("M4 16 L15.5 4.5 M9 4.5 H15.5 V11"), null),
            IconKind.Rectangle => (P("M5.5 4.5 H14.5 A2 2 0 0 1 16.5 6.5 V13.5 A2 2 0 0 1 14.5 15.5 H5.5 A2 2 0 0 1 3.5 13.5 V6.5 A2 2 0 0 1 5.5 4.5 Z"), null),
            IconKind.Ellipse => (P("M3 10 A7 5.75 0 1 0 17 10 A7 5.75 0 1 0 3 10 Z"), null),
            IconKind.Text => (P("M4.5 6 V4 H15.5 V6 M10 4 V16 M7.5 16 H12.5"), null),
            IconKind.Blur => (P("M10 2.8 C8.2 5.2 4.8 9 4.8 12 A5.2 5.2 0 0 0 15.2 12 C15.2 9 11.8 5.2 10 2.8 Z M7.6 12.4 A2.5 2.5 0 0 0 10 14.9"), null),
            IconKind.Pixelate => (P("M5.5 3.5 H14.5 A2 2 0 0 1 16.5 5.5 V14.5 A2 2 0 0 1 14.5 16.5 H5.5 A2 2 0 0 1 3.5 14.5 V5.5 A2 2 0 0 1 5.5 3.5 Z"),
                P("M6 6 H10 V10 H6 Z M10 10 H14 V14 H10 Z")),
            IconKind.RotateRight => (RotateArrow(clockwise: true), null),
            IconKind.RotateLeft => (RotateArrow(clockwise: false), null),
            IconKind.FlipHorizontal => (P("M10 3 V17 M7.5 5.5 L2.5 14.5 H7.5 Z M12.5 5.5 L17.5 14.5 H12.5 Z"), P("M12.5 5.5 L17.5 14.5 H12.5 Z")),
            IconKind.FlipVertical => (P("M3 10 H17 M5.5 7.5 L14.5 2.5 V7.5 Z M5.5 12.5 L14.5 17.5 V12.5 Z"), P("M5.5 12.5 L14.5 17.5 V12.5 Z")),
            IconKind.Resize => (P("M3.5 9 V3.5 H9 M3.5 3.5 L8.5 8.5 M16.5 11 V16.5 H11 M16.5 16.5 L11.5 11.5"), null),
            IconKind.ZoomIn => (P("M8.5 3 A5.5 5.5 0 1 0 8.5 14 A5.5 5.5 0 1 0 8.5 3 Z M12.5 12.5 L17 17 M6 8.5 H11 M8.5 6 V11"), null),
            IconKind.ZoomOut => (P("M8.5 3 A5.5 5.5 0 1 0 8.5 14 A5.5 5.5 0 1 0 8.5 3 Z M12.5 12.5 L17 17 M6 8.5 H11"), null),
            IconKind.Fit => (P("M3.5 7.5 V3.5 H7.5 M12.5 3.5 H16.5 V7.5 M16.5 12.5 V16.5 H12.5 M7.5 16.5 H3.5 V12.5"), null),
            IconKind.ActualSize => (P("M4.5 7 L6.5 5.5 V14.5 M12.5 7 L14.5 5.5 V14.5 M9.5 8.5 V8.6 M9.5 11.9 V12"), null),
            IconKind.Play => (P("M6.5 4.6 L15.4 10 L6.5 15.4 Z"), P("M6.5 4.6 L15.4 10 L6.5 15.4 Z")),
            IconKind.Pause => (P("M7 4.5 V15.5 M13 4.5 V15.5"), null),
            IconKind.Edit => (P("M12.4 4.1 A1.6 1.6 0 0 1 14.7 4.1 L15.2 4.6 A1.6 1.6 0 0 1 15.2 6.9 L8.2 13.9 L4.8 14.7 L5.6 11.3 Z M3.5 17.5 H16.5"), null),
            IconKind.Check => (P("M4.5 10.5 L8.3 14.3 L15.5 6"), null),
            IconKind.Close => (P("M5.5 5.5 L14.5 14.5 M14.5 5.5 L5.5 14.5"), null),
            IconKind.Link => (P("M8.2 11.8 L11.8 8.2 M9.4 6.2 L10.9 4.7 A2.8 2.8 0 0 1 15.3 9.1 L13.8 10.6 M10.6 13.8 L9.1 15.3 A2.8 2.8 0 0 1 4.7 10.9 L6.2 9.4"), null),
            // a picture frame torn down the middle (the halves apart), hills and a sun on the left half
            IconKind.BrokenImage => (P("M9 3.5 H5 A2 2 0 0 0 3 5.5 V14.5 A2 2 0 0 0 5 16.5 H8.6 L7.6 13.4 L9.6 10.6 L8.4 7.2 Z " +
                                       "M11.6 3.5 H15 A2 2 0 0 1 17 5.5 V14.5 A2 2 0 0 1 15 16.5 H11.2 L10.2 13.4 L12.2 10.6 L11 7.2 Z " +
                                       "M3.4 13 L6 10.4 L7.9 12.3 M13.2 12.6 L14.6 11.2 L16.6 13.2 M5.8 6.6 V6.7"), null),
            IconKind.Info => (P("M10 3 A7 7 0 1 0 10 17 A7 7 0 1 0 10 3 Z M10 9.2 V13.8 M10 6.5 V6.6"), null),
            _ => (null, null),
        };
        Cache[kind] = g;
        return g;
    }

    private static Geometry P(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    /// <summary>A three-quarter circle with an arrow head along its end tangent.</summary>
    private static Geometry RotateArrow(bool clockwise)
    {
        const double cx = 10, cy = 10.5, r = 5.8;
        Point At(double deg) => new(cx + r * Math.Cos(deg * Math.PI / 180), cy - r * Math.Sin(deg * Math.PI / 180));
        // clockwise: from the right side down, round the bottom and the left up to the top (open at the upper right), the head
        // pointing on along the circle; counter-clockwise mirrored
        double from = clockwise ? -10 : 190, to = clockwise ? -260 : 440;
        var start = At(from);
        var end = At(to);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(start, false, false);
            c.ArcTo(end, new Size(r, r), 0, true, clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, true);
            // tangent of travel at the end, arrow head 4 long, ±40°
            var t = to * Math.PI / 180;
            var dir = clockwise ? new Vector(Math.Sin(t), Math.Cos(t)) : new Vector(-Math.Sin(t), -Math.Cos(t));
            foreach (var a in new[] { 40.0, -40.0 })
            {
                var rad = a * Math.PI / 180;
                var back = new Vector(-(dir.X * Math.Cos(rad) - dir.Y * Math.Sin(rad)), -(dir.X * Math.Sin(rad) + dir.Y * Math.Cos(rad)));
                c.BeginFigure(end, false, false);
                c.LineTo(end + back * 4, true, true);
            }
        }
        g.Freeze();
        return g;
    }
}

/// <summary>Draws an <see cref="IconKind"/> in the inherited foreground color (like a glyph, but a real 1.5 px line icon).</summary>
public sealed class IconPath : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(IconKind), typeof(IconPath), new FrameworkPropertyMetadata(IconKind.Check, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(IconPath), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
        nameof(StrokeWidth), typeof(double), typeof(IconPath), new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender));

    public IconPath()
    {
        Width = 20;
        Height = 20;
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    public IconKind Kind
    {
        get => (IconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeWidth
    {
        get => (double)GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var (stroke, fill) = Icons.Get(Kind);
        var k = Math.Min(ActualWidth, ActualHeight) / 20;
        if (k <= 0) return;
        dc.PushTransform(new ScaleTransform(k, k));
        if (fill is not null)
        {
            dc.PushOpacity(Kind == IconKind.Play ? 1.0 : 0.45);
            dc.DrawGeometry(Foreground, null, fill);
            dc.Pop();
        }
        if (stroke is not null)
        {
            var pen = new Pen(Foreground, StrokeWidth / k) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, pen, stroke);
        }
        dc.Pop();
    }
}
