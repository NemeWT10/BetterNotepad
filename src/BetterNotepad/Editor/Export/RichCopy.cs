using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Editor.Highlighting;
using BetterNotepad.UI;
using BetterNotepad.UI.Themes;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;

namespace BetterNotepad.Editor.Export;

/// <summary>
/// "Copy with formatting" (Ctrl+Shift+C) and "Copy as thesis listing": the selection (or the whole file) goes to the
/// clipboard as HTML (Google Docs, Word), RTF (Word) and plain text.
/// </summary>
public static class RichCopy
{
    /// <summary>Above this many lines the copy keeps its text but drops syntax colors (highlighting every line costs).</summary>
    private const int MaxColoredLines = 20000;

    public static void CopyFormatted(CodeEditor ed)
    {
        var s = SettingsService.I.Get<CopySettings>();
        var block = Capture(ed, s.ThemeColors ? ThemeManager.I.Syntax : CopyPalettes.Document, s.Dedent, out var whole);
        if (block.Lines.Count == 0)
        {
            Toast(ed, Loc.T("copy.empty"), ToastKind.Info);
            return;
        }
        var style = FormattedStyle(s);
        if (!Put(block, style)) return;
        Toast(ed, Loc.Plural(whole ? "copy.formatted.doneFile" : "copy.formatted.done", block.Lines.Count), ToastKind.Success);
    }

    public static void CopyAsListing(CodeEditor ed) => ListingDialog.Show(ed);

    public static RichCodeStyle FormattedStyle(CopySettings s)
    {
        if (s.ThemeColors)
        {
            var t = ThemeManager.I.Current;
            return new RichCodeStyle
            {
                Text = Opaque(t.EditorText), Background = Opaque(t.EditorBg), Border = Opaque(t.Border), Gutter = Opaque(t.Border),
                LineNumberColor = Opaque(t.LineNumber), LineNumbers = s.LineNumbers, BorderWidth = 0.75,
            };
        }
        return new RichCodeStyle
        {
            Text = Color.FromRgb(0x1F, 0x23, 0x28), Background = Color.FromRgb(0xF6, 0xF8, 0xFA), Border = Color.FromRgb(0xD0, 0xD7, 0xDE),
            Gutter = Color.FromRgb(0xD0, 0xD7, 0xDE), LineNumberColor = Color.FromRgb(0x8C, 0x95, 0x9F), LineNumbers = s.LineNumbers, BorderWidth = 0.75,
        };
    }

    /// <summary>A theme color over the editor background, without alpha (documents have no Mica behind them).</summary>
    private static Color Opaque(Color c)
    {
        if (c.A == 255) return c;
        var bg = ThemeManager.I.Current.IsDark ? Color.FromRgb(0x1E, 0x1E, 0x24) : Colors.White;
        var a = c.A / 255.0;
        return Color.FromRgb((byte)(c.R * a + bg.R * (1 - a)), (byte)(c.G * a + bg.G * (1 - a)), (byte)(c.B * a + bg.B * (1 - a)));
    }

    /// <summary>HTML + RTF + plain text in one clipboard entry. In test mode nothing touches the real clipboard.</summary>
    public static bool Put(CodeBlock block, RichCodeStyle style)
    {
        var html = RichCodeWriter.Html(block, style);
        var rtf = RichCodeWriter.Rtf(block, style);
        var text = block.PlainText;
        if (AppEnv.TestMode)
        {
            LastTestCopy = (html, rtf, text);
            return true;
        }

        var data = new DataObject();
        data.SetData(DataFormats.Html, new MemoryStream(RichCodeWriter.ClipboardHtml(html)));
        data.SetData(DataFormats.Rtf, rtf);
        data.SetData(DataFormats.UnicodeText, text);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (ExternalException) when (attempt < 4)
            {
                Thread.Sleep(60); // another program holds the clipboard for a moment
            }
            catch (Exception ex)
            {
                Log.Error("copy: clipboard unavailable", ex);
                MainOf(null)?.Toasts.Show(Loc.T("copy.clipboardBusy"), ToastKind.Error);
                return false;
            }
        }
    }

    /// <summary>What the last copy would have put on the clipboard (test mode only; SelfTest reads it).</summary>
    internal static (string Html, string Rtf, string Text)? LastTestCopy { get; private set; }

    /// <summary>
    /// The selection — or the whole document when nothing is selected — as styled lines. A selection that starts in a
    /// line's indentation takes the whole indentation (so the common indent can be removed evenly).
    /// </summary>
    public static CodeBlock Capture(CodeEditor ed, IReadOnlyDictionary<SyntaxToken, SyntaxStyle>? syntax, bool dedent, out bool whole)
    {
        var doc = ed.Document;
        var area = ed.TextArea;
        var selection = area.Selection;
        whole = selection.IsEmpty;
        var rectangle = selection is RectangleSelection;
        List<(int, int)> segments = whole
            ? [(0, doc.TextLength)]
            : selection.Segments.Select(seg => (seg.StartOffset, seg.EndOffset)).ToList();

        var highlighter = syntax is null ? null : area.TextView.Services.GetService(typeof(IHighlighter)) as IHighlighter;
        var lineCount = segments.Sum(seg => doc.GetLineByOffset(seg.Item2).LineNumber - doc.GetLineByOffset(seg.Item1).LineNumber + 1);
        if (lineCount > MaxColoredLines) highlighter = null;

        var tab = Math.Max(1, ed.Options.IndentationSize);
        var raw = new List<RawCodeLine>();
        foreach (var (start, end) in segments)
        {
            for (var line = doc.GetLineByOffset(start); line is not null && line.Offset <= end; line = line.NextLine)
            {
                var from = Math.Max(start, line.Offset);
                var to = Math.Min(end, line.EndOffset);
                if (from == to && line.Offset == end && end > start && !rectangle) break; // selection ends at the start of this line
                var partial = false;
                if (from > line.Offset && !rectangle)
                {
                    var prefix = doc.GetText(line.Offset, from - line.Offset);
                    if (prefix.All(c => c is ' ' or '\t')) from = line.Offset;
                    else partial = true;
                }
                var startColumn = VisualColumn(doc.GetText(line.Offset, from - line.Offset), tab);
                raw.Add(new RawCodeLine(Runs(doc, highlighter, syntax, line, from, to), startColumn, partial));
            }
        }
        return CodeBlock.Build(raw, tab, dedent);
    }

    private static int VisualColumn(string prefix, int tab)
    {
        var col = 0;
        foreach (var ch in prefix) col = ch == '\t' ? col + tab - col % tab : col + 1;
        return col;
    }

    private static List<CodeRun> Runs(ICSharpCode.AvalonEdit.Document.TextDocument doc, IHighlighter? highlighter,
        IReadOnlyDictionary<SyntaxToken, SyntaxStyle>? syntax, ICSharpCode.AvalonEdit.Document.DocumentLine line, int from, int to)
    {
        var text = doc.GetText(from, to - from);
        var runs = new List<CodeRun>();
        if (text.Length == 0) return runs;
        var colors = new HighlightingColor?[text.Length];
        if (highlighter is not null && syntax is not null)
        {
            try
            {
                foreach (var s in highlighter.HighlightLine(line.LineNumber).Sections) // nested: inner sections come later and win
                {
                    var a = Math.Max(0, s.Offset - from);
                    var b = Math.Min(text.Length, s.Offset + s.Length - from);
                    for (var i = a; i < b; i++) colors[i] = s.Color;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("copy: highlighting line failed: " + ex.Message);
            }
        }

        var startIndex = 0;
        for (var i = 1; i <= text.Length; i++)
        {
            if (i < text.Length && ReferenceEquals(colors[i], colors[startIndex])) continue;
            runs.Add(MakeRun(text[startIndex..i], colors[startIndex], syntax));
            startIndex = i;
        }
        return runs;
    }

    private static CodeRun MakeRun(string text, HighlightingColor? color, IReadOnlyDictionary<SyntaxToken, SyntaxStyle>? syntax)
    {
        if (color?.Name is not { Length: > 0 } name || syntax is null) return new CodeRun(text);
        if (!syntax.TryGetValue(HighlightingProvider.TokenFor(name), out var style)) return new CodeRun(text);
        return new CodeRun(text, style.Color, style.Bold, style.Italic);
    }

    internal static UI.Windows.MainWindow? MainOf(DependencyObject? element)
        => (element is null ? null : Window.GetWindow(element) as UI.Windows.MainWindow)
           ?? Application.Current?.Windows.OfType<UI.Windows.MainWindow>().FirstOrDefault(w => w.IsActive)
           ?? Application.Current?.MainWindow as UI.Windows.MainWindow;

    private static void Toast(CodeEditor ed, string text, ToastKind kind) => MainOf(ed)?.Toasts.Show(text, kind);
}

/// <summary>Syntax colors for documents: a light palette for "Copy with formatting", a dark, print-safe one for listings.</summary>
public static class CopyPalettes
{
    /// <summary>Close to VS Code Light+: familiar and readable on white.</summary>
    public static readonly IReadOnlyDictionary<SyntaxToken, SyntaxStyle> Document = ThemeManager.SyntaxFor(Palettes.Daylight with
    {
        Id = "copy-document", // SyntaxFor caches by id
        EditorText = Color.FromRgb(0x1F, 0x23, 0x28),
        Syntax = new SyntaxPalette
        {
            Keyword = Rgb(0x00, 0x00, 0xFF), Control = Rgb(0xAF, 0x00, 0xDB), Type = Rgb(0x26, 0x7F, 0x99), Function = Rgb(0x79, 0x5E, 0x26),
            Variable = Rgb(0x00, 0x10, 0x80), Property = Rgb(0x00, 0x10, 0x80), String = Rgb(0xA3, 0x15, 0x15), Number = Rgb(0x09, 0x86, 0x58),
            Constant = Rgb(0x00, 0x00, 0xFF), Comment = Rgb(0x00, 0x80, 0x00), Preprocessor = Rgb(0x80, 0x80, 0x80), Operator = Rgb(0x1F, 0x23, 0x28),
            Punctuation = Rgb(0x1F, 0x23, 0x28), Attribute = Rgb(0x26, 0x7F, 0x99), Tag = Rgb(0x80, 0x00, 0x00), AttrName = Rgb(0xE5, 0x00, 0x00),
            ItalicComments = false,
        },
    });

    /// <summary>Dark, muted hues with bold keywords and italic comments: still legible printed in grayscale.</summary>
    public static readonly IReadOnlyDictionary<SyntaxToken, SyntaxStyle> Print = ThemeManager.SyntaxFor(Palettes.Daylight with
    {
        Id = "copy-print",
        EditorText = Colors.Black,
        Syntax = new SyntaxPalette
        {
            Keyword = Rgb(0x00, 0x00, 0x7F), Control = Rgb(0x5B, 0x0F, 0x80), Type = Rgb(0x00, 0x58, 0x5F), Function = Rgb(0x4A, 0x30, 0x00),
            Variable = Colors.Black, Property = Colors.Black, String = Rgb(0x8B, 0x00, 0x00), Number = Rgb(0x00, 0x56, 0x1B),
            Constant = Rgb(0x00, 0x00, 0x7F), Comment = Rgb(0x4F, 0x6F, 0x4F), Preprocessor = Rgb(0x55, 0x55, 0x55), Operator = Colors.Black,
            Punctuation = Colors.Black, Attribute = Rgb(0x00, 0x58, 0x5F), Tag = Rgb(0x6B, 0x00, 0x00), AttrName = Rgb(0x7A, 0x2E, 0x00),
            ItalicComments = true, BoldKeywords = true,
        },
    });

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
