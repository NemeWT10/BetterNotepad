using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterNotepad.Imaging;
using static BetterNotepad.Ui.Sample.Program;

namespace BetterNotepad.Ui.Sample;

internal static class ImagingScenarios
{
    [ModuleInitializer]
    internal static void Register() => Extra.Add(async only =>
    {
        if (only is "all" or "image") await Run();
    });

    private static async Task Run()
    {
        var dir = Path.Combine(Out, "fixtures", "img");
        Directory.CreateDirectory(dir);
        var photo = Path.Combine(dir, "photo.jpg");
        var alpha = Path.Combine(dir, "logo.png");
        var gif = Path.Combine(dir, "ball.gif");
        var exif = Path.Combine(dir, "rotated.jpg");
        MakePhoto(photo);
        MakeAlpha(alpha);
        MakeGif(gif);
        MakeExif(exif);
        Note("webp encoder available: " + ImageCodecs.CanEncode(ImageFileFormat.Webp));

        foreach (var theme in new[] { "aurora", "daylight" })
        {
            UseTheme(theme);
            await PhotoPart(photo, dir, theme);
            await Simple(alpha, $"img_alpha_{theme}.png", v => v.ZoomBy(1.0));
        }
        UseTheme("aurora");
        await GifPart(gif);
        await ExifPart(exif);
        await JpegFlattenPart(alpha, dir);
    }

    private static ImageViewer NewViewer() => new(new ImagingOptions { AnimationsEnabled = () => false });

    private static async Task<(ImageViewer, Window)> Open(string path, double w = 1100, double h = 720)
    {
        var v = NewViewer();
        var win = Host(v, w, h);
        await v.LoadAsync(path);
        await WaitFor(() => v.HasPicture, 10000);
        await Task.Delay(80);
        return (v, win);
    }

    private static async Task PhotoPart(string photo, string dir, string theme)
    {
        var (v, win) = await Open(photo);
        Check(v.Info is { Width: 3000, Height: 2000, Format: "JPEG" }, $"[{theme}] photo info 3000×2000 JPEG");
        Check(v.RasterScale < 0.7, $"[{theme}] decoded at display size (scale {v.RasterScale:0.00})");
        Snapshot(v, $"img_view_{theme}.png");

        v.ZoomToActual();
        Check(await WaitFor(() => v.HasDetail, 8000), $"[{theme}] 100 % zoom decodes full resolution");
        Check(Math.Abs(v.ZoomPercent - 100) < 0.5, $"[{theme}] zoom shows 100 % ({v.ZoomPercent:0.#})");
        Snapshot(v, $"img_view100_{theme}.png");
        v.ZoomToFit(animate: false);
        Check(!v.HasDetail, $"[{theme}] fit drops the full-resolution copy");

        // edit: every drawing tool through the pointer path
        v.BeginEdit();
        Point S(double x, double y) => v.ImageToStage(new Point(x, y));
        void Drag(EditTool tool, Point a, Point b, params Point[] via)
        {
            v.SetTool(tool);
            v.PointerDown(a, 1, false);
            foreach (var p in via) v.PointerMove(p, false);
            v.PointerMove(b, false);
            v.PointerUp(b, false);
        }
        v.SetColor(0);
        v.SetSize(1);
        Drag(EditTool.Arrow, S(1650, 1350), S(2200, 900));
        v.SetColor(2);
        Drag(EditTool.Rectangle, S(2250, 520), S(2850, 980));
        v.SetColor(5);
        Drag(EditTool.Ellipse, S(300, 300), S(900, 700));
        v.SetColor(3);
        Drag(EditTool.Pen, S(300, 1500), S(1200, 1600), S(500, 1400), S(700, 1700), S(900, 1450), S(1050, 1650));
        v.SetColor(2);
        v.SetSize(1);
        Drag(EditTool.Highlighter, S(250, 1880), S(1300, 1880));
        v.SetColor(7);
        v.SetSize(2);
        v.SetTool(EditTool.Text);
        v.PointerDown(S(1250, 260), 1, false);
        v.TypeText("Golden hour");
        Drag(EditTool.Blur, S(2300, 1450), S(2900, 1800));
        Drag(EditTool.Pixelate, S(1500, 1600), S(2100, 1900));
        Check(v.Edits.Count == 8, $"[{theme}] 8 edits recorded ({v.Edits.Count})");
        Check(v.IsModified, $"[{theme}] modified after edits");
        v.SetTool(EditTool.Arrow);
        await Task.Delay(50);
        Snapshot(v, $"img_edit_{theme}.png");
        if (theme == "aurora") Snapshot(v.ToolbarElement, "img_toolbar_icons_x3.png", 3);

        v.OpenStylePanel();
        await Task.Delay(50);
        Snapshot(v, $"img_style_{theme}.png");
        v.OpenStylePanel();
        v.OpenResizePanel();
        await Task.Delay(50);
        Snapshot(v, $"img_resize_{theme}.png");
        v.OpenResizePanel();

        // crop: pull the top-left corner in
        v.SetTool(EditTool.Crop);
        v.PointerDown(S(0, 0), 1, false);
        v.PointerMove(S(400, 250), false);
        v.PointerUp(S(400, 250), false);
        await Task.Delay(30);
        Snapshot(v, $"img_crop_{theme}.png");
        v.ApplyCropForTest();
        Check(v.PixelSize == new Size(2600, 1750), $"[{theme}] crop → {v.PixelSize}");
        v.Undo();
        Check(v.PixelSize == new Size(3000, 2000), $"[{theme}] undo crop");
        v.Redo();
        Check(v.PixelSize == new Size(2600, 1750), $"[{theme}] redo crop");
        v.Rotate(1);
        Check(v.PixelSize == new Size(1750, 2600), $"[{theme}] rotate swaps the size");
        v.Undo();
        v.SetTool(EditTool.None);
        await Task.Delay(50);
        Snapshot(v, $"img_edited_{theme}.png");

        var outPath = Path.Combine(dir, $"saved_{theme}.png");
        var t0 = Environment.TickCount64;
        await v.SaveAsync(outPath);
        var saved = BitmapFrame.Create(new Uri(outPath), BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
        Check(saved.PixelWidth == 2600 && saved.PixelHeight == 1750, $"[{theme}] saved at full resolution {saved.PixelWidth}×{saved.PixelHeight} ({Environment.TickCount64 - t0} ms)");
        v.EndEdit();
        win.Close();
        v.Dispose();
    }

    private static async Task Simple(string path, string png, Action<ImageViewer>? tweak = null)
    {
        var (v, win) = await Open(path, 900, 620);
        tweak?.Invoke(v);
        await Task.Delay(50);
        Snapshot(v, png);
        win.Close();
        v.Dispose();
    }

    private static async Task GifPart(string gif)
    {
        var (v, win) = await Open(gif, 900, 620);
        Check(v.Info?.Frames == 6, $"gif: 6 frames ({v.Info?.Frames})");
        Check(v.IsAnimating, "gif plays while visible");
        v.Visibility = Visibility.Collapsed;
        Check(!v.IsAnimating, "gif stops when hidden");
        v.Visibility = Visibility.Visible;
        Check(v.IsAnimating, "gif resumes when shown");
        v.BeginEdit();
        Check(!v.IsAnimating, "gif paused while editing");
        v.EndEdit();
        await Task.Delay(250);
        Snapshot(v, "img_gif_aurora.png");
        v.ReleaseContent();
        Check(!v.HasPicture && !v.IsAnimating, "release frees the decoded frames");
        v.Visibility = Visibility.Collapsed;
        v.Visibility = Visibility.Visible;
        Check(await WaitFor(() => v.HasPicture, 5000), "shown again: decoded anew");
        win.Close();
        v.Dispose();
    }

    private static async Task ExifPart(string exif)
    {
        var (v, win) = await Open(exif, 700, 620);
        Check(v.Info is { Width: 200, Height: 400, Orientation: 6 }, $"EXIF orientation 6 applied ({v.Info?.Width}×{v.Info?.Height})");
        Snapshot(v, "img_exif_aurora.png");
        win.Close();
        v.Dispose();
    }

    private static async Task JpegFlattenPart(string alpha, string dir)
    {
        var (v, win) = await Open(alpha, 700, 500);
        var jpg = Path.Combine(dir, "logo_flat.jpg");
        await v.SaveAsync(jpg);
        var f = BitmapFrame.Create(new Uri(jpg), BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
        var px = new byte[4];
        new FormatConvertedBitmap(f, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(2, 2, 1, 1), px, 4, 0);
        Check(px[0] > 240 && px[1] > 240 && px[2] > 240, $"JPEG from a transparent PNG: transparent → white ({px[2]},{px[1]},{px[0]})");
        win.Close();
        v.Dispose();
    }

    // ───────────── fixtures ─────────────

    private static void Encode(BitmapSource src, string path, BitmapEncoder enc)
    {
        enc.Frames.Add(BitmapFrame.Create(src));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static RenderTargetBitmap Render(int w, int h, Action<DrawingContext> draw)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) draw(dc);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static void MakePhoto(string path)
    {
        if (File.Exists(path)) return;
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
        Encode(bmp, path, new JpegBitmapEncoder { QualityLevel = 88 });
    }

    private static void MakeAlpha(string path)
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
        Encode(bmp, path, new PngBitmapEncoder());
    }

    private static void MakeGif(string path)
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
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static void MakeExif(string path)
    {
        // stored landscape 400×200 with "rotate 90° clockwise" (6): shown portrait, the arrow pointing up
        var bmp = Render(400, 200, dc =>
        {
            dc.DrawRectangle(new SolidColorBrush(C("#F5F6FA")), null, new Rect(0, 0, 400, 200));
            var pen = new Pen(new SolidColorBrush(C("#4F46E5")), 14) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            dc.DrawLine(pen, new Point(330, 100), new Point(70, 100));
            dc.DrawLine(pen, new Point(70, 100), new Point(130, 50));
            dc.DrawLine(pen, new Point(70, 100), new Point(130, 150));
        });
        var md = new BitmapMetadata("jpg");
        md.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
        var enc = new JpegBitmapEncoder { QualityLevel = 92 };
        enc.Frames.Add(BitmapFrame.Create(bmp, null, md, null));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
