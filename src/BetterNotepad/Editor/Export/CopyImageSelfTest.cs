#if DEBUG
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.UI.Themes;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Editor.Export;

/// <summary>
/// Debug self-test of "Copy as image": commands and menu, 2× pixel size, real pixels (theme background, transparent
/// shadow room, syntax colors), line numbers, the light option, wrapping, the size cap, empty files and file names.
/// The clipboard is never touched and no dialog opens (test mode). With BETTERNOTEPAD_SNAPSHOT=&lt;dir&gt; the images of
/// all five themes are written to copyimage_&lt;theme&gt;_&lt;variant&gt;.png for review.
/// </summary>
internal static class CopyImageSelfTest
{
    private const string Code = """
        using System.Text;

        namespace Demo.Parsing;

        /// <summary>Turns "key = value" lines into a dictionary.</summary>
        public sealed class Parser(string source)
        {
            private static readonly char[] Separators = ['=', ':'];

            public Dictionary<string, string> Parse()
            {
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in source.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue; // comments and blank lines
                    var at = line.IndexOfAny(Separators);
                    if (at > 0) result[line[..at].TrimEnd()] = line[(at + 1)..].TrimStart();
                }
                return result;
            }

            public override string ToString() => $"Parser({source.Length} chars, {Separators.Length} separators) — this line is long enough that the image has to wrap it";
        }
        """;

    public static async Task Run(MainWindow w, List<string> failures)
    {
        void Check(bool ok, string what)
        {
            if (!ok) failures.Add("copy image: " + what);
        }

        var appearance = SettingsService.I.Get<AppearanceSettings>();
        var s = SettingsService.I.Get<CopySettings>();
        var (themeBefore, numbersBefore, lightBefore) = (appearance.Theme, s.ImageLineNumbers, s.ImageLightTheme);
        var opened = new List<TextDocument>();
        try
        {
            // ── commands, no default shortcut, menus ──
            Check(CommandRegistry.Get("editor.copyAsImage") is not null && CommandRegistry.Get("editor.saveAsImage") is not null, "commands registered");
            Check(Keymap.GestureText("editor.copyAsImage") == "" && Keymap.GestureText("editor.saveAsImage") == "", "no default shortcut");

            var doc = w.NewTab(content: Code, languageId: "csharp");
            opened.Add(doc);
            doc.UntitledName = "parser.cs";
            await Idle(w);
            if (w.ActiveView?.Editor is not { } ed)
            {
                failures.Add("copy image: no editor");
                return;
            }
            if (ed.ContextMenu is { } menu)
            {
                w.FillEditorMenu(ed, menu);
                var title = CommandRegistry.Get("editor.copyAsImage")!.Title;
                Check(menu.Items.OfType<System.Windows.Controls.MenuItem>().Any(i => i.Header as string == title), "editor context menu has Copy as image");
            }

            appearance.Theme = "aurora";
            s.ImageLineNumbers = false;
            s.ImageLightTheme = false;
            await Idle(w);

            // ── a selection: lines 10–21 (the Parse method) ──
            SelectLines(ed, 10, 21);
            var plain = Copy(w);
            Check(plain is not null, "copy produced an image");
            if (plain is null) return;
            Check(plain.Lines == 12 && plain.Total == 12 && !plain.Whole && !plain.Truncated, $"12 selected lines, got {plain.Lines}/{plain.Total} whole={plain.Whole}");
            Check(plain.FirstLine == 10, "first line number follows the file: " + plain.FirstLine);
            Check(Math.Abs(plain.Image.DpiX - 192) < 0.5 && plain.Image.PixelWidth == (int)Math.Ceiling(plain.Image.Width * 2), $"2× scale: {plain.Image.PixelWidth} px for {plain.Image.Width:0.#} DIP at {plain.Image.DpiX} dpi");
            Check(plain.Png.Length > 2000 && plain.Png[1] == (byte)'P' && plain.Png[2] == (byte)'N' && plain.Png[3] == (byte)'G', "PNG bytes");
            var px = Pixels(plain.Image);
            Check(Alpha(px, plain.Image, 1, 1) == 0, "the shadow room is transparent");
            var inside = Pixel(px, plain.Image, plain.Card.Right - 6, plain.Card.Bottom - 6);
            Check(Near(inside, Palettes.Aurora.EditorBg), $"card background = Aurora editor background, got {inside}");
            var colors = DistinctColors(px, plain.Image, plain.Card);
            Check(colors >= 6, "syntax colors drawn (distinct saturated colors: " + colors + ")");
            Check(plain.Card.Width >= 320 && plain.Card.Height > 12 * 20, $"card size {plain.Card.Width:0}×{plain.Card.Height:0}");

            // ── line numbers widen the card ──
            s.ImageLineNumbers = true;
            var numbered = Copy(w);
            Check(numbered is not null && numbered.Card.Width > plain.Card.Width + 15, $"line numbers widen the card: {plain.Card.Width:0} → {numbered?.Card.Width:0}");

            // ── the light option: Daylight on a dark theme ──
            s.ImageLightTheme = true;
            var light = Copy(w);
            if (light is not null)
            {
                var c = Pixel(Pixels(light.Image), light.Image, light.Card.Right - 6, light.Card.Bottom - 6);
                Check(Near(c, Palettes.Daylight.EditorBg), $"light option = Daylight background, got {c}");
            }
            else Check(false, "light image");
            s.ImageLightTheme = false;

            // ── whole file: long line wraps, card width stays bounded ──
            ed.Select(ed.CaretOffset, 0);
            var whole = Copy(w);
            Check(whole is not null && whole.Whole && whole.Lines == 24 && whole.FirstLine == 1, $"whole file: {whole?.Lines} lines from {whole?.FirstLine}");
            var longLine = RichCopy.Capture(ed, null, dedent: true, out _).Lines[22];
            Check(CodeWrap.Line(longLine, 22, CodeImage.MaxColumns).Count == 2, "the long line wraps into 2 rows");
            Check(whole is not null && whole.Card.Width < CodeImage.MaxColumns * 14.5 * 0.62 + 140, $"wrapped card width bounded: {whole?.Card.Width:0}");

            // ── save: file name from the tab (test mode: no dialog) ──
            w.RunCommand("editor.saveAsImage");
            Check(CopyImage.LastTestSave == "parser.cs.png", "save name: " + CopyImage.LastTestSave);

            // ── huge file: capped rows, selection untouched ──
            var big = w.NewTab(content: string.Join("\n", Enumerable.Range(1, 400).Select(i => $"line {i} = {i * i};")), languageId: "python");
            opened.Add(big);
            await Idle(w);
            if (w.ActiveView?.Editor is { } bigEd)
            {
                bigEd.Select(0, 0);
                var capped = Copy(w);
                Check(capped is not null && capped.Truncated && capped.Lines == CodeImage.MaxRows && capped.Total == 400, $"cap: {capped?.Lines}/{capped?.Total} truncated={capped?.Truncated}");
                Check(capped is not null && capped.Image.PixelHeight < 2 * (CodeImage.MaxRows + 4) * 26, "capped height " + capped?.Image.PixelHeight);
                Check(bigEd.SelectionLength == 0, "the selection is put back after narrowing");
                if (capped is not null && Environment.GetEnvironmentVariable("BETTERNOTEPAD_SNAPSHOT") is { Length: > 0 } shots)
                {
                    Directory.CreateDirectory(shots);
                    File.WriteAllBytes(Path.Combine(shots, "copyimage_aurora_capped.png"), capped.Png);
                }
                w.RunCommand("editor.saveAsImage");
                Check(CopyImage.LastTestSave == Loc.T("copyImage.defaultName") + ".py.png", "untitled save name: " + CopyImage.LastTestSave);
            }

            // ── empty file: nothing rendered ──
            var empty = w.NewTab(content: "", languageId: "csharp");
            opened.Add(empty);
            await Idle(w);
            var before = CopyImage.LastTest;
            w.RunCommand("editor.copyAsImage");
            Check(ReferenceEquals(before, CopyImage.LastTest), "an empty file renders nothing");

            var dir = Environment.GetEnvironmentVariable("BETTERNOTEPAD_SNAPSHOT");
            if (!string.IsNullOrWhiteSpace(dir))
            {
                await Snapshots(w, doc, Path.GetFullPath(dir), failures);
            }
        }
        catch (Exception ex)
        {
            failures.Add("copy image: " + ex);
        }
        finally
        {
            appearance.Theme = themeBefore;
            s.ImageLineNumbers = numbersBefore;
            s.ImageLightTheme = lightBefore;
            foreach (var d in opened)
            {
                if (Workspace.I.Locate(d) is { } where) where.Group.Remove(d);
                DocumentManager.I.Close(d);
            }
            await Idle(w);
        }
    }

    private static async Task Snapshots(MainWindow w, TextDocument doc, string dir, List<string> failures)
    {
        Directory.CreateDirectory(dir);
        var appearance = SettingsService.I.Get<AppearanceSettings>();
        var s = SettingsService.I.Get<CopySettings>();
        foreach (var theme in new[] { "aurora", "graphite", "daylight", "neon", "sunset" })
        {
            appearance.Theme = theme;
            await Idle(w);
            if (await ShowAsync(w, doc) is not { } ed) return;

            void Shot(string variant, bool numbers, bool light, int from, int to)
            {
                s.ImageLineNumbers = numbers;
                s.ImageLightTheme = light;
                if (from > 0) SelectLines(ed, from, to);
                else ed.Select(0, 0);
                var snap = Copy(w);
                if (snap is null)
                {
                    failures.Add($"copy image: snapshot {theme}/{variant} failed");
                    return;
                }
                File.WriteAllBytes(Path.Combine(dir, $"copyimage_{theme}_{variant}.png"), snap.Png);
                // how it lands in a light and in a dark chat
                Write(Path.Combine(dir, $"copyimage_{theme}_{variant}_onwhite.png"), CodeImage.Flatten(snap.Image, Colors.White));
                Write(Path.Combine(dir, $"copyimage_{theme}_{variant}_ondark.png"), CodeImage.Flatten(snap.Image, Color.FromRgb(0x31, 0x33, 0x38)));
            }

            Shot("plain", numbers: false, light: false, 10, 21);
            Shot("numbers", numbers: true, light: false, 10, 21);
            if (theme is "aurora" or "daylight") Shot("long", numbers: true, light: false, 0, 0);
            if (theme == "aurora") Shot("light", numbers: true, light: true, 6, 24);
            if (theme == "aurora") Shot("short", numbers: false, light: false, 8, 8);
        }
    }

    // ───────────── helpers ─────────────

    private static Task Idle(MainWindow w) => w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private static async Task<CodeEditor?> ShowAsync(MainWindow w, TextDocument doc)
    {
        w.Model.ActiveGroup.Active = doc;
        await Idle(w);
        return w.ActiveView?.Editor;
    }

    private static CopyImage.Snapshot? Copy(MainWindow w)
    {
        var before = CopyImage.LastTest;
        w.RunCommand("editor.copyAsImage");
        return ReferenceEquals(before, CopyImage.LastTest) ? null : CopyImage.LastTest;
    }

    private static void SelectLines(CodeEditor ed, int from, int to)
    {
        var d = ed.Document;
        var start = d.GetLineByNumber(from).Offset;
        var end = d.GetLineByNumber(to).EndOffset;
        ed.Select(start, end - start);
    }

    private static void Write(string path, BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static byte[] Pixels(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var px = new byte[image.PixelWidth * image.PixelHeight * 4];
        bgra.CopyPixels(px, image.PixelWidth * 4, 0);
        return px;
    }

    /// <summary>Pixel at a DIP position.</summary>
    private static Color Pixel(byte[] px, BitmapSource image, double x, double y)
    {
        var i = Index(image, x, y);
        return Color.FromArgb(px[i + 3], px[i + 2], px[i + 1], px[i]);
    }

    private static byte Alpha(byte[] px, BitmapSource image, double x, double y) => px[Index(image, x, y) + 3];

    private static int Index(BitmapSource image, double x, double y)
    {
        var sx = Math.Clamp((int)(x * image.PixelWidth / image.Width), 0, image.PixelWidth - 1);
        var sy = Math.Clamp((int)(y * image.PixelHeight / image.Height), 0, image.PixelHeight - 1);
        return (sy * image.PixelWidth + sx) * 4;
    }

    private static bool Near(Color a, Color b) => a.A == 255 && Math.Abs(a.R - b.R) <= 3 && Math.Abs(a.G - b.G) <= 3 && Math.Abs(a.B - b.B) <= 3;

    /// <summary>Distinct saturated colors inside the card, quantized (text colors, not antialiasing shades of gray).</summary>
    private static int DistinctColors(byte[] px, BitmapSource image, System.Windows.Rect card)
    {
        var seen = new HashSet<int>();
        var x0 = (int)(card.X * 2);
        var y0 = (int)(card.Y * 2);
        var x1 = Math.Min(image.PixelWidth, (int)(card.Right * 2));
        var y1 = Math.Min(image.PixelHeight, (int)(card.Bottom * 2));
        for (var y = y0; y < y1; y += 1)
        for (var x = x0; x < x1; x += 1)
        {
            var i = (y * image.PixelWidth + x) * 4;
            int b = px[i], g = px[i + 1], r = px[i + 2];
            if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 60) continue;
            seen.Add((r >> 5) << 6 | (g >> 5) << 3 | b >> 5);
        }
        return seen.Count;
    }
}
#endif
