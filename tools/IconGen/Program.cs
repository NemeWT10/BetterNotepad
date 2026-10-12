using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IconGen;

/// <summary>
/// Rounded square on deep #0F1117 with a subtle aurora gradient (#8B5CFF → #5B7BFF → #22D3EE),
/// a line-style page with three text lines (one in the accent gradient) and a caret.
/// Usage: IconGen [output.ico] — default: src\BetterNotepad\Assets\app.ico found from the repo root.
/// </summary>
internal static class Program
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    private static readonly Color Violet = Color.FromRgb(0x8B, 0x5C, 0xFF);
    private static readonly Color Blue = Color.FromRgb(0x5B, 0x7B, 0xFF);
    private static readonly Color Cyan = Color.FromRgb(0x22, 0xD3, 0xEE);
    private static readonly Color Deep = Color.FromRgb(0x0F, 0x11, 0x17);

    [STAThread]
    private static int Main(string[] args)
    {
        var root = FindRoot();
        var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(root, "src", "BetterNotepad", "Assets", "app.ico");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var frames = Sizes.Select(s => (Size: s, Png: RenderPng(s))).ToList();
        WriteIco(output, frames);
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(output)!, "app-256.png"), frames.Last().Png);

        var preview = Path.Combine(root, "tests", "output", "icon");
        Directory.CreateDirectory(preview);
        foreach (var f in frames) File.WriteAllBytes(Path.Combine(preview, $"icon-{f.Size}.png"), f.Png);
        Console.WriteLine($"Wrote {output} ({frames.Count} frames, {new FileInfo(output).Length} bytes)");
        return 0;
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    private static byte[] RenderPng(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
            Draw(dc, small: size <= 24, tiny: size <= 16);
            dc.Pop();
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    private static LinearGradientBrush Aurora(Point a, Point b, byte alpha = 255)
    {
        var g = new LinearGradientBrush { StartPoint = a, EndPoint = b };
        g.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, Violet.R, Violet.G, Violet.B), 0));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, Blue.R, Blue.G, Blue.B), 0.5));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, Cyan.R, Cyan.G, Cyan.B), 1));
        g.Freeze();
        return g;
    }

    private static Pen Line(Brush brush, double width)
    {
        var p = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    private static StreamGeometry RoundedPage(double left, double top, double right, double bottom, double r, double fold)
    {
        var page = new StreamGeometry();
        using (var g = page.Open())
        {
            g.BeginFigure(new Point(left + r, top), isFilled: false, isClosed: true);
            if (fold > 0)
            {
                g.LineTo(new Point(right - fold, top), true, true);
                g.LineTo(new Point(right, top + fold), true, true);
            }
            else
            {
                g.LineTo(new Point(right - r, top), true, true);
                g.ArcTo(new Point(right, top + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            }
            g.LineTo(new Point(right, bottom - r), true, true);
            g.ArcTo(new Point(right - r, bottom), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            g.LineTo(new Point(left + r, bottom), true, true);
            g.ArcTo(new Point(left, bottom - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            g.LineTo(new Point(left, top + r), true, true);
            g.ArcTo(new Point(left + r, top), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
        }
        page.Freeze();
        return page;
    }

    /// <summary>Everything is drawn in a 256 × 256 design space.</summary>
    private static void Draw(DrawingContext dc, bool small, bool tiny)
    {
        // ── tile ──
        var tile = new Rect(6, 6, 244, 244);
        var radius = small ? 54 : 58;
        dc.DrawRoundedRectangle(Frozen(new SolidColorBrush(Deep)), null, tile, radius, radius);

        // subtle aurora wash: diagonal gradient at low alpha + soft glows (violet top-left, cyan bottom-right)
        dc.DrawRoundedRectangle(Aurora(new Point(0, 0), new Point(1, 1), small ? (byte)0x46 : (byte)0x30), null, tile, radius, radius);
        if (!small)
        {
            var glowA = new RadialGradientBrush(Color.FromArgb(0x4A, Violet.R, Violet.G, Violet.B), Colors.Transparent)
            { Center = new Point(0.15, 0.1), GradientOrigin = new Point(0.15, 0.1), RadiusX = 0.75, RadiusY = 0.75 };
            glowA.Freeze();
            dc.DrawRoundedRectangle(glowA, null, tile, radius, radius);
            var glowB = new RadialGradientBrush(Color.FromArgb(0x38, Cyan.R, Cyan.G, Cyan.B), Colors.Transparent)
            { Center = new Point(0.9, 0.95), GradientOrigin = new Point(0.9, 0.95), RadiusX = 0.7, RadiusY = 0.7 };
            glowB.Freeze();
            dc.DrawRoundedRectangle(glowB, null, tile, radius, radius);
            // glassy edge
            dc.DrawRoundedRectangle(null, new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255))), 2), new Rect(7, 7, 242, 242), radius - 1, radius - 1);
        }

        var ink = Frozen(new SolidColorBrush(Color.FromRgb(0xE9, 0xEC, 0xF6)));
        var dim = Frozen(new SolidColorBrush(Color.FromArgb(0xB4, 0xC9, 0xCF, 0xE0)));
        var accent = Aurora(new Point(0, 0.5), new Point(1, 0.5));
        var cyan = Frozen(new SolidColorBrush(Cyan));

        // ── page (line style) ──
        double stroke = tiny ? 20 : small ? 17 : 10;
        double left = 72, top = 46, right = 184, bottom = 210;
        double fold = small ? 0 : 30;
        dc.DrawGeometry(null, Line(ink, stroke), RoundedPage(left, top, right, bottom, small ? 16 : 14, fold));
        if (fold > 0)
        {
            // the folded corner
            var corner = new StreamGeometry();
            using (var g = corner.Open())
            {
                g.BeginFigure(new Point(right - fold, top), false, false);
                g.LineTo(new Point(right - fold, top + fold), true, true);
                g.LineTo(new Point(right, top + fold), true, true);
            }
            corner.Freeze();
            dc.DrawGeometry(null, Line(ink, stroke * 0.8), corner);
        }

        // ── text lines ──
        if (small)
        {
            double w = tiny ? 18 : 15;
            dc.DrawLine(Line(dim, w), new Point(100, 108), new Point(156, 108));
            dc.DrawLine(Line(accent, w), new Point(100, 150), new Point(142, 150));
            return;
        }
        const double lw = 10;
        dc.DrawLine(Line(dim, lw), new Point(98, 104), new Point(160, 104));
        dc.DrawLine(Line(accent, lw), new Point(98, 134), new Point(146, 134));
        dc.DrawLine(Line(dim, lw), new Point(98, 164), new Point(130, 164));
        // caret after the last line
        dc.DrawLine(Line(cyan, 8), new Point(146, 152), new Point(146, 178));
    }

    private static void WriteIco(string path, List<(int Size, byte[] Png)> frames)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)frames.Count);
        var offset = 6 + 16 * frames.Count;
        foreach (var (size, png) in frames)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write((uint)png.Length);
            w.Write((uint)offset);
            offset += png.Length;
        }
        foreach (var (_, png) in frames) w.Write(png);
    }
}
