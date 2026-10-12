using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterNotepad.Imaging;

public enum EditTool { None, Crop, Pen, Highlighter, Line, Arrow, Rectangle, Ellipse, Text, Blur, Pixelate }

/// <summary>
/// One edit. Coordinates are pixels of the image as it is at that point of the list (after the edits before it), so the same
/// list renders a screen-sized proxy for editing and the full-resolution picture for saving.
/// </summary>
public abstract record EditOp;

public sealed record CropOp(Rect Area) : EditOp;

/// <summary>Quarter turns clockwise (negative: counter-clockwise).</summary>
public sealed record RotateOp(int Quarters) : EditOp;

public sealed record FlipOp(bool Horizontal) : EditOp;

public sealed record ResizeOp(int Width, int Height) : EditOp;

public sealed record RegionOp(Rect Area, bool Pixelate) : EditOp;

public sealed record ShapeOp(Annotation Annotation) : EditOp;

/// <summary>A vector drawing (BetterShot-style): sizes and points in image pixels.</summary>
public sealed record Annotation(EditTool Kind, Color Color, double Size, Point[] Points, string? Text = null)
{
    private Geometry? _geometry;

    public void Render(DrawingContext dc)
    {
        if (Points.Length == 0) return;
        switch (Kind)
        {
            case EditTool.Pen:
            case EditTool.Highlighter:
                RenderStroke(dc);
                break;
            case EditTool.Line:
            case EditTool.Arrow:
                if (Points.Length >= 2) RenderLine(dc, Points[0], Points[^1]);
                break;
            case EditTool.Rectangle:
            case EditTool.Ellipse:
                if (Points.Length >= 2) RenderBox(dc, new Rect(Points[0], Points[^1]));
                break;
            case EditTool.Text:
                RenderText(dc);
                break;
        }
    }

    private Pen MakePen(Color c, double width, PenLineCap cap = PenLineCap.Round)
    {
        var pen = new Pen(new SolidColorBrush(c), width) { StartLineCap = cap, EndLineCap = cap, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    private void RenderStroke(DrawingContext dc)
    {
        var marker = Kind == EditTool.Highlighter;
        var ink = marker ? Color.FromArgb(0x66, Color.R, Color.G, Color.B) : Color;
        if (Points.Length == 1)
        {
            var b = new SolidColorBrush(ink);
            b.Freeze();
            dc.DrawEllipse(b, null, Points[0], Size / 2, Size / 2);
            return;
        }
        _geometry ??= Smooth(Points);
        dc.DrawGeometry(null, MakePen(ink, Size, marker ? PenLineCap.Square : PenLineCap.Round), _geometry);
    }

    /// <summary>Quadratic smoothing through segment midpoints.</summary>
    private static Geometry Smooth(Point[] pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], false, false);
            if (pts.Length == 2)
            {
                c.LineTo(pts[1], true, true);
            }
            else
            {
                for (var i = 1; i < pts.Length - 1; i++)
                    c.QuadraticBezierTo(pts[i], new Point((pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2), true, true);
                c.LineTo(pts[^1], true, true);
            }
        }
        g.Freeze();
        return g;
    }

    private void RenderLine(DrawingContext dc, Point a, Point b)
    {
        var v = b - a;
        var len = v.Length;
        if (len < 0.5) return;
        if (Kind == EditTool.Line)
        {
            dc.DrawLine(MakePen(Color, Size), a, b);
            return;
        }
        // straight shaft + solid triangle head whose tip is exactly at b (BetterShot's arrow)
        var u = v / len;
        var n = new Vector(-u.Y, u.X);
        var head = Math.Min(Math.Max(Size * 3.6, 10), len * 0.6);
        var half = head * 0.58;
        var basePt = b - u * head;
        var tri = new StreamGeometry();
        using (var c = tri.Open())
        {
            c.BeginFigure(b, true, true);
            c.LineTo(basePt + n * half, true, false);
            c.LineTo(basePt - n * half, true, false);
        }
        tri.Freeze();
        var shaftEnd = basePt + u * Math.Min(head * 0.5, Size);
        if ((shaftEnd - a) * u > 0) dc.DrawLine(MakePen(Color, Size), a, shaftEnd);
        var fill = new SolidColorBrush(Color);
        fill.Freeze();
        dc.DrawGeometry(fill, MakePen(Color, Math.Max(1, Size * 0.3)), tri);
    }

    private void RenderBox(DrawingContext dc, Rect r)
    {
        if (r.Width < 0.5 && r.Height < 0.5) return;
        var pen = MakePen(Color, Size);
        if (Kind == EditTool.Ellipse)
        {
            dc.DrawEllipse(null, pen, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
            return;
        }
        var radius = Math.Min(Size * 1.6, Math.Min(r.Width, r.Height) / 2);
        dc.DrawRoundedRectangle(null, pen, r, radius, radius);
    }

    public static readonly FontFamily TextFont = new("Segoe UI Variable Display, Segoe UI");

    public FormattedText? Layout()
    {
        if (string.IsNullOrEmpty(Text)) return null;
        var brush = new SolidColorBrush(Color);
        brush.Freeze();
        return new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(TextFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), Math.Max(1, Size), brush, 1.0);
    }

    private void RenderText(DrawingContext dc)
    {
        var ft = Layout();
        if (ft is null) return;
        // a soft contrasting halo keeps the caption readable on any photo
        var lum = (0.2126 * Color.R + 0.7152 * Color.G + 0.0722 * Color.B) / 255;
        var halo = lum > 0.6 ? Color.FromArgb(0x8C, 0x10, 0x12, 0x18) : Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF);
        var outline = ft.BuildGeometry(Points[0]);
        dc.DrawGeometry(null, MakePen(halo, Math.Max(1.5, Size * 0.12)), outline);
        dc.DrawText(ft, Points[0]);
    }

    public Rect Bounds
    {
        get
        {
            if (Points.Length == 0) return Rect.Empty;
            if (Kind == EditTool.Text && Layout() is { } ft)
                return new Rect(Points[0], new System.Windows.Size(ft.WidthIncludingTrailingWhitespace, ft.Height));
            var r = new Rect(Points[0], Points[0]);
            foreach (var p in Points) r.Union(p);
            r.Inflate(Size * (Kind == EditTool.Arrow ? 3 : 1), Size * (Kind == EditTool.Arrow ? 3 : 1));
            return r;
        }
    }
}

/// <summary>The edit list with snapshot undo / redo (records are immutable, a snapshot is a cheap list copy).</summary>
public sealed class EditDocument
{
    private readonly Stack<List<EditOp>> _undo = new();
    private readonly Stack<List<EditOp>> _redo = new();

    public List<EditOp> Ops { get; private set; } = [];

    public int Version { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>The picture differs from the file (saving bakes the edits into the file and resets the list).</summary>
    public bool IsModified => Ops.Count > 0;

    public event Action? Changed;

    public void Push(EditOp op)
    {
        _undo.Push(Ops);
        if (_undo.Count > 300) Trim();
        _redo.Clear();
        Ops = [.. Ops, op];
        Version++;
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (!CanUndo) return;
        _redo.Push(Ops);
        Ops = _undo.Pop();
        Version++;
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _undo.Push(Ops);
        Ops = _redo.Pop();
        Version++;
        Changed?.Invoke();
    }

    /// <summary>The image was saved with these edits baked in: start a fresh list (the file is the new base).</summary>
    public void Reset()
    {
        _undo.Clear();
        _redo.Clear();
        Ops = [];
        Version++;
        Changed?.Invoke();
    }

    private void Trim()
    {
        var keep = _undo.Reverse().Skip(1).ToList();
        _undo.Clear();
        foreach (var s in keep) _undo.Push(s);
    }
}

/// <summary>Applies an edit list to a picture at a given scale (proxy for the screen, 1 for saving).</summary>
internal static class Composer
{
    public sealed record State(BitmapSource Raster, double Scale, System.Windows.Size Logical);

    public sealed record Result(BitmapSource Raster, double Scale, System.Windows.Size Logical, List<Annotation> Pending);

    /// <summary>
    /// <paramref name="cap"/>: largest raster side kept after a resize (proxy) — <see cref="double.PositiveInfinity"/> for the real
    /// thing. <paramref name="checkpoints"/> caches intermediate rasters so undoing or adding a drawing does not recompute it all.
    /// </summary>
    public static Result Run(State start, IReadOnlyList<EditOp> ops, double cap, Checkpoints? checkpoints = null)
    {
        var state = start;
        var from = 0;
        if (checkpoints?.Find(ops) is { } hit)
        {
            state = hit.State;
            from = hit.Count;
        }
        var pending = new List<Annotation>();
        for (var i = from; i < ops.Count; i++)
        {
            var op = ops[i];
            if (op is ShapeOp s)
            {
                pending.Add(s.Annotation);
                continue;
            }
            if (pending.Count > 0)
            {
                state = state with { Raster = Bake(state.Raster, state.Scale, pending) };
                pending.Clear();
            }
            state = Apply(state, op, cap);
            checkpoints?.Add(ops, i + 1, state);
        }
        return new Result(state.Raster, state.Scale, state.Logical, pending);
    }

    /// <summary>Full result with every drawing burnt in.</summary>
    public static BitmapSource Flatten(State start, IReadOnlyList<EditOp> ops)
    {
        var r = Run(start, ops, double.PositiveInfinity);
        return r.Pending.Count == 0 ? r.Raster : Bake(r.Raster, r.Scale, r.Pending);
    }

    private static State Apply(State st, EditOp op, double cap)
    {
        var (raster, s, logical) = (st.Raster, st.Scale, st.Logical);
        switch (op)
        {
            case CropOp c:
            {
                var area = Rect.Intersect(c.Area, new Rect(logical));
                if (area.IsEmpty || area.Width < 1 || area.Height < 1) return st;
                var px = PixelRect(area, s, raster);
                var cropped = new CroppedBitmap(raster, px);
                cropped.Freeze();
                return new State(cropped, px.Width / area.Width, area.Size);
            }
            case RotateOp r:
            {
                var q = ((r.Quarters % 4) + 4) % 4;
                if (q == 0) return st;
                var t = new TransformedBitmap(raster, new RotateTransform(90 * q));
                t.Freeze();
                return new State(t, s, q % 2 == 1 ? new System.Windows.Size(logical.Height, logical.Width) : logical);
            }
            case FlipOp f:
            {
                var t = new TransformedBitmap(raster, new ScaleTransform(f.Horizontal ? -1 : 1, f.Horizontal ? 1 : -1));
                t.Freeze();
                return new State(t, s, logical);
            }
            case ResizeOp z:
            {
                if (z.Width < 1 || z.Height < 1) return st;
                var ns = Math.Min(1, cap / Math.Max(z.Width, z.Height));
                var tw = Math.Max(1, (int)Math.Round(z.Width * ns));
                var th = Math.Max(1, (int)Math.Round(z.Height * ns));
                return new State(Scale(raster, tw, th), ns, new System.Windows.Size(z.Width, z.Height));
            }
            case RegionOp g:
            {
                var area = Rect.Intersect(g.Area, new Rect(logical));
                if (area.IsEmpty || area.Width < 2 || area.Height < 2) return st;
                var px = PixelRect(area, s, raster);
                // strength from the area in image pixels, so the proxy and the saved file look alike
                var strength = g.Pixelate
                    ? Math.Clamp(Math.Min(area.Width, area.Height) / 9, 8, 56) * s
                    : Math.Clamp(Math.Min(area.Width, area.Height) / 10, 6, 42) * s;
                return st with { Raster = PixelOps.Region(raster, px, g.Pixelate, strength) };
            }
        }
        return st;
    }

    private static Int32Rect PixelRect(Rect area, double s, BitmapSource raster)
    {
        var x = (int)Math.Floor(area.X * s);
        var y = (int)Math.Floor(area.Y * s);
        var r = (int)Math.Ceiling(area.Right * s);
        var b = (int)Math.Ceiling(area.Bottom * s);
        x = Math.Clamp(x, 0, raster.PixelWidth - 1);
        y = Math.Clamp(y, 0, raster.PixelHeight - 1);
        r = Math.Clamp(r, x + 1, raster.PixelWidth);
        b = Math.Clamp(b, y + 1, raster.PixelHeight);
        return new Int32Rect(x, y, r - x, b - y);
    }

    public static BitmapSource Scale(BitmapSource src, int width, int height)
    {
        if (src.PixelWidth == width && src.PixelHeight == height) return src;
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, width, height));
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Burns drawings (image pixels) into a raster that holds <paramref name="s"/> raster pixels per image pixel.</summary>
    public static BitmapSource Bake(BitmapSource raster, double s, IReadOnlyList<Annotation> shapes)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(raster, new Rect(0, 0, raster.PixelWidth, raster.PixelHeight));
            dc.PushTransform(new ScaleTransform(s, s));
            foreach (var shape in shapes) shape.Render(dc);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(raster.PixelWidth, raster.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}

/// <summary>Rasters after raster edits, keyed by the exact op prefix that made them (a few, newest first).</summary>
internal sealed class Checkpoints
{
    private readonly List<(EditOp[] Prefix, Composer.State State)> _items = [];
    private const int Max = 4;

    public (int Count, Composer.State State)? Find(IReadOnlyList<EditOp> ops)
    {
        (int, Composer.State)? best = null;
        foreach (var (prefix, state) in _items)
        {
            if (prefix.Length > ops.Count || (best is { } b && prefix.Length <= b.Item1)) continue;
            var same = true;
            for (var i = 0; i < prefix.Length && same; i++) same = ReferenceEquals(prefix[i], ops[i]);
            if (same) best = (prefix.Length, state);
        }
        return best;
    }

    public void Add(IReadOnlyList<EditOp> ops, int count, Composer.State state)
    {
        var prefix = ops.Take(count).ToArray();
        _items.RemoveAll(c => c.Prefix.Length == count && c.Prefix.SequenceEqual(prefix, ReferenceEqualityComparer.Instance));
        _items.Insert(0, (prefix, state));
        if (_items.Count > Max) _items.RemoveAt(_items.Count - 1);
    }

    public void Clear() => _items.Clear();
}

/// <summary>Blur and pixelate on a region of BGRA pixels.</summary>
internal static class PixelOps
{
    public static BitmapSource Region(BitmapSource src, Int32Rect r, bool pixelate, double strength)
    {
        var bgra = src.Format == PixelFormats.Bgra32 || src.Format == PixelFormats.Pbgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var wb = new WriteableBitmap(bgra);
        var stride = r.Width * 4;
        var buf = new byte[stride * r.Height];
        wb.CopyPixels(r, buf, stride, 0);
        if (pixelate) Pixelate(buf, r.Width, r.Height, Math.Max(3, (int)Math.Round(strength)));
        else
        {
            var radius = Math.Max(2, (int)Math.Round(strength));
            for (var pass = 0; pass < 3; pass++)
            {
                BoxH(buf, r.Width, r.Height, radius);
                BoxV(buf, r.Width, r.Height, radius);
            }
        }
        wb.WritePixels(r, buf, stride, 0);
        wb.Freeze();
        return wb;
    }

    private static void Pixelate(byte[] p, int w, int h, int block)
    {
        for (var by = 0; by < h; by += block)
        for (var bx = 0; bx < w; bx += block)
        {
            int bw = Math.Min(block, w - bx), bh = Math.Min(block, h - by);
            long s0 = 0, s1 = 0, s2 = 0, s3 = 0;
            for (var y = by; y < by + bh; y++)
            for (var x = bx; x < bx + bw; x++)
            {
                var i = (y * w + x) * 4;
                s0 += p[i];
                s1 += p[i + 1];
                s2 += p[i + 2];
                s3 += p[i + 3];
            }
            var n = bw * bh;
            byte a0 = (byte)(s0 / n), a1 = (byte)(s1 / n), a2 = (byte)(s2 / n), a3 = (byte)(s3 / n);
            for (var y = by; y < by + bh; y++)
            for (var x = bx; x < bx + bw; x++)
            {
                var i = (y * w + x) * 4;
                p[i] = a0;
                p[i + 1] = a1;
                p[i + 2] = a2;
                p[i + 3] = a3;
            }
        }
    }

    private static void BoxH(byte[] p, int w, int h, int r)
    {
        var line = new byte[w * 4];
        for (var y = 0; y < h; y++)
        {
            var row = y * w * 4;
            Buffer.BlockCopy(p, row, line, 0, w * 4);
            for (var c = 0; c < 4; c++)
            {
                int sum = 0, n = 0;
                for (var x = -r; x <= r; x++)
                {
                    var xx = Math.Clamp(x, 0, w - 1);
                    sum += line[xx * 4 + c];
                    n++;
                }
                for (var x = 0; x < w; x++)
                {
                    p[row + x * 4 + c] = (byte)(sum / n);
                    var add = Math.Min(w - 1, x + r + 1);
                    var sub = Math.Max(0, x - r);
                    sum += line[add * 4 + c] - line[sub * 4 + c];
                }
            }
        }
    }

    private static void BoxV(byte[] p, int w, int h, int r)
    {
        var col = new byte[h];
        for (var x = 0; x < w; x++)
        for (var c = 0; c < 4; c++)
        {
            for (var y = 0; y < h; y++) col[y] = p[(y * w + x) * 4 + c];
            int sum = 0, n = 0;
            for (var y = -r; y <= r; y++)
            {
                sum += col[Math.Clamp(y, 0, h - 1)];
                n++;
            }
            for (var y = 0; y < h; y++)
            {
                p[(y * w + x) * 4 + c] = (byte)(sum / n);
                var add = Math.Min(h - 1, y + r + 1);
                var sub = Math.Max(0, y - r);
                sum += col[add] - col[sub];
            }
        }
    }
}
