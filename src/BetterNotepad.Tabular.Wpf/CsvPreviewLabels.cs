namespace BetterNotepad.Tabular.Wpf;

/// <summary>UI texts of the table preview. The app fills them from its PL / EN resources; defaults are a fallback.</summary>
public sealed class CsvPreviewLabels
{
    public string FilterPlaceholder { get; init; } = "Filter rows";
    public string HeaderRow { get; init; } = "Header row";
    public string HeaderRowTip { get; init; } = "Treat the first row as column names";

    /// <summary>"{0} rows" (number already formatted).</summary>
    public string RowsFormat { get; init; } = "{0} rows";

    /// <summary>"{0} of {1} rows" while a filter is active.</summary>
    public string FilteredFormat { get; init; } = "{0} of {1} rows";

    /// <summary>"{0} columns".</summary>
    public string ColumnsFormat { get; init; } = "{0} columns";

    public string Comma { get; init; } = "comma";
    public string Semicolon { get; init; } = "semicolon";
    public string Tab { get; init; } = "tab";
    public string Pipe { get; init; } = "pipe";
    public string Sorting { get; init; } = "Sorting…";
    public string Copy { get; init; } = "Copy";
    public string EditCell { get; init; } = "Edit cell";
    public string ShowInText { get; init; } = "Show in text";
    public string SortAscending { get; init; } = "Sort ascending";
    public string SortDescending { get; init; } = "Sort descending";
    public string ClearSort { get; init; } = "Original order";
    public string SortTip { get; init; } = "Click to sort (view only — the file keeps its order)";
    public string ReadOnlyHint { get; init; } = "The file is locked";
}

public sealed class CsvPreviewOptions
{
    public CsvPreviewLabels Labels { get; set; } = new();

    /// <summary>Cell editing off (locked file). Changeable later through <see cref="CsvTablePreview.IsReadOnly"/>.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Delay before the text is parsed again after a change.</summary>
    public TimeSpan RefreshDelay { get; set; } = TimeSpan.FromMilliseconds(180);
}
