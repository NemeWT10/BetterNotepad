#if DEBUG
using System.IO;
using System.Text;
using System.Windows.Controls;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Session;
using BetterNotepad.Documents.Views;
using BetterNotepad.Modules.Markdown;
using BetterNotepad.Modules.Pdf;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.UI.Tray;

/// <summary>
/// Debug self-test of the tray rest (part of SelfTest): hide → the session and unsaved buffers are on disk, PDF pages and the
/// Markdown preview are dropped, memory is trimmed, nothing runs (no toast timer, no wake-up when another program writes
/// next to an open file) → restore (as the tray icon / hotkey does) → the same views with the same tabs, active tabs, split,
/// caret, scroll, PDF zoom and page, Markdown reading position and unsaved text; an external change of an open file is still
/// noticed; then hide again and open a file "from Explorer" while hidden (the second-instance path).
/// </summary>
internal static class TraySelfTest
{
    public static async Task Run(MainWindow w, List<string> failures)
    {
        void Check(bool ok, string what)
        {
            if (!ok) failures.Add("tray: " + what);
        }

        var dir = Path.Combine(AppEnv.LocalDir, "selftest-tray");
        Directory.CreateDirectory(dir);
        var code = Path.Combine(dir, "long.cs");
        var md = Path.Combine(dir, "long.md");
        var pdf = Path.Combine(dir, "manual.pdf");
        var late = Path.Combine(dir, "from-explorer.txt");
        File.WriteAllText(code, string.Join("\n", Enumerable.Range(1, 400).Select(i => $"int line{i} = {i}; // tray self-test")), new UTF8Encoding(false));
        File.WriteAllText(md, string.Join("\n\n", Enumerable.Range(1, 60).Select(i => $"## Section {i}\n\nParagraph {i} of the tray self-test, long enough to scroll.")), new UTF8Encoding(false));
        File.WriteAllText(late, "opened while in the tray\n", new UTF8Encoding(false));
        PdfFixtures.WriteManual(pdf, pages: 12);

        // ── layout: group A = long.cs (active) + long.md (Preview) + an unsaved new tab; group B (split) = the PDF ──
        var a = w.Model.ActiveGroup;
        w.OpenFile(md);
        var mdDoc = a.Tabs.OfType<TextDocument>().FirstOrDefault(d => FileUtil.PathEquals(d.FilePath, md));
        if (mdDoc is null)
        {
            failures.Add("tray: markdown did not open");
            return;
        }
        await mdDoc.LoadAsync();
        mdDoc.ViewMode = ViewMode.Preview;
        await Idle(w, 300);
        var mdView = (w.ActiveView as TextDocumentView)?.Preview as MarkdownPreviewView;
        mdView?.Preview.FlushPending();
        await Idle(w, 100);
        mdView?.Preview.ScrollToLine(60, 0);
        await Idle(w, 150);
        var mdTop = mdView?.Preview.GetTopLine(out _) ?? 0;
        Check(mdView is not null && mdTop > 20, $"markdown preview scrolled (top line {mdTop})");

        var scratch = w.NewTab(content: "unsaved text that must survive the tray");
        w.OpenFile(pdf);
        await Until(() => w.ActiveView is PdfDocumentView { Handle: not null });
        w.Split(Orientation.Horizontal, moveInsteadOfCopy: true);
        var b = w.Model.ActiveGroup;
        await Until(() => w.ActiveView is PdfDocumentView { Handle: not null } v && v.RenderedPageCount > 0);
        var pv = w.ActiveView as PdfDocumentView;
        Check(b != a && pv is not null && w.Model.Groups.Count == 2, "PDF in its own group (split)");
        if (pv is null) return;
        pv.ZoomStep(+1);
        await Idle(w, 200);
        pv.ScrollToPage(5);
        await Until(() => pv.CurrentPage == 5 && pv.RenderedPageCount > 0);
        var pdfZoom = pv.ZoomFactor;
        var pdfPage = pv.CurrentPage;

        w.Model.ActiveGroup = a; // files open in the active group
        w.OpenFile(code);
        var codeDoc = a.Tabs.OfType<TextDocument>().FirstOrDefault(d => FileUtil.PathEquals(d.FilePath, code));
        await Until(() => w.ActiveView?.Editor is { } e && e.Document?.LineCount > 300);
        var codeView = w.ActiveView as TextDocumentView;
        var ed = codeView?.Editor;
        Check(codeDoc is not null && ed is not null, "code file in group A");
        if (codeDoc is null || ed is null || codeView is null) return;
        ed.ScrollToLine(200);
        ed.CaretOffset = ed.Document.GetLineByNumber(210).Offset + 4;
        await Idle(w, 200);
        var caret = ed.CaretOffset;
        var scroll = ed.VerticalOffset;
        Check(scroll > 100, $"editor scrolled ({scroll:0})");
        var activeB = b.Active;

        // ── hide to the tray (with a toast on screen: it must go, timer included, instead of fading out in the hidden window) ──
        w.Toasts.Show("tray self-test", seconds: 1);
        var sessionBefore = File.Exists(SessionService.SessionPath) ? File.GetLastWriteTimeUtc(SessionService.SessionPath) : DateTime.MinValue;
        var trims = TrayRest.Trims;
        w.HideToTray();
        Check(!w.IsVisible, "window still visible after hiding to the tray");
        Check(BackupStore.Exists(scratch.Id), "unsaved new tab has no backup on disk after hiding");
        Check(File.Exists(SessionService.SessionPath) && File.GetLastWriteTimeUtc(SessionService.SessionPath) >= sessionBefore, "session not written when hiding");
        Check(pv.IsHibernated && pv.LivePageCount == 0, $"PDF pages kept while hidden ({pv.LivePageCount})");
        Check(mdView is { IsReleased: true }, "markdown preview kept its document while hidden");
        Check(w.Toasts.Children.Count == 0, "toasts left in the hidden window");
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(TrayRest.Trims > trims, "no memory trim after hiding");
        Check(TrayRest.Pending, "the later trims are not scheduled");

        // nothing may run in the tray: count dispatcher work for a moment (our own wake-up is the only expected one);
        // on a failure the first operation's stack says who started it
        // the hide itself may still commit one last frame (window just went away): let it settle before counting
        await Task.Delay(400);
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var ops = new List<string>();
        string? firstStack = null;
        void OnPosted(object? s, DispatcherHookEventArgs e)
        {
            var what = PerfProbe.Describe(e.Operation);
            ops.Add(what);
            if (firstStack is null && !what.StartsWith("ApplicationIdle", StringComparison.Ordinal))
                firstStack = string.Join(" ← ", Environment.StackTrace.Split('\n').Skip(4).Take(10).Select(l => l.Trim()));
        }
        w.Dispatcher.Hooks.OperationPosted += OnPosted;
        // another program busy in the folder of an open file: the watcher must not wake the app for files it does not show
        for (var i = 0; i < 5; i++) File.WriteAllText(Path.Combine(dir, "unrelated.log"), $"line {i}\n");
        await Task.Delay(1500);
        w.Dispatcher.Hooks.OperationPosted -= OnPosted;
        Check(ops.Count <= 1, $"work in the tray: {ops.Count} dispatcher operations ({string.Join(", ", ops.GroupBy(o => o).Select(g => $"{g.Count()}× {g.Key}"))}); first posted by {firstStack}");

        // ── restore (tray icon click / hotkey / tray menu: BringToFront) ──
        w.BringToFront();
        await Idle(w, 200);
        Check(w.IsVisible, "window not visible after restore");
        Check(!TrayRest.Pending, "trims still scheduled while the window is up");
        Check(w.Model.Groups.Count == 2 && w.Model.Groups[0] == a && w.Model.Groups[1] == b && w.Model.SplitOrientation == Orientation.Horizontal, "split layout changed");
        Check(w.Model.ActiveGroup == a && a.Active == codeDoc && b.Active == activeB, "active group / tabs changed");
        Check(ReferenceEquals(w.ActiveView, codeView) && ReferenceEquals(codeView.Editor, ed), "the editor view was rebuilt (should be kept)");
        Check(ed.CaretOffset == caret, $"caret {caret} → {ed.CaretOffset}");
        Check(Math.Abs(ed.VerticalOffset - scroll) < 1, $"scroll {scroll:0} → {ed.VerticalOffset:0}");
        Check(scratch.IsDirty && scratch.HasBuffer && scratch.Buffer.Text == "unsaved text that must survive the tray", "unsaved new tab changed");
        await Until(() => pv.RenderedPageCount > 0);
        Check(!pv.IsHibernated && pv.RenderedPageCount > 0, "PDF pages did not render again after restore");
        Check(Math.Abs(pv.ZoomFactor - pdfZoom) < 0.001 && pv.CurrentPage == pdfPage, $"PDF zoom / page {pdfZoom}/{pdfPage} → {pv.ZoomFactor}/{pv.CurrentPage}");
        a.Active = mdDoc;
        await Idle(w, 300);
        mdView?.Preview.FlushPending();
        await Idle(w, 200);
        var mdTopAfter = mdView?.Preview.GetTopLine(out _) ?? 0;
        Check(mdView is { IsReleased: false } && mdView.Preview.Headings.Count == 60, $"markdown preview not rendered again ({mdView?.Preview.Headings.Count})");
        Check(Math.Abs(mdTopAfter - mdTop) <= 2, $"markdown reading position {mdTop} → {mdTopAfter}");
        a.Active = codeDoc;
        await Idle(w, 100);

        // a change to an open file made by another program still gets through the (filtering) watcher
        File.AppendAllText(code, "\n// changed by another program\n");
        await Until(() => codeDoc.External == ExternalChange.Modified);
        Check(codeDoc.External == ExternalChange.Modified, "the watcher missed an external change of an open file");

        // ── hidden again, a file arrives from Explorer (second instance → App.OpenFiles + BringToFront) ──
        w.HideToTray();
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        App.Current.OpenFiles([late], w);
        w.BringToFront();
        await Idle(w, 200);
        Check(w.IsVisible && w.ActiveDocument is { FilePath: { } p } && FileUtil.PathEquals(p, late), "file opened while in the tray is not the active tab");
        Check(w.ActiveView?.Editor?.Document.Text.StartsWith("opened while in the tray", StringComparison.Ordinal) == true, "file opened while in the tray has no content");

        // ── clean up ──
        w.Unsplit();
        await Idle(w, 100);
        foreach (var g in w.Model.Groups.ToList())
            foreach (var d in g.Tabs.Where(d => d == scratch || (d.FilePath is { } f && f.StartsWith(dir, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                g.Remove(d);
                if (!Workspace.I.IsOpenAnywhere(d)) DocumentManager.I.Close(d);
            }
        await Idle(w, 100);
    }

    private static async Task Idle(MainWindow w, int ms)
    {
        await Task.Delay(ms);
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
    }
}
#endif
