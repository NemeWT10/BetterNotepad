#if DEBUG
using System.Text.RegularExpressions;
using System.Windows.Threading;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Editor.Export;

/// <summary>
/// Debug self-test of "Copy with formatting" and the thesis listing: capture (indentation, dedent, whole file),
/// document palette, listing caption and numbering, options, long-line warning, context menu. The clipboard is never
/// touched — in test mode <see cref="RichCopy.LastTestCopy"/> holds what would have been copied.
/// </summary>
internal static class ExportSelfTest
{
    private const string Code =
        "namespace Demo;\n" +
        "\n" +
        "public class Parser\n" +
        "{\n" +
        "    // Komentarz: zażółć gęślą jaźń\n" +
        "    public int Parse(string text)\n" +
        "    {\n" +
        "        var total = 0; // <tag> & \"quotes\"\n" +
        "        var message = \"This line is deliberately much longer than eighty characters to trigger the warning\";\n" +
        "        return total;\n" +
        "    }\n" +
        "}\n";

    public static async Task Run(MainWindow w, List<string> failures)
    {
        void Check(bool ok, string what)
        {
            if (!ok) failures.Add("export: " + what);
        }

        var doc = w.NewTab(content: Code, languageId: "csharp");
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (w.ActiveView?.Editor is not { } ed)
        {
            failures.Add("export: no editor");
            return;
        }
        var s = SettingsService.I.Get<CopySettings>();
        try
        {
            // ── command, shortcut, menus ──
            Check(CommandRegistry.Get("editor.copyFormatted") is not null && CommandRegistry.Get("editor.copyAsListing") is not null, "commands registered");
            Check(Keymap.GestureText("editor.copyFormatted") == "Ctrl+Shift+C", "Ctrl+Shift+C = copy with formatting, got " + Keymap.GestureText("editor.copyFormatted"));
            if (ed.ContextMenu is { } menu)
            {
                w.FillEditorMenu(ed, menu);
                var headers = menu.Items.OfType<System.Windows.Controls.MenuItem>().Select(i => i.Header as string).ToList();
                Check(headers.Contains(CommandRegistry.Get("editor.copyFormatted")!.Title) && headers.Contains(CommandRegistry.Get("editor.copyAsListing")!.Title),
                    "editor context menu has both copy commands");
            }
            else Check(false, "editor has a context menu");

            // ── copy with formatting: a selection starting inside the indentation takes the whole indent, then dedents ──
            var d = ed.Document;
            var start = d.GetLineByNumber(6).Offset + 2;
            var end = d.GetLineByNumber(11).EndOffset;
            ed.Select(start, end - start);
            s.ThemeColors = false;
            s.LineNumbers = false;
            s.Dedent = true;
            RichCopy.CopyFormatted(ed);
            var copy = RichCopy.LastTestCopy;
            Check(copy is not null, "copy with formatting produced output");
            if (copy is { } c)
            {
                Check(c.Text.StartsWith("public int Parse(string text)\r\n{", StringComparison.Ordinal), "dedented plain text: " + Head(c.Text));
                Check(c.Text.Split("\r\n").Length == 6, "6 lines copied");
                Check(c.Html.Contains("font-family:Consolas") && c.Html.Contains("background-color:#F6F8FA") && c.Html.Contains("<table"), "document box (Consolas, light background)");
                Check(c.Html.Contains("color:#0000FF") || c.Html.Contains("color:#AF00DB") || c.Html.Contains("color:#267F99"), "syntax colors from the document palette: " + string.Join(",", Regex.Matches(c.Html, "color:#([0-9A-F]{6})").Select(m => m.Groups[1].Value).Distinct())
                    + " highlighter=" + (ed.TextArea.TextView.Services.GetService(typeof(ICSharpCode.AvalonEdit.Highlighting.IHighlighter)) is not null) + " lang=" + doc.LanguageId + " def=" + (ed.SyntaxHighlighting?.Name ?? "null"));
                Check(c.Html.Contains("&lt;tag&gt; &amp; &quot;quotes&quot;"), "HTML escaping");
                Check(c.Rtf.StartsWith(@"{\rtf1", StringComparison.Ordinal) && c.Rtf.Contains(@"\trowd"), "RTF table");
            }
            s.LineNumbers = true;
            RichCopy.CopyFormatted(ed);
            Check(RichCopy.LastTestCopy is { } ln && ln.Html.Contains("text-align:right") && ln.Html.Contains(">6</span>"), "line numbers column 1..6");
            s.LineNumbers = false;
            s.Dedent = false;
            RichCopy.CopyFormatted(ed);
            Check(RichCopy.LastTestCopy is { } nd && nd.Text.StartsWith("    public int Parse", StringComparison.Ordinal), "dedent off keeps the indentation");
            s.Dedent = true;

            // whole file when nothing is selected
            ed.Select(0, 0);
            RichCopy.CopyFormatted(ed);
            Check(RichCopy.LastTestCopy is { } wf && wf.Text.StartsWith("namespace Demo;", StringComparison.Ordinal) && wf.Text.TrimEnd().EndsWith('}'), "no selection = whole file");

            // ── thesis listing ──
            ed.Select(start, end - start);
            s.ListingChapter = 2;
            s.ListingNumber = 4;
            s.ListingColors = s.ListingLineNumbers = s.ListingFrame = s.ListingCaptionBelow = s.ListingSource = false;
            s.ListingDedent = true;
            s.ListingFont = "Consolas";
            s.ListingFontSize = 10;
            var dlg = ListingDialog.CreateForTest(ed);
            Check(dlg.NumberText == "5", "suggests the next number in the last chapter, got " + dlg.NumberText);
            dlg.SetChapter("3");
            Check(dlg.NumberText == "1", "a new chapter starts at 1, got " + dlg.NumberText);
            dlg.SetChapter("2");
            Check(dlg.NumberText == "5", "back to the last chapter suggests 5 again, got " + dlg.NumberText);
            dlg.SetChapter("3");
            dlg.SetDescription("Implementacja parsera.");
            Check(dlg.PreviewText == "Listing 3.1. Implementacja parsera", "caption preview without the final period: " + dlg.PreviewText);
            Check(dlg.WarningShown, "warns about the line longer than 79 characters");
            Check(dlg.Copy(null), "listing copied");
            if (RichCopy.LastTestCopy is { } l)
            {
                Check(l.Html.StartsWith("<p style=\"margin-top:6pt;margin-bottom:6pt;", StringComparison.Ordinal) && l.Html.Contains("text-align:center;page-break-after:avoid"),
                    "caption first: 6 pt before/after, centered, kept with the code");
                Check(l.Html.Contains("font-family:Arial;font-size:10pt;color:#000000") && l.Html.Contains(">Listing 3.1. Implementacja parsera</span>"), "caption Arial 10 pt");
                Check(!l.Html.Contains("<table"), "default listing has no table");
                Check(l.Html.Contains("text-indent:0pt") && l.Html.Contains("line-height:100%") && l.Html.Contains("font-family:Consolas,'Courier New',monospace;font-size:10pt"), "code without indents, single spacing, Consolas 10 pt");
                var colors = Regex.Matches(l.Html, "color:#([0-9A-F]{6})").Select(m => m.Groups[1].Value).Distinct().ToList();
                Check(colors.SequenceEqual(["000000"]), "listing is black only: " + string.Join(",", colors));
                Check(l.Rtf.Contains(@"\qc") && l.Rtf.Contains(@"\keepn") && l.Rtf.Contains(@"\f1\fs20") && l.Rtf.Contains(@"\fi0"), "RTF caption and code paragraphs");
                Check(l.Text.StartsWith("public int Parse", StringComparison.Ordinal), "plain text is the code only");
            }
            Check(s.ListingChapter == 3 && s.ListingNumber == 1, $"remembers listing 3.1, got {s.ListingChapter}.{s.ListingNumber}");
            Check(ListingDialog.CreateForTest(ed).NumberText == "2", "next dialog suggests 3.2");

            // options
            s.ListingCaptionBelow = true;
            s.ListingSource = true;
            var below = ListingDialog.CreateForTest(ed);
            below.SetDescription("Opis");
            below.Copy(null);
            if (RichCopy.LastTestCopy is { } b)
            {
                Check(b.Html.IndexOf("Listing 3.2. Opis", StringComparison.Ordinal) > b.Html.IndexOf("Parse", StringComparison.Ordinal), "caption below the code");
                Check(b.Html.TrimEnd().EndsWith(Core.Localization.Loc.T("listing.sourceDefault") + "</span></p>", StringComparison.Ordinal), "source line last");
            }
            s.ListingCaptionBelow = false;
            s.ListingSource = false;
            s.ListingFrame = true;
            s.ListingLineNumbers = true;
            s.ListingColors = true;
            s.ListingFont = "Courier New";
            var boxed = ListingDialog.CreateForTest(ed);
            boxed.Copy(null);
            if (RichCopy.LastTestCopy is { } x)
            {
                Check(x.Html.Contains("<table") && x.Html.Contains("border-top:solid #000000 0.75pt;mso-border-top-alt:solid #000000 0.5pt")
                      && x.Rtf.Contains(@"\brdrw10"), "frame = black border (0.5 pt for Word, 1 px in HTML)");
                Check(x.Html.Contains("font-family:'Courier New'"), "Courier New option");
                Check(Regex.Matches(x.Html, "color:#([0-9A-F]{6})").Select(m => m.Groups[1].Value).Any(v => v is not ("000000" or "595959")), "listing syntax colors when enabled");
            }
        }
        catch (Exception ex)
        {
            failures.Add("export: " + ex);
        }
        finally
        {
            s.ListingFrame = s.ListingLineNumbers = s.ListingColors = false;
            s.ListingFont = "Consolas";
            w.Model.ActiveGroup.Remove(doc);
            DocumentManager.I.Close(doc);
        }
    }

    private static string Head(string text) => text.Length > 60 ? text[..60] : text;
}
#endif
