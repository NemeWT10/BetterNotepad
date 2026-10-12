using BetterNotepad.Tabular;

namespace BetterNotepad.Tabular.Sample;

internal static class TableTests
{
    private static readonly CsvDialect Comma = new() { Delimiter = ',', HasHeader = false, LineEnding = "\n" };

    private static CsvTable T(string text, CsvDialect? d = null) => CsvTable.Parse(text, d ?? Comma);

    private static string[][] Rows(CsvTable t) => Enumerable.Range(0, t.RowCount).Select(t.GetRow).ToArray();

    private static void RowsEqual(string[][] expected, CsvTable t)
    {
        Check.Equal(expected.Length, t.RowCount, "row count");
        for (var r = 0; r < expected.Length; r++) Check.SeqEqual(expected[r], t.GetRow(r), "row " + r);
    }

    // ------------------------------------------------------------------ tokenizer

    [Case]
    public static void QuotedFieldsWithDelimiterNewlineAndEscapedQuote()
    {
        var text = "a,\"b,c\",\"d\"\"e\",\"f\ng\"\n";
        var t = T(text);
        RowsEqual([["a", "b,c", "d\"e", "f\ng"]], t);
        var f = t.GetFields(0);
        Check.SeqEqual(new[] { "a", "\"b,c\"", "\"d\"\"e\"", "\"f\ng\"" }, f.Select(x => text.Substring(x.Start, x.Length)), "raw cells");
        Check.SeqEqual(new[] { false, true, true, true }, f.Select(x => x.Quoted), "quoted flags");
        Check.Equal(1, t.RowCount, "a newline inside quotes does not start a row");
    }

    [Case]
    public static void EmptyFieldsAndTrailingDelimiters()
    {
        RowsEqual([["", "", ""]], T(",,\n"));
        RowsEqual([["a", ""]], T("a,\n"));
        RowsEqual([[""]], T("\n"));
        RowsEqual([["", "x"]], T(",x"));
    }

    [Case]
    public static void LineBreaksAtTheEnd()
    {
        Check.Equal(0, T("").RowCount, "empty text");
        Check.Equal(2, T("a,b\n1,2").RowCount, "no trailing newline");
        Check.Equal(2, T("a,b\n1,2\n").RowCount, "trailing newline adds no phantom row");
        Check.Equal(3, T("a,b\n1,2\n\n").RowCount, "an empty last line is a record");
        RowsEqual([["a", "b"], ["1", "2"], [""]], T("a,b\n1,2\n\n"));
        Check.Equal(2, T("a\r\nb\r\n").RowCount, "crlf");
        Check.True(T("a\nb\n").EndsWithLineBreak, "ends with break");
        Check.True(!T("a\nb").EndsWithLineBreak, "no break at the end");
    }

    [Case]
    public static void CrLfLfAndCrAreAllRecordBreaks()
    {
        RowsEqual([["a"], ["b"], ["c"], ["d"]], T("a\r\nb\nc\rd"));
        var t = T("a\r\nb\n");
        Check.Equal(1, t.RowEnd(0), "end excludes the terminator");
        Check.Equal(3, t.RowStart(1), "crlf is one break");
        Check.Equal(3, t.RowNext(0));
        Check.Equal(5, t.RowNext(1));
    }

    [Case]
    public static void StrayTextAfterClosingQuoteBelongsToTheField()
    {
        var t = T("\"abc\"def,x\n\"abc\" ,y\n");
        RowsEqual([["abcdef", "x"], ["abc ", "y"]], t);
        Check.Equal("\"abc\"def", t.GetCellRaw(0, 0), "raw");
    }

    [Case]
    public static void QuoteInTheMiddleOfAnUnquotedFieldIsLiteral()
    {
        RowsEqual([["a\"b", "c"], ["5\" pipe", "d"]], T("a\"b,c\n5\" pipe,d\n"));
        // a quote after a space does not open a quoted field either
        RowsEqual([[" \"x", "y"]], T(" \"x,y"));
    }

    [Case]
    public static void UnterminatedQuoteSwallowsTheRest()
    {
        var t = T("a,\"b\nc,d\ne");
        Check.True(t.HasUnterminatedQuote, "flag");
        Check.Equal(1, t.RowCount, "one record");
        Check.Equal("b\nc,d\ne", t.GetCell(0, 1), "value");
        Check.True(!T("a,\"b\"\n").HasUnterminatedQuote, "closed");
    }

    [Case]
    public static void StreamingTokenizerKeepsExactOffsets()
    {
        var text = "﻿id;txt\r\n1;\"x;y\"\r\n2;\"l1\r\nl2\"\r\n";
        var d = new CsvDialect { Delimiter = ';' };
        var t = new CsvTokenizer(text, d);
        var seen = new List<string>();
        while (t.MoveNextRecord())
        {
            foreach (var f in t.Fields) seen.Add(text.Substring(f.Start, f.Length));
            seen.Add("|" + t.Record.Start + "-" + t.Record.End + ">" + t.Record.Next);
        }

        Check.SeqEqual(new[] { "id", "txt", "|1-7>9", "1", "\"x;y\"", "|9-16>18", "2", "\"l1\r\nl2\"", "|18-28>30" }, seen);
        var all = CsvTokenizer.Read(text, d).ToList();
        Check.Equal(3, all.Count, "records");
        Check.Equal("l1\r\nl2", all[2].Value(text, 1), "decoded multi-line value");
    }

    [Case]
    public static void TokenizerRangeAndDecode()
    {
        var text = "x,y\n1,2\n3,4\n";
        var t = new CsvTokenizer(text, Comma, start: 4, end: 8);
        Check.True(t.MoveNextRecord());
        Check.Equal("2", t.Value(1));
        Check.True(!t.MoveNextRecord(), "range ends");
    }

    // ------------------------------------------------------------------ table

    [Case]
    public static void RaggedRowsAndColumnCount()
    {
        var t = T("a,b,c\n1,2\n3,4,5,6\n");
        Check.Equal(4, t.ColumnCount, "columns");
        Check.Equal(2, t.GetFieldCount(1), "short row");
        Check.Equal("", t.GetCell(1, 3), "past the end");
        Check.True(!t.TryGetField(1, 3, out _), "no field");
        Check.SeqEqual(new[] { "A", "B", "C", "D" }, t.GetHeaders(), "headerless names");
        Check.SeqEqual(new[] { "a", "b", "c", "D" }, CsvTable.Parse("a,b,c\n1,2\n3,4,5,6\n", Comma with { HasHeader = true }).GetHeaders(), "header names padded");
        Check.Equal("AA", CsvTable.ColumnLetters(26));
        Check.Equal("Z", CsvTable.ColumnLetters(25));
        Check.Equal("AB", CsvTable.ColumnLetters(27));
    }

    [Case]
    public static void HeaderRowIsNotADataRow()
    {
        var t = CsvTable.Parse("h1,h2\n1,2\n3,4\n", Comma with { HasHeader = true });
        Check.Equal(3, t.RowCount, "rows");
        Check.Equal(1, t.HeaderRowCount, "header rows");
        Check.Equal(2, t.DataRowCount, "data rows");
        Check.Equal(0, T("h1,h2\n1,2\n").HeaderRowCount, "no header");
        Check.Equal(0, CsvTable.Parse("", Comma with { HasHeader = true }).HeaderRowCount, "empty");
    }

    [Case]
    public static void OffsetsMapToCells()
    {
        var text = "a,bb,ccc\n1,22,333\n";
        var t = T(text);
        Check.True(t.TryFindCell(3, out var r, out var c) && (r, c) == (0, 1), "inside bb");
        Check.True(t.TryFindCell(1, out r, out c) && (r, c) == (0, 0), "on the first delimiter belongs to the left cell");
        Check.True(t.TryFindCell(14, out r, out c) && (r, c) == (1, 2), "inside 333");
        Check.True(t.TryFindCell(text.Length, out r, out c) && (r, c) == (1, 2), "end of text");
        Check.Equal(1, t.FindRow(9), "start of row 2");
        Check.Equal(0, t.FindRow(8), "line break of row 1");
        Check.Equal(-1, T("").FindRow(0), "empty table");
    }

    [Case]
    public static void CellsRoundTripToExactRanges()
    {
        var text = "﻿n;v\r\n\"a;b\";\"1,5\"\r\n\"multi\r\nline\";2\r\n";
        var t = CsvTable.Parse(text, new CsvDialect { Delimiter = ';' });
        Check.Equal(3, t.RowCount, "rows");
        var rebuilt = text[..t.ContentStart];
        for (var r = 0; r < t.RowCount; r++)
        {
            var f = t.GetFields(r);
            Check.Equal(t.RowStart(r), f[0].Start, "first cell starts the row");
            Check.Equal(t.RowEnd(r), f[^1].End, "last cell ends the row");
            rebuilt += string.Join(";", f.Select(x => text.Substring(x.Start, x.Length))) + text[t.RowEnd(r)..t.RowNext(r)];
        }

        Check.Equal(text, rebuilt, "text rebuilt from cell ranges");
    }

    [Case]
    public static void Cancellation()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 100_000; i++) sb.Append("a,b\n");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Check.Throws<OperationCanceledException>(() => CsvTable.Parse(sb.ToString(), Comma, ct: cts.Token), "parse");
    }

    // ------------------------------------------------------------------ SetCell

    private static string Set(string text, int row, int col, string value, CsvDialect? d = null)
    {
        var t = T(text, d);
        return t.SetCell(row, col, value).ApplyTo(text);
    }

    [Case]
    public static void SetCellPlain()
    {
        Check.Equal("a,b\n1,x\n", Set("a,b\n1,2\n", 1, 1, "x"));
        Check.Equal("a,b\n,2\n", Set("a,b\n1,2\n", 1, 0, ""), "empty value");
        Check.Equal("a,b\n1,z", Set("a,b\n1,2", 1, 1, "z"), "no trailing newline");
    }

    [Case]
    public static void SetCellQuotesOnlyWhenNeeded()
    {
        Check.Equal("a,b\n\"p,q\",2\n", Set("a,b\n1,2\n", 1, 0, "p,q"), "delimiter");
        Check.Equal("a,b\n\"say \"\"hi\"\"\",2\n", Set("a,b\n1,2\n", 1, 0, "say \"hi\""), "quotes are doubled");
        Check.Equal("a,b\n1,\"l1\nl2\"\n", Set("a,b\n1,2\n", 1, 1, "l1\nl2"), "line break");
        Check.Equal("a,b\n1,  spaced  \n", Set("a,b\n1,2\n", 1, 1, "  spaced  "), "spaces need no quotes");
        Check.Equal("a,b\n1,ż ó ł\n", Set("a,b\n1,2\n", 1, 1, "ż ó ł"), "non ASCII");
    }

    [Case]
    public static void SetCellKeepsAQuotedCellQuoted()
    {
        Check.Equal("a,b\n\"7\",2\n", Set("a,b\n\"1\",2\n", 1, 0, "7"));
        Check.Equal("a,b\n\"\",2\n", Set("a,b\n\"1\",2\n", 1, 0, ""), "empty quoted");
        Check.Equal("a,b\n\"x\",2\n", Set("a,b\n\"l1\nl2\",2\n", 1, 0, "x"), "multi-line cell replaced whole");
        Check.Equal("a,b\n\"p\"\"q\",2\n", Set("a,b\n\"1\",2\n", 1, 0, "p\"q"), "escaping inside a quoted cell");
    }

    [Case]
    public static void SetCellKeepsDelimiterAndPolishDecimals()
    {
        var d = new CsvDialect { Delimiter = ';', LineEnding = "\r\n", HasHeader = true };
        var text = "Produkt;Cena\r\nChleb;4,50\r\n";
        Check.Equal("Produkt;Cena\r\nChleb;5,25\r\n", Set(text, 1, 1, "5,25", d), "decimal comma needs no quoting in a ; file");
        Check.Equal("Produkt;Cena\r\n\"Chleb; razowy\";4,50\r\n", Set(text, 1, 0, "Chleb; razowy", d), "semicolon in the value");
        var tsv = new CsvDialect { Delimiter = '\t', HasHeader = false, LineEnding = "\n" };
        Check.Equal("a\tb\n1\t\"x\ty\"\n", Set("a\tb\n1\t2\n", 1, 1, "x\ty", tsv), "tab in the value");
        Check.Equal("a\tb\n1\tx,y\n", Set("a\tb\n1\t2\n", 1, 1, "x,y", tsv), "comma is plain text in a tsv");
    }

    [Case]
    public static void SetCellPadsShortRows()
    {
        Check.Equal("a,b,c\n1,,,x\n", Set("a,b,c\n1\n", 1, 3, "x"));
        Check.Equal("a,b\n,z\n", Set("a,b\n\n", 1, 1, "z"), "blank line");
        Check.Equal("a,b\nx\n", Set("a,b\n\n", 1, 0, "x"), "blank line first cell");
        var noop = T("a,b,c\n1\n").SetCell(1, 5, "");
        Check.True(noop.IsNoOp, "an empty value past the end changes nothing");
    }

    [Case]
    public static void SetCellToTheSameValueIsANoOp()
    {
        var text = "a,b\n1,\"x,y\"\n\"q\",2\n";
        var t = T(text);
        for (var r = 0; r < t.RowCount; r++)
        {
            for (var c = 0; c < 2; c++)
            {
                var e = t.SetCell(r, c, t.GetCell(r, c));
                Check.True(e.IsNoOp, $"cell {r},{c}");
                Check.Equal(text, e.ApplyTo(text), $"text after cell {r},{c}");
            }
        }
    }

    [Case]
    public static void SetCellKeepsBomAndOtherCells()
    {
        Check.Equal("﻿x,b\n1,2\n", Set("﻿a,b\n1,2\n", 0, 0, "x"));
        var e = T("﻿a,b\n1,2\n").SetCell(0, 0, "x");
        Check.Equal(1, e.Offset, "edit starts after the BOM");
        Check.Equal(1, e.Length);
    }

    [Case]
    public static void QuoteStylesAreRespectedWhenWriting()
    {
        var all = new CsvDialect { Delimiter = ',', HasHeader = true, QuoteStyle = CsvQuoteStyle.All, LineEnding = "\n" };
        Check.Equal("\"a\",\"b\"\n,\"2\"\n", Set("\"a\",\"b\"\n\"1\",\"2\"\n", 1, 0, "", all).Replace("\"\"", ""), "empty stays empty for a bare edit");
        Check.Equal("\"a\",\"b\"\n\"7\",\"2\"\n", Set("\"a\",\"b\"\n,\"2\"\n", 1, 0, "7", all), "all: a bare empty cell becomes quoted");
        var nn = new CsvDialect { Delimiter = ',', HasHeader = true, QuoteStyle = CsvQuoteStyle.NonNumeric, LineEnding = "\n" };
        var text = "\"name\",\"age\"\nplain,30\n";
        Check.Equal("\"name\",\"age\"\n\"abc\",30\n", Set(text, 1, 0, "abc", nn), "text gets quotes");
        Check.Equal("\"name\",\"age\"\nplain,41\n", Set(text, 1, 1, "41", nn), "numbers stay bare");
    }

    // ------------------------------------------------------------------ AddRow / DeleteRow

    [Case]
    public static void AddRowInTheMiddleAndAtTheEnd()
    {
        var text = "a,b\n1,2\n3,4\n";
        var t = T(text);
        Check.Equal("a,b\nx,y\n1,2\n3,4\n", t.AddRow(1, ["x", "y"]).ApplyTo(text), "middle");
        Check.Equal("x,y\na,b\n1,2\n3,4\n", t.AddRow(0, ["x", "y"]).ApplyTo(text), "first");
        Check.Equal("a,b\n1,2\n3,4\nx,y\n", t.AddRow(3, ["x", "y"]).ApplyTo(text), "append keeps the trailing newline");
        var open = "a,b\n1,2";
        Check.Equal("a,b\n1,2\nx,y", T(open).AddRow(2, ["x", "y"]).ApplyTo(open), "append to a file without trailing newline");
    }

    [Case]
    public static void AddRowUsesTheFilesLineEndingQuotingAndWidth()
    {
        var crlf = new CsvDialect { Delimiter = ';', LineEnding = "\r\n", HasHeader = true };
        var text = "a;b;c\r\n1;2;3\r\n";
        var t = CsvTable.Parse(text, crlf);
        Check.Equal("a;b;c\r\n1;2;3\r\n;;\r\n", t.AddRow(2).ApplyTo(text), "empty row has the width of the table");
        Check.Equal("a;b;c\r\nx;;\r\n1;2;3\r\n", t.AddRow(1, ["x"]).ApplyTo(text), "short values are padded");
        Check.Equal("a;b;c\r\n\"p;q\";r;\r\n1;2;3\r\n", t.AddRow(1, ["p;q", "r"]).ApplyTo(text), "quoted when needed");
        Check.Equal("x,y\r\n", CsvTable.Parse("", new CsvDialect { LineEnding = "\r\n" }).AddRow(0, ["x", "y"]).ApplyTo(""), "empty file");
        var all = new CsvDialect { Delimiter = ',', LineEnding = "\n", QuoteStyle = CsvQuoteStyle.All };
        var at = "\"a\",\"b\"\n\"1\",\"2\"\n";
        Check.Equal(at + "\"x\",\"\"\n", CsvTable.Parse(at, all).AddRow(2, ["x", ""]).ApplyTo(at), "quote all");
        Check.Throws<ArgumentOutOfRangeException>(() => t.AddRow(4), "past the end");
    }

    [Case]
    public static void DeleteRows()
    {
        var text = "a,b\n1,2\n3,4\n";
        var t = T(text);
        Check.Equal("1,2\n3,4\n", t.DeleteRow(0).ApplyTo(text), "first");
        Check.Equal("a,b\n3,4\n", t.DeleteRow(1).ApplyTo(text), "middle");
        Check.Equal("a,b\n1,2\n", t.DeleteRow(2).ApplyTo(text), "last");
        var open = "a,b\n1,2\n3,4";
        Check.Equal("a,b\n1,2", T(open).DeleteRow(2).ApplyTo(open), "last row without a trailing newline takes the break before it");
        Check.Equal("", T("a,b").DeleteRow(0).ApplyTo("a,b"), "only row");
        Check.Equal("", T("a,b\n").DeleteRow(0).ApplyTo("a,b\n"), "only row with newline");
        var crlf = "a,b\r\n1,2\r\n3,4\r\n";
        Check.Equal("a,b\r\n3,4\r\n", T(crlf).DeleteRow(1).ApplyTo(crlf), "crlf");
        var multi = "a,b\n\"x\ny\",2\n3,4\n";
        Check.Equal("a,b\n3,4\n", T(multi).DeleteRow(1).ApplyTo(multi), "a record with a line break inside");
        Check.Equal("﻿1,2\n", T("﻿a,b\n1,2\n").DeleteRow(0).ApplyTo("﻿a,b\n1,2\n"), "BOM survives");
    }

    [Case]
    public static void DeleteSeveralRowsAtOnce()
    {
        var text = "r0\nr1\nr2\nr3\nr4\nr5";
        var t = T(text);
        var edits = t.DeleteRows([1, 2, 4]);
        Check.Equal(2, edits.Count, "adjacent rows merge");
        Check.Equal("r0\nr3\nr5", TextEdit.ApplyAll(text, edits), "result");
        var tail = t.DeleteRows([4, 5]);
        Check.Equal("r0\nr1\nr2\nr3", TextEdit.ApplyAll(text, tail), "tail without trailing newline");
        Check.Equal("", TextEdit.ApplyAll(text, t.DeleteRows(Enumerable.Range(0, 6))), "all rows");
        var applied = t.WithEdits(edits);
        Check.Equal("r0\nr3\nr5", applied.Text, "WithEdits text");
        Check.Equal(3, applied.RowCount, "WithEdits rows");
    }

    // ------------------------------------------------------------------ incremental table updates

    internal static void SameAsFullParse(CsvTable incremental, string context)
    {
        var full = CsvTable.Parse(incremental.Text, incremental.Dialect);
        Check.Equal(full.RowCount, incremental.RowCount, context + " row count");
        for (var r = 0; r < full.RowCount; r++)
        {
            if (full.RowStart(r) != incremental.RowStart(r) || full.RowEnd(r) != incremental.RowEnd(r))
                throw new CheckFailed($"{context}: row {r} is {incremental.RowStart(r)}..{incremental.RowEnd(r)} but a full parse says {full.RowStart(r)}..{full.RowEnd(r)}");
        }

        Check.Equal(full.HasUnterminatedQuote, incremental.HasUnterminatedQuote, context + " unterminated");
        Check.Equal(full.EndsWithLineBreak, incremental.EndsWithLineBreak, context + " ends with break");
        Check.Equal(full.ColumnCount, incremental.ColumnCount, context + " columns");
    }

    [Case]
    public static void IncrementalUpdateMatchesAFullParse()
    {
        var text = "a,b,c\n1,2,3\n\"x\ny\",5,6\n7,8,9\n";
        var t = T(text);
        _ = t.ColumnCount; // prime the cache so it is carried over
        var cases = new (string Name, TextEdit Edit)[]
        {
            ("set plain", t.SetCell(1, 1, "22")),
            ("set to multi-line", t.SetCell(3, 2, "l1\nl2")),
            ("set widens", t.SetCell(1, 5, "w")),
            ("add first", t.AddRow(0, ["q", "r", "s"])),
            ("add middle", t.AddRow(2, ["q", "r", "s"])),
            ("append", t.AddRow(4, ["q", "r", "s"])),
            ("delete header", t.DeleteRow(0)),
            ("delete multi-line", t.DeleteRow(2)),
            ("delete last", t.DeleteRow(3)),
            ("open a quote", new TextEdit(t.RowStart(1), 0, "\"")),
            ("break a row", new TextEdit(t.RowStart(1) + 2, 0, "\n")),
            ("join rows", new TextEdit(t.RowEnd(1), t.RowNext(1) - t.RowEnd(1), "")),
        };
        foreach (var (name, edit) in cases)
        {
            var next = t.WithEdit(edit);
            Check.Equal(edit.ApplyTo(text), next.Text, name + " text");
            SameAsFullParse(next, name);
        }

        Check.Equal(6, t.SetCell(1, 5, "w").ApplyTo(text).Split('\n')[1].Split(',').Length, "padding produced the expected number of cells");
    }

    [Case]
    public static void IncrementalUpdateHandlesCrLfSplitsAndJoins()
    {
        var text = "a\r\nb\r\nc\r\n";
        var t = T(text);
        // remove the CR of a CRLF: the LF alone still breaks the row
        SameAsFullParse(t.WithEdit(new TextEdit(1, 1, "")), "drop CR");
        // insert an LF right after a lone CR so they pair up
        var cr = T("a\rb\rc\r");
        SameAsFullParse(cr.WithEdit(new TextEdit(2, 0, "\n")), "CR + LF");
        // delete the row before a row that starts with LF
        var lf = T("a\r\n\nb");
        SameAsFullParse(lf.WithEdit(new TextEdit(1, 1, "")), "LF after CR");
    }
}
