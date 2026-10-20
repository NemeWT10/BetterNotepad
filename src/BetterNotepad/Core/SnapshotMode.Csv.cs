using System.IO;
using System.Windows;
using System.Windows.Controls;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Views;
using BetterNotepad.Modules.Csv;
using BetterNotepad.Tabular.Wpf;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Core;

internal static partial class SnapshotMode
{
    /// <summary>
    /// 25. CSV / TSV table: the table (themes, EN / PL), sorted + filtered with matches marked, a cell being edited, the split
    /// view with the cell selected in the text, ragged rows and an unclosed quote, the separator menu and an override, a locked
    /// file, a narrow window (compact bar), an empty file and a wide table scrolled sideways.
    /// </summary>
    private static async Task CsvShots(MainWindow w)
    {
        var appearance = SettingsService.I.Get<AppearanceSettings>();
        var dir = Path.Combine(AppEnv.LocalDir, "snapshot-fixtures", "csv");
        var orders = CsvFixtures.Write(dir, "orders.csv", CsvFixtures.Orders(2400));
        var contacts = CsvFixtures.Write(dir, "contacts.csv", CsvFixtures.Contacts);
        var sensors = CsvFixtures.Write(dir, "sensors.tsv", CsvFixtures.Sensors);
        var ragged = CsvFixtures.Write(dir, "parts.csv", CsvFixtures.Ragged);
        var unclosed = CsvFixtures.Write(dir, "notes.csv", CsvFixtures.Unclosed);
        var empty = CsvFixtures.Write(dir, "new.csv", "");
        var wide = CsvFixtures.Write(dir, "matrix.csv", CsvFixtures.Wide(60, 300));
        var group = w.Model.ActiveGroup;
        var opened = new List<TextDocument>();

        async Task<(TextDocument Doc, CsvPreviewView View)> Open(string path, ViewMode mode = ViewMode.Preview)
        {
            w.OpenFile(path);
            var doc = group.Tabs.OfType<TextDocument>().First(d => d.FilePath is { } fp && FileUtil.PathEquals(fp, path));
            if (!opened.Contains(doc)) opened.Add(doc);
            group.Active = doc;
            await doc.LoadAsync();
            doc.ViewMode = mode;
            await Settle(w, 120);
            var view = await WaitFor(() => (w.ActiveView as TextDocumentView)?.Preview as CsvPreviewView) ?? throw new InvalidOperationException("no CSV table for " + path);
            await Ready(view);
            return (doc, view);
        }

        async Task Ready(CsvPreviewView view)
        {
            for (var i = 0; i < 60 && (view.Table.IsRefreshing || !view.Table.Pending.IsCompleted); i++)
            {
                await view.Table.Pending;
                await Settle(w, 30);
            }
            await Settle(w, 60);
        }

        // the table in every theme (EN), and Daylight in Polish
        var (ordersDoc, ordersView) = await Open(orders);
        var t = ordersView.Table;
        t.SelectForTest(3, 2, 3, 2);
        t.Focus();
        foreach (var theme in new[] { "aurora", "graphite", "daylight", "neon", "sunset" })
        {
            await UseTheme(w, theme);
            await Ready(ordersView);
            await Save(w, $"25_csv_table_{theme}_en");
        }
        await UseTheme(w, "daylight");
        appearance.Language = "pl";
        await Settle(w, 150);
        await Ready(ordersView);
        await Save(w, "25_csv_table_daylight_pl");

        // sorted by amount (descending) and filtered to one city, matches marked; hover on a row
        await UseTheme(w, "aurora");
        appearance.Language = "en";
        await Settle(w, 120);
        t.SortForTest(4, descending: true);
        await Ready(ordersView);
        t.FilterForTest("Kraków");
        t.FlushFilterForTest();
        await Ready(ordersView);
        t.SelectForTest(1, 1, 1, 1);
        t.HoverForTest(4, -1);
        await Save(w, "25_csv_sorted_filtered_aurora_en");
        // the same filter limited to one column (the scope chip shows it)
        t.FilterColumnForTest(2);
        await Ready(ordersView);
        await Save(w, "25_csv_filter_column_aurora_en");
        t.HoverForTest(-1, -1);
        t.FilterColumnForTest(-1);
        t.FilterForTest("");
        t.FlushFilterForTest();
        t.SortForTest(-1, false);
        await Ready(ordersView);

        // a cell being edited (Daylight, Polish), the value wider than the column
        await UseTheme(w, "daylight");
        appearance.Language = "pl";
        await Settle(w, 120);
        t.BeginEditForTest(2, 6);
        t.SetEditorTextForTest("Prosi o „fakturę” na firmę i dostawę po 16:00");
        await Settle(w, 120);
        await Save(w, "25_csv_editing_daylight_pl");
        t.CancelEditForTest();
        t.BeginEditForTest(5, 4);
        await Settle(w, 120);
        await Save(w, "25_csv_editing_number_daylight_pl");
        t.CancelEditForTest();

        // split view: the picked cell is selected in the text as well
        await UseTheme(w, "aurora");
        appearance.Language = "en";
        var (contactsDoc, contactsView) = await Open(contacts, ViewMode.Split);
        contactsView.Table.SelectForTest(2, 0, 2, 0);
        await Settle(w, 200);
        await Save(w, "25_csv_split_aurora_en");
        await UseTheme(w, "daylight");
        appearance.Language = "pl";
        await Settle(w, 150);
        await Ready(contactsView);
        contactsView.Table.SelectForTest(5, 2, 5, 2);
        await Settle(w, 200);
        await Save(w, "25_csv_split_daylight_pl");

        // malformed: ragged rows (marker in the row numbers + the warning chip), an unclosed quote
        await UseTheme(w, "aurora");
        appearance.Language = "en";
        var (_, raggedView) = await Open(ragged);
        raggedView.Table.GoToWarningForTest();
        await Settle(w, 100);
        await Save(w, "25_csv_ragged_aurora_en");
        await UseTheme(w, "daylight");
        appearance.Language = "pl";
        await Settle(w, 150);
        await Ready(raggedView);
        await Save(w, "25_csv_ragged_daylight_pl");
        var (_, unclosedView) = await Open(unclosed);
        unclosedView.Table.GoToWarningForTest();
        await Settle(w, 100);
        await Save(w, "25_csv_unclosed_daylight_pl");
        await UseTheme(w, "aurora");
        appearance.Language = "en";
        await Settle(w, 150);
        await Ready(unclosedView);
        await Save(w, "25_csv_unclosed_aurora_en");

        // the separator menu, then an override (TSV read with the header row switched off: columns A, B, C…)
        var (_, sensorsView) = await Open(sensors);
        var chip = sensorsView.Table.DialectChipForTest;
        var menu = sensorsView.Table.BuildDialectMenu();
        menu.IsOpen = true;
        await Settle(w, 200);
        await SaveWithPopup(w, menu, chip.TranslatePoint(new Point(-4, chip.ActualHeight), w), "25_csv_dialect_menu_aurora_en");
        menu.IsOpen = false;
        await Settle(w);
        sensorsView.Table.HasHeader = false;
        await Ready(sensorsView);
        await Save(w, "25_csv_override_aurora_en");
        await UseTheme(w, "daylight");
        appearance.Language = "pl";
        sensorsView.Table.Override = sensorsView.Table.Override with { Delimiter = ',' };
        await Settle(w, 150);
        await Ready(sensorsView);
        await Save(w, "25_csv_override_comma_daylight_pl");
        sensorsView.Table.Override = CsvDialectOverride.None;
        await Ready(sensorsView);

        // locked file: read only, the padlock chip in the bar
        await UseTheme(w, "aurora");
        appearance.Language = "en";
        group.Active = ordersDoc;
        ordersDoc.ViewMode = ViewMode.Preview;
        ordersDoc.IsLocked = true;
        await Settle(w, 150);
        await Ready(ordersView);
        await Save(w, "25_csv_locked_aurora_en");
        appearance.Language = "pl";
        await Settle(w, 150);
        await Ready(ordersView);
        await Save(w, "25_csv_locked_aurora_pl");
        ordersDoc.IsLocked = false;
        appearance.Language = "en";

        // narrow window: the bar folds to symbols
        var (width, height) = (w.Width, w.Height);
        w.Width = 600;
        await Settle(w, 200);
        await Ready(ordersView);
        await Save(w, "25_csv_narrow_aurora_en");
        group.Active = contactsDoc;
        await Settle(w, 200);
        await Ready(contactsView);
        await Save(w, "25_csv_narrow_split_aurora_en");
        w.Width = width;
        w.Height = height;
        await Settle(w, 200);

        // an empty file and a wide table scrolled sideways
        await Open(empty);
        await Save(w, "25_csv_empty_aurora_en");
        var (_, wideView) = await Open(wide);
        wideView.Table.SelectForTest(10, 30, 12, 33);
        wideView.Table.ScrollToCellForTest(12, 36);
        wideView.Table.Focus();
        await Settle(w, 150);
        await Save(w, "25_csv_wide_aurora_en");

        foreach (var d in opened)
        {
            if (d.HasBuffer)
                while (d.Buffer.UndoStack.CanUndo && d.IsDirty) d.Buffer.UndoStack.Undo();
            if (Workspace.I.Locate(d) is { } where) await w.CloseTabAsync(where.Group, d);
        }
        await UseTheme(w, "aurora");
        appearance.Language = "en";
        await Settle(w);
    }
}
