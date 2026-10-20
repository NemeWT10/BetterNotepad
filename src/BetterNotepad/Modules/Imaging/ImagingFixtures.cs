using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterNotepad.Modules.Imaging;

/// <summary>Test pictures made in code (self-test and snapshots): nothing comes from outside the project.</summary>
internal static class ImagingFixtures
{
    /// <summary>A 3000 × 2000 "photo" (sunset over hills) as JPEG, with camera EXIF and GPS.</summary>
    public static void WritePhoto(string path, bool withMetadata = true)
    {
        var bmp = Render(3000, 2000, dc =>
        {
            dc.DrawRectangle(new LinearGradientBrush(new GradientStopCollection
            {
                new(C("#1B2A4A"), 0), new(C("#5A4A8A"), 0.38), new(C("#F08A6A"), 0.62), new(C("#FFD29A"), 0.72),
            }, 90), null, new Rect(0, 0, 3000, 2000));
            var sun = new RadialGradientBrush(C("#FFF4D6"), C("#00FFC07A")) { GradientOrigin = new Point(0.5, 0.5) };
            dc.DrawEllipse(sun, null, new Point(1900, 1250), 520, 520);
            dc.DrawEllipse(new SolidColorBrush(C("#FFF7E4")), null, new Point(1900, 1250), 150, 150);
            void Ridge(string color, double baseY, double amp, double phase)
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(0, 2000), true, true);
                    for (var x = 0; x <= 3000; x += 30)
                        c.LineTo(new Point(x, baseY - amp * (0.6 * Math.Sin(x / 310.0 + phase) + 0.4 * Math.Sin(x / 97.0 + phase * 2))), true, true);
                    c.LineTo(new Point(3000, 2000), true, true);
                }
                dc.DrawGeometry(new SolidColorBrush(C(color)), null, g);
            }
            Ridge("#7A4E7E", 1360, 120, 0.4);
            Ridge("#4C3566", 1480, 150, 1.7);
            Ridge("#2A2142", 1620, 110, 2.9);
            dc.DrawRectangle(new LinearGradientBrush(C("#33FFD29A"), C("#AA141026"), 90), null, new Rect(0, 1700, 3000, 300));
        });
        var md = withMetadata ? CameraMetadata(orientation: 1) : null;
        var enc = new JpegBitmapEncoder { QualityLevel = 88 };
        enc.Frames.Add(BitmapFrame.Create(bmp, null, md, null));
        Save(enc, path);
    }

    /// <summary>
    /// Stored landscape 400 × 200 with EXIF orientation 6 ("rotate 90° clockwise"): shown portrait 200 × 400 with the arrow
    /// pointing up; camera make / model and a GPS position in the EXIF.
    /// </summary>
    public static void WriteRotatedJpeg(string path)
    {
        var bmp = Render(400, 200, dc =>
        {
            dc.DrawRectangle(new SolidColorBrush(C("#F5F6FA")), null, new Rect(0, 0, 400, 200));
            var pen = new Pen(new SolidColorBrush(C("#4F46E5")), 14) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            dc.DrawLine(pen, new Point(330, 100), new Point(70, 100));
            dc.DrawLine(pen, new Point(70, 100), new Point(130, 50));
            dc.DrawLine(pen, new Point(70, 100), new Point(130, 150));
        });
        var enc = new JpegBitmapEncoder { QualityLevel = 92 };
        enc.Frames.Add(BitmapFrame.Create(bmp, null, CameraMetadata(orientation: 6), null));
        Save(enc, path);
    }

    /// <summary>A 640 × 420 logo on a transparent background (PNG).</summary>
    public static void WriteLogo(string path)
    {
        var bmp = Render(640, 420, dc =>
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), null, new Rect(70, 70, 500, 300), 60, 60);
            dc.DrawRoundedRectangle(new LinearGradientBrush(C("#8B5CFF"), C("#22D3EE"), 30), null, new Rect(60, 50, 500, 300), 60, 60);
            var pen = new Pen(Brushes.White, 22) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen, new Point(170, 150), new Point(450, 150));
            dc.DrawLine(pen, new Point(170, 210), new Point(400, 210));
            dc.DrawLine(pen, new Point(170, 270), new Point(330, 270));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), null, new Point(470, 270), 30, 30);
        });
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        Save(enc, path);
    }

    /// <summary>A 6-frame animated GIF (a ball crossing).</summary>
    public static void WriteAnimatedGif(string path)
    {
        var enc = new GifBitmapEncoder();
        for (var i = 0; i < 6; i++)
        {
            var k = i;
            var bmp = Render(240, 160, dc =>
            {
                dc.DrawRectangle(new SolidColorBrush(C("#1E2230")), null, new Rect(0, 0, 240, 160));
                dc.DrawEllipse(new SolidColorBrush(C("#7C5CFF")), null, new Point(40 + k * 32, 80 + Math.Sin(k) * 30), 22, 22);
            });
            enc.Frames.Add(BitmapFrame.Create(bmp));
        }
        Save(enc, path);
    }

    /// <summary>A 320 × 200 bar chart (BMP).</summary>
    public static void WriteBitmap(string path)
    {
        var bmp = Render(320, 200, dc =>
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 320, 200));
            var colors = new[] { "#4F46E5", "#22C55E", "#F59E0B", "#EF4444" };
            for (var i = 0; i < 4; i++) dc.DrawRectangle(new SolidColorBrush(C(colors[i])), null, new Rect(30 + i * 70, 170 - (i + 1) * 35, 46, (i + 1) * 35));
        });
        var enc = new BmpBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bmp, PixelFormats.Bgr24, null, 0)));
        Save(enc, path);
    }

    /// <summary>An icon with three sizes (16, 48, 256 px), each a PNG entry.</summary>
    public static void WriteIcon(string path)
    {
        var sizes = new[] { 16, 48, 256 };
        var images = sizes.Select(s =>
        {
            var bmp = Render(s, s, dc =>
            {
                dc.DrawRoundedRectangle(new LinearGradientBrush(C("#7C5CFF"), C("#22D3EE"), 45), null, new Rect(0, 0, s, s), s * 0.22, s * 0.22);
                dc.DrawEllipse(Brushes.White, null, new Point(s / 2.0, s / 2.0), s * 0.18, s * 0.18);
            });
            var ms = new MemoryStream();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            enc.Save(ms);
            return ms.ToArray();
        }).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write((uint)images[i].Length);
            w.Write((uint)offset);
            offset += images[i].Length;
        }
        foreach (var img in images) w.Write(img);
    }

    /// <summary>A file named like a picture that is not one.</summary>
    public static void WriteBroken(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
    }

    /// <summary>Make, model, orientation and a GPS position (Lublin) in the EXIF of a JPEG.</summary>
    private static BitmapMetadata CameraMetadata(int orientation)
    {
        var md = new BitmapMetadata("jpg");
        md.SetQuery("/app1/ifd/{ushort=271}", "BetterCam");
        md.SetQuery("/app1/ifd/{ushort=272}", "Model 7");
        md.SetQuery("/app1/ifd/{ushort=274}", (ushort)orientation);
        try
        {
            // RATIONAL values: numerator in the low, denominator in the high 32 bits
            static ulong R(uint num, uint den) => num | (ulong)den << 32;
            md.SetQuery("/app1/ifd/gps/{ushort=0}", new byte[] { 2, 2, 0, 0 });
            md.SetQuery("/app1/ifd/gps/{ushort=1}", "N");
            md.SetQuery("/app1/ifd/gps/{ushort=2}", new[] { R(51, 1), R(14, 1), R(3123, 100) });
            md.SetQuery("/app1/ifd/gps/{ushort=3}", "E");
            md.SetQuery("/app1/ifd/gps/{ushort=4}", new[] { R(22, 1), R(34, 1), R(1290, 100) });
        }
        catch
        {
            // a WIC without GPS writing: the camera tags are enough to show the stripping
        }
        return md;
    }

    private static RenderTargetBitmap Render(int w, int h, Action<DrawingContext> draw)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) draw(dc);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }

    private static void Save(BitmapEncoder enc, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
