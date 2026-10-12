#if DEBUG
using System.IO;
using System.Text;
using System.Windows.Documents;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Views;
using BetterNotepad.UI.Windows;
using TextDocument = BetterNotepad.Documents.TextDocument;

namespace BetterNotepad.Modules.Markdown;

/// <summary>Debug-only checks of the Markdown preview wiring (part of SelfTest).</summary>
internal static class MarkdownSelfTest
{
    public static async Task Run(MainWindow w, List<string> failures)
    {
        void Check(bool ok, string what)
        {
            if (!ok) failures.Add("markdown: " + what);
        }

        var dir = Path.Combine(AppEnv.LocalDir, "selftest-md");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "notes.md");
        var other = Path.Combine(dir, "other notes.md");
        File.WriteAllText(path, "# Notes\n\n## Tasks\n\n- [ ] first task\n- [x] second task\n\n```csharp\nvar x = 1; // one\n```\n\n[other](other%20notes.md#top)\n", new UTF8Encoding(false));
        File.WriteAllText(other, "# Other\n", new UTF8Encoding(false));

        w.OpenFile(path);
        var group = w.Model.ActiveGroup;
        var doc = group.Tabs.OfType<TextDocument>().FirstOrDefault(d => d.FilePath is { } p && FileUtil.PathEquals(p, path));
        Check(doc is not null, "file did not open");
        if (doc is null) return;
        group.Active = doc;
        await doc.LoadAsync();
        doc.ViewMode = ViewMode.Split;
        await Idle(w, 300);

        var pv = (w.ActiveView as TextDocumentView)?.Preview as MarkdownPreviewView;
        Check(pv is not null, "no preview registered for Markdown");
        if (pv is not null)
        {
            pv.Preview.FlushPending();
            await Idle(w, 100);
            Check(pv.Preview.Headings.Count == 2, $"headings: {pv.Preview.Headings.Count}");

            // live update from the buffer
            doc.Buffer.Insert(doc.Buffer.TextLength, "\n## Added\n");
            await Idle(w, 400);
            pv.Preview.FlushPending();
            Check(pv.Preview.Headings.Any(h => h.Text == "Added"), "preview did not follow the buffer");

            // task box → one undo step in the buffer
            pv.SimulateTask(5, true);
            Check(doc.Buffer.GetText(doc.Buffer.GetLineByNumber(5)) == "- [x] first task", "task not ticked: " + doc.Buffer.GetText(doc.Buffer.GetLineByNumber(5)));
            doc.Buffer.UndoStack.Undo();
            Check(doc.Buffer.GetText(doc.Buffer.GetLineByNumber(5)) == "- [ ] first task", "task tick is not one undo step");
            pv.SimulateTask(6, false);
            Check(doc.Buffer.GetText(doc.Buffer.GetLineByNumber(6)) == "- [ ] second task", "task not unticked");
            doc.Buffer.UndoStack.Undo();
            doc.Buffer.UndoStack.Undo(); // the inserted heading

            // relative link → a tab
            pv.SimulateLink("other%20notes.md#top");
            await Idle(w, 100);
            Check(DocumentManager.I.FindByPath(other) is not null, "relative link did not open the file");

            // table of contents
            pv.ToggleToc();
            await Idle(w, 100);
            Check(pv.TocOpen, "contents panel did not open");
            pv.ToggleToc();
        }

        // fenced code is colored by the editor's definitions
        var runs = CodeHighlighter.Highlight("csharp", "var x = 1; // one")?.OfType<Run>().ToList() ?? [];
        Check(runs.Count >= 3 && runs.Any(r => r.ReadLocalValue(TextElement.ForegroundProperty) != System.Windows.DependencyProperty.UnsetValue),
            $"code highlighting: {runs.Count} runs");
        Check(CodeHighlighter.Highlight("no-such-language", "x") is null, "unknown fence language must stay plain");

        foreach (var d in new[] { other, path }.Select(p => DocumentManager.I.FindByPath(p)).OfType<Document>().ToList())
            if (Workspace.I.Locate(d) is { } where) await w.CloseTabAsync(where.Group, d);
        await Idle(w, 100);
    }

    private static async Task Idle(MainWindow w, int ms)
    {
        await Task.Delay(ms);
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }
}
#endif
