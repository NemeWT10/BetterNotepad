using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterNotepad.Hex;

namespace BetterNotepad.Tabular.Sample;

/// <summary>Offscreen host: a message-only HwndSource (never shown) gives the tree a PresentationSource; PNGs come from RenderTargetBitmap.</summary>
internal sealed class HexHost : IDisposable
{
    public readonly HwndSource Source;
    public readonly Border Root;
    public readonly HexView View;
    private readonly double _width;
    private readonly double _height;

    public HexHost(double width, double height, Func<ResourceDictionary>? theme = null)
    {
        var app = EnsureApp();
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add((theme ?? FakeTheme.Aurora)());
        _width = width;
        _height = height;
        Source = new HwndSource(new HwndSourceParameters("BnHexOffscreen")
        {
            Width = (int)width,
            Height = (int)height,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE: no visible window at all
        });
        View = new HexView();
        Root = new Border { Width = width, Height = height, Child = View };
        Source.RootVisual = Root;
        Layout();
    }

    public static Application EnsureApp() => Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

    public static void SetTheme(Func<ResourceDictionary> theme)
    {
        var app = EnsureApp();
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(theme());
    }

    public void Layout()
    {
        Root.Measure(new Size(_width, _height));
        Root.Arrange(new Rect(0, 0, _width, _height));
        Root.UpdateLayout();
    }

    public static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Renders the tree (not the screen) at <paramref name="scale"/>x to a PNG.</summary>
    public void SavePng(string path, double scale = 2)
    {
        Layout();
        Pump();
        var bmp = new RenderTargetBitmap((int)(_width * scale), (int)(_height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(Root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>Renders the tree into a bitmap and throws it away (timing).</summary>
    public void RenderOnly()
    {
        var bmp = new RenderTargetBitmap((int)_width, (int)_height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(Root);
    }

    public void Dispose()
    {
        View.Close();
        Source.RootVisual = null;
        Source.Dispose();
    }
}

internal static class HexSample
{
    public static string Dir { get; set; } = "";

    /// <summary>A 2 MB binary with recognisable patterns, generated here (nothing from outside the project).</summary>
    public static string WriteSampleFile()
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, "sample-2mb.bin");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp1250 = Encoding.GetEncoding(1250);
        var rnd = new Random(2026);
        var data = new byte[2 * 1024 * 1024];

        var banner = cp1250.GetBytes("BetterNotepad hex sample\r\nZażółć gęślą jaźń: ĄĆĘŁŃÓŚŹŻ ąćęłńóśźż\r\n\u0000\u0001\u0002 bytes 0x00-0xFF follow");
        banner.CopyTo(data, 0);
        for (var i = 0; i < 256; i++) data[0x100 + i] = (byte)i;                                       // every byte value
        for (var m = 0; m < 8; m++) Encoding.ASCII.GetBytes($"MARK{m:00}").CopyTo(data, 0x200 + m * 0x40); // sparse markers in zeros
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52 }.CopyTo(data, 0x400);

        string[] sentences =
        [
            "Zażółć gęślą jaźń, a potem napisz plik w notatniku. ",
            "The quick brown fox jumps over the lazy dog 0123456789. ",
            "Wszystkie bajty od 0x80 do 0xFF to polskie litery w Windows-1250. ",
        ];
        var pos = 0x500;
        var kind = 0;
        while (pos < data.Length)
        {
            var len = Math.Min(data.Length - pos, 0x1000);
            var span = data.AsSpan(pos, len);
            switch (kind % 5)
            {
                case 0: rnd.NextBytes(span); break;
                case 1:
                    var text = cp1250.GetBytes(string.Concat(Enumerable.Range(0, 80).Select(i => sentences[(kind + i) % sentences.Length])));
                    text.AsSpan(0, Math.Min(text.Length, len)).CopyTo(span);
                    break;
                case 2:
                    for (var i = 0; i + 4 <= len; i += 4) BitConverter.TryWriteBytes(span[i..], (uint)((pos + i) / 4));
                    break;
                case 3:
                    for (var i = 0; i < len; i++) span[i] = rnd.Next(10) < 8 ? (byte)0 : (byte)rnd.Next(256);
                    break;
                default:
                    for (var i = 0; i + 4 <= len; i += 4)
                    {
                        span[i] = 0xDE;
                        span[i + 1] = 0xAD;
                        span[i + 2] = 0xBE;
                        span[i + 3] = 0xEF;
                    }

                    span[(len / 2)..(len / 2 + 64)].Fill(0xFF);
                    break;
            }

            pos += len;
            kind++;
        }

        File.WriteAllBytes(path, data);
        return path;
    }

    public static void RenderAll()
    {
        Directory.CreateDirectory(Dir);
        var file = WriteSampleFile();
        Console.WriteLine($"  sample file {file}");

        var themes = new (string Name, Func<ResourceDictionary> Theme)[] { ("aurora", FakeTheme.Aurora), ("daylight", FakeTheme.Daylight) };
        foreach (var (name, theme) in themes)
        {
            // 1) the top of the file: header, text banner, selection across rows made in the hex pane
            using (var host = new HexHost(1000, 460, theme))
            {
                var v = host.View;
                v.OffsetLabel = "Offset";
                v.TextLabel = "Tekst";
                v.AlwaysActiveSelection = true;
                v.SmoothScrolling = false;
                v.OpenFile(file);
                host.Layout();
                v.Select(0x23, 0x3A);
                v.ActivePane = HexPane.Hex;
                host.SavePng(Path.Combine(Dir, $"hex-{name}.png"));
            }

            // 2) deep in the file after a go-to-offset, selection made in the text pane, focus away (inactive selection)
            using (var host = new HexHost(1000, 460, theme))
            {
                var v = host.View;
                v.OffsetLabel = "Offset";
                v.TextLabel = "Tekst";
                v.SmoothScrolling = false;
                v.OpenFile(file);
                host.Layout();
                v.GoToOffset(0x1AF2A0, 12);
                v.ActivePane = HexPane.Text;
                host.SavePng(Path.Combine(Dir, $"hex-{name}-goto.png"));
            }
        }

        // 3) narrow: the width does not fit 16 bytes, so the view falls back to 8 per row
        using (var host = new HexHost(520, 360))
        {
            var v = host.View;
            v.OffsetLabel = "Offset";
            v.TextLabel = "Tekst";
            v.AlwaysActiveSelection = true;
            v.SmoothScrolling = false;
            v.OpenFile(file);
            host.Layout();
            v.Select(0x100, 20);
            host.SavePng(Path.Combine(Dir, "hex-aurora-narrow.png"));
        }

        // 4) empty file with a message passed in by the host
        using (var host = new HexHost(700, 240, FakeTheme.Daylight))
        {
            host.View.EmptyText = "Plik jest pusty";
            host.View.OffsetLabel = "Offset";
            host.View.TextLabel = "Tekst";
            host.View.SetBytes(Array.Empty<byte>());
            host.Layout();
            host.SavePng(Path.Combine(Dir, "hex-daylight-empty.png"));
        }

        // 5) a small file: partial last row, scroll bar with a large thumb
        using (var host = new HexHost(700, 300, FakeTheme.Daylight))
        {
            var v = host.View;
            v.OffsetLabel = "Offset";
            v.TextLabel = "Tekst";
            v.AlwaysActiveSelection = true;
            v.SetBytes(File.ReadAllBytes(file).AsMemory(0x400, 700));
            host.Layout();
            v.Select(0x2C0, 7);
            v.ScrollToRow(0);
            host.SavePng(Path.Combine(Dir, "hex-daylight-small.png"));
        }

        // frame cost: scroll through the sample and render a full 1000x800 view each time (includes the software rasterizer)
        using (var host = new HexHost(1000, 800))
        {
            var v = host.View;
            v.SmoothScrolling = false;
            v.OpenFile(file);
            host.Layout();
            v.Select(0x1000, 300);
            host.RenderOnly();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int frames = 60;
            for (var i = 0; i < frames; i++)
            {
                v.ScrollToRow(1000 + i * 7.3);
                host.Layout();
                host.RenderOnly();
            }

            Console.WriteLine($"  font {v.ResolvedFontName}, {v.Geometry.CharWidth}x{v.Geometry.RowHeight} px cell, {v.ViewportRows:0.0} rows: {sw.Elapsed.TotalMilliseconds / frames:0.0} ms per scrolled frame (render + rasterize)");
        }

        Console.WriteLine($"  PNGs written to {Dir}");
    }
}
