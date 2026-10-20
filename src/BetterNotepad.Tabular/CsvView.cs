using System.Collections.Concurrent;
using System.Globalization;

namespace BetterNotepad.Tabular;

/// <summary>How a column is ordered.</summary>
public enum CsvSortMode
{
    /// <summary>Decided from the data: numbers when ~80 % of the non-empty cells are numbers, dates likewise, else text.</summary>
    Auto,

    /// <summary>Natural text order: "item2" before "item10", case-insensitive, culture aware.</summary>
    Text,

    /// <summary>Numeric order; cells that are not numbers follow the numbers.</summary>
    Number,

    /// <summary>Chronological order; cells that are not dates follow the dates.</summary>
    Date,
}

/// <summary>"Contains" filter on one column, or on any column when <see cref="Column"/> is -1. Case-insensitive.</summary>
public readonly record struct CsvColumnFilter(int Column, string Text);

/// <summary>What a view shows. Immutable, so a UI can compare / copy it freely.</summary>
public sealed record CsvViewSpec
{
    /// <summary>Column to sort by, -1 = file order.</summary>
    public int SortColumn { get; init; } = -1;

    public bool SortDescending { get; init; }

    public CsvSortMode SortMode { get; init; } = CsvSortMode.Auto;

    /// <summary>All filters must match (AND). Empty text is ignored.</summary>
    public IReadOnlyList<CsvColumnFilter> Filters { get; init; } = [];

    /// <summary>Default: dot decimal in comma files (where a comma inside a number is a thousands mark), automatic elsewhere.</summary>
    public CsvNumberStyle NumberStyle { get; init; } = CsvNumberStyle.Auto;

    /// <summary>Text order for non-ASCII data; <see langword="null"/> = the current culture (Polish letters sort in alphabet order).</summary>
    public CultureInfo? Culture { get; init; }

    public bool IsDefault => SortColumn < 0 && Filters.All(f => string.IsNullOrEmpty(f.Text));
}

/// <summary>
/// A sorted / filtered window onto the DATA rows of a <see cref="CsvTable"/> (the header record is not part of it).
/// The view never changes the text: it is only a mapping from display position to record index.
/// Building one is the expensive step (O(n log n) for a sort) and has no UI affinity, so do it on a background thread.
/// </summary>
public sealed class CsvView
{
    private readonly int[]? _order; // display index → record index; null = file order of all data rows
    private int[]? _inverse;

    private CsvView(CsvTable table, CsvViewSpec spec, int[]? order, CsvSortMode resolvedMode)
    {
        Table = table;
        Spec = spec;
        _order = order;
        ResolvedSortMode = resolvedMode;
    }

    public CsvTable Table { get; }

    public CsvViewSpec Spec { get; }

    /// <summary>The sort mode actually used when <see cref="CsvViewSpec.SortMode"/> was Auto (Text when not sorted).</summary>
    public CsvSortMode ResolvedSortMode { get; }

    /// <summary>Number of rows shown.</summary>
    public int RowCount => _order?.Length ?? Table.DataRowCount;

    /// <summary>True when the view is the file order with no filter (display index = data row index).</summary>
    public bool IsIdentity => _order is null;

    /// <summary>Builds the view. Filters first (file order), then the sort (stable: equal cells keep file order; empty cells always last).</summary>
    public static CsvView Create(CsvTable table, CsvViewSpec? spec = null, CancellationToken ct = default)
    {
        spec ??= new CsvViewSpec();
        var filters = spec.Filters.Where(f => !string.IsNullOrEmpty(f.Text)).ToArray();
        var sort = spec.SortColumn >= 0;
        if (filters.Length == 0 && !sort) return new CsvView(table, spec, null, CsvSortMode.Text);

        var rows = filters.Length == 0 ? SequentialDataRows(table) : Filter(table, filters, ct);
        var mode = CsvSortMode.Text;
        if (sort)
        {
            ct.ThrowIfCancellationRequested();
            var keys = table.GetColumnKeys(spec.SortColumn, spec.SortMode, ResolveNumberStyle(table, spec), spec.Culture, ct);
            mode = keys.Mode;
            var h = table.HeaderRowCount;
            var desc = spec.SortDescending;
            Array.Sort(rows, (a, b) =>
            {
                var r = keys.Compare(a - h, b - h, desc);
                return r != 0 ? r : a.CompareTo(b);
            });
        }

        return new CsvView(table, spec, rows, mode);
    }

    /// <summary>Same view definition over another table (typically the table after an edit).</summary>
    public CsvView Rebind(CsvTable table, CancellationToken ct = default) => Create(table, Spec, ct);

    /// <summary>
    /// The same rows in the same order over <paramref name="table"/>, without sorting or filtering again — for the table
    /// after cell edits (same records, new values): an edited row stays where the user is looking instead of jumping to its
    /// new sort position or vanishing from the filter. Falls back to <see cref="Rebind"/> when the record count changed.
    /// </summary>
    public CsvView Retain(CsvTable table)
    {
        if (table.RowCount != Table.RowCount || table.HeaderRowCount != Table.HeaderRowCount) return Rebind(table);
        return new CsvView(table, Spec, _order, ResolvedSortMode);
    }

    /// <summary>Another definition over the same table (column sort keys are cached on the table, so re-sorting / re-filtering is cheap).</summary>
    public CsvView With(CsvViewSpec spec, CancellationToken ct = default) => Create(Table, spec, ct);

    /// <summary>Record index (in <see cref="CsvTable"/>) of the row shown at <paramref name="displayIndex"/>.</summary>
    public int SourceRow(int displayIndex)
    {
        if ((uint)displayIndex >= (uint)RowCount) throw new ArgumentOutOfRangeException(nameof(displayIndex));
        return _order is null ? Table.HeaderRowCount + displayIndex : _order[displayIndex];
    }

    /// <summary>Display position of a record, or -1 when it is the header or was filtered out.</summary>
    public int DisplayIndexOf(int sourceRow)
    {
        var h = Table.HeaderRowCount;
        if (sourceRow < h || sourceRow >= Table.RowCount) return -1;
        if (_order is null) return sourceRow - h;
        var inv = _inverse;
        if (inv is null)
        {
            inv = new int[Table.RowCount - h];
            Array.Fill(inv, -1);
            for (var i = 0; i < _order.Length; i++) inv[_order[i] - h] = i;
            _inverse = inv;
        }

        return inv[sourceRow - h];
    }

    public string GetCell(int displayIndex, int col) => Table.GetCell(SourceRow(displayIndex), col);

    public string[] GetRow(int displayIndex) => Table.GetRow(SourceRow(displayIndex));

    // ------------------------------------------------------------------ internals

    private static CsvNumberStyle ResolveNumberStyle(CsvTable table, CsvViewSpec spec)
        => spec.NumberStyle != CsvNumberStyle.Auto ? spec.NumberStyle : table.Dialect.Delimiter == ',' ? CsvNumberStyle.DotDecimal : CsvNumberStyle.Auto;

    private static int[] SequentialDataRows(CsvTable table)
    {
        var h = table.HeaderRowCount;
        var rows = new int[table.DataRowCount];
        for (var i = 0; i < rows.Length; i++) rows[i] = h + i;
        return rows;
    }

    private sealed record Prepared(int Column, string Needle, bool RawSafe);

    private static int[] Filter(CsvTable table, CsvColumnFilter[] filters, CancellationToken ct)
    {
        var d = table.Dialect;
        var prepared = filters.Select(f => new Prepared(f.Column, f.Text,
            f.Text.AsSpan().IndexOfAny(d.Delimiter, d.Quote) < 0 && f.Text.AsSpan().IndexOfAny('\r', '\n') < 0)).ToArray();

        var h = table.HeaderRowCount;
        var n = table.DataRowCount;
        const int chunk = 8192;
        var chunks = (n + chunk - 1) / chunk;
        var parts = new int[chunks][];

        void Work(int c)
        {
            ct.ThrowIfCancellationRequested();
            var from = h + c * chunk;
            var to = Math.Min(table.RowCount, from + chunk);
            var list = new List<int>(Math.Min(chunk, to - from));
            for (var r = from; r < to; r++) if (RowMatches(table, r, prepared)) list.Add(r);
            parts[c] = list.ToArray();
        }

        if (n < 30000) for (var c = 0; c < chunks; c++) Work(c);
        else Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct }, Work);

        var total = 0;
        foreach (var p in parts) total += p.Length;
        var result = new int[total];
        var at = 0;
        foreach (var p in parts)
        {
            p.CopyTo(result, at);
            at += p.Length;
        }

        return result;
    }

    private static bool RowMatches(CsvTable table, int row, Prepared[] filters)
    {
        var text = table.Text;
        var d = table.Dialect;
        var start = table.RowStart(row);
        var end = table.RowEnd(row);
        foreach (var f in filters)
        {
            if (f.Column < 0)
            {
                if (f.RawSafe)
                {
                    if (!text.AsSpan(start, end - start).Contains(f.Needle, StringComparison.OrdinalIgnoreCase)) return false;
                    continue;
                }

                var any = false;
                foreach (var field in table.GetFields(row))
                {
                    string? scratch = null;
                    if (CsvScanner.DecodeSpan(text, field, d.Quote, ref scratch).Contains(f.Needle, StringComparison.OrdinalIgnoreCase))
                    {
                        any = true;
                        break;
                    }
                }

                if (!any) return false;
            }
            else
            {
                if (!CsvScanner.FieldAt(text, start, end, d.Delimiter, d.Quote, f.Column, out var field)) return false;
                string? scratch = null;
                if (!CsvScanner.DecodeSpan(text, field, d.Quote, ref scratch).Contains(f.Needle, StringComparison.OrdinalIgnoreCase)) return false;
            }
        }

        return true;
    }
}

/// <summary>Sort keys of one column (indexed by data row), cached on the table.</summary>
internal sealed class ColumnKeys(CsvSortMode mode, byte[] cls, double[] numbers, string[] texts, NaturalComparer comparer)
{
    public CsvSortMode Mode { get; } = mode;

    /// <summary>0 = comparable value, 1 = text in a number / date column (after the values), 2 = empty (always last).</summary>
    private readonly byte[] _class = cls;

    private readonly double[] _numbers = numbers;
    private readonly string[] _texts = texts;

    public int Compare(int a, int b, bool descending)
    {
        var ca = _class[a];
        var cb = _class[b];
        if (ca != cb) return ca < cb ? -1 : 1;
        if (ca == 2) return 0;
        var r = ca == 0 && Mode != CsvSortMode.Text ? _numbers[a].CompareTo(_numbers[b]) : comparer.Compare(_texts[a], _texts[b]);
        return descending ? -r : r;
    }
}

public sealed partial class CsvTable
{
    private readonly ConcurrentDictionary<(int Col, CsvSortMode Mode, CsvNumberStyle Style, string Culture), ColumnKeys> _keyCache = new();

    internal ColumnKeys GetColumnKeys(int col, CsvSortMode mode, CsvNumberStyle style, CultureInfo? culture, CancellationToken ct)
    {
        culture ??= CultureInfo.CurrentCulture;
        var key = (col, mode, style, culture.Name);
        if (_keyCache.TryGetValue(key, out var cached)) return cached;

        var h = HeaderRowCount;
        var n = RowCount - h;
        var texts = new string[n];
        var anyNonAscii = false;
        for (var i = 0; i < n; i++)
        {
            if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            var t = GetCell(h + i, col);
            texts[i] = t;
            if (!anyNonAscii && !System.Text.Ascii.IsValid(t)) anyNonAscii = true;
        }

        var comparer = anyNonAscii ? new NaturalComparer(culture.CompareInfo) : NaturalComparer.Ordinal;
        var cls = new byte[n];
        var nums = new double[n];
        var resolved = mode;

        if (mode != CsvSortMode.Text)
        {
            var dateOrder = CsvValue.DetectSlashOrder(texts);
            var kinds = new byte[n]; // 0 empty, 1 number, 2 date, 3 text
            var dates = new double[n];
            int numbers = 0, datesFound = 0, nonEmpty = 0;
            for (var i = 0; i < n; i++)
            {
                var s = texts[i];
                if (CsvValue.Trim(s).Length == 0) continue;
                nonEmpty++;
                if (mode != CsvSortMode.Date && CsvValue.TryParseNumber(s, style, out nums[i]))
                {
                    kinds[i] = 1;
                    numbers++;
                }
                else if (mode != CsvSortMode.Number && CsvValue.TryParseDate(s, dateOrder, out dates[i]))
                {
                    kinds[i] = 2;
                    datesFound++;
                }
                else kinds[i] = 3;
            }

            if (mode == CsvSortMode.Auto)
            {
                resolved = nonEmpty > 0 && numbers >= 0.8 * nonEmpty ? CsvSortMode.Number
                    : nonEmpty > 0 && datesFound >= 0.8 * nonEmpty ? CsvSortMode.Date
                    : CsvSortMode.Text;
            }

            if (resolved != CsvSortMode.Text)
            {
                var want = resolved == CsvSortMode.Number ? (byte)1 : (byte)2;
                for (var i = 0; i < n; i++)
                {
                    cls[i] = kinds[i] == 0 ? (byte)2 : kinds[i] == want ? (byte)0 : (byte)1;
                    if (resolved == CsvSortMode.Date) nums[i] = dates[i];
                }
            }
        }

        if (resolved == CsvSortMode.Text)
        {
            for (var i = 0; i < n; i++) cls[i] = CsvValue.Trim(texts[i]).Length == 0 ? (byte)2 : (byte)0;
        }

        var keys = new ColumnKeys(resolved, cls, nums, texts, comparer);
        if (_keyCache.Count >= 6) _keyCache.Clear();
        _keyCache[key] = keys;
        return keys;
    }
}
