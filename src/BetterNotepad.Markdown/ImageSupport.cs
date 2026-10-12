using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;

namespace BetterNotepad.Markdown;

/// <summary>Shared width of the readable column; images bind their MaxWidth to it.</summary>
internal sealed class ColumnInfo : DependencyObject
{
    public static readonly DependencyProperty ContentWidthProperty =
        DependencyProperty.Register(nameof(ContentWidth), typeof(double), typeof(ColumnInfo), new PropertyMetadata(800.0));

    public double ContentWidth
    {
        get => (double)GetValue(ContentWidthProperty);
        set => SetValue(ContentWidthProperty, value);
    }
}

internal enum ImageProblem { Remote, NotFound, Unsupported }

/// <summary>Placeholder for one image; becomes the picture when the async load finishes.</summary>
internal sealed class ImageHost : Border
{
    private static readonly Geometry Icon = Geometry.Parse(
        "M3 3.2h10a1.5 1.5 0 0 1 1.5 1.5v6.6A1.5 1.5 0 0 1 13 12.8H3a1.5 1.5 0 0 1-1.5-1.5V4.7A1.5 1.5 0 0 1 3 3.2z M2 11.3l3.4-3.3 2.5 2.4 1.9-1.8 4.2 3.2 M10.6 6.4h.01");

    private readonly string _alt;
    private readonly MarkdownLabels _labels;
    private readonly ColumnInfo _column;
    private readonly double? _width;
    private readonly double? _height;

    /// <summary>The inline container hosting this element; set by the builder.</summary>
    public System.Windows.Documents.InlineUIContainer? Container { get; set; }

    /// <summary>
    /// FlowDocument caches the size of embedded elements; re-attaching the child marks the paragraph dirty so it is re-measured
    /// after the picture arrived.
    /// </summary>
    private void Remeasure()
    {
        if (Container is { } c && ReferenceEquals(c.Child, this))
        {
            c.Child = null;
            c.Child = this;
        }
    }

    public ImageHost(string alt, string? title, double? width, double? height, MarkdownLabels labels, ColumnInfo column, int relativeLine)
    {
        _alt = alt;
        _labels = labels;
        _column = column;
        _width = width;
        _height = height;
        Tag = Res.Box(relativeLine);
        if (!string.IsNullOrWhiteSpace(title)) ToolTip = title;
        else if (!string.IsNullOrWhiteSpace(alt)) ToolTip = alt;

    }

    private Image? _image;

    private Image MakeImage(int pixelWidth)
    {
        var image = new Image
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            Width = _width ?? pixelWidth,
        };
        if (_height is { } h) image.Height = h;
        image.SetBinding(MaxWidthProperty, new Binding(nameof(ColumnInfo.ContentWidth)) { Source = _column });
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    /// <summary>
    /// Reserves the final size from the file header before the pixels are decoded, so the document never reflows
    /// (FlowDocument does not re-measure an embedded element whose size changes later).
    /// </summary>
    public void Reserve(int pixelWidth, int pixelHeight)
    {
        var drawing = new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, pixelWidth, pixelHeight)));
        drawing.Freeze();
        var source = new DrawingImage(drawing);
        source.Freeze();
        _image = MakeImage(pixelWidth);
        _image.Source = source;
        Child = _image;
    }

    public void ShowPicture(BitmapSource bitmap)
    {
        if (_image is null)
        {
            _image = MakeImage(bitmap.PixelWidth);
            _image.Source = bitmap;
            Child = _image;
            Remeasure();
            return;
        }

        _image.Source = bitmap;
    }

    public void ShowProblem(ImageProblem problem, string? name)
    {
        _image = null;
        var detail = problem switch
        {
            ImageProblem.Remote => _labels.RemoteImage,
            ImageProblem.Unsupported => _labels.ImageUnsupported,
            _ => _labels.ImageNotFound,
        };
        var caption = !string.IsNullOrWhiteSpace(_alt) ? _alt : name ?? "";

        var icon = new Path
        {
            Data = Icon,
            StrokeThickness = 1.25,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(Shape.StrokeProperty, K.TextFaint);

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(icon);
        if (caption.Length > 0)
        {
            var t = new TextBlock { Text = caption, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 360 };
            t.SetResourceReference(TextBlock.ForegroundProperty, K.TextDim);
            panel.Children.Add(t);
        }

        var d = new TextBlock { Text = caption.Length > 0 ? "  ·  " + detail : detail, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        d.SetResourceReference(TextBlock.ForegroundProperty, K.TextFaint);
        panel.Children.Add(d);

        var frame = new Border
        {
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 12, 6),
            Child = panel,
        };
        frame.SetResourceReference(BackgroundProperty, K.CodeBg);
        frame.SetResourceReference(BorderBrushProperty, K.Border);
        Child = frame;
        Remeasure();
    }
}

/// <summary>Resolves, loads (off the UI thread) and caches local/data-URI images. Never touches the network.</summary>
internal sealed class ImageService : IDisposable
{
    private const long Budget = 48L << 20;

    private sealed class Entry
    {
        public required BitmapSource Bitmap;
        public long Bytes;
        public int Stamp;
    }

    private readonly Dictionary<string, Entry> _cache = new();
    private readonly Dictionary<string, Task<BitmapSource?>> _inflight = new();
    private readonly System.Windows.Threading.Dispatcher _ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly int _maxPixelWidth;
    private long _bytes;
    private int _clock;
    private bool _disposed;

    public ImageService(int maxPixelWidth) => _maxPixelWidth = Math.Max(256, maxPixelWidth);

    public void Load(ImageHost host, string url, string? baseFolder)
    {
        if (_disposed) return;
        var r = Resolve(url, baseFolder);
        switch (r.Kind)
        {
            case Kind.Remote:
                host.ShowProblem(ImageProblem.Remote, HostName(url));
                return;
            case Kind.Unsupported:
                host.ShowProblem(ImageProblem.Unsupported, r.Name);
                return;
            case Kind.Missing:
                host.ShowProblem(ImageProblem.NotFound, r.Name);
                return;
        }

        if (_cache.TryGetValue(r.Key!, out var hit))
        {
            hit.Stamp = ++_clock;
            host.ShowPicture(hit.Bitmap);
            return;
        }

        if (Probe(r) is { } size) host.Reserve(size.W, size.H);
        StartLoad(host, r);
    }

    private readonly Dictionary<string, (int W, int H)> _sizes = new();

    private (int W, int H)? Probe(Resolved r)
    {
        if (_sizes.TryGetValue(r.Key!, out var known)) return known;
        try
        {
            using Stream s = r.Data is { } d ? new MemoryStream(d, false) : new FileStream(r.Path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var frame = BitmapDecoder.Create(s, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            var size = (frame.PixelWidth, frame.PixelHeight);
            if (size.PixelWidth <= 0 || size.PixelHeight <= 0) return null;
            if (_sizes.Count > 256) _sizes.Clear();
            _sizes[r.Key!] = size;
            return size;
        }
        catch
        {
            return null;
        }
    }

    private void StartLoad(ImageHost host, Resolved r)
    {
        if (!_inflight.TryGetValue(r.Key!, out var task))
        {
            var token = _cts.Token;
            var max = _maxPixelWidth;
            task = Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var bytes = r.Data ?? ReadFile(r.Path!);
                return bytes is null ? null : Decode(bytes, max);
            }, token);
            _inflight[r.Key!] = task;
        }

        task.ContinueWith(t => _ui.BeginInvoke(new Action(() => Finish(host, r, t))), TaskScheduler.Default);
    }

    private void Finish(ImageHost host, Resolved r, Task<BitmapSource?> t)
    {
        if (_disposed || t.IsCanceled) return;
        _inflight.Remove(r.Key!);
        var bitmap = t.IsFaulted ? null : t.Result;
        if (bitmap is null)
        {
            host.ShowProblem(ImageProblem.Unsupported, r.Name);
            return;
        }

        if (!_cache.ContainsKey(r.Key!))
        {
            var bytes = (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;
            _cache[r.Key!] = new Entry { Bitmap = bitmap, Bytes = bytes, Stamp = ++_clock };
            _bytes += bytes;
            Evict();
        }

        host.ShowPicture(bitmap);
    }

    private void Evict()
    {
        while (_bytes > Budget && _cache.Count > 1)
        {
            var oldest = _cache.MinBy(kv => kv.Value.Stamp);
            _bytes -= oldest.Value.Bytes;
            _cache.Remove(oldest.Key);
        }
    }

    public void Clear()
    {
        _cache.Clear();
        _sizes.Clear();
        _bytes = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _cache.Clear();
        _inflight.Clear();
        _bytes = 0;
    }

    private static byte[]? ReadFile(string path)
    {
        var info = new FileInfo(path);
        return !info.Exists || info.Length > (64L << 20) ? null : File.ReadAllBytes(path);
    }

    private static BitmapSource? Decode(byte[] bytes, int maxWidth)
    {
        try
        {
            using var header = new MemoryStream(bytes, false);
            var frame = BitmapDecoder.Create(header, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            var width = frame.PixelWidth;

            using var ms = new MemoryStream(bytes, false);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = ms;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (width > maxWidth) bi.DecodePixelWidth = maxWidth;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    private enum Kind { File, Data, Remote, Missing, Unsupported }

    private readonly record struct Resolved(Kind Kind, string? Key, string? Path, byte[]? Data, string? Name);

    private static string HostName(string url)
    {
        try
        {
            return new Uri(url.StartsWith("//") ? "https:" + url : url).Host;
        }
        catch
        {
            return "";
        }
    }

    private static Resolved Resolve(string url, string? baseFolder)
    {
        url = url.Trim();
        if (url.Length == 0) return new Resolved(Kind.Missing, null, null, null, null);

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',');
            if (comma < 0) return new Resolved(Kind.Missing, null, null, null, null);
            var meta = url.AsSpan(5, comma - 5);
            if (!meta.Contains("image/", StringComparison.OrdinalIgnoreCase)) return new Resolved(Kind.Unsupported, null, null, null, null);
            if (meta.Contains("svg", StringComparison.OrdinalIgnoreCase)) return new Resolved(Kind.Unsupported, null, null, null, "svg");
            try
            {
                var payload = url[(comma + 1)..];
                var bytes = meta.Contains("base64", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(payload)
                    : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
                return new Resolved(Kind.Data, "data:" + bytes.Length + ":" + payload.GetHashCode(), null, bytes, null);
            }
            catch
            {
                return new Resolved(Kind.Unsupported, null, null, null, null);
            }
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("//"))
            return new Resolved(Kind.Remote, null, null, null, null);

        string path;
        try
        {
            if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                path = new Uri(url).LocalPath;
            }
            else
            {
                var cut = url.IndexOfAny(['?', '#']);
                var rel = Uri.UnescapeDataString(cut >= 0 ? url[..cut] : url).Replace('/', '\\');
                if (System.IO.Path.IsPathRooted(rel) && !rel.StartsWith('\\')) path = rel;
                else if (baseFolder is null) return new Resolved(Kind.Missing, null, null, null, System.IO.Path.GetFileName(rel));
                else path = System.IO.Path.Combine(baseFolder, rel.TrimStart('\\'));
                path = System.IO.Path.GetFullPath(path);
            }
        }
        catch
        {
            return new Resolved(Kind.Missing, null, null, null, null);
        }

        var name = System.IO.Path.GetFileName(path);
        var ext = System.IO.Path.GetExtension(path);
        if (ext.Equals(".svg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".svgz", StringComparison.OrdinalIgnoreCase))
            return new Resolved(Kind.Unsupported, null, null, null, name);

        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return new Resolved(Kind.Missing, null, null, null, name);
            return new Resolved(Kind.File, path.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks, path, null, name);
        }
        catch
        {
            return new Resolved(Kind.Missing, null, null, null, name);
        }
    }
}
