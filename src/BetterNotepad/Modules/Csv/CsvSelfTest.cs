#if DEBUG
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Views;
using BetterNotepad.Tabular.Wpf;
using BetterNotepad.UI.Windows;
using TextDocument = BetterNotepad.Documents.TextDocument;

namespace BetterNotepad.Modules.Csv;

/// <summary>
/// Debug-only checks of the CSV / TSV table (part of SelfTest): dialects and header detection, sorting and filtering leave the
/// text alone, cell edits keep the quoting and are one undo step each (Ctrl+Z / Ctrl+Y from the table), a locked file is read
/// only, a 100k-row file opens fast, the split view selects the cell in the text, the tray drops the table and it comes back.
/// The clipboard is never touched (test mode).
/// </summary>
internal static class CsvSelfTest
{
    public static async Task Run(MainWindow w, List<string> failures)
    {
        void Check(bool ok, string what)
        {
            if (!ok) failures.Add("csv: " + what);
        }

        var dir = Path.Combine(AppEnv.LocalDir, "selftest-csv");
        var group = w.Model.ActiveGroup;
        var opened = new List<TextDocument>();
        Check(TextDocument.DefaultViewMode(DocumentKind.Csv) == ViewMode.Preview, "CSV does not open as a table by default");
        Check(DocumentViewRegistry.HasPreview(DocumentKind.Csv), "no table registered for CSV");

        async Task<(TextDocument Doc, CsvPreviewView View)?> Open(string path, ViewMode? mode = null)
        {
            w.OpenFile(path);
            var doc = group.Tabs.OfType<TextDocument>().FirstOrDefault(d => d.FilePath is { } fp && FileUtil.PathEquals(fp, path));
            if (doc is null)
            {
                failures.Add("csv: did not open " + Path.GetFileName(path));
                return null;
            }
            if (!opened.Contains(doc)) opened.Add(doc);
            group.Active = doc;
            await doc.LoadAsync();
            if (mode is { } m) doc.ViewMode = m;
            await Idle(w, 120);
            CsvPreviewView? view = null;
            for (var i = 0; i < 50 && view is null; i++)
            {
                view = (w.ActiveView as TextDocumentView)?.Preview as CsvPreviewView;
                if (view is null) await Idle(w, 40);
            }
            if (view is null)
            {
                failures.Add("csv: no table view for " + Path.GetFileName(path));
                return null;
            }
            await Ready(w, view);
            return (doc, view);
        }

        // ── dialects: , ; tab |, header yes / no ──
        foreach (var (name, text, delimiter, header, rows, cols) in new[]
                 {
                     ("contacts.csv", CsvFixtures.Contacts, ',', true, 6, 4),
                     ("orders.csv", CsvFixtures.Orders(300), ';', true, 300, 7),
                     ("sensors.tsv", CsvFixtures.Sensors, '\t', true, 5, 5),
                     ("inventory.psv", CsvFixtures.Inventory, '|', true, 4, 4),
                     ("numbers.csv", CsvFixtures.NoHeader, ',', false, 4, 3),
                 })
        {
            var path = CsvFixtures.Write(dir, name, text);
            if (await Open(path) is not { } o) continue;
            Check(o.Doc.ViewMode == ViewMode.Preview, $"{name}: not opened as a table ({o.Doc.ViewMode})");
            var t = o.View.Table;
            Check(t.Dialect is { } d && d.Delimiter == delimiter && d.HasHeader == header,
                $"{name}: dialect {(t.Dialect is { } x ? $"'{x.Delimiter}' header={x.HasHeader}" : "none")}, expected '{delimiter}' header={header}");
            Check(t.Table is { } tb && tb.DataRowCount == rows && tb.ColumnCount == cols, $"{name}: {t.Table?.DataRowCount}×{t.Table?.ColumnCount}, expected {rows}×{cols}");
        }

        var contactsPath = Path.Combine(dir, "contacts.csv");
        if (await Open(contactsPath) is { } c)
        {
            var (doc, view) = c;
            var t = view.Table;
            var original = doc.Buffer.Text;

            // ── sorting and filtering change only the view ──
            t.SortForTest(3, descending: true);
            await Ready(w, view);
            Check(t.View?.GetCell(0, 0) == "Piotr Zieliński" && t.View.GetCell(5, 0) == "Zofia \"Zosia\" Mazur", $"sort by score (numbers, desc): first {t.View?.GetCell(0, 0)}");
            t.FilterForTest("KOWAL");
            t.FlushFilterForTest();
            await Ready(w, view);
            Check(t.View?.RowCount == 1 && t.View.GetCell(0, 0) == "Kowalski, Jan", $"filter (case-insensitive): {t.View?.RowCount} rows");
            t.FilterColumnForTest(2);
            await Ready(w, view);
            Check(t.View?.RowCount == 0, $"filter in the city column only: {t.View?.RowCount} rows");
            Check(doc.Buffer.Text == original && !doc.IsDirty, "sorting / filtering changed the text");
            t.FilterColumnForTest(-1);
            t.FilterForTest("");
            t.FlushFilterForTest();
            t.SortForTest(-1, false);
            await Ready(w, view);
            Check(t.View is { RowCount: 6, IsIdentity: true }, "back to the file order");

            // ── copy as TSV (test mode: our own buffer, never the clipboard) ──
            t.SelectForTest(0, 0, 1, 1);
            t.CopySelection();
            Check(CsvPreviewView.LastTestCopy == "Kowalski, Jan\tjan@example.com\r\nAnna Nowak\tanna@example.com\r\n", $"copy: '{CsvPreviewView.LastTestCopy}'");

            // ── cell edits keep the separator and the quoting; each one is one undo step, also from the table ──
            t.EditForTest(0, 0, "Nowak, Anna"); // a quoted cell stays quoted
            var afterFirst = doc.Buffer.Text;
            Check(afterFirst == original.Replace("\"Kowalski, Jan\"", "\"Nowak, Anna\""), "edit of a quoted cell: " + Line(afterFirst, 2));
            Check(t.Table?.GetCell(1, 0) == "Nowak, Anna", "the table does not show the edit");
            t.EditForTest(1, 2, "Gdańsk \"Oliwa\""); // a bare cell gets quotes because of the quote inside
            var afterSecond = doc.Buffer.Text;
            Check(Line(afterSecond, 3) == "Anna Nowak,anna@example.com,\"Gdańsk \"\"Oliwa\"\"\",92.5", "edit with a quote: " + Line(afterSecond, 3));
            Check(doc.IsDirty, "an edited table should leave the tab unsaved");
            t.EditForTest(5, 2, "Lublin\n(Stare Miasto)"); // a line break typed into a cell is written with the file's CRLF
            Check(doc.Buffer.Text.Contains("\"Lublin\r\n(Stare Miasto)\",71", StringComparison.Ordinal), "line break in a cell not written as CRLF");
            t.KeyForTest(Key.Z, ModifierKeys.Control);
            await Ready(w, view);
            Check(doc.Buffer.Text == afterSecond, "Ctrl+Z in the table did not undo the last cell edit in one step");
            t.KeyForTest(Key.Z, ModifierKeys.Control);
            await Ready(w, view);
            Check(doc.Buffer.Text == afterFirst, "second Ctrl+Z");
            t.KeyForTest(Key.Z, ModifierKeys.Control);
            await Ready(w, view);
            Check(doc.Buffer.Text == original && !doc.IsDirty, "the third Ctrl+Z should bring the file back exactly");
            Check(t.Table?.GetCell(1, 0) == "Kowalski, Jan", "the table did not follow the undo");
            t.KeyForTest(Key.Y, ModifierKeys.Control);
            await Ready(w, view);
            Check(doc.Buffer.Text == afterFirst && t.Table?.GetCell(1, 0) == "Nowak, Anna", "Ctrl+Y in the table did not redo");
            doc.Buffer.UndoStack.Undo();
            await Ready(w, view);

            // clearing a block of cells is one step too
            t.SelectForTest(0, 1, 1, 2);
            t.ClearForTest();
            Check(Line(doc.Buffer.Text, 2) == "\"Kowalski, Jan\",,,87" && Line(doc.Buffer.Text, 3) == "Anna Nowak,,,92.5", "clear 2×2: " + Line(doc.Buffer.Text, 2));
            doc.Buffer.UndoStack.Undo();
            await Ready(w, view);
            Check(doc.Buffer.Text == original, "clearing cells is not one undo step");

            // the editor's own typing reaches the table
            doc.Buffer.Insert(doc.Buffer.TextLength, "Nowy Wiersz,nowy@example.com,Opole,50\r\n");
            await Ready(w, view, settle: 250);
            Check(t.Table?.DataRowCount == 7 && t.Table.GetCell(7, 2) == "Opole", $"typed row not in the table ({t.Table?.DataRowCount})");
            doc.Buffer.UndoStack.Undo();
            await Ready(w, view, settle: 250);

            // F2 (claimed by the window for rename) edits the cell while the table has the focus
            if (PresentationSource.FromVisual(w) is { } source)
            {
                t.SelectForTest(1, 1, 1, 1);
                var f2 = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.F2) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                Check(t.HandleWindowKey(f2) && t.IsEditing, "F2 in the table should start editing the cell");
                t.CancelEditForTest();
                Check(!t.IsEditing && doc.Buffer.Text == original, "Esc should leave the cell as it was");
            }

            // ── locked file → read only ──
            doc.IsLocked = true;
            await Idle(w, 60);
            Check(t.IsReadOnly && t.LockChipVisibleForTest, "a locked file should make the table read only (with the padlock)");
            t.EditForTest(0, 1, "x@example.com");
            t.SelectForTest(0, 0, 2, 2);
            t.ClearForTest();
            Check(doc.Buffer.Text == original && !t.IsEditing, "a locked table changed the text");
            doc.IsLocked = false;
            await Idle(w, 60);
            Check(!t.IsReadOnly && !t.LockChipVisibleForTest, "unlocking should make the table editable again");

            // ── split view: the picked cell is selected in the text ──
            doc.ViewMode = ViewMode.Split;
            await Idle(w, 300);
            await Ready(w, view);
            var editor = (w.ActiveView as TextDocumentView)?.Editor;
            t.SelectForTest(2, 1, 2, 1);
            await Idle(w, 60);
            Check(editor is not null && editor.TextArea.Caret.Line == 4 && editor.SelectedText == "ola@example.com",
                $"split: table selection → editor line {editor?.TextArea.Caret.Line}, '{editor?.SelectedText}'");
            t.SelectForTest(5, 2, 5, 2); // a quoted cell over two lines: the raw text is selected, quotes included
            await Idle(w, 60);
            Check(editor?.SelectedText == "\"Lublin\r\n(centrum)\"", $"split: multi-line cell → '{editor?.SelectedText}'");
            doc.ViewMode = ViewMode.Preview;
            await Idle(w, 100);

            // ── language ──
            var appearance = SettingsService.I.Get<AppearanceSettings>();
            appearance.Language = "pl";
            await Idle(w, 150);
            Check(t.StatusForTest.Contains("wierszy", StringComparison.Ordinal) && t.StatusForTest.Contains("kolumny", StringComparison.Ordinal), "Polish status: " + t.StatusForTest);
            appearance.Language = "en";
            await Idle(w, 150);
            Check(t.StatusForTest.Contains("6 rows", StringComparison.Ordinal) && t.StatusForTest.Contains("4 columns", StringComparison.Ordinal), "English status: " + t.StatusForTest);
        }

        // ── malformed: ragged rows, an unclosed quote ──
        if (await Open(CsvFixtures.Write(dir, "parts.csv", CsvFixtures.Ragged)) is { } r)
        {
            var t = r.View.Table;
            Check(t.Shape is { RaggedCount: 2, ExpectedColumns: 4 } && t.WarningForTest.Length > 0, $"ragged rows: {t.Shape.RaggedCount}, warning '{t.WarningForTest}'");
            t.GoToWarningForTest();
            Check(t.ActiveForTest.Row == 1, $"warning chip → first ragged row ({t.ActiveForTest.Row})");
            t.GoToWarningForTest();
            Check(t.ActiveForTest.Row == 3, $"warning chip → next ragged row ({t.ActiveForTest.Row})");
        }
        if (await Open(CsvFixtures.Write(dir, "notes.csv", CsvFixtures.Unclosed)) is { } u)
        {
            var t = u.View.Table;
            var before = u.Doc.Buffer.Text;
            Check(t.Table is { HasUnterminatedQuote: true, DataRowCount: 3 } && t.WarningForTest.Contains('4'), $"unclosed quote: '{t.WarningForTest}'");
            t.EditForTest(2, 1, "x");
            Check(u.Doc.Buffer.Text == before, "a cell that swallows the rest of the file must not be edited");
            t.EditForTest(0, 1, "pierwszy!");
            Check(u.Doc.Buffer.Text == before.Replace("1,pierwszy,", "1,pierwszy!,"), "other cells of a broken file stay editable");
            u.Doc.Buffer.UndoStack.Undo();
        }

        // ── the user's dialect choice ──
        if (await Open(Path.Combine(dir, "sensors.tsv")) is { } s)
        {
            var t = s.View.Table;
            t.Override = new CsvDialectOverride(Delimiter: ',');
            await Ready(s.View.Table, w);
            Check(t.Table?.ColumnCount == 1 && t.Dialect?.Delimiter == ',', "separator override");
            t.HasHeader = false;
            await Ready(s.View.Table, w);
            Check(t.Table?.DataRowCount == 6, "header override");
            t.Override = CsvDialectOverride.None;
            await Ready(s.View.Table, w);
            Check(t.Dialect is { Delimiter: '\t', HasHeader: true } && t.Table?.ColumnCount == 5, "back to automatic");
        }

        // ── a big file: 100k rows ──
        var bigPath = CsvFixtures.Write(dir, "big.csv", CsvFixtures.Orders(100_000, seed: 11));
        var sw = Stopwatch.StartNew();
        if (await Open(bigPath) is { } b)
        {
            sw.Stop();
            var t = b.View.Table;
            Log.Info($"csv: 100,000 rows ({new FileInfo(bigPath).Length / 1024} KB) open as a table in {sw.ElapsedMilliseconds} ms");
            Check(t.Table is { DataRowCount: 100_000, ColumnCount: 7 }, $"big file: {t.Table?.DataRowCount} rows");
            Check(sw.ElapsedMilliseconds < 8000, $"big file took {sw.ElapsedMilliseconds} ms");
            sw.Restart();
            t.SortForTest(4, descending: true);
            await Ready(w, b.View);
            sw.Stop();
            Log.Info($"csv: sorted 100,000 rows by amount in {sw.ElapsedMilliseconds} ms");
            var top = t.View is { RowCount: > 0 } v ? v.GetCell(0, 4) : "";
            Check(top == MaxAmount(t), $"big sort: top {top}");
            sw.Restart();
            t.FilterForTest("Gdańsk");
            t.FlushFilterForTest();
            await Ready(w, b.View);
            sw.Stop();
            Log.Info($"csv: filtered 100,000 rows in {sw.ElapsedMilliseconds} ms ({t.View?.RowCount} match)");
            Check(t.View is { RowCount: > 1000 and < 100_000 }, $"big filter: {t.View?.RowCount}");
            t.FilterForTest("");
            t.FlushFilterForTest();
            t.SortForTest(-1, false);
            await Ready(w, b.View);
            // editor → table scroll sync, file order
            t.ScrollToLine(50_001, 0, 0.5);
            Check(Math.Abs(t.ScrollYForTest - 49_999 * CsvGrid.RowHeight) < 1, $"line 50001 → table y {t.ScrollYForTest}");

            // ── the tray drops the table (and its copy of the text); showing the window parses it again ──
            w.HideToTray();
            Check(b.View.IsReleased && t.Table is null && t.Text.Length == 0, "the table kept its content in the tray");
            w.BringToFront();
            await Idle(w, 300);
            await Ready(w, b.View);
            Check(!b.View.IsReleased && t.Table?.DataRowCount == 100_000, "the table did not come back after the tray");
        }

        foreach (var d in opened)
        {
            if (d.HasBuffer)
                while (d.IsDirty && d.Buffer.UndoStack.CanUndo) d.Buffer.UndoStack.Undo();
            if (d.IsDirty)
            {
                failures.Add("csv: left unsaved: " + d.Title);
                continue;
            }
            if (Workspace.I.Locate(d) is { } where) await w.CloseTabAsync(where.Group, d);
        }
        await Idle(w, 100);
        try
        {
            File.Delete(bigPath);
        }
        catch (IOException)
        {
        }
    }

    private static string MaxAmount(CsvTablePreview t)
    {
        var table = t.Table!;
        var best = double.MinValue;
        var text = "";
        for (var r = 1; r < table.RowCount; r++)
        {
            var v = table.GetCell(r, 4);
            if (double.TryParse(v.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) && x > best)
            {
                best = x;
                text = v;
            }
        }
        return text;
    }

    private static string Line(string text, int line)
    {
        var lines = text.Split("\r\n");
        return line - 1 < lines.Length ? lines[line - 1] : "";
    }

    private static Task Ready(CsvTablePreview table, MainWindow w) => ReadyCore(w, null, table, 60);

    private static Task Ready(MainWindow w, CsvPreviewView view, int settle = 60) => ReadyCore(w, view, view.Table, settle);

    /// <summary>Until the buffer's latest text is in the table: the view's pending copy, the table's parse / sort / filter.</summary>
    private static async Task ReadyCore(MainWindow w, CsvPreviewView? view, CsvTablePreview table, int settle)
    {
        await Idle(w, settle);
        for (var i = 0; i < 200 && (view?.PullPending == true || table.IsRefreshing || !table.Pending.IsCompleted); i++)
        {
            if (!table.Pending.IsCompleted) await table.Pending;
            await Idle(w, 25);
        }
        await Idle(w, 20);
    }

    private static async Task Idle(MainWindow w, int ms)
    {
        await Task.Delay(ms);
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }
}
#endif
