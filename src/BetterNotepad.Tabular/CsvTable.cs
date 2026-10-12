namespace BetterNotepad.Tabular;

/// <summary>
/// An immutable table over a text snapshot. The text stays the single source of truth: the table keeps only two int
/// arrays (start and end offset of every record, 8 bytes per row) and tokenizes a row into cells on demand, so a file
/// with hundreds of thousands of rows costs almost nothing and any cell maps to an exact range of the text.
/// Edits are computed as <see cref="TextEdit"/>s (see <c>CsvTable.Edit.cs</c>) which the host applies to its buffer;
/// <see cref="WithEdit(TextEdit)"/> then derives the table of the new text without scanning the whole file again.
/// Thread safe (read only), so sort / filter views can be built on a background thread.
/// </summary>
public sealed partial class CsvTable
{
    private readonly int[] _starts;
    private readonly int[] _ends;
    private int _columnCount = -1;

    private CsvTable(string text, CsvDialect dialect, int contentStart, int[] starts, int[] ends, bool unterminatedQuote)
    {
        Text = text;
        Dialect = dialect;
        ContentStart = contentStart;
        _starts = starts;
        _ends = ends;
        HasUnterminatedQuote = unterminatedQuote;
    }

    /// <summary>The exact text this table describes.</summary>
    public string Text { get; }

    public CsvDialect Dialect { get; }

    /// <summary>1 when the text starts with a BOM character (it is not part of any cell), else 0.</summary>
    public int ContentStart { get; }

    /// <summary>A quoted field is never closed: it swallows the rest of the file (the host may warn).</summary>
    public bool HasUnterminatedQuote { get; }

    /// <summary>Number of records, header included. A text that ends with a line break has no extra empty record.</summary>
    public int RowCount => _starts.Length;

    /// <summary>1 when the dialect has a header and there is a first record, else 0.</summary>
    public int HeaderRowCount => Dialect.HasHeader && RowCount > 0 ? 1 : 0;

    public int DataRowCount => RowCount - HeaderRowCount;

    /// <summary>The last record is closed by a line break (new rows are then appended with a trailing break too).</summary>
    public bool EndsWithLineBreak => RowCount > 0 && _ends[^1] < Text.Length;

    /// <summary>Widest record (exact; computed on first use, in parallel for big tables).</summary>
    public int ColumnCount
    {
        get
        {
            var c = Volatile.Read(ref _columnCount);
            if (c >= 0) return c;
            c = ComputeColumnCount();
            Volatile.Write(ref _columnCount, c);
            return c;
        }
    }

    // ------------------------------------------------------------------ parsing

    /// <summary>
    /// Indexes the records of <paramref name="text"/>. With no <paramref name="dialect"/> it is detected
    /// (<see cref="CsvDialect.Detect"/>); pass one to keep the user's choice (delimiter, header on / off).
    /// </summary>
    public static CsvTable Parse(string text, CsvDialect? dialect = null, string? fileName = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        dialect ??= CsvDialect.Detect(text, fileName);
        dialect.Validate();
        var contentStart = CsvScanner.ContentStart(text);
        var (starts, ends, unterminated) = ScanRecords(text, contentStart, text.Length, dialect, ct);
        return new CsvTable(text, dialect, contentStart, starts, ends, unterminated);
    }

    private static (int[] Starts, int[] Ends, bool Unterminated) ScanRecords(string text, int from, int to, CsvDialect d, CancellationToken ct)
    {
        var starts = new int[1024];
        var ends = new int[1024];
        var n = 0;
        var pos = from;
        var unterminated = false;
        var span = text.AsSpan(0, to);
        while (CsvScanner.ReadRecord(span, pos, d.Delimiter, d.Quote, out var rec))
        {
            if (n == starts.Length)
            {
                Array.Resize(ref starts, n * 2);
                Array.Resize(ref ends, n * 2);
            }

            starts[n] = rec.Start;
            ends[n] = rec.End;
            n++;
            pos = rec.Next;
            if (rec.UnterminatedQuote) unterminated = true;
            if ((n & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
        }

        if (n != starts.Length)
        {
            Array.Resize(ref starts, n);
            Array.Resize(ref ends, n);
        }

        return (starts, ends, unterminated);
    }

    private int ComputeColumnCount()
    {
        var n = RowCount;
        if (n == 0) return 0;
        var text = Text;
        var d = Dialect.Delimiter;
        var q = Dialect.Quote;
        var starts = _starts;
        var ends = _ends;
        if (n < 20000)
        {
            var max = 1;
            for (var i = 0; i < n; i++) max = Math.Max(max, CsvScanner.CountFields(text, starts[i], ends[i], d, q));
            return max;
        }

        var result = 1;
        const int chunk = 8192;
        Parallel.For(0, (n + chunk - 1) / chunk, () => 1, (c, _, local) =>
        {
            var to = Math.Min(n, (c + 1) * chunk);
            for (var i = c * chunk; i < to; i++) local = Math.Max(local, CsvScanner.CountFields(text, starts[i], ends[i], d, q));
            return local;
        }, local =>
        {
            int seen;
            while (local > (seen = Volatile.Read(ref result)) && Interlocked.CompareExchange(ref result, local, seen) != seen) { }
        });
        return result;
    }

    // ------------------------------------------------------------------ rows and cells

    /// <summary>Offset of the first character of record <paramref name="row"/>.</summary>
    public int RowStart(int row) => _starts[row];

    /// <summary>Offset after the last character of the record (line terminator excluded).</summary>
    public int RowEnd(int row) => _ends[row];

    /// <summary>Offset of the next record (or the end of the text): the record plus its line break is <c>RowStart..RowNext</c>.</summary>
    public int RowNext(int row) => row + 1 < _starts.Length ? _starts[row + 1] : Text.Length;

    /// <summary>All cells of a record with their exact offsets.</summary>
    public CsvField[] GetFields(int row)
    {
        CheckRow(row);
        Span<CsvField> buf = stackalloc CsvField[48];
        var n = CsvScanner.SplitFields(Text, _starts[row], _ends[row], Dialect.Delimiter, Dialect.Quote, buf);
        if (n <= buf.Length) return buf[..n].ToArray();
        var all = new CsvField[n];
        CsvScanner.SplitFields(Text, _starts[row], _ends[row], Dialect.Delimiter, Dialect.Quote, all);
        return all;
    }

    /// <summary>Number of cells in a record (at least 1; a blank line is one empty cell).</summary>
    public int GetFieldCount(int row)
    {
        CheckRow(row);
        return CsvScanner.CountFields(Text, _starts[row], _ends[row], Dialect.Delimiter, Dialect.Quote);
    }

    /// <summary>The cell's range in the text, or false when the record is shorter than <paramref name="col"/>.</summary>
    public bool TryGetField(int row, int col, out CsvField field)
    {
        CheckRow(row);
        if (col < 0)
        {
            field = default;
            return false;
        }

        return CsvScanner.FieldAt(Text, _starts[row], _ends[row], Dialect.Delimiter, Dialect.Quote, col, out field);
    }

    /// <summary>Decoded value of a cell (empty string past the end of a short record).</summary>
    public string GetCell(int row, int col)
        => TryGetField(row, col, out var f) ? CsvScanner.Decode(Text, f, Dialect.Quote) : string.Empty;

    /// <summary>Raw source text of a cell, quotes included.</summary>
    public string GetCellRaw(int row, int col)
        => TryGetField(row, col, out var f) ? Text.Substring(f.Start, f.Length) : string.Empty;

    /// <summary>Decoded values of the whole record.</summary>
    public string[] GetRow(int row)
    {
        var fields = GetFields(row);
        var result = new string[fields.Length];
        for (var i = 0; i < fields.Length; i++) result[i] = CsvScanner.Decode(Text, fields[i], Dialect.Quote);
        return result;
    }

    /// <summary>Column names: the header record when the dialect has one, else A, B, C ... AA (padded to <see cref="ColumnCount"/>).</summary>
    public string[] GetHeaders()
    {
        var cols = ColumnCount;
        var names = new string[cols];
        var header = HeaderRowCount > 0 ? GetRow(0) : [];
        for (var i = 0; i < cols; i++) names[i] = i < header.Length ? header[i] : ColumnLetters(i);
        return names;
    }

    /// <summary>Spreadsheet style column letters: 0 → A, 25 → Z, 26 → AA.</summary>
    public static string ColumnLetters(int index)
    {
        var s = "";
        for (var i = index; i >= 0; i = i / 26 - 1) s = (char)('A' + i % 26) + s;
        return s;
    }

    // ------------------------------------------------------------------ text offsets → cells

    /// <summary>The record that contains <paramref name="offset"/> (0 inside the BOM; -1 for an empty table).</summary>
    public int FindRow(int offset)
    {
        if (RowCount == 0) return -1;
        var i = Array.BinarySearch(_starts, offset);
        if (i < 0) i = ~i - 1;
        return Math.Clamp(i, 0, RowCount - 1);
    }

    /// <summary>The cell under a text offset (an offset on a delimiter belongs to the cell on its left). False for an empty table.</summary>
    public bool TryFindCell(int offset, out int row, out int col)
    {
        row = FindRow(offset);
        col = 0;
        if (row < 0) return false;
        var fields = GetFields(row);
        for (var i = 0; i < fields.Length; i++)
        {
            if (offset <= fields[i].End || i == fields.Length - 1)
            {
                col = i;
                break;
            }
        }

        return true;
    }

    private void CheckRow(int row)
    {
        if ((uint)row >= (uint)_starts.Length) throw new ArgumentOutOfRangeException(nameof(row), row, "Row is outside the table.");
    }
}
