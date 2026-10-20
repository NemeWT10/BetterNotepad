namespace BetterNotepad.Tabular;

/// <summary>
/// How regular a table is: the column count records are expected to have (the header's, else the most common one) and the
/// records that differ from it ("ragged" rows). Blank lines are not counted: they are common and harmless.
/// </summary>
public sealed record CsvShape(int ExpectedColumns, int RaggedCount, IReadOnlyList<int> RaggedRows)
{
    /// <summary>At most this many ragged record indices are kept (navigation "next ragged row").</summary>
    public const int MaxListed = 10_000;

    public static CsvShape Empty { get; } = new(0, 0, []);

    public bool IsRagged => RaggedCount > 0;
}

public sealed partial class CsvTable
{
    /// <summary>
    /// Scans every record once (in parallel for big tables) and reports which ones have a different number of cells than the
    /// header (or, without a header, than the most common count). Also settles <see cref="ColumnCount"/> on the way.
    /// </summary>
    public CsvShape AnalyzeShape(CancellationToken ct = default)
    {
        var n = RowCount;
        if (n == 0) return CsvShape.Empty;
        var counts = new int[n];
        var text = Text;
        var d = Dialect.Delimiter;
        var q = Dialect.Quote;
        var starts = _starts;
        var ends = _ends;

        void Count(int from, int to)
        {
            for (var i = from; i < to; i++)
            {
                if (((i - from) & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                // a blank line: one empty cell, never "ragged"
                counts[i] = ends[i] == starts[i] ? 0 : CsvScanner.CountFields(text, starts[i], ends[i], d, q);
            }
        }

        const int chunk = 8192;
        if (n < 20000) Count(0, n);
        else Parallel.For(0, (n + chunk - 1) / chunk, new ParallelOptions { CancellationToken = ct }, c => Count(c * chunk, Math.Min(n, (c + 1) * chunk)));

        var max = 1;
        var histogram = new Dictionary<int, int>();
        foreach (var c in counts)
        {
            if (c == 0) continue;
            if (c > max) max = c;
            histogram[c] = histogram.GetValueOrDefault(c) + 1;
        }

        if (Volatile.Read(ref _columnCount) < 0) Volatile.Write(ref _columnCount, max);

        int expected;
        if (HeaderRowCount > 0 && counts[0] > 0) expected = counts[0];
        else
        {
            expected = 0;
            var best = -1;
            foreach (var (cols, f) in histogram)
            {
                if (f > best || (f == best && cols > expected))
                {
                    expected = cols;
                    best = f;
                }
            }
        }

        if (expected == 0) return CsvShape.Empty;
        var ragged = 0;
        var list = new List<int>();
        for (var i = 0; i < n; i++)
        {
            var c = counts[i];
            if (c == 0 || c == expected) continue;
            ragged++;
            if (list.Count < CsvShape.MaxListed) list.Add(i);
        }

        return new CsvShape(expected, ragged, list);
    }
}
