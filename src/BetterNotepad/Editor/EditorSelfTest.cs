#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Editor.Completion;
using BetterNotepad.Editor.Folding;
using BetterNotepad.Editor.MultiCaret;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Editor;

/// <summary>
/// Debug self-test of the IDE layer (part of BETTERNOTEPAD_SELFTEST): multiple carets (typing, undo, Ctrl+D,
/// select all, paste), auto-close, smart Enter, bracket match, folding, bookmarks, occurrences. With
/// BETTERNOTEPAD_EDITOR_PNG=&lt;dir&gt; it renders the editor off-screen (RenderTargetBitmap, never the screen) in Aurora and Daylight.
/// </summary>
internal static class EditorSelfTest
{
    private static void Check(List<string> failures, bool ok, string what)
    {
        if (!ok) failures.Add("editor: " + what);
    }

    private static Task Idle(MainWindow w) => w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    public static async Task Run(MainWindow w, List<string> failures)
    {
        var doc = w.NewTab(content: "foo\nfoo\nfoo\n");
        await Idle(w);
        if (w.ActiveView?.Editor is not { } ed)
        {
            failures.Add("editor: no scratch editor");
            return;
        }
        try
        {
            await Carets(w, ed, failures);
            Typing(ed, doc, failures);
            await Decorations(w, ed, doc, failures);
            Bookmarks(ed, failures);
            Completion(ed, doc, failures);
            Commands(ed, failures);
            if (Environment.GetEnvironmentVariable("BETTERNOTEPAD_EDITOR_PNG") is { Length: > 0 } dir) await Snapshots(w, ed, doc, dir, failures);
        }
        finally
        {
            ExtraCaretLayer.ForceVisible = false;
            FoldMargin.ForceShowAll = false;
            CompletionController.ForceOpen = false;
            ed.Features.Completion.Close();
            w.Model.ActiveGroup.Remove(doc);
            DocumentManager.I.Close(doc);
        }
    }

    private static string T(CodeEditor ed) => ed.Document.Text.Replace("\r\n", "\n");

    private static async Task Carets(MainWindow w, CodeEditor ed, List<string> failures)
    {
        var mc = ed.Features.Carets;
        ed.Document.Text = "foo\nfoo\nfoo\n";
        ed.Document.UndoStack.ClearAll();
        ed.CaretOffset = 0;
        mc.AddCaretVertical(+1);
        mc.AddCaretVertical(+1);
        Check(failures, mc.Count == 3, $"Ctrl+Alt+Down twice gave {mc.Count} carets");

        // typing through the real input route (PreviewTextInput on the text area)
        var input = new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, ed.TextArea, "x"))
        {
            RoutedEvent = TextCompositionManager.PreviewTextInputEvent,
        };
        ed.TextArea.RaiseEvent(input);
        Check(failures, T(ed) == "xfoo\nxfoo\nxfoo\n", "multi-caret typing gave " + T(ed).Replace("\n", "|"));
        mc.Delete(backward: true, word: false);
        mc.TypeText("ab");
        Check(failures, T(ed) == "abfoo\nabfoo\nabfoo\n", "multi-caret backspace + type gave " + T(ed).Replace("\n", "|"));
        mc.Move(CaretMove.LineEnd, extend: false);
        mc.TypeText(";");
        Check(failures, T(ed) == "abfoo;\nabfoo;\nabfoo;\n", "End + type gave " + T(ed).Replace("\n", "|"));
        mc.Move(CaretMove.WordLeft, extend: true);
        Check(failures, mc.All().All(r => r.Length > 0), "Ctrl+Shift+Left did not extend every selection");
        ed.Undo();
        Check(failures, T(ed) == "abfoo\nabfoo\nabfoo\n", "one undo step should remove ';' from all lines, gave " + T(ed).Replace("\n", "|"));
        Check(failures, mc.Count == 1, "undo should collapse to one caret");

        // paste: one line per caret when the counts match
        ed.CaretOffset = 0;
        mc.AddCaretVertical(+1);
        mc.AddCaretVertical(+1);
        mc.Paste("1\r\n2\r\n3\r\n");
        Check(failures, T(ed) == "1abfoo\n2abfoo\n3abfoo\n", "distributed paste gave " + T(ed).Replace("\n", "|"));
        mc.Clear();
        Check(failures, mc.Count == 1, "Esc (collapse)");

        // Ctrl+D: word first, then whole-word next occurrences (abcd is skipped), wrapping around
        ed.Document.Text = "abc abcd abc\nabc";
        ed.CaretOffset = 1;
        mc.AddNextOccurrence();
        Check(failures, mc.Count == 1 && ed.SelectionStart == 0 && ed.SelectionLength == 3, "first Ctrl+D selects the word");
        mc.AddNextOccurrence();
        mc.AddNextOccurrence();
        Check(failures, mc.Count == 3 && ed.SelectionStart == 13, $"Ctrl+D ×3 gave {mc.Count} carets, primary at {ed.SelectionStart}");
        mc.AddNextOccurrence();
        Check(failures, mc.Count == 3, "Ctrl+D past the last occurrence must not add duplicates");
        mc.TypeText("Z");
        Check(failures, T(ed) == "Z abcd Z\nZ", "typing over Ctrl+D selections gave " + T(ed).Replace("\n", "|"));
        ed.Undo();
        Check(failures, T(ed) == "abc abcd abc\nabc", "undo after Ctrl+D typing gave " + T(ed).Replace("\n", "|"));

        // Ctrl+Shift+L
        ed.TextArea.ClearSelection();
        ed.CaretOffset = 5;
        mc.SelectAllOccurrences();
        Check(failures, mc.Count == 1, "abcd occurs once");
        ed.TextArea.ClearSelection();
        ed.CaretOffset = 0;
        mc.SelectAllOccurrences();
        Check(failures, mc.Count == 3 && mc.All().All(r => r.Length == 3), $"Ctrl+Shift+L gave {mc.Count} carets");

        // Alt+click toggles a caret
        mc.Clear();
        ed.TextArea.ClearSelection();
        ed.CaretOffset = 0;
        mc.ToggleCaretAt(9, mc.Primary(), []);
        Check(failures, mc.Count == 2 && ed.CaretOffset == 9, "Alt+click adds a caret");
        mc.ToggleCaretAt(9, mc.Primary(), [.. mc.Extra]);
        Check(failures, mc.Count == 1 && ed.CaretOffset == 0, "Alt+click on a caret removes it");
        await Idle(w);
    }

    private static void Typing(CodeEditor ed, TextDocument doc, List<string> failures)
    {
        var mc = ed.Features.Carets;
        doc.LanguageId = "csharp";
        ed.Document.Text = "";
        mc.TypeText("(");
        Check(failures, T(ed) == "()" && ed.CaretOffset == 1, "auto-close ( gave " + T(ed));
        mc.TypeText(")");
        Check(failures, T(ed) == "()" && ed.CaretOffset == 2, "type-over ) gave " + T(ed));
        ed.Document.Text = "";
        mc.TypeText("[");
        mc.Delete(backward: true, word: false);
        Check(failures, T(ed) == "", "backspace in [] gave " + T(ed));
        ed.Document.Text = "word";
        ed.Select(0, 4);
        mc.TypeText("\"");
        Check(failures, T(ed) == "\"word\"" && ed.SelectedText == "word", "wrap selection in quotes gave " + T(ed));
        ed.Document.Text = "    if (x) {}";
        ed.CaretOffset = 12;
        mc.TypeText("\n");
        Check(failures, T(ed) == "    if (x) {\n        \n    }" && ed.TextArea.Caret.Line == 2 && ed.TextArea.Caret.Column == 9,
            "smart Enter between braces gave " + T(ed).Replace("\n", "|"));
        ed.Document.Text = "    foo();";
        ed.CaretOffset = ed.Document.TextLength;
        mc.TypeText("\n");
        Check(failures, T(ed) == "    foo();\n    ", "Enter keeps indentation, gave " + T(ed).Replace("\n", "|"));
    }

    private static async Task Decorations(MainWindow w, CodeEditor ed, TextDocument doc, List<string> failures)
    {
        var f = ed.Features;
        ed.Document.Text = "f(\")\") + a[b(c)]";
        ed.CaretOffset = 2;
        f.Brackets.Update();
        Check(failures, f.Brackets.Pair == (1, 5), $"bracket match skipping a string gave {f.Brackets.Pair}");
        ed.CaretOffset = ed.Document.TextLength;
        f.Brackets.Update();
        Check(failures, f.Brackets.Pair == (10, 15), $"bracket match before caret gave {f.Brackets.Pair}");

        ed.Document.Text = "foo bar foo";
        ed.Select(0, 3);
        f.Occurrences.Update();
        Check(failures, f.Occurrences.Term == "foo", "occurrence term");

        // folding: braces (strings with braces must not count), indent, markdown
        ed.Document.Text = "class A\n{\n    string s = \"{\";\n    void F()\n    {\n    }\n}\n";
        await Idle(w);
        f.Folding.UpdateNow();
        var count = f.Folding.Manager?.AllFoldings.Count() ?? -1;
        Check(failures, count == 2, $"brace folding found {count} regions");
        f.FoldAll(true);
        Check(failures, f.Folding.Manager?.AllFoldings.All(s => s.IsFolded) == true, "fold all");
        f.FoldAll(false);
        ed.CaretOffset = ed.Document.GetLineByNumber(4).Offset + 5;
        f.Fold();
        var folded = f.Folding.Manager?.AllFoldings.Where(s => s.IsFolded).ToList();
        Check(failures, folded?.Count == 1 && ed.Document.GetLineByOffset(folded[0].StartOffset).LineNumber == 4, "fold at caret folds the method body (Allman brace folds from the signature line)");
        f.Unfold();
        Check(failures, f.Folding.Manager?.AllFoldings.Any(s => s.IsFolded) == false, "unfold");

        var py = FoldingStrategies.Indent("def f():\n    x = 1\n\n    if x:\n        y\nz\n", 4);
        Check(failures, py.Count == 2, $"indent folding found {py.Count} regions");
        var md = FoldingStrategies.Markdown("# A\ntext\n## B\nmore\n```\ncode\n```\n# C\nend\n");
        Check(failures, md.Count == 4, $"markdown folding found {md.Count} regions");
        doc.LanguageId = "text";
        await Idle(w);
        Check(failures, f.Folding.Manager is null, "plain text has no folding");
        doc.LanguageId = "csharp";
    }

    private static void Bookmarks(CodeEditor ed, List<string> failures)
    {
        var f = ed.Features;
        ed.Document.Text = "a\nb\nc\nd\n";
        var set = f.Bookmarks;
        Check(failures, set is not null, "bookmark set");
        if (set is null) return;
        set.Clear();
        ed.CaretOffset = ed.Document.GetLineByNumber(2).Offset;
        f.ToggleBookmark();
        Check(failures, ed.Model!.Bookmarks.SequenceEqual([2]), "toggle bookmark on line 2");
        ed.Document.Insert(0, "x\n");
        Check(failures, ed.Model.Bookmarks.SequenceEqual([3]), "bookmark follows its line, got " + string.Join(",", ed.Model.Bookmarks));
        ed.CaretOffset = 0;
        Check(failures, f.GoToBookmark(backward: false) && ed.TextArea.Caret.Line == 3, "next bookmark");
        set.Toggle(5);
        Check(failures, f.GoToBookmark(backward: true) && ed.TextArea.Caret.Line == 5, "previous bookmark wraps around");
        set.Clear();
        Check(failures, ed.Model.Bookmarks.Count == 0 && !f.GoToBookmark(false), "clear bookmarks");
    }

    private static void Completion(CodeEditor ed, TextDocument doc, List<string> failures)
    {
        var c = ed.Features.Completion;
        doc.LanguageId = "csharp";
        CompletionController.ForceOpen = true;
        const string nl = "\n";
        ed.Document.Text = "alphabet alpine" + nl + "pu";
        ed.CaretOffset = ed.Document.TextLength;
        c.Refresh(openIfClosed: true);
        Check(failures, c.IsOpen && c.Items.Any(i => i.Text == "public" && i.Keyword), "completion offers the keyword 'public', got " + string.Join(",", c.Items.Select(i => i.Text)));
        c.Close();
        ed.Document.Text = "alphabet alpine" + nl + "al";
        ed.CaretOffset = ed.Document.TextLength;
        c.Refresh(openIfClosed: true);
        Check(failures, c.Items.Count >= 2 && c.Items[0].Text == "alpine" && c.Items.Any(i => i.Text == "alphabet"), "completion words: " + string.Join(",", c.Items.Select(i => i.Text)));
        c.Accept();
        Check(failures, T(ed) == "alphabet alpine" + nl + "alpine" && !c.IsOpen, "accepting a completion gave " + T(ed).Replace(nl, "|"));
        ed.Document.Text = "x";
        ed.CaretOffset = 1;
        c.Refresh(openIfClosed: true);
        Check(failures, !c.IsOpen, "one letter must not open completion");
        // opens on its own while typing (Notepad++ style) after AutoChars letters; 0 = only Ctrl+Space
        var auto = c.AutoChars;
        c.AutoChars = 2;
        ed.Document.Text = "alphabet alpine" + nl;
        ed.CaretOffset = ed.Document.TextLength;
        ed.TextArea.PerformTextInput("a");
        Check(failures, !c.IsOpen, "typing one letter must not open completion");
        ed.TextArea.PerformTextInput("l");
        Check(failures, c.IsOpen && c.Items.Any(i => i.Text == "alpine"), "typing two letters opens completion on its own");
        ed.TextArea.PerformTextInput("p");
        Check(failures, c.IsOpen && c.Items.All(i => i.Text.StartsWith("alp", StringComparison.OrdinalIgnoreCase)), "typing keeps filtering the completion");
        ed.TextArea.PerformTextInput(" ");
        Check(failures, !c.IsOpen, "a space closes completion");
        c.AutoChars = 0;
        ed.TextArea.PerformTextInput("a");
        ed.TextArea.PerformTextInput("l");
        Check(failures, !c.IsOpen, "threshold 0 = only Ctrl+Space");
        c.AutoChars = auto;
        c.Close();
        CompletionController.ForceOpen = false;
    }

    private static void Commands(CodeEditor ed, List<string> failures)
    {
        foreach (var (id, key) in new[] { ("editor.addNextOccurrence", "Ctrl+D"), ("editor.selectAllOccurrences", "Ctrl+Shift+L"), ("editor.addCursorBelow", "Ctrl+Alt+Down"), ("editor.fold", "Ctrl+Shift+["), ("bookmarks.toggle", "Ctrl+F2") })
            Check(failures, Keymap.GestureText(id) == key, $"{id} should be {key}");
        var ctrlD = ed.TextArea.DefaultInputHandler.Editing.InputBindings.Any(b => b.Gesture is KeyGesture { Key: Key.D, Modifiers: ModifierKeys.Control });
        Check(failures, !ctrlD, "AvalonEdit's Ctrl+D (delete line) is still bound");
    }

    // ───────────── off-screen PNGs for visual review ─────────────

    private const string Sample =
        "using System;\n" +
        "\n" +
        "namespace Demo\n" +
        "{\n" +
        "    public sealed class Counter\n" +
        "    {\n" +
        "        private int count;\n" +
        "        private readonly string name = \"count\";\n" +
        "\n" +
        "        public void Add(int value)\n" +
        "        {\n" +
        "            if (value > 0)\n" +
        "            {\n" +
        "                count += value;\n" +
        "            }\n" +
        "        }\n" +
        "\n" +
        "        public int Reset()\n" +
        "        {\n" +
        "            var old = count;\n" +
        "            count = 0;\n" +
        "            return old;\n" +
        "        }\n" +
        "    }\n" +
        "}\n";

    private static async Task Snapshots(MainWindow w, CodeEditor ed, TextDocument doc, string dir, List<string> failures)
    {
        Directory.CreateDirectory(dir);
        var appearance = SettingsService.I.Get<AppearanceSettings>();
        var animations = appearance.Animations;
        appearance.Animations = false; // end states only
        var f = ed.Features;
        foreach (var theme in new[] { "aurora", "daylight" })
        {
            appearance.Theme = theme;
            doc.LanguageId = "csharp";
            ed.Document.Text = Sample;
            await Idle(w);
            f.Folding.UpdateNow();
            // fold Reset(), bookmark two lines, three selections on "count" (Ctrl+D) plus one empty caret
            var reset = f.Folding.Manager?.AllFoldings.FirstOrDefault(s => ed.Document.GetLineByOffset(s.StartOffset).LineNumber == 18);
            if (reset is not null) reset.IsFolded = true;
            f.Bookmarks?.Clear();
            f.Bookmarks?.Toggle(5);
            f.Bookmarks?.Toggle(10);
            var line7 = ed.Document.GetLineByNumber(7);
            ed.Select(line7.Offset + ed.Document.GetText(line7).IndexOf("count", StringComparison.Ordinal), 5);
            f.Carets.AddNextOccurrence();
            f.Carets.AddNextOccurrence();
            var line10 = ed.Document.GetLineByNumber(10);
            f.Carets.Commit([.. f.Carets.All(), new CaretRange(line10.EndOffset, line10.EndOffset)], reveal: false);
            Check(failures, f.Carets.Count == 4, $"snapshot carets: {f.Carets.Count}");
            f.Occurrences.Update();
            var line12 = ed.Document.GetLineByNumber(12);
            var bracketAt = ed.Document.GetText(line12).IndexOf('(');
            ExtraCaretLayer.ForceVisible = true;
            FoldMargin.ForceShowAll = true;
            Repaint(ed);
            await Idle(w);
            Render(ed, Path.Combine(dir, $"editor-{theme}.png"), failures);

            f.Carets.Clear();
            ed.TextArea.ClearSelection();
            ed.CaretOffset = line12.Offset + bracketAt + 1;
            f.Brackets.Update();
            f.Occurrences.Update();
            FoldMargin.ForceShowAll = false;
            Repaint(ed);
            await Idle(w);
            Render(ed, Path.Combine(dir, $"editor-{theme}-brackets.png"), failures);

            // completion popover for "co" on a new line in Add()
            CompletionController.ForceOpen = true;
            var line14 = ed.Document.GetLineByNumber(14);
            ed.Document.Insert(line14.EndOffset, Environment.NewLine + "                co");
            ed.CaretOffset = ed.Document.GetLineByNumber(15).EndOffset;
            f.Completion.Refresh(openIfClosed: true);
            await Idle(w);
            if (f.Completion.IsOpen) Render(f.Completion.Panel, Path.Combine(dir, $"editor-{theme}-completion.png"), failures, pad: 20);
            else failures.Add("editor: completion popover did not open for the snapshot");
            f.Completion.Close();
            CompletionController.ForceOpen = false;
        }
        appearance.Theme = "system";
        appearance.Animations = animations;
    }

    private static void Repaint(CodeEditor ed)
    {
        ed.Features.CaretLayer.InvalidateVisual();
        foreach (var m in ed.TextArea.LeftMargins.OfType<UIElement>()) m.InvalidateVisual();
    }

    /// <summary>Renders <paramref name="element"/> (plus <paramref name="pad"/> around it, over the editor background) to a PNG.</summary>
    private static void Render(FrameworkElement element, string path, List<string> failures, double pad = 0)
    {
        element.UpdateLayout();
        var width = element.ActualWidth + 2 * pad;
        var height = Math.Min(element.ActualHeight, 560) + 2 * pad;
        if (width < 10 || height < 10)
        {
            failures.Add($"editor: nothing to render ({width}x{height})");
            return;
        }
        const double scale = 1.25;
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = new SolidColorBrush(UI.Themes.ThemeManager.I.Current.EditorBg);
            dc.DrawRectangle(bg, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(-pad, -pad, width, height) },
                null, new Rect(0, 0, width, height));
        }
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Log.Info("editor snapshot: " + path);
    }
}
#endif
