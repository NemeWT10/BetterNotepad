using System.Text;

namespace BetterNotepad.Tabular;

/// <summary>Turns cell values into field text the way the file writes them.</summary>
public static class CsvWriter
{
    /// <summary>True when the value cannot be written bare: it holds the delimiter, the quote or a line break.</summary>
    public static bool NeedsQuoting(string value, CsvDialect dialect)
    {
        var s = value.AsSpan();
        return s.IndexOfAny(dialect.Delimiter, dialect.Quote) >= 0 || s.IndexOfAny('\r', '\n') >= 0;
    }

    /// <summary>
    /// Every line break in <paramref name="value"/> (CRLF, LF, a lone CR) written as <paramref name="lineEnding"/>, so a break typed
    /// into a cell matches the file's own line endings.
    /// </summary>
    public static string NormalizeLineBreaks(string value, string lineEnding)
    {
        if (value.AsSpan().IndexOfAny('\r', '\n') < 0) return value;
        var unified = value.Replace("\r\n", "\n").Replace('\r', '\n');
        return lineEnding == "\n" ? unified : unified.Replace("\n", lineEnding);
    }

    /// <summary>
    /// Field text for <paramref name="value"/>: quoted only when needed (or when <paramref name="forceQuote"/>, or when the
    /// dialect's style quotes everything / every text field); quotes inside are doubled. Embedded line breaks are written as given.
    /// </summary>
    public static string Encode(string value, CsvDialect dialect, bool forceQuote = false, bool quoteEmpty = false)
    {
        var quote = forceQuote || NeedsQuoting(value, dialect);
        if (!quote)
        {
            switch (dialect.QuoteStyle)
            {
                case CsvQuoteStyle.All:
                    quote = value.Length > 0 || quoteEmpty;
                    break;
                case CsvQuoteStyle.NonNumeric:
                    quote = value.Length > 0 && !CsvValue.TryParseNumber(value, dialect.Delimiter == ',' ? CsvNumberStyle.DotDecimal : CsvNumberStyle.Auto, out _);
                    break;
            }
        }

        if (!quote) return value;
        var q = dialect.Quote;
        var sb = new StringBuilder(value.Length + 4);
        sb.Append(q);
        foreach (var c in value)
        {
            if (c == q) sb.Append(q);
            sb.Append(c);
        }

        return sb.Append(q).ToString();
    }
}

public sealed partial class CsvTable
{
    // ------------------------------------------------------------------ edit API (returns TextEdits, never mutates anything)

    /// <summary>
    /// Replaces the value of one cell. The delimiter stays, and the quoting style stays: a cell that was quoted stays quoted,
    /// a bare cell is quoted only when the new value needs it (or the file quotes everything). Setting a column past the
    /// end of a short record pads it with delimiters. A change that leaves the text identical returns a no-op edit
    /// (<see cref="TextEdit.IsNoOp"/>).
    /// </summary>
    public TextEdit SetCell(int row, int col, string value)
    {
        CheckRow(row);
        ArgumentNullException.ThrowIfNull(value);
        if (col < 0) throw new ArgumentOutOfRangeException(nameof(col));
        var n = GetFieldCount(row);
        if (TryGetField(row, col, out var f))
        {
            var encoded = CsvWriter.Encode(value, Dialect, forceQuote: f.Quoted);
            // the only cell of an unterminated last record must not vanish into an empty text (that would drop the record)
            if (encoded.Length == 0 && n == 1 && row == RowCount - 1 && !EndsWithLineBreak) encoded = new string(Dialect.Quote, 2);
            if (Text.AsSpan(f.Start, f.Length).SequenceEqual(encoded)) return new TextEdit(f.Start, 0, "");
            return new TextEdit(f.Start, f.Length, encoded);
        }

        // beyond the end of a short record: nothing to write for an empty value
        if (value.Length == 0 && Dialect.QuoteStyle != CsvQuoteStyle.All) return new TextEdit(_ends[row], 0, "");
        var sb = new StringBuilder();
        sb.Append(Dialect.Delimiter, col - n + 1);
        sb.Append(CsvWriter.Encode(value, Dialect, quoteEmpty: true));
        return new TextEdit(_ends[row], 0, sb.ToString());
    }

    /// <summary>
    /// Inserts a record before record <paramref name="index"/> (<see cref="RowCount"/> = append). Missing values are empty;
    /// the record is padded to <see cref="ColumnCount"/> cells. Uses the file's delimiter, quoting style and line ending;
    /// a file that did not end with a line break still does not after appending.
    /// </summary>
    public TextEdit AddRow(int index, IReadOnlyList<string>? values = null)
    {
        if (index < 0 || index > RowCount) throw new ArgumentOutOfRangeException(nameof(index));
        var cols = Math.Max(values?.Count ?? 0, ColumnCount);
        var d = Dialect;
        var sb = new StringBuilder();
        for (var i = 0; i < cols; i++)
        {
            if (i > 0) sb.Append(d.Delimiter);
            sb.Append(CsvWriter.Encode(i < (values?.Count ?? 0) ? values![i] : "", d, quoteEmpty: d.QuoteStyle == CsvQuoteStyle.All));
        }

        var row = sb.ToString();
        if (index < RowCount) return KeepBreaksApart(new TextEdit(_starts[index], 0, row + d.LineEnding));
        if (RowCount == 0) return new TextEdit(Text.Length, 0, row + d.LineEnding);
        if (EndsWithLineBreak) return KeepBreaksApart(new TextEdit(Text.Length, 0, row + d.LineEnding));
        // an empty record after an unterminated tail would vanish (a line break after "a" does not add a record): write it as an empty quoted field
        if (row.Length == 0) row = new string(d.Quote, 2);
        return new TextEdit(Text.Length, 0, d.LineEnding + row);
    }

    /// <summary>A CR next to an LF would fuse into one CRLF and swallow the new record; only possible in files with mixed line breaks.</summary>
    private TextEdit KeepBreaksApart(TextEdit e)
    {
        var rep = e.Replacement;
        if (rep.Length == 0) return e;
        if (e.Offset > 0 && Text[e.Offset - 1] == '\r' && rep == "\n") rep = "\r";
        else if (rep[^1] == '\r' && e.Offset < Text.Length && Text[e.Offset] == '\n') rep += '\n';
        return e with { Replacement = rep };
    }

    /// <summary>Removes a record together with its line break. Deleting an unterminated last record removes the break before it instead.</summary>
    public TextEdit DeleteRow(int row)
    {
        CheckRow(row);
        return DeleteRun(row, row);
    }

    /// <summary>Removes several records. Adjacent ones become one edit; the edits are non-overlapping and ordered from the end of the text to the start.</summary>
    public IReadOnlyList<TextEdit> DeleteRows(IEnumerable<int> rows)
    {
        var sorted = rows.Distinct().OrderBy(r => r).ToList();
        foreach (var r in sorted) CheckRow(r);
        var edits = new List<TextEdit>();
        var i = 0;
        while (i < sorted.Count)
        {
            var a = sorted[i];
            var b = a;
            while (i + 1 < sorted.Count && sorted[i + 1] == b + 1) b = sorted[++i];
            edits.Add(DeleteRun(a, b));
            i++;
        }

        edits.Reverse();
        return edits;
    }

    private TextEdit DeleteRun(int first, int last)
    {
        var from = _starts[first];
        var to = RowNext(last);
        // removing the unterminated tail: take the break that precedes it, so the file still ends the same way
        // (not after a blank line: that record could not stay as the last, unterminated one)
        if (last == RowCount - 1 && !EndsWithLineBreak && first > 0 && _ends[first - 1] > _starts[first - 1]) from = _ends[first - 1];
        return new TextEdit(from, to - from, "");
    }

    // ------------------------------------------------------------------ the table of the edited text

    /// <summary>The table of <c>edit.ApplyTo(Text)</c>, same dialect (header / delimiter choices survive edits).</summary>
    public CsvTable WithEdit(TextEdit edit) => WithEdit(edit, edit.ApplyTo(Text));

    /// <summary>
    /// The table of <paramref name="newText"/>, which must equal <c>edit.ApplyTo(Text)</c> (pass the editor's text to avoid building it twice).
    /// Only the records touched by the edit are scanned again and the offsets of the rest are shifted; when that is not provably the
    /// same as a full parse (a quote opened or closed across rows, a CR/LF pair split or joined) the text is parsed completely.
    /// </summary>
    public CsvTable WithEdit(TextEdit edit, string newText)
    {
        if (edit.Offset < 0 || edit.Length < 0 || edit.End > Text.Length) throw new ArgumentOutOfRangeException(nameof(edit));
        if (edit.IsNoOp) return this;
        if (newText.Length != Text.Length + edit.Delta) throw new ArgumentException("newText does not match the edit.", nameof(newText));
        return TryIncremental(edit, newText) ?? Parse(newText, Dialect);
    }

    /// <summary>Applies edits (all against this table's text, e.g. from <see cref="DeleteRows"/>) one after another and returns the final table.</summary>
    public CsvTable WithEdits(IEnumerable<TextEdit> edits)
    {
        var table = this;
        foreach (var e in edits.Where(e => !e.IsNoOp).OrderByDescending(e => e.Offset)) table = table.WithEdit(e);
        return table;
    }

    private CsvTable? TryIncremental(TextEdit edit, string newText)
    {
        var n = RowCount;
        if (n == 0 || HasUnterminatedQuote || edit.Offset < _starts[0]) return null;
        if ((newText.Length > 0 && newText[0] == '\uFEFF') != (ContentStart == 1)) return null; // a BOM appeared or disappeared at the very start

        var o = edit.Offset;
        var oe = edit.End;
        var ra = UpperBound(o) - 1; // last row starting at or before the edit
        var rb = Math.Max(LowerBound(oe), ra + 1); // first row starting at or after the end of the edit
        var oldStart = _starts[ra];
        var oldEnd = rb < n ? _starts[rb] : Text.Length;
        var newEnd = oldEnd + edit.Delta;

        // a CR at the start of the region joined with the LF of an earlier break or vice versa would change the pairing
        if (oldStart > 0 && oldStart < newText.Length && newText[oldStart - 1] == '\r' && newText[oldStart] == '\n') return null;

        var d = Dialect;
        var region = newText.AsSpan(0, newEnd);
        var rs = new List<int>();
        var re = new List<int>();
        var pos = oldStart;
        var lastTerminator = 0;
        while (pos < newEnd)
        {
            if (!CsvScanner.ReadRecord(region, pos, d.Delimiter, d.Quote, out var rec) || rec.UnterminatedQuote) return null;
            rs.Add(rec.Start);
            re.Add(rec.End);
            lastTerminator = rec.Next - rec.End;
            pos = rec.Next;
        }

        if (newEnd < newText.Length)
        {
            // the following rows stay as they are only if the region ends with a complete line break
            if (rs.Count > 0 && lastTerminator == 0) return null;
            if (rs.Count > 0 && lastTerminator == 1 && newText[newEnd - 1] == '\r' && newText[newEnd] == '\n') return null;
            if (rs.Count == 0 && newText[newEnd] == '\n' && newEnd > 0 && newText[newEnd - 1] == '\r') return null;
        }

        var m = rs.Count;
        var count = ra + m + (n - rb);
        var starts = new int[count];
        var ends = new int[count];
        Array.Copy(_starts, 0, starts, 0, ra);
        Array.Copy(_ends, 0, ends, 0, ra);
        for (var i = 0; i < m; i++)
        {
            starts[ra + i] = rs[i];
            ends[ra + i] = re[i];
        }

        for (var i = rb; i < n; i++)
        {
            starts[ra + m + (i - rb)] = _starts[i] + edit.Delta;
            ends[ra + m + (i - rb)] = _ends[i] + edit.Delta;
        }

        var result = new CsvTable(newText, Dialect, ContentStart, starts, ends, false);

        // column count: exact when the widest record cannot have been among the replaced ones
        var known = Volatile.Read(ref _columnCount);
        if (known >= 0)
        {
            var oldMax = 0;
            for (var i = ra; i < rb; i++) oldMax = Math.Max(oldMax, GetFieldCount(i));
            var newMax = 0;
            for (var i = 0; i < m; i++) newMax = Math.Max(newMax, CsvScanner.CountFields(newText, rs[i], re[i], d.Delimiter, d.Quote));
            if (oldMax < known) result._columnCount = Math.Max(known, newMax);
            else if (newMax >= known) result._columnCount = newMax;
        }

        return result;
    }

    /// <summary>Number of rows whose start is at or before <paramref name="offset"/>.</summary>
    private int UpperBound(int offset)
    {
        int lo = 0, hi = _starts.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_starts[mid] <= offset) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    /// <summary>Index of the first row whose start is at or after <paramref name="offset"/>.</summary>
    private int LowerBound(int offset)
    {
        int lo = 0, hi = _starts.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_starts[mid] < offset) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }
}
