using System.Globalization;

namespace BetterNotepad.Tabular.Wpf;

/// <summary>UI texts of the table preview. The app fills them from its PL / EN resources; the defaults are an English fallback.</summary>
public sealed class CsvPreviewLabels
{
    /// <summary>Numbers in the bar (row and column counts) are formatted with this culture.</summary>
    public CultureInfo Culture { get; init; } = CultureInfo.CurrentCulture;

    public string FilterPlaceholder { get; init; } = "Filter rows";
    public string FilterClear { get; init; } = "Clear the filter (Esc)";
    public string FilterAllColumns { get; init; } = "All columns";
    public string FilterScopeTip { get; init; } = "Where to look: all columns or one of them";
    public string FilterColumn { get; init; } = "Filter this column";

    public string HeaderRow { get; init; } = "Header row";
    public string HeaderRowTip { get; init; } = "Treat the first row as column names";

    /// <summary>"1,234 rows"; null = <see cref="RowsFormat"/>.</summary>
    public Func<int, string>? Rows { get; init; }

    /// <summary>(shown, total) → "56 of 1,234 rows" while a filter is active; null = <see cref="FilteredFormat"/>.</summary>
    public Func<int, int, string>? Filtered { get; init; }

    /// <summary>"7 columns"; null = <see cref="ColumnsFormat"/>.</summary>
    public Func<int, string>? Columns { get; init; }

    public string RowsFormat { get; init; } = "{0} rows";
    public string FilteredFormat { get; init; } = "{0} of {1} rows";
    public string ColumnsFormat { get; init; } = "{0} columns";

    public string Comma { get; init; } = "Comma";
    public string Semicolon { get; init; } = "Semicolon";
    public string Tab { get; init; } = "Tab";
    public string Pipe { get; init; } = "Pipe";
    public string DoubleQuote { get; init; } = "Double quotes";
    public string SingleQuote { get; init; } = "Single quotes";

    /// <summary>Tooltip of the separator chip: {0} separator name, {1} quote name, {2} <see cref="Detected"/> or <see cref="ChosenByYou"/>.</summary>
    public string DialectTip { get; init; } = "Separator: {0} · {1}\n{2} — click to change";
    public string Detected { get; init; } = "Detected automatically";
    public string ChosenByYou { get; init; } = "Chosen by you";
    public string DetectAgain { get; init; } = "Detect automatically";

    public string Sorting { get; init; } = "Sorting…";
    public string Copy { get; init; } = "Copy";
    public string EditCell { get; init; } = "Edit cell";
    public string ClearCells { get; init; } = "Clear cells";
    public string ShowInText { get; init; } = "Show in text";
    public string SortAscending { get; init; } = "Sort ascending";
    public string SortDescending { get; init; } = "Sort descending";
    public string ClearSort { get; init; } = "Original order";
    public string SortTip { get; init; } = "Click to sort (view only — the file keeps its order)";
    public string Monospace { get; init; } = "Monospace font";

    public string ReadOnly { get; init; } = "Read-only";
    public string ReadOnlyTip { get; init; } = "The file is locked — click to unlock it";

    /// <summary>(count) → "3 rows with a different number of cells"; null = an English default.</summary>
    public Func<int, string>? Ragged { get; init; }

    /// <summary>{0} = the expected number of cells.</summary>
    public string RaggedTip { get; init; } = "These rows do not have {0} cells like the header. Click to go to the next one.";

    /// <summary>{0} = the line where the quote opens.</summary>
    public string UnclosedQuote { get; init; } = "Unclosed quote in line {0}";
    public string UnclosedQuoteTip { get; init; } = "A quote is never closed, so the rest of the file reads as one cell. Click to go to it.";

    /// <summary>Row-number tooltip of a ragged row: {0} cells in it, {1} expected.</summary>
    public string RowCellsTip { get; init; } = "This row has {0} cells, {1} expected";

    /// <summary>Row-number tooltip: {0} = the line in the file.</summary>
    public string LineTip { get; init; } = "Line {0} in the file · double-click to show it in the text";

    public string Empty { get; init; } = "No rows yet";
    public string EmptyHint { get; init; } = "Switch to Text to start writing";
    public string NoMatches { get; init; } = "No rows match the filter";

    internal string RowsText(int n) => Rows?.Invoke(n) ?? string.Format(Culture, RowsFormat, n.ToString("N0", Culture));

    internal string FilteredText(int shown, int total) =>
        Filtered?.Invoke(shown, total) ?? string.Format(Culture, FilteredFormat, shown.ToString("N0", Culture), total.ToString("N0", Culture));

    internal string ColumnsText(int n) => Columns?.Invoke(n) ?? string.Format(Culture, ColumnsFormat, n.ToString("N0", Culture));

    internal string RaggedText(int n) => Ragged?.Invoke(n) ?? (n == 1 ? "1 row with a different number of cells" : n.ToString("N0", Culture) + " rows with a different number of cells");

    internal string DelimiterName(char d) => d switch
    {
        ',' => Comma,
        ';' => Semicolon,
        '\t' => Tab,
        '|' => Pipe,
        _ => "‘" + d + "’",
    };
}

public sealed class CsvPreviewOptions
{
    public CsvPreviewLabels Labels { get; set; } = new();

    /// <summary>Cell editing off (locked file). Changeable later through <see cref="CsvTablePreview.IsReadOnly"/>.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Delay before the text is parsed again after a change (a size-dependent extra is added for big texts).</summary>
    public TimeSpan RefreshDelay { get; set; } = TimeSpan.FromMilliseconds(60);

    /// <summary>Puts copied cells on the clipboard; null = <c>Clipboard.SetText</c>. Return false when it failed.</summary>
    public Func<string, bool>? SetClipboard { get; set; }

    /// <summary>Small transitions (the cell editor, the warning chip); null = always on.</summary>
    public Func<bool>? AnimationsEnabled { get; set; }

    /// <summary>Font family used when <see cref="CsvTablePreview.Monospace"/> is on.</summary>
    public string MonospaceFont { get; set; } = "Cascadia Mono, Consolas";
}

/// <summary>
/// What the user chose over the detected dialect (null = keep what was detected). Applied on top of every detection, so a
/// choice survives edits and re-parses; <see cref="IsEmpty"/> = back to automatic.
/// </summary>
public sealed record CsvDialectOverride(char? Delimiter = null, char? Quote = null, bool? HasHeader = null)
{
    public static CsvDialectOverride None { get; } = new();

    public bool IsEmpty => Delimiter is null && Quote is null && HasHeader is null;

    public CsvDialect ApplyTo(CsvDialect detected)
    {
        if (IsEmpty) return detected;
        var d = detected with
        {
            Delimiter = Delimiter ?? detected.Delimiter,
            Quote = Quote ?? detected.Quote,
            HasHeader = HasHeader ?? detected.HasHeader,
        };
        return d.Delimiter == d.Quote ? detected : d;
    }
}
