using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BetterNotepad.Markdown.Sample;

internal static class Program
{
    private static string _out = "";

    [STAThread]
    private static int Main(string[] args)
    {
        var root = FindRoot();
        _out = Path.Combine(root, "tests", "output", "markdown");
        Directory.CreateDirectory(Path.Combine(_out, "assets"));
        Directory.CreateDirectory(Path.Combine(_out, "slices"));
        MakeAssets(Path.Combine(_out, "assets"));

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var only = args.Length > 0 ? args[0] : "";
        if (only == "quick")
        {
            app.Resources.MergedDictionaries.Add(FakeTheme.Aurora());
            RendererChecks.Run(_out);
            return 0;
        }

        if (only == "debug")
        {
            app.Resources.MergedDictionaries.Add(FakeTheme.Aurora());
            var file = Path.Combine(_out, "debug", args[1]);
            Snapshot("debug-" + Path.GetFileNameWithoutExtension(file) + ".png", double.Parse(Environment.GetEnvironmentVariable("MD_WIDTH") ?? "900"), File.ReadAllText(file));
            return 0;
        }


        foreach (var (name, theme) in new (string, Func<ResourceDictionary>)[] { ("aurora", FakeTheme.Aurora), ("daylight", FakeTheme.Daylight) })
        {
            if (only.Length > 0 && only != name && only != "all") continue;
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add(theme());
            Snapshot(name + ".png", 900, SampleText.Document);
            if (name == "aurora") Snapshot("aurora-narrow.png", 520, SampleText.Document);
        }

        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(FakeTheme.Aurora());
        RendererChecks.Run(_out);
        if (only != "quick") Checks();
        return 0;
    }

    // ------------------------------------------------------------------ offscreen host

    private sealed class Host : IDisposable
    {
        public readonly HwndSource Source;
        public readonly Border Root;
        public readonly MarkdownPreview Preview;
        public ScrollViewer? Scroll;

        public Host(double width, double height, MarkdownOptions options)
        {
            // A hidden (never shown) message-only window gives the tree a PresentationSource without any visible UI.
            Source = new HwndSource(new HwndSourceParameters("BnMarkdownOffscreen")
            {
                Width = (int)width,
                Height = (int)height,
                WindowStyle = 0,
                ParentWindow = new IntPtr(-3),
            });
            Preview = new MarkdownPreview(options);
            Root = new Border { Width = width, Height = height, Child = Preview };
            Root.SetResourceReference(Border.BackgroundProperty, "Brush.Bg");
            Source.RootVisual = Root;
            Layout();
        }

        public void Layout()
        {
            Root.Measure(new Size(Root.Width, Root.Height));
            Root.Arrange(new Rect(0, 0, Root.Width, Root.Height));
            Root.UpdateLayout();
            Scroll ??= Find<ScrollViewer>(Preview);
        }

        public void Dispose()
        {
            Preview.Dispose();
            Source.RootVisual = null;
            Source.Dispose();
        }
    }

    private static T? Find<T>(DependencyObject o) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
        {
            var c = VisualTreeHelper.GetChild(o, i);
            if (c is T t) return t;
            if (Find<T>(c) is { } f) return f;
        }

        return null;
    }

    private static void Pump(DispatcherPriority priority = DispatcherPriority.ContextIdle)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(priority, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Settle(int rounds = 8)
    {
        for (var i = 0; i < rounds; i++)
        {
            Pump();
            Thread.Sleep(25);
        }
    }

    private static MarkdownOptions Options() => new()
    {
        HeadingFont = Environment.GetEnvironmentVariable("MD_HFONT") ?? "Segoe UI Variable Display, Segoe UI",
        Highlighter = SampleHighlighter.Highlight,
        AlertTitle = k => k switch
        {
            AlertKind.Note => "Informacja",
            AlertKind.Tip => "Wskazówka",
            AlertKind.Important => "Ważne",
            AlertKind.Warning => "Ostrzeżenie",
            _ => "Uwaga",
        },
        Labels = new MarkdownLabels
        {
            Copy = "Kopiuj",
            Copied = "Skopiowano",
            SelectAll = "Zaznacz wszystko",
            RemoteImage = "obraz zdalny, nie ładowany",
            ImageNotFound = "nie znaleziono pliku",
            ImageUnsupported = "format nieobsługiwany",
            BackToReference = "Wróć do tekstu",
        },
    };

    private static void Snapshot(string file, double width, string markdown)
    {
        using var host = new Host(width, 1000, Options());
        host.Preview.Update(markdown, _out);
        host.Preview.FlushPending();
        Settle(60); // let async image decoding finish

        // grow the host to the full document height so the whole page is in one picture
        var stable = 0;
        for (var i = 0; i < 12 && stable < 2; i++)
        {
            host.Layout();
            host.Preview.UpdateLayout();
            Settle(5);
            var wanted = Math.Max(200, Math.Ceiling(host.Scroll!.ExtentHeight));
            if (Environment.GetEnvironmentVariable("MD_VERBOSE") == "1") Console.WriteLine($"  iter {i}: extent={host.Scroll!.ExtentHeight:F0} viewport={host.Scroll.ViewportHeight:F0} offset={host.Scroll.VerticalOffset:F0} root={host.Root.Height:F0} scrollable={host.Scroll.ScrollableHeight:F0}");
            if (Math.Abs(wanted - host.Root.Height) < 1) stable++;
            else
            {
                stable = 0;
                host.Root.Height = wanted;
            }
        }

        if (Environment.GetEnvironmentVariable("MD_HOVER") == "1")
        {
            foreach (var cbv in AllLogical<CodeBlockView>(host.Preview.Document))
                cbv.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
            Settle(2);
        }

        if (Environment.GetEnvironmentVariable("MD_NUDGE") == "2")
        {
            var pp = host.Preview.Document.PagePadding;
            host.Preview.Document.PagePadding = new Thickness(pp.Left + 1, pp.Top, pp.Right, pp.Bottom);
            host.Layout();
            Settle(3);
            host.Preview.Document.PagePadding = pp;
            host.Layout();
            Settle(3);
            Console.WriteLine($"  after nudge: extent={host.Scroll!.ExtentHeight:F0}");
        }

        if (Environment.GetEnvironmentVariable("MD_NUDGE") == "1")
        {
            host.Preview.InvalidateMeasure();
            host.Preview.InvalidateArrange();
            host.Layout();
            Settle(3);
        }

        host.Layout();
        var w = (int)host.Root.Width;
        var h = (int)host.Root.Height;
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host.Root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        var path = Path.Combine(_out, file);
        using (var fs = File.Create(path)) enc.Save(fs);
        var sliceH = 1000;
        for (var s = 0; s * sliceH < h; s++)
        {
            var sh = Math.Min(sliceH, h - s * sliceH);
            var slice = new CroppedBitmap(rtb, new Int32Rect(0, s * sliceH, w, sh));
            var se = new PngBitmapEncoder();
            se.Frames.Add(BitmapFrame.Create(slice));
            using var sf = File.Create(Path.Combine(_out, "slices", Path.GetFileNameWithoutExtension(file) + "-" + (s + 1) + ".png"));
            se.Save(sf);
        }

        Console.WriteLine($"{path}  {w}x{h}  headings={host.Preview.Headings.Count} rich={host.Preview.RichContent}");
    }

    // ------------------------------------------------------------------ API checks

    private static void Checks()
    {
        Console.WriteLine("--- checks");
        using var host = new Host(900, 700, Options());
        var p = host.Preview;
        var tasks = new List<(int, bool)>();
        var rich = new List<RichContent>();
        var headingsChanged = 0;
        var topEvents = new List<int>();
        p.TaskToggled += (l, c) => tasks.Add((l, c));
        p.NeedsRichRenderer += rich.Add;
        p.HeadingsChanged += () => headingsChanged++;
        p.TopLineChanged += (l, _) => topEvents.Add(l);

        p.Update(SampleText.Document, _out);
        p.FlushPending();
        Settle();
        host.Layout();
        var sv = host.Scroll!;

        Console.WriteLine($"headings={p.Headings.Count} changed={headingsChanged} rich={string.Join(",", rich)} blocks={p.Blocks.Count} extent={sv.ExtentHeight:F0}");
        var heading = p.Headings.First(h => h.Text.StartsWith("Tabele"));
        foreach (var off in new[] { 0.0, 400, 1500, 0 })
        {
            sv.ScrollToVerticalOffset(off);
            Settle(2);
            host.Layout();
            Console.WriteLine($"scroll {sv.VerticalOffset,6:F0}: y('{heading.Text}')={p.OffsetOfLine(heading.Line):F1}  topLine={p.GetTopLine(out var f)} (+{f:F2})");
        }

        foreach (var line in new[] { 1, 8, 20, 45, 80, 110, 140 })
        {
            var off = p.OffsetOfLine(line, 0);
            if (off is null) continue;
            var back = p.LineAtOffset(off.Value + 0.5, out var fr);
            Console.WriteLine($"  line {line,3} -> y {off:F1} -> line {back} (+{fr:F2})");
        }

        topEvents.Clear();
        p.ScrollToLine(heading.Line, 0);
        Settle(3);
        var top = p.GetTopLine(out var tf);
        Console.WriteLine($"ScrollToLine({heading.Line}) -> offset {sv.VerticalOffset:F1}, top line {top} (+{tf:F2}), user-scroll events fired by our own scroll: {topEvents.Count}");
        sv.ScrollToVerticalOffset(900);
        Settle(3);
        Console.WriteLine($"user scroll to 900 -> TopLineChanged events: {string.Join(",", topEvents)}");

        var cb = FindLogical<CheckBox>(p.Document);
        if (cb is not null)
        {
            cb.IsChecked = !(cb.IsChecked ?? false);
            cb.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Console.WriteLine($"task toggled: {string.Join(";", tasks)}");
        }

        var links = new List<string>();
        p.LinkClicked += links.Add;
        foreach (var hl in AllLogical<Hyperlink>(p.Document).Take(60))
        {
            if (hl.Tag is string u && (u.StartsWith("https://github.com/NemeWT10/BetterNotepad\"") || u == "https://example.com/docs"))
                hl.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        }

        Console.WriteLine($"links clicked -> {string.Join(" ; ", links)}");
        var anchor = AllLogical<Hyperlink>(p.Document).First(h => h.Tag is string t && t.StartsWith("#"));
        sv.ScrollToVerticalOffset(0);
        Settle(2);
        anchor.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        Settle(2);
        Console.WriteLine($"anchor {anchor.Tag} -> scrolled to {sv.VerticalOffset:F0}, top line {p.GetTopLine(out _)} (heading is at line {p.Headings.First(h => h.Slug == ((string)anchor.Tag)[1..]).Line})");

        p.Zoom = 150;
        Settle(4);
        host.Layout();
        p.ScrollToLine(heading.Line, 0);
        Settle(3);
        Console.WriteLine($"zoom 150: ScrollToLine({heading.Line}) -> top line {p.GetTopLine(out var zf)} (+{zf:F2}), extent {sv.ExtentHeight:F0}, offset {sv.VerticalOffset:F0}");
        p.Zoom = 100;
        Settle(4);
        host.Layout();

        // keep position when content above changes height
        p.ScrollToLine(p.Headings.First(h => h.Text == "Kod").Line, 0);
        Settle(3);
        var topBefore = p.GetTopLine(out var fb);
        var offBefore = sv.VerticalOffset;
        var inserted = string.Join("\n\n", Enumerable.Range(0, 6).Select(i => $"Wstawiony akapit numer {i}, który zmienia wysokość treści nad widokiem."));
        p.Update(SampleText.Document.Replace("## Akapity i nagłówki", inserted + "\n\n## Akapity i nagłówki"), _out);
        p.FlushPending();
        Settle(3);
        var topAfter = p.GetTopLine(out var fa);
        Console.WriteLine($"content inserted above: offset {offBefore:F0} -> {sv.VerticalOffset:F0}; top line {topBefore}(+{fb:F2}) -> {topAfter}(+{fa:F2}) (expected {topBefore + 12})");
        p.Update(SampleText.Document, _out);
        p.FlushPending();

        sv.ScrollToVerticalOffset(1500);
        Settle(2);
        var before = sv.VerticalOffset;
        var sw = Stopwatch.StartNew();
        p.Update(SampleText.Document.Replace("Drugi akapit jest dłuższy", "Drugi akapit jest dużo dłuższy"), _out);
        p.FlushPending();
        sw.Stop();
        Console.WriteLine($"1-block edit above the view: update+layout {sw.Elapsed.TotalMilliseconds:F1} ms, offset {before:F1} -> {sv.VerticalOffset:F1}");

        PerfRun();
    }

    private static IEnumerable<T> AllLogical<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is T t) yield return t;
            if (child is DependencyObject d)
                foreach (var x in AllLogical<T>(d)) yield return x;
        }
    }

    private static T? FindLogical<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is T t) return t;
            if (child is DependencyObject d && FindLogical<T>(d) is { } f) return f;
        }

        return null;
    }

    private static void PerfRun()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 40; i++)
            sb.AppendLine(SampleText.Document.Replace("assets/sample.png", "assets/sample.png").Replace("---\ntitle", "---\ntitle")).AppendLine();
        // strip footnotes / references / front matter so the copies stay independent and incremental
        var big = sb.ToString().Replace("[^1]", "").Replace("[^1]: To jest treść przypisu dolnego, z `kodem` i [linkiem](https://example.com).", "");
        var kb = big.Length / 1024.0;

        using var host = new Host(900, 700, Options());
        var p = host.Preview;
        var r = new MarkdownRenderer(Options());
        var sw = Stopwatch.StartNew();
        r.Render(big, _out);
        var parse = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"build FlowDocument only: {kb:F0} KB in {parse:F0} ms = {parse / kb:F2} ms/KB");
        r.Dispose();

        sw.Restart();
        p.Update(big, _out);
        p.FlushPending();
        host.Layout();
        sw.Stop();
        Console.WriteLine($"first render + layout: {kb:F0} KB in {sw.Elapsed.TotalMilliseconds:F0} ms = {sw.Elapsed.TotalMilliseconds / kb:F2} ms/KB");
        Settle(6);

        var mid = big.IndexOf("Drugi akapit jest dłuższy", big.Length / 2, StringComparison.Ordinal);
        for (var i = 0; i < 3; i++)
        {
            var edited = big.Insert(mid + 5, "x");
            sw.Restart();
            p.Update(edited, _out);
            p.FlushPending();
            host.Layout();
            sw.Stop();
            Console.WriteLine($"edit one char (incremental) render + layout: {sw.Elapsed.TotalMilliseconds:F1} ms");
            big = edited;
        }
    }

    // ------------------------------------------------------------------ fixtures

    private static void MakeAssets(string dir)
    {
        Write(Path.Combine(dir, "sample.png"), 720, 260, (x, y, w, h) =>
        {
            var t = (double)x / w;
            var u = (double)y / h;
            var r = (byte)(60 + 140 * t);
            var g = (byte)(70 + 90 * (1 - u) * t);
            var b = (byte)(200 - 60 * t + 40 * u);
            var band = ((x + y) / 18) % 2 == 0 ? 0 : 14;
            return (r + band, g + band, b + band);
        });
        Write(Path.Combine(dir, "icon.png"), 128, 128, (x, y, w, h) =>
        {
            var dx = x - w / 2.0;
            var dy = y - h / 2.0;
            var d = Math.Sqrt(dx * dx + dy * dy) / (w / 2.0);
            return d > 0.92 ? (30, 33, 48) : ((int)(124 - 40 * d), (int)(92 + 90 * d), (int)(255 - 30 * d));
        });
    }

    private static void Write(string path, int w, int h, Func<int, int, int, int, (int, int, int)> color)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = color(x, y, w, h);
                var i = (y * w + x) * 4;
                px[i] = (byte)Math.Clamp(b, 0, 255);
                px[i + 1] = (byte)Math.Clamp(g, 0, 255);
                px[i + 2] = (byte)Math.Clamp(r, 0, 255);
                px[i + 3] = 255;
            }
        }

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BetterNotepad.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
