using BetterNotepad.Tabular;

namespace BetterNotepad.Tabular.Sample;

internal static class DialectTests
{
    [Case]
    public static void CommaFileWithHeader()
    {
        var d = CsvDialect.Detect("Name,Age,City\nAnna,30,Warszawa\nJan,41,Kraków\nEwa,25,Gdańsk\n");
        Check.Equal(',', d.Delimiter, "delimiter");
        Check.Equal('"', d.Quote, "quote");
        Check.True(d.HasHeader, "header");
        Check.Equal("\n", d.LineEnding, "eol");
        Check.Equal(CsvQuoteStyle.Minimal, d.QuoteStyle, "quote style");
        Check.True(d.LooksTabular, "looks tabular");
    }

    [Case]
    public static void PolishExcelSemicolonWithDecimalCommas()
    {
        var text = "Produkt;Cena;Ilość\r\nChleb;4,50;2\r\nMleko;3,29;10\r\nMasło;8,99;1\r\nSer;12,00;3\r\n";
        var d = CsvDialect.Detect(text);
        Check.Equal(';', d.Delimiter, "delimiter");
        Check.True(d.HasHeader, "header");
        Check.Equal("\r\n", d.LineEnding, "eol");
        var t = CsvTable.Parse(text);
        Check.Equal(3, t.ColumnCount, "columns");
        Check.Equal("4,50", t.GetCell(1, 1), "decimal comma stays one cell");
        Check.SeqEqual(new[] { "Produkt", "Cena", "Ilość" }, t.GetHeaders());
    }

    [Case]
    public static void DecimalCommaDoesNotBeatTheSemicolon()
    {
        // every row has exactly one ';' and exactly one ',': a tie, decided by priority (';' before ',')
        Check.Equal(';', CsvDialect.Detect("Chleb;4,50\nMleko;3,29\nMasło;8,99\n").Delimiter, "tie");
        // with a header line the comma loses on consistency alone
        Check.Equal(';', CsvDialect.Detect("Produkt;Cena\nChleb;4,50\nMleko;3,29\n").Delimiter, "header");
        Check.Equal(';', CsvDialect.Detect("a;1,5;2,5\nb;3,5;4,5\nc;5,5;6,5\n").Delimiter, "three columns");
    }

    [Case]
    public static void TsvWithCommasInText()
    {
        var d = CsvDialect.Detect("id\tname\tnote\n1\tAnna\ta, b, c\n2\tJan\tx,y\n3\tEwa\tz\n");
        Check.Equal('\t', d.Delimiter);
        Check.True(d.HasHeader);
    }

    [Case]
    public static void PipeDelimited()
    {
        var d = CsvDialect.Detect("a|b|c\n1|2|3\n4|5|6\n");
        Check.Equal('|', d.Delimiter);
        Check.True(d.HasHeader, "text over numbers is a header");
    }

    [Case]
    public static void DelimitersInsideQuotesAreNotCounted()
    {
        var d = CsvDialect.Detect("name,note\n\"x,y\",z\n\"p;q;r\",w\n\"1\t2\",v\n");
        Check.Equal(',', d.Delimiter);
    }

    [Case]
    public static void HeaderHeuristics()
    {
        Check.True(!CsvDialect.Detect("1,2,3\n4,5,6\n7,8,9\n").HasHeader, "all numeric");
        Check.True(CsvDialect.Detect("id,name\n1,Anna\n2,Jan\n").HasHeader, "id,name");
        Check.True(!CsvDialect.Detect("2026-01-01,5\n2026-01-02,7\n2026-01-03,9\n").HasHeader, "date first row");
        Check.True(CsvDialect.Detect("date,value\n2026-01-01,5\n2026-01-02,7\n").HasHeader, "date column");
        Check.True(CsvDialect.Detect("Name,City\nAnna,Warsaw\nJan,Krakow\n").HasHeader, "all text, unique names");
        Check.True(CsvDialect.Detect("Cena;Ilość\n4,50;2\n3,29;10\n").HasHeader, "decimal comma data under text");
        Check.True(!CsvDialect.Detect("4,50;2\n3,29;10\n8,99;1\n").HasHeader, "decimal comma data only");
        Check.True(!CsvDialect.Detect("1,2,3\n").HasHeader, "single numeric line");
        Check.True(CsvDialect.Detect("a,b,c\n").HasHeader, "single text line");
    }

    [Case]
    public static void NoStructureFallsBackToTheExtension()
    {
        var plain = CsvDialect.Detect("alpha\nbeta\ngamma\n");
        Check.Equal(',', plain.Delimiter);
        Check.Equal(0.0, plain.Confidence, "confidence");
        Check.True(!plain.LooksTabular, "looks tabular");
        Check.Equal('\t', CsvDialect.Detect("alpha\nbeta\n", "notes.tsv").Delimiter, "tsv default");
        Check.Equal(',', CsvDialect.Detect("", "x.csv").Delimiter, "empty");
        Check.Equal("\r\n", CsvDialect.Detect("").LineEnding, "empty eol");
    }

    [Case]
    public static void ExtensionBreaksTies()
    {
        // one pipe and one semicolon per row: both consistent; the semicolon wins on priority unless the extension says .psv
        var text = "a|b;c\n1|2;3\n4|5;6\n";
        Check.Equal(';', CsvDialect.Detect(text).Delimiter, "no extension");
        Check.Equal('|', CsvDialect.Detect(text, "data.psv").Delimiter, ".psv");
    }

    [Case]
    public static void BomIsIgnored()
    {
        var text = "﻿a;b\n1;2\n3;4\n";
        var d = CsvDialect.Detect(text);
        Check.Equal(';', d.Delimiter);
        var t = CsvTable.Parse(text, d);
        Check.Equal(1, t.ContentStart, "content start");
        Check.Equal(1, t.RowStart(0), "first row starts after the BOM");
        Check.Equal("a", t.GetCell(0, 0), "header cell has no BOM");
    }

    [Case]
    public static void SingleQuoteQuoting()
    {
        var d = CsvDialect.Detect("'a';'b'\n'1';'2'\n'3';'4'\n");
        Check.Equal('\'', d.Quote, "quote");
        Check.Equal(';', d.Delimiter, "delimiter");
        // apostrophes inside plain text do not turn it on
        var o = CsvDialect.Detect("name,note\nO'Brien,it's\nD'Arcy,can't\n");
        Check.Equal('"', o.Quote, "apostrophes");
    }

    [Case]
    public static void QuoteStyles()
    {
        Check.Equal(CsvQuoteStyle.All, CsvDialect.Detect("\"a\",\"b\"\n\"1\",\"2\"\n\"3\",\"4\"\n").QuoteStyle, "all");
        Check.Equal(CsvQuoteStyle.NonNumeric, CsvDialect.Detect("\"name\",\"age\"\n\"Anna\",30\n\"Jan\",41\n").QuoteStyle, "nonnumeric");
        Check.Equal(CsvQuoteStyle.Minimal, CsvDialect.Detect("a,\"b,c\"\n1,2\n").QuoteStyle, "minimal");
    }

    [Case]
    public static void LineEndingFollowsTheRecordTerminators()
    {
        // LF inside a quoted cell does not count, the CRLF between records does
        Check.Equal("\r\n", CsvDialect.Detect("a,b\r\n\"x\ny\",z\r\n1,2\r\n").LineEnding, "crlf");
        Check.Equal("\n", CsvDialect.Detect("a,b\n1,2\n3,4\n").LineEnding, "lf");
        Check.Equal("\r", CsvDialect.Detect("a,b\r1,2\r3,4\r").LineEnding, "cr");
        Check.Equal("\r\n", CsvDialect.Detect("a,b").LineEnding, "none");
    }

    [Case]
    public static void RaggedRowsStillDetect()
    {
        var text = "a,b,c\n1,2,3\n4,5\n6,7,8\n9,10,11\n12,13,14\n";
        Check.Equal(',', CsvDialect.Detect(text).Delimiter);
    }

    [Case]
    public static void LargeSampleIsBounded()
    {
        var sb = new System.Text.StringBuilder("a;b;c\n");
        for (var i = 0; i < 200_000; i++) sb.Append(i).Append(';').Append(i * 2).Append(";x\n");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var d = CsvDialect.Detect(sb.ToString());
        Check.Equal(';', d.Delimiter);
        Check.True(sw.ElapsedMilliseconds < 300, $"detection of a big file took {sw.ElapsedMilliseconds} ms");
    }
}
