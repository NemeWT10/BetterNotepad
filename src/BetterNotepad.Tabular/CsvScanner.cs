using System.Buffers;
using System.Collections.Concurrent;
using System.Text;

namespace BetterNotepad.Tabular;

/// <summary>
/// One field of a record, as an exact range of the source text. <see cref="Start"/>..<see cref="End"/> covers the
/// whole raw field (opening and closing quote, doubled quotes and any stray text after the closing quote included),
/// never the delimiter. Replacing exactly this range replaces exactly this cell.
/// </summary>
public readonly record struct CsvField(int Start, int End, bool Quoted)
{
    public int Length => End - Start;
}

/// <summary>Where one record lives in the text. <see cref="End"/> excludes the line terminator, <see cref="Next"/> is the start of the next record.</summary>
public readonly record struct CsvRecordBounds(int Start, int End, int Next, bool UnterminatedQuote)
{
    public int TerminatorLength => Next - End;
}

/// <summary>
/// Low level, allocation-free CSV scanning. Rules (identical in <see cref="ReadRecord"/> and <see cref="SplitFields"/>):
/// a quote opens a quoted field only at the very start of a field; inside it a doubled quote is a literal quote;
/// after the closing quote any stray text up to the next delimiter belongs to the field; a quote in the middle of an
/// unquoted field is an ordinary character; CRLF, LF and CR all end a record (like the editor's lines);
/// an unterminated quote swallows the rest of the text.
/// </summary>
public static class CsvScanner
{
    private static readonly SearchValues<char> DoubleQuoteBreaks = SearchValues.Create("\"\r\n");
    private static readonly SearchValues<char> SingleQuoteBreaks = SearchValues.Create("'\r\n");
    private static readonly ConcurrentDictionary<char, SearchValues<char>> OtherBreaks = new();

    private static SearchValues<char> Breaks(char quote) => quote switch
    {
        '"' => DoubleQuoteBreaks,
        '\'' => SingleQuoteBreaks,
        _ => OtherBreaks.GetOrAdd(quote, q => SearchValues.Create([q, '\r', '\n'])),
    };

    /// <summary>1 when the text starts with a BOM character (U+FEFF), else 0. Records begin after it.</summary>
    public static int ContentStart(ReadOnlySpan<char> text) => text.Length > 0 && text[0] == '﻿' ? 1 : 0;

    /// <summary>
    /// Finds the record that starts at <paramref name="position"/>. Returns false at the end of the text, so a text that
    /// ends with a line break has no phantom empty last record, while a blank line in the middle is a record with one empty field.
    /// </summary>
    public static bool ReadRecord(ReadOnlySpan<char> text, int position, char delimiter, char quote, out CsvRecordBounds record)
    {
        if (position >= text.Length)
        {
            record = default;
            return false;
        }

        var breaks = Breaks(quote);
        var pos = position;
        while (true)
        {
            var rel = text[pos..].IndexOfAny(breaks);
            if (rel < 0)
            {
                record = new CsvRecordBounds(position, text.Length, text.Length, false);
                return true;
            }

            var j = pos + rel;
            var c = text[j];
            if (c == quote)
            {
                if (j == position || text[j - 1] == delimiter)
                {
                    // a quoted field: skip to its closing quote (doubled quotes are escapes)
                    var i = j + 1;
                    while (true)
                    {
                        var q = i < text.Length ? text[i..].IndexOf(quote) : -1;
                        if (q < 0)
                        {
                            record = new CsvRecordBounds(position, text.Length, text.Length, true);
                            return true;
                        }

                        i += q + 1;
                        if (i < text.Length && text[i] == quote)
                        {
                            i++;
                            continue;
                        }

                        break;
                    }

                    pos = i;
                }
                else pos = j + 1;

                continue;
            }

            var next = c == '\r' && j + 1 < text.Length && text[j + 1] == '\n' ? j + 2 : j + 1;
            record = new CsvRecordBounds(position, j, next, false);
            return true;
        }
    }

    /// <summary>
    /// Splits the record <c>[start, end)</c> (terminator excluded) into fields. Returns the TOTAL field count (always at
    /// least 1); only as many fields as fit are written to <paramref name="dest"/>, so an empty span just counts.
    /// </summary>
    public static int SplitFields(ReadOnlySpan<char> text, int start, int end, char delimiter, char quote, Span<CsvField> dest)
    {
        var count = 0;
        var pos = start;
        while (true)
        {
            var fieldEnd = FieldEnd(text, pos, end, delimiter, quote, out var quoted);
            if (count < dest.Length) dest[count] = new CsvField(pos, fieldEnd, quoted);
            count++;
            if (fieldEnd >= end) break;
            pos = fieldEnd + 1;
        }

        return count;
    }

    /// <summary>The field number <paramref name="column"/> of the record <c>[start, end)</c>; false when the record has fewer fields. Stops scanning at that field.</summary>
    public static bool FieldAt(ReadOnlySpan<char> text, int start, int end, char delimiter, char quote, int column, out CsvField field)
    {
        var pos = start;
        for (var index = 0; ; index++)
        {
            var fieldEnd = FieldEnd(text, pos, end, delimiter, quote, out var quoted);
            if (index == column)
            {
                field = new CsvField(pos, fieldEnd, quoted);
                return true;
            }

            if (fieldEnd >= end) break;
            pos = fieldEnd + 1;
        }

        field = default;
        return false;
    }

    /// <summary>End (exclusive, delimiter not included) of the field that starts at <paramref name="pos"/> inside the record that ends at <paramref name="end"/>.</summary>
    private static int FieldEnd(ReadOnlySpan<char> text, int pos, int end, char delimiter, char quote, out bool quoted)
    {
        quoted = false;
        if (pos < end && text[pos] == quote)
        {
            quoted = true;
            var i = pos + 1;
            while (true)
            {
                var q = i < end ? text[i..end].IndexOf(quote) : -1;
                if (q < 0) return end; // unterminated: the field runs to the end of the record
                i += q + 1;
                if (i < end && text[i] == quote)
                {
                    i++;
                    continue;
                }

                break;
            }

            // closed: stray text after the closing quote belongs to the field, up to the next delimiter
            var d = i < end ? text[i..end].IndexOf(delimiter) : -1;
            return d < 0 ? end : i + d;
        }

        var dd = pos < end ? text[pos..end].IndexOf(delimiter) : -1;
        return dd < 0 ? end : pos + dd;
    }

    /// <summary>Number of fields in the record <c>[start, end)</c>.</summary>
    public static int CountFields(ReadOnlySpan<char> text, int start, int end, char delimiter, char quote)
        => SplitFields(text, start, end, delimiter, quote, default);

    /// <summary>The cell value: quotes removed, doubled quotes collapsed; stray text after a closing quote is kept literally.</summary>
    public static string Decode(ReadOnlySpan<char> text, CsvField field, char quote)
    {
        var raw = text.Slice(field.Start, field.Length);
        if (!field.Quoted || raw.Length == 0) return raw.ToString();

        // fast path: no escaped quote
        var close = raw[1..].IndexOf(quote);
        if (close < 0) return raw[1..].ToString(); // unterminated
        close += 1;
        if (close + 1 >= raw.Length || raw[close + 1] != quote)
            return close + 1 == raw.Length ? raw[1..close].ToString() : string.Concat(raw[1..close], raw[(close + 1)..]);

        var sb = new StringBuilder(raw.Length);
        var i = 1;
        while (i < raw.Length)
        {
            var c = raw[i];
            if (c == quote)
            {
                if (i + 1 < raw.Length && raw[i + 1] == quote)
                {
                    sb.Append(quote);
                    i += 2;
                    continue;
                }

                // closing quote: the rest is literal
                sb.Append(raw[(i + 1)..]);
                return sb.ToString();
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString(); // unterminated
    }

    /// <summary>The cell value without allocating when nothing needs unescaping; otherwise the decoded text lands in <paramref name="scratch"/>.</summary>
    internal static ReadOnlySpan<char> DecodeSpan(ReadOnlySpan<char> text, CsvField field, char quote, ref string? scratch)
    {
        var raw = text.Slice(field.Start, field.Length);
        if (!field.Quoted || raw.Length == 0) return raw;
        var close = raw[1..].IndexOf(quote);
        if (close >= 0)
        {
            close += 1;
            if (close + 1 == raw.Length) return raw[1..close];
        }

        scratch = Decode(text, field, quote);
        return scratch;
    }
}

/// <summary>A decoded record with the exact offsets of its cells.</summary>
public readonly record struct CsvRecord(int Index, CsvRecordBounds Bounds, CsvField[] Fields)
{
    public string Value(string text, int column, char quote = '"')
        => column < Fields.Length ? CsvScanner.Decode(text, Fields[column], quote) : string.Empty;
}

/// <summary>
/// Streaming record / field tokenizer over a text snapshot. Memory use is independent of the file size:
/// each call to <see cref="MoveNextRecord"/> yields the next record with its cells' exact source offsets.
/// <code>
/// var t = new CsvTokenizer(text, dialect);
/// while (t.MoveNextRecord()) foreach (var f in t.Fields) { /* f.Start..f.End in text */ }
/// </code>
/// </summary>
public struct CsvTokenizer
{
    private readonly string _text;
    private readonly char _delimiter;
    private readonly char _quote;
    private readonly int _end;
    private int _pos;
    private CsvField[]? _fields;
    private int _fieldCount;

    /// <param name="text">The text snapshot.</param>
    /// <param name="dialect">Delimiter and quote.</param>
    /// <param name="start">First offset to read (default: after a BOM).</param>
    /// <param name="end">Exclusive end offset (default: the end of the text).</param>
    public CsvTokenizer(string text, CsvDialect dialect, int start = -1, int end = -1)
    {
        _text = text;
        _delimiter = dialect.Delimiter;
        _quote = dialect.Quote;
        _end = end < 0 ? text.Length : end;
        _pos = start < 0 ? CsvScanner.ContentStart(text) : start;
        RecordIndex = -1;
        Record = default;
        _fields = null;
        _fieldCount = 0;
    }

    public CsvRecordBounds Record { get; private set; }

    /// <summary>Zero based index of the current record.</summary>
    public int RecordIndex { get; private set; }

    /// <summary>The cells of the current record (valid until the next <see cref="MoveNextRecord"/>).</summary>
    public readonly ReadOnlySpan<CsvField> Fields => _fields is null ? default : _fields.AsSpan(0, _fieldCount);

    public bool MoveNextRecord()
    {
        if (!CsvScanner.ReadRecord(_text.AsSpan(0, _end), _pos, _delimiter, _quote, out var rec)) return false;
        _pos = rec.Next;
        Record = rec;
        RecordIndex++;
        _fields ??= new CsvField[16];
        var n = CsvScanner.SplitFields(_text, rec.Start, rec.End, _delimiter, _quote, _fields);
        if (n > _fields.Length)
        {
            _fields = new CsvField[n + 16];
            CsvScanner.SplitFields(_text, rec.Start, rec.End, _delimiter, _quote, _fields);
        }

        _fieldCount = n;
        return true;
    }

    /// <summary>Decoded value of cell <paramref name="column"/> of the current record.</summary>
    public readonly string Value(int column)
        => column < _fieldCount ? CsvScanner.Decode(_text, _fields![column], _quote) : string.Empty;

    /// <summary>Lazily enumerates all records of <paramref name="text"/> (allocates a field array per record).</summary>
    public static IEnumerable<CsvRecord> Read(string text, CsvDialect dialect)
    {
        var t = new CsvTokenizer(text, dialect);
        while (t.MoveNextRecord()) yield return new CsvRecord(t.RecordIndex, t.Record, t.Fields.ToArray());
    }
}
