using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BetterNotepad.Imaging;

/// <summary>What the viewer knows about a file (pixel size after the EXIF orientation).</summary>
public sealed record ImageInfo(int Width, int Height, string Format, long FileSize, int Frames, int Orientation);

public enum ImageFileFormat { Png, Jpeg, Bmp, Gif, Tiff, Webp }

/// <summary>Decoding (off the UI thread, frozen results) and encoding.</summary>
public static class ImageCodecs
{
    private static readonly Guid WebpContainer = new("e094b0e2-67f2-45b3-b0ea-115337ca7cf3");
    private static bool? _webp;

    /// <summary>Formats a "save as" dialog may offer (WebP only with a WIC WebP encoder installed — Windows has none built in).</summary>
    public static IReadOnlyList<ImageFileFormat> Writable =>
        Enum.GetValues<ImageFileFormat>().Where(CanEncode).ToArray();

    public static bool CanEncode(ImageFileFormat f)
    {
        if (f != ImageFileFormat.Webp) return true;
        if (_webp is { } known) return known;
        try
        {
            _ = BitmapEncoder.Create(WebpContainer);
            _webp = true;
        }
        catch
        {
            _webp = false;
        }
        return _webp.Value;
    }

    public static ImageFileFormat? FromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => ImageFileFormat.Png,
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => ImageFileFormat.Jpeg,
        ".bmp" or ".dib" => ImageFileFormat.Bmp,
        ".gif" => ImageFileFormat.Gif,
        ".tif" or ".tiff" => ImageFileFormat.Tiff,
        ".webp" => ImageFileFormat.Webp,
        _ => null,
    };

    public static string Extension(ImageFileFormat f) => f switch
    {
        ImageFileFormat.Jpeg => ".jpg",
        ImageFileFormat.Tiff => ".tiff",
        _ => "." + f.ToString().ToLowerInvariant(),
    };

    /// <summary>Encodes <paramref name="image"/> (writes a temp file next to the target, then replaces it).</summary>
    public static void Save(BitmapSource image, string path, ImageFileFormat format, int jpegQuality = 92)
    {
        BitmapEncoder enc = format switch
        {
            ImageFileFormat.Png => new PngBitmapEncoder(),
            ImageFileFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) },
            ImageFileFormat.Bmp => new BmpBitmapEncoder(),
            ImageFileFormat.Gif => new GifBitmapEncoder(),
            ImageFileFormat.Tiff => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            ImageFileFormat.Webp => BitmapEncoder.Create(WebpContainer),
            _ => new PngBitmapEncoder(),
        };
        // JPEG / BMP have no alpha: flatten over white instead of letting the encoder turn transparency black
        var src = format is ImageFileFormat.Jpeg or ImageFileFormat.Bmp && HasAlpha(image) ? OverWhite(image) : image;
        enc.Frames.Add(BitmapFrame.Create(src));
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + ".bn-tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                enc.Save(fs);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static bool HasAlpha(BitmapSource b) =>
        b.Format == PixelFormats.Bgra32 || b.Format == PixelFormats.Pbgra32 || b.Format == PixelFormats.Prgba64 || b.Format == PixelFormats.Rgba64 ||
        b.Format == PixelFormats.Indexed8 || b.Format == PixelFormats.Indexed4 || b.Format == PixelFormats.Indexed2 || b.Format == PixelFormats.Indexed1;

    private static BitmapSource OverWhite(BitmapSource b)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, b.PixelWidth, b.PixelHeight));
            dc.DrawImage(b, new Rect(0, 0, b.PixelWidth, b.PixelHeight));
        }
        var rtb = new RenderTargetBitmap(b.PixelWidth, b.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var bgr = new FormatConvertedBitmap(rtb, PixelFormats.Bgr24, null, 0);
        bgr.Freeze();
        return bgr;
    }
}

/// <summary>Runs work on a fresh STA thread (WIC decoding and RenderTargetBitmap off the UI thread); results must be frozen.</summary>
internal static class Sta
{
    public static Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
            finally
            {
                Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
            Name = "BetterNotepad.Imaging worker",
        };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }
}

/// <summary>Decoded picture data (all frozen).</summary>
internal sealed record Decoded(ImageInfo Info, BitmapSource Image, double Scale, GifAnimation.FrameData[]? Frames, System.Windows.Size Screen);

internal static class ImageLoader
{
    /// <summary>
    /// Decodes for display: at most <paramref name="maxSide"/> pixels on the long side (JPEG decodes scaled by the codec, so a
    /// 24 MP photo costs a few MB instead of ~100), oriented by EXIF. <paramref name="maxSide"/> = 0: full resolution.
    /// </summary>
    public static Task<Decoded> DecodeAsync(string path, int maxSide, CancellationToken ct) => Sta.Run(() => Decode(path, maxSide, ct));

    public static Decoded Decode(string path, int maxSide, CancellationToken ct = default)
    {
        var bytes = File.ReadAllBytes(path);
        ct.ThrowIfCancellationRequested();
        using var ms = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var first = decoder.Frames[0];
        var orientation = Orientation(first);
        int rawW = first.PixelWidth, rawH = first.PixelHeight;
        var swap = orientation is >= 5 and <= 8;
        var format = FormatName(decoder);
        var frames = decoder.Frames.Count;
        var info = new ImageInfo(swap ? rawH : rawW, swap ? rawW : rawH, format, bytes.LongLength, frames, orientation);

        if (decoder is GifBitmapDecoder && frames > 1)
        {
            ms.Position = 0;
            var all = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var anim = GifAnimation.Read(all, out var screen);
            ct.ThrowIfCancellationRequested();
            var still = GifAnimation.FirstComposite(anim, screen);
            return new Decoded(info with { Width = (int)screen.Width, Height = (int)screen.Height }, still, 1, anim, screen);
        }

        ms.Position = 0;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bmp.StreamSource = ms;
        var scale = 1.0;
        if (maxSide > 0 && Math.Max(rawW, rawH) > maxSide)
        {
            scale = (double)maxSide / Math.Max(rawW, rawH);
            if (rawW >= rawH) bmp.DecodePixelWidth = maxSide;
            else bmp.DecodePixelHeight = maxSide;
        }
        bmp.EndInit();
        bmp.Freeze();
        ct.ThrowIfCancellationRequested();
        BitmapSource img = Orient(bmp, orientation);
        // the decoder may round: the real scale is what came out
        scale = (double)img.PixelWidth / info.Width;
        return new Decoded(info, img, scale, null, new System.Windows.Size(info.Width, info.Height));
    }

    private static int Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata md && md.GetQuery("System.Photo.Orientation") is ushort o && o is >= 1 and <= 8) return o;
        }
        catch
        {
            // no / unreadable metadata
        }
        return 1;
    }

    private static string FormatName(BitmapDecoder d) => d switch
    {
        JpegBitmapDecoder => "JPEG",
        PngBitmapDecoder => "PNG",
        GifBitmapDecoder => "GIF",
        BmpBitmapDecoder => "BMP",
        TiffBitmapDecoder => "TIFF",
        IconBitmapDecoder => "ICO",
        WmpBitmapDecoder => "JPEG XR",
        _ => (d.CodecInfo?.FriendlyName ?? "").Replace(" Decoder", "", StringComparison.OrdinalIgnoreCase).Trim() is { Length: > 0 } n ? n : "Image",
    };

    /// <summary>Applies the EXIF orientation (1..8) to the pixels.</summary>
    internal static BitmapSource Orient(BitmapSource src, int o)
    {
        if (o <= 1 || o > 8) return src;
        var g = new TransformGroup();
        switch (o)
        {
            case 2: g.Children.Add(new ScaleTransform(-1, 1)); break;
            case 3: g.Children.Add(new RotateTransform(180)); break;
            case 4: g.Children.Add(new ScaleTransform(1, -1)); break;
            case 5: g.Children.Add(new RotateTransform(90)); g.Children.Add(new ScaleTransform(-1, 1)); break;
            case 6: g.Children.Add(new RotateTransform(90)); break;
            case 7: g.Children.Add(new RotateTransform(270)); g.Children.Add(new ScaleTransform(-1, 1)); break;
            case 8: g.Children.Add(new RotateTransform(270)); break;
        }
        var t = new TransformedBitmap(src, g);
        t.Freeze();
        return t;
    }
}

/// <summary>
/// Animated GIF: frames composed onto one surface (offsets, transparency, disposal), driven by a timer that the viewer runs only
/// while it is visible. Frames are decoded once; their pixels are cached while the cache stays small.
/// </summary>
internal sealed class GifAnimation
{
    internal sealed record FrameData(BitmapSource Pixels, Int32Rect Area, int DelayMs, int Disposal);

    private readonly FrameData[] _frames;
    private readonly WriteableBitmap _surface;
    private readonly byte[]?[] _cache;
    private readonly bool _useCache;
    private readonly DispatcherTimer _timer;
    private byte[]? _restore;
    private int _index = -1;

    public GifAnimation(FrameData[] frames, System.Windows.Size screen, Dispatcher dispatcher)
    {
        _frames = frames;
        _surface = new WriteableBitmap((int)screen.Width, (int)screen.Height, 96, 96, PixelFormats.Bgra32, null);
        _cache = new byte[frames.Length][];
        _useCache = frames.Sum(f => (long)f.Area.Width * f.Area.Height * 4) < 48L << 20;
        _timer = new DispatcherTimer(DispatcherPriority.Render, dispatcher);
        _timer.Tick += (_, _) => Step();
        Step();
    }

    public BitmapSource Surface => _surface;

    public bool IsRunning => _timer.IsEnabled;

    public void Start()
    {
        if (_timer.IsEnabled) return;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, _frames[Math.Max(0, _index)].DelayMs));
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Step()
    {
        var w = _surface.PixelWidth;
        var h = _surface.PixelHeight;
        // dispose the frame shown so far
        if (_index >= 0)
        {
            var prev = _frames[_index];
            var area = Clip(prev.Area, w, h);
            if (prev.Disposal == 2 && area.Width > 0) _surface.WritePixels(area, new byte[area.Width * area.Height * 4], area.Width * 4, 0);
            else if (prev.Disposal == 3 && _restore is not null && area.Width > 0) _surface.WritePixels(area, _restore, area.Width * 4, 0);
        }
        _index = (_index + 1) % _frames.Length;
        if (_index == 0)
            _surface.WritePixels(new Int32Rect(0, 0, w, h), new byte[w * h * 4], w * 4, 0);
        var f = _frames[_index];
        var r = Clip(f.Area, w, h);
        if (r.Width > 0 && r.Height > 0)
        {
            var stride = r.Width * 4;
            var current = new byte[stride * r.Height];
            _surface.CopyPixels(r, current, stride, 0);
            if (f.Disposal == 3) _restore = (byte[])current.Clone();
            var px = _cache[_index] ?? Pixels(f);
            if (_useCache) _cache[_index] = px;
            var fw = f.Area.Width;
            for (var y = 0; y < r.Height; y++)
            for (var x = 0; x < r.Width; x++)
            {
                var si = (y * fw + x) * 4;
                if (px[si + 3] == 0) continue; // GIF transparency: keep what is below
                var di = (y * r.Width + x) * 4;
                current[di] = px[si];
                current[di + 1] = px[si + 1];
                current[di + 2] = px[si + 2];
                current[di + 3] = 255;
            }
            _surface.WritePixels(r, current, stride, 0);
        }
        if (_timer.IsEnabled) _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, f.DelayMs));
    }

    private static Int32Rect Clip(Int32Rect a, int w, int h)
    {
        var x = Math.Clamp(a.X, 0, w);
        var y = Math.Clamp(a.Y, 0, h);
        return new Int32Rect(x, y, Math.Clamp(a.Width, 0, w - x), Math.Clamp(a.Height, 0, h - y));
    }

    private static byte[] Pixels(FrameData f)
    {
        var bgra = new FormatConvertedBitmap(f.Pixels, PixelFormats.Bgra32, null, 0);
        var buf = new byte[f.Area.Width * f.Area.Height * 4];
        bgra.CopyPixels(buf, f.Area.Width * 4, 0);
        return buf;
    }

    public static FrameData[] Read(BitmapDecoder decoder, out System.Windows.Size screen)
    {
        int sw = 0, sh = 0;
        try
        {
            if (decoder.Metadata is { } md)
            {
                sw = Convert.ToInt32(md.GetQuery("/logscrdesc/Width"));
                sh = Convert.ToInt32(md.GetQuery("/logscrdesc/Height"));
            }
        }
        catch
        {
            // fall back to the frame sizes
        }
        var list = new List<FrameData>(decoder.Frames.Count);
        foreach (var frame in decoder.Frames)
        {
            int left = 0, top = 0, delay = 100, disposal = 0;
            if (frame.Metadata is BitmapMetadata m)
            {
                left = Q(m, "/imgdesc/Left", 0);
                top = Q(m, "/imgdesc/Top", 0);
                delay = Q(m, "/grctlext/Delay", 10) * 10;
                disposal = Q(m, "/grctlext/Disposal", 0);
            }
            if (delay <= 10) delay = 100; // browsers treat 0 / 1 as 100 ms
            frame.Freeze();
            list.Add(new FrameData(frame, new Int32Rect(left, top, frame.PixelWidth, frame.PixelHeight), delay, disposal));
            sw = Math.Max(sw, left + frame.PixelWidth);
            sh = Math.Max(sh, top + frame.PixelHeight);
        }
        screen = new System.Windows.Size(Math.Max(1, sw), Math.Max(1, sh));
        return list.ToArray();
    }

    private static int Q(BitmapMetadata m, string query, int fallback)
    {
        try
        {
            return m.GetQuery(query) is { } v ? Convert.ToInt32(v) : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>The first frame as a still (the base for editing and the picture before playback starts).</summary>
    public static BitmapSource FirstComposite(FrameData[] frames, System.Windows.Size screen)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var f = frames[0];
            dc.DrawImage(f.Pixels, new Rect(f.Area.X, f.Area.Y, f.Area.Width, f.Area.Height));
        }
        var rtb = new RenderTargetBitmap((int)screen.Width, (int)screen.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
