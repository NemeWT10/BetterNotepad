using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BetterNotepad.Core.Localization;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Views;
using BetterNotepad.Tabular.Wpf;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Modules.Csv;

/// <summary>
/// CSV / TSV as a table (Table | Split | Text): the <see cref="CsvTablePreview"/> next to the editor, fed from the shared
/// buffer, its cell edits written back as one undo step each.
/// </summary>
public static class CsvModule
{
    private static bool _registered;

    public static void Register()
    {
        DocumentViewRegistry.RegisterPreview(DocumentKind.Csv, doc => new CsvPreviewView(doc));
        if (_registered) return;
        _registered = true;
        // the window maps F2 (rename) and Ctrl+F (find in the hidden editor) before the table sees them; class handlers run first
        EventManager.RegisterClassHandler(typeof(MainWindow), Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnWindowPreviewKey));
    }

    private static void OnWindowPreviewKey(object sender, KeyEventArgs e)
    {
        if (e.Handled || Keyboard.FocusedElement is not DependencyObject focus) return;
        if (TableAround(focus) is { } table && table.HandleWindowKey(e)) e.Handled = true;
    }

    internal static CsvTablePreview? TableAround(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is CsvTablePreview t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}

internal static class CsvStrings
{
    [ModuleInitializer]
    internal static void Register() => Loc.Register(new Dictionary<string, (string Pl, string En)>
    {
        ["csv.filter"] = ("Filtruj wiersze", "Filter rows"),
        ["csv.filterClear"] = ("Wyczyść filtr (Esc)", "Clear the filter (Esc)"),
        ["csv.allColumns"] = ("Wszystkie kolumny", "All columns"),
        ["csv.filterScopeTip"] = ("Gdzie szukać: we wszystkich kolumnach albo w jednej", "Where to look: in all columns or in one of them"),
        ["csv.filterColumn"] = ("Filtruj w tej kolumnie", "Filter in this column"),
        ["csv.header"] = ("Nagłówek", "Header row"),
        ["csv.headerTip"] = ("Pierwszy wiersz zawiera nazwy kolumn", "The first row holds the column names"),
        ["csv.rows.one"] = ("{0:N0} wiersz", "{0:N0} row"),
        ["csv.rows.few"] = ("{0:N0} wiersze", "{0:N0} rows"),
        ["csv.rows.many"] = ("{0:N0} wierszy", "{0:N0} rows"),
        ["csv.rowsOf.one"] = ("{0:N0} wiersza", "{0:N0} row"),
        ["csv.rowsOf.few"] = ("{0:N0} wierszy", "{0:N0} rows"),
        ["csv.rowsOf.many"] = ("{0:N0} wierszy", "{0:N0} rows"),
        ["csv.filtered"] = ("{0:N0} z {1}", "{0:N0} of {1}"),
        ["csv.columns.one"] = ("{0:N0} kolumna", "{0:N0} column"),
        ["csv.columns.few"] = ("{0:N0} kolumny", "{0:N0} columns"),
        ["csv.columns.many"] = ("{0:N0} kolumn", "{0:N0} columns"),
        ["csv.comma"] = ("Przecinek", "Comma"),
        ["csv.semicolon"] = ("Średnik", "Semicolon"),
        ["csv.tab"] = ("Tabulator", "Tab"),
        ["csv.pipe"] = ("Pionowa kreska", "Pipe"),
        ["csv.doubleQuote"] = ("Cudzysłów", "Double quotes"),
        ["csv.singleQuote"] = ("Apostrof", "Single quotes"),
        ["csv.dialectTip"] = ("Separator: {0} · {1}\n{2} — kliknij, aby zmienić", "Separator: {0} · {1}\n{2} — click to change"),
        ["csv.detected"] = ("Wykryte automatycznie", "Detected automatically"),
        ["csv.chosen"] = ("Wybrane przez Ciebie", "Chosen by you"),
        ["csv.detectAgain"] = ("Wykryj automatycznie", "Detect automatically"),
        ["csv.sorting"] = ("Sortowanie…", "Sorting…"),
        ["csv.copy"] = ("Kopiuj", "Copy"),
        ["csv.editCell"] = ("Edytuj komórkę", "Edit cell"),
        ["csv.clearCells"] = ("Wyczyść komórki", "Clear cells"),
        ["csv.showInText"] = ("Pokaż w tekście", "Show in text"),
        ["csv.sortAsc"] = ("Sortuj rosnąco", "Sort ascending"),
        ["csv.sortDesc"] = ("Sortuj malejąco", "Sort descending"),
        ["csv.sortNone"] = ("Kolejność z pliku", "Original order"),
        ["csv.sortTip"] = ("Kliknij, aby posortować (tylko widok — plik zachowuje kolejność)", "Click to sort (view only — the file keeps its order)"),
        ["csv.monospace"] = ("Czcionka o stałej szerokości", "Monospace font"),
        ["csv.readOnly"] = ("Tylko do odczytu", "Read-only"),
        ["csv.readOnlyTip"] = ("Plik jest zablokowany — kliknij, aby go odblokować", "The file is locked — click to unlock it"),
        ["csv.ragged.one"] = ("{0:N0} wiersz z inną liczbą komórek", "{0:N0} row with a different number of cells"),
        ["csv.ragged.few"] = ("{0:N0} wiersze z inną liczbą komórek", "{0:N0} rows with a different number of cells"),
        ["csv.ragged.many"] = ("{0:N0} wierszy z inną liczbą komórek", "{0:N0} rows with a different number of cells"),
        ["csv.raggedTip"] = ("Oczekiwana liczba komórek w wierszu: {0}. Kliknij, aby przejść do następnego wiersza, który się różni.",
            "Expected {0} cells per row. Click to go to the next row that differs."),
        ["csv.unclosed"] = ("Niezamknięty cudzysłów w linii {0}", "Unclosed quote in line {0}"),
        ["csv.unclosedTip"] = ("Cudzysłów nie jest nigdzie zamknięty, więc reszta pliku czyta się jako jedna komórka. Kliknij, aby przejść do tego miejsca.",
            "A quote is never closed, so the rest of the file reads as one cell. Click to go to it."),
        ["csv.rowCellsTip"] = ("Komórki w tym wierszu: {0} (oczekiwano {1})", "Cells in this row: {0} (expected {1})"),
        ["csv.lineTip"] = ("Linia {0} w pliku · dwuklik pokazuje ją w tekście", "Line {0} in the file · double-click to show it in the text"),
        ["csv.empty"] = ("Brak wierszy", "No rows yet"),
        ["csv.emptyHint"] = ("Przełącz na Tekst, aby zacząć pisać", "Switch to Text to start writing"),
        ["csv.noMatches"] = ("Żaden wiersz nie pasuje do filtra", "No rows match the filter"),
        ["csv.locked"] = ("Plik jest zablokowany — odblokuj go, aby edytować komórki.", "The file is locked — unlock it to edit cells."),
        ["csv.unlock"] = ("Odblokuj", "Unlock"),
        ["csv.unclosedEdit"] = ("Tej komórki nie da się edytować, bo jej cudzysłów nie jest zamknięty — popraw go w widoku Tekst.",
            "This cell can’t be edited because its quote is never closed — fix it in the Text view."),
        ["csv.stale"] = ("Tabela właśnie się odświeżała — spróbuj jeszcze raz.", "The table was just refreshing — please try again."),
        ["csv.copied.one"] = ("Skopiowano {0:N0} komórkę", "Copied {0:N0} cell"),
        ["csv.copied.few"] = ("Skopiowano {0:N0} komórki", "Copied {0:N0} cells"),
        ["csv.copied.many"] = ("Skopiowano {0:N0} komórek", "Copied {0:N0} cells"),
    });

    /// <summary>The table's texts in the current UI language.</summary>
    internal static CsvPreviewLabels Labels() => new()
    {
        Culture = Loc.I.Culture,
        FilterPlaceholder = Loc.T("csv.filter"),
        FilterClear = Loc.T("csv.filterClear"),
        FilterAllColumns = Loc.T("csv.allColumns"),
        FilterScopeTip = Loc.T("csv.filterScopeTip"),
        FilterColumn = Loc.T("csv.filterColumn"),
        HeaderRow = Loc.T("csv.header"),
        HeaderRowTip = Loc.T("csv.headerTip"),
        Rows = n => Loc.Plural("csv.rows", n),
        Filtered = (shown, total) => Loc.T("csv.filtered", shown, Loc.Plural("csv.rowsOf", total)),
        Columns = n => Loc.Plural("csv.columns", n),
        Comma = Loc.T("csv.comma"),
        Semicolon = Loc.T("csv.semicolon"),
        Tab = Loc.T("csv.tab"),
        Pipe = Loc.T("csv.pipe"),
        DoubleQuote = Loc.T("csv.doubleQuote"),
        SingleQuote = Loc.T("csv.singleQuote"),
        DialectTip = Loc.T("csv.dialectTip"),
        Detected = Loc.T("csv.detected"),
        ChosenByYou = Loc.T("csv.chosen"),
        DetectAgain = Loc.T("csv.detectAgain"),
        Sorting = Loc.T("csv.sorting"),
        Copy = Loc.T("csv.copy"),
        EditCell = Loc.T("csv.editCell"),
        ClearCells = Loc.T("csv.clearCells"),
        ShowInText = Loc.T("csv.showInText"),
        SortAscending = Loc.T("csv.sortAsc"),
        SortDescending = Loc.T("csv.sortDesc"),
        ClearSort = Loc.T("csv.sortNone"),
        SortTip = Loc.T("csv.sortTip"),
        Monospace = Loc.T("csv.monospace"),
        ReadOnly = Loc.T("csv.readOnly"),
        ReadOnlyTip = Loc.T("csv.readOnlyTip"),
        Ragged = n => Loc.Plural("csv.ragged", n),
        RaggedTip = Loc.T("csv.raggedTip"),
        UnclosedQuote = Loc.T("csv.unclosed"),
        UnclosedQuoteTip = Loc.T("csv.unclosedTip"),
        RowCellsTip = Loc.T("csv.rowCellsTip"),
        LineTip = Loc.T("csv.lineTip"),
        Empty = Loc.T("csv.empty"),
        EmptyHint = Loc.T("csv.emptyHint"),
        NoMatches = Loc.T("csv.noMatches"),
    };
}
