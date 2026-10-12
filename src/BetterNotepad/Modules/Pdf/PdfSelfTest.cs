#if DEBUG
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using BetterNotepad.Core;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Session;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Modules.Pdf;

/// <summary>
/// Debug self-test of the PDF viewer: zoom shortcuts (PDF vs editor), selection across pages (text, overlays, drag
/// into a gap, Ctrl+A, copy limit) and the password prompt (wrong → inline error, right → opens, remembered in memory
/// only, cancel → locked tab → "Enter password"). Never touches the clipboard (shared with the user's session).
/// </summary>
internal static class PdfSelfTest
{
    public static async Task Run(MainWindow w, List<string> failures)
    {
        void Check(bool ok, string what)
        {
            if (!ok) failures.Add("pdf: " + what);
        }

        var dir = Path.Combine(AppEnv.LocalDir, "selftest-pdf");
        Directory.CreateDirectory(dir);
        var manual = Path.Combine(dir, "manual.pdf");
        PdfFixtures.WriteManual(manual, pages: 12);
        w.OpenFile(manual);
        var pv = await ViewOf(w, manual, v => v.Handle is not null);
        if (pv?.Handle is null)
        {
            failures.Add("pdf: manual did not open");
            return;
        }
        await Task.Delay(300);

        // ── 1. zoom shortcuts drive the PDF (toolbar steps, Ctrl+0 = fit width); the editor zoom stays ──
        var editor = SettingsService.I.Get<EditorSettings>();
        var editorZoom = editor.Zoom;
        var fit = pv.ZoomFactor;
        Check(pv.FitsWidth, "opens fitted to the width");
        Check(Press(w, Key.OemPlus) == "view.zoomIn" && pv.ZoomFactor > fit + 0.001 && !pv.FitsWidth, $"Ctrl+= zoom {fit} -> {pv.ZoomFactor}");
        var z = pv.ZoomFactor;
        Check(Press(w, Key.Add) == "view.zoomInAlt" && pv.ZoomFactor > z + 0.001, "Ctrl+NumPlus zooms in");
        z = pv.ZoomFactor;
        Check(Press(w, Key.OemMinus) == "view.zoomOut" && pv.ZoomFactor < z - 0.001, "Ctrl+- zooms out");
        z = pv.ZoomFactor;
        Check(Press(w, Key.Subtract) == "view.zoomOutAlt" && pv.ZoomFactor < z - 0.001, "Ctrl+NumMinus zooms out");
        Check(Press(w, Key.D0) == "view.zoomReset" && pv.FitsWidth && Math.Abs(pv.ZoomFactor - fit) < 0.01, "Ctrl+0 fits the width");
        Check(Math.Abs(editor.Zoom - editorZoom) < 0.001, "PDF zoom shortcuts changed the editor zoom");
        var scratch = w.NewTab(content: "zoom");
        await Task.Delay(100);
        Press(w, Key.OemPlus);
        Check(Math.Abs(editor.Zoom - (editorZoom + 0.1)) < 0.001, "Ctrl+= in a text tab zooms the editor");
        Press(w, Key.D0);
        Check(Math.Abs(editor.Zoom - 1) < 0.001, "Ctrl+0 in a text tab resets the editor zoom");
        editor.Zoom = editorZoom;
        w.Model.ActiveGroup.Remove(scratch);
        DocumentManager.I.Close(scratch);
        w.Model.ActiveGroup.Active = pv.Document;
        await Task.Delay(150);

        // ── 2. selection across pages ──
        var l0 = await pv.Handle.GetPageTextLayoutAsync(0);
        var l2 = await pv.Handle.GetPageTextLayoutAsync(2);
        var p1 = await pv.Handle.GetPageTextAsync(1);
        pv.SelectRange(0, 30, 2, 12);
        var sel = await pv.SelectedTextAsync();
        var expected = l0.Text[30..] + "\r\n" + p1 + "\r\n" + l2.Text[..12];
        Check(sel.FirstPage == 0 && sel.LastPage == 2 && !sel.Truncated && sel.Text == expected, $"text of pages 1-3: '{sel.Text}'");
        pv.SelectRange(2, 12, 0, 30); // dragged upwards: the same text
        Check((await pv.SelectedTextAsync()).Text == expected, "selection made upwards");
        await Task.Delay(250);
        var drawn = pv.PagesWithSelectionDrawn;
        Check(drawn.Contains(0) && drawn.Contains(1), "highlight on every visible selected page: " + string.Join(",", drawn));

        // a drag from page 1 to page 3 (pointer mapped through the strip), then into the gap under page 2
        var k = pv.ZoomFactor * 96 / 72;
        await pv.DragSelectAsync(0, new Point(80 * k, 110 * k), 2, new Point(200 * k, 130 * k));
        var dragged = await pv.SelectedTextAsync();
        Check(pv.Selection is { Start.Page: 0, End.Page: 2 } && dragged.Text.Contains("Chapter 2") && dragged.Text.Contains("Chapter 3"), $"drag 1 -> 3: {pv.Selection}");
        var size = pv.Handle.PageSizes[1];
        await pv.DragSelectAsync(0, new Point(80 * k, 110 * k), 1, new Point(100 * k, size.Height * k + 6));
        Check(pv.Selection is { End: (1, int.MaxValue) }, $"drag into the gap below page 2 selects to its end: {pv.Selection}");

        // Ctrl+A = the whole document; huge selections are copied up to the limit (and say so)
        pv.SelectAll();
        var all = await pv.SelectedTextAsync();
        Check(all.FirstPage == 0 && all.LastPage == 11 && all.Text.Contains("BetterNotepad manual") && all.Text.Contains("Line 30 of chapter 12"), "Ctrl+A text");
        var limit = PdfDocumentView.MaxCopyChars;
        PdfDocumentView.MaxCopyChars = 3000;
        var cut = await pv.SelectedTextAsync();
        PdfDocumentView.MaxCopyChars = limit;
        Check(cut.Truncated && cut.LastPage < 11 && cut.Text.Length <= 3000 && cut.Text.Length > 0, $"copy limit: {cut.LastPage} {cut.Text.Length}");
        pv.ClearSelection();
        Check(pv.Selection is null, "selection cleared");
        await w.CloseTabAsync(w.Model.ActiveGroup, pv.Document);

        // ── 3. password-protected file ──
        var secret = Path.Combine(dir, "secret.pdf");
        PdfFixtures.WriteProtected(secret);
        PdfPasswords.Forget(secret);
        w.OpenFile(secret);
        var sv = await ViewOf(w, secret, v => v.Prompt is not null && w.DialogLayer.IsOpen);
        Check(sv?.Prompt is not null, "password prompt shown");
        if (sv?.Prompt is null) return;
        sv.Prompt.SetPassword("wrong");
        SnapshotMode.PressDialogButton(w, Loc.T("pdf.pw.open"));
        await Until(() => sv.Prompt?.ErrorShown == true);
        Check(sv.Prompt?.ErrorShown == true && w.DialogLayer.IsOpen && sv.Handle is null, "wrong password: the prompt stays with an inline error");
        sv.Prompt?.SetPassword(PdfFixtures.Password);
        SnapshotMode.PressDialogButton(w, Loc.T("pdf.pw.open"));
        await Until(() => sv.Handle is not null && !w.DialogLayer.IsOpen);
        Check(sv.Handle?.PageCount == 1 && !w.DialogLayer.IsOpen, "right password opens the file");
        if (sv.Handle is not null) Check((await sv.Handle.GetPageTextAsync(0)).Contains("secret text"), "decrypted text");
        Check(PdfPasswords.Get(secret) == PdfFixtures.Password, "password remembered for the session");

        // the password is never written anywhere (session, settings, logs, caches)
        SessionService.I.Flush();
        SettingsService.I.SaveNow();
        Log.Info("selftest: pdf password check");
        var needles = new[] { Encoding.UTF8.GetBytes(PdfFixtures.Password), Encoding.Unicode.GetBytes(PdfFixtures.Password) };
        foreach (var root in new[] { AppEnv.RoamingDir, AppEnv.LocalDir }.Distinct())
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (new FileInfo(file).Length > 20_000_000) continue;
                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(file);
                }
                catch (IOException)
                {
                    continue;
                }
                foreach (var needle in needles)
                    if (bytes.AsSpan().IndexOf(needle) >= 0) failures.Add("pdf: password written to " + file);
            }

        // the same file again in this session: no second prompt
        await w.CloseTabAsync(w.Model.ActiveGroup, sv.Document);
        w.OpenFile(secret);
        var again = await ViewOf(w, secret, v => v.Handle is not null || v.Prompt is not null);
        Check(again?.Handle is not null && again.Prompt is null, "reopened in the same session without asking");
        if (again is not null) await w.CloseTabAsync(w.Model.ActiveGroup, again.Document);

        // cancel → the tab stays, locked, with "Enter password" → asks again → opens
        PdfPasswords.Forget(secret);
        w.OpenFile(secret);
        var lv = await ViewOf(w, secret, v => v.Prompt is not null && w.DialogLayer.IsOpen);
        if (lv is null)
        {
            failures.Add("pdf: no prompt after forgetting the password");
            return;
        }
        SnapshotMode.PressDialogButton(w, Loc.T("common.cancel"));
        await Until(() => lv.IsLocked && !w.DialogLayer.IsOpen);
        Check(lv.IsLocked && lv.Handle is null && !w.DialogLayer.IsOpen && w.Model.ActiveGroup.Tabs.Contains(lv.Document), "cancel keeps a locked tab");
        _ = lv.UnlockAsync();
        await Until(() => lv.Prompt is not null && w.DialogLayer.IsOpen);
        Check(lv.Prompt is not null, "Enter password asks again");
        lv.Prompt?.SetPassword(PdfFixtures.Password);
        if (lv.Prompt is not null) SnapshotMode.PressDialogButton(w, Loc.T("pdf.pw.open"));
        await Until(() => lv.Handle is not null && !w.DialogLayer.IsOpen);
        Check(lv.Handle is not null && !lv.IsLocked, "unlocked from the locked state");
        await w.CloseTabAsync(w.Model.ActiveGroup, lv.Document);
        PdfPasswords.Forget(secret);
    }

    /// <summary>Ctrl + key through the window's keymap, as a key press would go; returns the command run.</summary>
    private static string? Press(MainWindow w, Key key)
    {
        var ctx = w.Context();
        var cmd = Keymap.Match(new KeyChord(ModifierKeys.Control, key), ctx);
        cmd?.Execute(ctx);
        return cmd?.Id;
    }

    private static async Task<PdfDocumentView?> ViewOf(MainWindow w, string path, Func<PdfDocumentView, bool> ready)
    {
        for (var i = 0; i < 120; i++)
        {
            if (w.ActiveView is PdfDocumentView v && v.Document.FilePath is { } p && FileUtil.PathEquals(p, path) && ready(v)) return v;
            await Task.Delay(50);
        }
        return w.ActiveView as PdfDocumentView;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 120 && !condition(); i++) await Task.Delay(50);
    }
}
#endif
