#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Session;
using BetterNotepad.UI.Windows;
using TextDocument = BetterNotepad.Documents.TextDocument;

namespace BetterNotepad.UI.Palette;

/// <summary>
/// Debug-only checks of the palette and the tab switcher (part of SelfTest): fuzzy ranking, symbol scanning,
/// every palette mode end to end, the switcher. With BETTERNOTEPAD_SNAPSHOT=&lt;dir&gt; it also renders the
/// window off-screen (RenderTargetBitmap, never the screen) to palette-*.png in Aurora/PL and Daylight/EN.
/// </summary>
internal static class PaletteSelfTest
{
    private const string Sample = """
        using System.Text;

        namespace Demo.Greetings;

        /// <summary>Says hello, politely and repeatedly.</summary>
        public sealed class Greeter
        {
            private readonly string _name;

            public Greeter(string name) => _name = name;

            public string Greet(int times)
            {
                var sb = new StringBuilder();
                for (var i = 0; i < times; i++) sb.Append("Hello, ").Append(_name).Append('!');
                return sb.ToString();
            }

            private static async Task<int> CountAsync(Dictionary<string, int> map)
            {
                if (map.Count > 0) { return 1; }
                return await Task.FromResult(map.Count);
            }

            public override string ToString() => $"Greeter({_name})";
        }

        public interface IShape
        {
            double Area();
        }

        public enum Mood { Calm, Cheerful, Sleepy }

        internal static class Program
        {
            public static void Main(string[] args)
            {
                var greeter = new Greeter(args.Length > 0 ? args[0] : "world");
                Console.WriteLine(greeter.Greet(3));
            }
        }
        """;

    // lines must not start with '#' inside #if DEBUG (a Release build lexes them as directives): '~#' becomes '#'
    private static readonly string Readme = """
        ~# BetterNotepad

        A light notepad for technical people.

        ~## Features

        ~### Tabs and session

        Text here.

        ```bash
        ~# not a heading
        ```

        ~## Install
        Setext heading
        ===
        """.Replace("~#", "#");

    private static List<string> _failures = new();

    private static void Check(bool ok, string what)
    {
        if (!ok) _failures.Add("palette: " + what);
    }

    private static Task Idle(MainWindow w) => w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    public static async Task Run(MainWindow w, List<string> failures)
    {
        _failures = failures;
        CommandPalette.Close(w);
        await Idle(w);

        Fuzzy();
        Symbols();

        var dir = Path.Combine(AppEnv.LocalDir, "fixtures");
        Directory.CreateDirectory(dir);
        string Fixture(string name, string content)
        {
            var p = Path.Combine(dir, name);
            File.WriteAllText(p, content.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            return p;
        }
        var cs = Fixture("Greeter.cs", Sample);
        var md = Fixture("README.md", Readme);
        var log = Fixture("server.log", "2026-10-12 10:00:01 INFO  started\r\n2026-10-12 10:00:02 WARN  slow disk\r\n");
        var notes = Fixture("notes.txt", "shopping: milk, bread\r\n");
        var json = Fixture("appsettings.json", "{ \"a\": 1 }\r\n");
        RecentFiles.I.Add(json);
        RecentFiles.I.Add(notes);

        var group = w.Model.ActiveGroup;
        var opened = new List<Document>();
        foreach (var p in new[] { log, md, cs })
        {
            w.OpenFile(p);
            if (w.ActiveDocument is TextDocument t)
            {
                await t.LoadAsync();
                opened.Add(t);
            }
        }
        await Idle(w);
        var csDoc = w.ActiveDocument as TextDocument;
        Check(csDoc is not null && csDoc.Title == "Greeter.cs", "fixture Greeter.cs is the active tab");
        if (csDoc is null) return;

        await CommandsFlow(w);
        await PrefixFlow(w);
        await LineFlow(w);
        await SymbolFlow(w);
        await LanguageFlow(w, csDoc);
        await FilesFlow(w, md);
        await SwitcherFlow(w);

        var snapshots = Environment.GetEnvironmentVariable("BETTERNOTEPAD_SNAPSHOT");
        if (!string.IsNullOrWhiteSpace(snapshots)) await Snapshots(w, snapshots, csDoc);

        CommandPalette.Close(w);
        foreach (var d in opened.ToList())
            if (Workspace.I.Locate(d) is { } where) await w.CloseTabAsync(where.Group, d);
        foreach (var d in group.Tabs.Where(t => t.IsUntitled && t.IsDirty).ToList())
        {
            group.Remove(d);
            DocumentManager.I.Close(d);
        }
    }

    // ───────────── pure logic ─────────────

    private static void Fuzzy()
    {
        double S(string q, string t) => FuzzyMatcher.Match(q, t)?.Score ?? double.NegativeInfinity;

        Check(FuzzyMatcher.Match("zapisz", "Zapisz jako…")?.Positions.SequenceEqual([0, 1, 2, 3, 4, 5]) == true, "contiguous prefix positions");
        Check(FuzzyMatcher.Match("zamien", "Zamień") is not null, "diacritics are folded");
        Check(FuzzyMatcher.Match("ŁÓDŹ", "lodz") is not null, "Polish capitals fold");
        Check(FuzzyMatcher.Match("xyz", "Save") is null, "non-match");
        Check(S("sav", "Save") > S("sav", "Save as…") && S("sav", "Save as…") > S("sav", "Reopen saved file"), "shorter / earlier wins");
        Check(FuzzyMatcher.Match("gtl", "Go to line")?.Positions.SequenceEqual([0, 3, 6]) == true, "word starts preferred");
        Check(S("gtl", "Go to line") > S("gtl", "gather total lines count"), "initials beat scattered letters");
        Check(FuzzyMatcher.Match("as save", "Save as…") is not null, "words in any order");
        Check(S("tab", "Next tab") > S("tab", "Toggle a bookmark"), "contiguous beats scattered");

        var items = new List<PaletteCandidate>
        {
            new() { Title = "Sort lines descending", Order = 0 },
            new() { Title = "Sort lines ascending", Order = 1 },
            new() { Title = "Close tab", Order = 2, Group = "x" },
        };
        var rows = PaletteFilter.Apply(items, "sort asc");
        Check(rows.Count >= 1 && rows[0].Item.Title == "Sort lines ascending", "filter ranks 'sort asc' first, got " + string.Join(", ", rows.Select(r => r.Item.Title)));
        var all = PaletteFilter.Apply(items, "");
        Check(all.Count == 3 && all[2].ShowGroup && all[2].GroupStart && !all[0].ShowGroup, "unfiltered list keeps groups");
    }

    private static void Symbols()
    {
        var cs = SymbolScanner.Scan(Sample, "csharp");
        string Dump(IEnumerable<Symbol> s) => string.Join(", ", s.Select(x => $"{x.Kind}:{x.Name}@{x.Line}"));
        Check(cs.Any(s => s is { Name: "Greeter", Kind: SymbolKind.Class }), "C# class: " + Dump(cs));
        Check(cs.Any(s => s is { Name: "Greet", Kind: SymbolKind.Method, Container: "Greeter" }), "C# method with container: " + Dump(cs));
        Check(cs.Any(s => s.Name == "CountAsync"), "C# generic async method: " + Dump(cs));
        Check(cs.Any(s => s is { Name: "IShape", Kind: SymbolKind.Interface }) && cs.Any(s => s is { Name: "Mood", Kind: SymbolKind.Enum }), "C# interface / enum: " + Dump(cs));
        Check(cs.Any(s => s.Name == "Main" && s.Container == "Program"), "C# Main in Program: " + Dump(cs));
        Check(!cs.Any(s => s.Name is "if" or "for" or "Append" or "WriteLine" or "FromResult"), "C# no calls / keywords: " + Dump(cs));

        var py = SymbolScanner.Scan("import os\n\nclass Cat:\n    def meow(self):\n        pass\n\ndef main():\n    Cat().meow()\n", "python");
        Check(py.Any(s => s is { Name: "Cat", Kind: SymbolKind.Class }) && py.Any(s => s is { Name: "meow", Kind: SymbolKind.Method }) && py.Any(s => s is { Name: "main", Kind: SymbolKind.Function }), "python: " + Dump(py));

        var js = SymbolScanner.Scan("export function load(url) {\n}\nconst add = (a, b) => a + b;\nclass Store {\n  get(key) {\n    if (key) {\n    }\n  }\n}\n", "javascript");
        Check(js.Any(s => s.Name == "load") && js.Any(s => s.Name == "add") && js.Any(s => s is { Name: "Store", Kind: SymbolKind.Class }) && js.Any(s => s is { Name: "get", Kind: SymbolKind.Method }) && !js.Any(s => s.Name == "if"), "javascript: " + Dump(js));

        var mdSyms = SymbolScanner.Scan(Readme, "markdown");
        Check(mdSyms.Select(s => s.Name).SequenceEqual(["BetterNotepad", "Features", "Tabs and session", "Install", "Setext heading"]), "markdown headings: " + Dump(mdSyms));
        Check(mdSyms.FirstOrDefault(s => s.Name == "Tabs and session") is { Level: 3, Container: "Features" }, "markdown levels / parents");

        var go = SymbolScanner.Scan("package main\n\ntype Server struct {\n}\n\nfunc (s *Server) Run() error {\n}\n\nfunc main() {\n}\n", "go");
        Check(go.Any(s => s is { Name: "Server", Kind: SymbolKind.Struct }) && go.Any(s => s is { Name: "Run", Kind: SymbolKind.Method }) && go.Any(s => s is { Name: "main", Kind: SymbolKind.Function }), "go: " + Dump(go));

        var c = SymbolScanner.Scan("#include <stdio.h>\n\nstatic int add(int a, int b)\n{\n    return add2(a, b);\n}\n\nint main(void) {\n    printf(\"x\");\n}\nint decl(int x);\n", "c");
        Check(c.Any(s => s.Name == "add") && c.Any(s => s.Name == "main") && !c.Any(s => s.Name is "decl" or "printf" or "add2"), "c: " + Dump(c));
    }

    // ───────────── flows ─────────────

    private static PaletteView? Open(MainWindow w, PaletteMode mode, string? text = null)
    {
        CommandPalette.Show(w, mode, text);
        return CommandPalette.Find(w);
    }

    private static async Task CommandsFlow(MainWindow w)
    {
        var scratch = w.NewTab(content: "pear\napple\nfig");
        await Idle(w);
        var v = Open(w, PaletteMode.Commands);
        Check(v is { Mode: PaletteMode.Commands }, "commands mode opens");
        if (v is null) return;
        Check(v.Rows.Count > 20, $"many commands listed ({v.Rows.Count})");
        Check(v.Rows.All(r => r.Item.Accept is not null), "every command row runs something");
        Check(v.Rows.Any(r => r.Item.Id == "file.save" && r.Item.Gesture?.ToString() == "Ctrl+S"), "key chips from the keymap");
        Check(v.Rows.All(r => CommandRegistry.Get(r.Item.Id!)?.ShowInPalette == true), "hidden commands stay hidden");

        var title = CommandRegistry.Get("tools.sortAsc")!.Title;
        v.SetQuery(title);
        Check(v.Selected?.Item.Id == "tools.sortAsc", "exact title ranks first, got " + v.Selected?.Item.Id);
        Check(v.Selected?.TitleHits?.Length == title.Length, "whole title highlighted");
        v.SetQuery("sort lines asc");
        Check(v.Rows.FirstOrDefault()?.Item.Id == "tools.sortAsc", "English words find the command in any UI language, got " + v.Rows.FirstOrDefault()?.Item.Id);
        v.AcceptSelected();
        Check(!CommandPalette.IsOpen(w), "palette closes before the command runs");
        await Idle(w);
        Check(w.ActiveView?.Editor?.Document.Text.Replace("\r\n", "\n") == "apple\nfig\npear", "command ran on the active tab: " + w.ActiveView?.Editor?.Document.Text);
        Check(PaletteHistory.I.Commands.FirstOrDefault() == "tools.sortAsc", "command remembered as recently used");

        v = Open(w, PaletteMode.Commands);
        Check(v?.Rows.FirstOrDefault() is { ShowGroup: true } first && first.Item.Id == "tools.sortAsc", "recently used command leads the list");
        v?.SetQuery("zzzzqqq");
        Check(v?.Rows.Count == 0, "no match → empty state");
        v?.Close();
        // the fade-out completes on the render clock, which runs slower on a hidden desktop: wait up to 2 s
        for (var i = 0; i < 20 && w.OverlayLayer.Children.OfType<PaletteView>().Any(); i++) await Task.Delay(100);
        Check(CommandPalette.Find(w) is null && !w.OverlayLayer.Children.OfType<PaletteView>().Any(), "closed palette leaves the overlay layer");

        w.Model.ActiveGroup.Remove(scratch);
        DocumentManager.I.Close(scratch);
        await Idle(w);
    }

    private static async Task PrefixFlow(MainWindow w)
    {
        var v = Open(w, PaletteMode.Files);
        if (v is null)
        {
            Check(false, "files mode opens");
            return;
        }
        v.SetQuery(">");
        Check(v.Mode == PaletteMode.Commands && v.Query == "", "'>' switches to commands");
        v.BackspaceOnEmpty();
        Check(v.Mode == PaletteMode.Files, "Backspace on empty returns to files");
        v.SetQuery(":12");
        Check(v.Mode == PaletteMode.GoToLine && v.Query == "12", "':' switches to go to line with the rest kept");
        v.SetQuery("@");
        Check(v.Mode == PaletteMode.Symbols, "'@' switches to symbols");
        CommandPalette.Show(w, PaletteMode.Commands);
        Check(v.Mode == PaletteMode.Commands && CommandPalette.Find(w) == v, "another palette shortcut switches the open palette");
        var before = v.Rows.Count > 1 ? v.Selected : null;
        CommandPalette.Show(w, PaletteMode.Commands);
        Check(before is null || v.Selected != before, "same shortcut again walks the list");
        v.Close();
        await Idle(w);
    }

    private static async Task LineFlow(MainWindow w)
    {
        var ed = w.ActiveView?.Editor;
        if (ed is null)
        {
            Check(false, "editor for go to line");
            return;
        }
        ed.TextArea.Caret.Location = new ICSharpCode.AvalonEdit.Document.TextLocation(2, 1);
        var v = Open(w, PaletteMode.GoToLine);
        Check(v?.Rows.FirstOrDefault()?.Item.IsInfo == true, "empty go to line shows the prompt");
        v?.SetQuery("30");
        Check(ed.TextArea.Caret.Line == 30, "live preview moves the caret, at " + ed.TextArea.Caret.Line);
        v?.Close();
        Check(ed.TextArea.Caret.Line == 2 && ed.SelectionLength == 0, "Esc restores the caret");
        await Idle(w);

        v = Open(w, PaletteMode.GoToLine, ":25:3");
        Check(v?.Query == "25:3", "initial text with prefix");
        v?.AcceptSelected();
        await Idle(w);
        Check(ed.TextArea.Caret.Line == 25 && ed.TextArea.Caret.Column == 3, $"Enter goes to 25:3, at {ed.TextArea.Caret.Line}:{ed.TextArea.Caret.Column}");

        v = Open(w, PaletteMode.GoToLine, "9999");
        Check(v?.Selected?.Item.Id?.StartsWith("line:" + ed.Document.LineCount + ":") == true, "line beyond the end clamps to the last line");
        v?.SetQuery("abc");
        Check(v?.Selected?.Item.IsInfo == true, "non-number shows a hint");
        v?.Close();
        await Idle(w);
    }

    private static async Task SymbolFlow(MainWindow w)
    {
        var ed = w.ActiveView?.Editor;
        if (ed is null) return;
        ed.TextArea.Caret.Location = new ICSharpCode.AvalonEdit.Document.TextLocation(1, 1);
        var v = Open(w, PaletteMode.Symbols);
        Check(v?.Rows.Any(r => r.Item.Title == "Greet") == true, "symbols listed: " + string.Join(", ", v?.Rows.Select(r => r.Item.Title) ?? []));
        v?.SetQuery("greet");
        Check(v?.Selected?.Item.Title == "Greet", "exact symbol first, got " + v?.Selected?.Item.Title);
        var greetLine = Sample.Replace("\r\n", "\n").Split('\n').ToList().FindIndex(l => l.Contains("string Greet(")) + 1;
        Check(ed.TextArea.Caret.Line == greetLine, $"symbol preview on line {greetLine}, at {ed.TextArea.Caret.Line}");
        v?.AcceptSelected();
        await Idle(w);
        Check(ed.TextArea.Caret.Line == greetLine, "Enter jumps to the symbol");
    }

    private static async Task LanguageFlow(MainWindow w, TextDocument doc)
    {
        var v = Open(w, PaletteMode.Language);
        Check(v?.Selected?.Item.Id == "lang:csharp", "current language preselected, got " + v?.Selected?.Item.Id);
        v?.SetQuery("pyth");
        Check(v?.Selected?.Item.Id == "lang:python", "language found by name");
        v?.AcceptSelected();
        await Idle(w);
        Check(doc.LanguageId == "python" && doc.LanguageExplicit, "language applied explicitly");
        v = Open(w, PaletteMode.Language);
        v?.SetQuery(".cs");
        Check(v?.Rows.FirstOrDefault()?.Item.Id == "lang:csharp", "language found by extension, got " + v?.Rows.FirstOrDefault()?.Item.Id);
        v?.AcceptSelected();
        await Idle(w);
        Check(doc.LanguageId == "csharp", "language back to C#");
    }

    private static async Task FilesFlow(MainWindow w, string mdPath)
    {
        var v = Open(w, PaletteMode.Files);
        if (v is null) return;
        Check(v.Rows.Count >= 5, $"tabs + recent files listed ({v.Rows.Count})");
        Check(v.Selected == v.Rows.ElementAtOrDefault(1), "the previous tab is preselected");
        Check(v.Rows.Any(r => r.Item.Id == "file:" + FileUtil.Normalize(Path.Combine(AppEnv.LocalDir, "fixtures", "notes.txt"))), "recent file listed");
        Check(v.Rows.Count(r => r.ShowGroup) == 2, "two groups: open tabs, recently opened");
        v.SetQuery("readme");
        Check(v.Selected?.Item.Title == "README.md", "file found by name, got " + v.Selected?.Item.Title);
        v.SetQuery("fixtures rdm");
        Check(v.Selected?.Item.Title == "README.md" && v.Selected.DetailHits?.Length > 0, "file found by folder + name");
        v.AcceptSelected();
        await Idle(w);
        Check(FileUtil.PathEquals(w.ActiveDocument?.FilePath, mdPath), "Enter activates the open tab");

        v = Open(w, PaletteMode.Symbols);
        Check(v?.Rows.Select(r => r.Item.Letter).Take(3).SequenceEqual(["H1", "H2", "H3"]) == true, "markdown headings as symbols");
        v?.Close();
        w.Execute("tabs.next"); // back to Greeter.cs for the snapshots (quick switch, no Ctrl held)
        await Idle(w);
    }

    private static async Task SwitcherFlow(MainWindow w)
    {
        var group = w.Model.ActiveGroup;
        var mru = group.Mru.ToList();
        if (mru.Count < 3)
        {
            Check(false, "three tabs for the switcher");
            return;
        }
        var s = TabSwitcher.Open(w, reverse: false, held: true, showNow: true);
        Check(s.IsOpen && s.SelectedIndex == 1 && s.PanelCard is not null, "held Ctrl+Tab opens the panel on the next tab");
        s.Step(1);
        Check(s.SelectedIndex == 2, "Tab steps");
        s.Step(-1);
        s.Step(-1);
        s.Step(-1);
        Check(s.SelectedIndex == mru.Count - 1, "Shift+Tab wraps");
        s.Cancel();
        Check(!s.IsOpen && group.Active == mru[0], "Esc keeps the tab");

        s = TabSwitcher.Open(w, reverse: false, held: true, showNow: true);
        s.Step(1);
        s.Commit();
        Check(group.Active == mru[2], "release activates the selection");

        var now = group.Active;
        var previous = group.Mru[1];
        s = TabSwitcher.Open(w, reverse: false, held: false, showNow: false);
        Check(!s.IsOpen && group.Active == previous && s.PanelCard is null, "quick tap switches without a panel");
        TabSwitcher.Open(w, reverse: false, held: false, showNow: false);
        Check(group.Active == now, "two taps toggle between the last two tabs");
        // the fade-out completes on the render clock, which runs slower on a hidden desktop: wait up to 2 s
        for (var i = 0; i < 20 && w.OverlayLayer.Children.OfType<FrameworkElement>().Any(e => e.GetType().Name == "SwitcherPanel"); i++) await Task.Delay(100);
        Check(!w.OverlayLayer.Children.OfType<FrameworkElement>().Any(e => e.GetType().Name == "SwitcherPanel"), "switcher panel removed");
    }

    // ───────────── off-screen renders ─────────────

    private static async Task Snapshots(MainWindow w, string dir, TextDocument csDoc)
    {
        Directory.CreateDirectory(dir);
        var appearance = SettingsService.I.Get<AppearanceSettings>();
        var group = w.Model.ActiveGroup;
        foreach (var (theme, lang) in new[] { ("aurora", "pl"), ("daylight", "en") })
        {
            appearance.Theme = theme;
            appearance.Language = lang;
            group.Active = csDoc;
            await Idle(w);
            if (w.ActiveView?.Editor is { } ed)
            {
                ed.TextArea.Caret.Location = new ICSharpCode.AvalonEdit.Document.TextLocation(12, 9);
                ed.ScrollToHome();
            }

            async Task Shot(string name, PaletteMode mode, string? text, bool crop = true)
            {
                var v = Open(w, mode, text);
                await Task.Delay(420);
                await Idle(w);
                Save(w, Path.Combine(dir, $"palette-{name}-{theme}.png"), null);
                if (crop && v is not null) Save(w, Path.Combine(dir, $"palette-{name}-{theme}-2x.png"), v.Card);
                v?.Close();
                await Task.Delay(200);
            }

            await Shot("commands", PaletteMode.Commands, null);
            await Shot("commands-query", PaletteMode.Commands, "sort");
            await Shot("files", PaletteMode.Files, null);
            await Shot("files-query", PaletteMode.Files, "fix gre");
            await Shot("line", PaletteMode.GoToLine, ":17");
            await Shot("symbols", PaletteMode.Symbols, null);
            await Shot("language", PaletteMode.Language, null, crop: false);
            await Shot("empty", PaletteMode.Commands, ">qqqzzz", crop: false);

            var s = TabSwitcher.Open(w, reverse: false, held: true, showNow: true);
            await Task.Delay(420);
            await Idle(w);
            Save(w, Path.Combine(dir, $"palette-switcher-{theme}.png"), null);
            Save(w, Path.Combine(dir, $"palette-switcher-{theme}-2x.png"), s.PanelCard);
            s.Cancel();
            await Task.Delay(200);
        }
        appearance.Theme = "system";
        appearance.Language = "system";
        await Idle(w);
    }

    /// <summary>Renders the window's visual tree (never the screen) over the theme background; optional 2× crop.</summary>
    private static void Save(MainWindow w, string path, FrameworkElement? crop)
    {
        var root = w.Root;
        root.UpdateLayout();
        var width = root.ActualWidth;
        var height = root.ActualHeight;
        var scale = crop is null ? 1.0 : 2.0;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (Application.Current.TryFindResource("Brush.Bg") is Brush bg) dc.DrawRectangle(bg, null, new Rect(0, 0, width, height));
            var vb = new VisualBrush(root)
            {
                Stretch = Stretch.None,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, width, height),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, width, height),
            };
            dc.DrawRectangle(vb, null, new Rect(0, 0, width, height));
        }
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(dv);
        BitmapSource image = rtb;
        if (crop is not null)
        {
            var r = crop.TransformToAncestor(root).TransformBounds(new Rect(0, 0, crop.ActualWidth, crop.ActualHeight));
            r.Inflate(28, 28);
            r.Intersect(new Rect(0, 0, width, height));
            image = new CroppedBitmap(rtb, new Int32Rect((int)(r.X * scale), (int)(r.Y * scale), (int)(r.Width * scale), (int)(r.Height * scale)));
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
#endif
