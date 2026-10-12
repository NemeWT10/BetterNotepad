using System.Globalization;
using BetterNotepad.Tabular;

namespace BetterNotepad.Tabular.Sample;

internal static class ValueTests
{
    [Case]
    public static void Numbers()
    {
        (string Text, double Value)[] good =
        [
            ("12", 12), ("-3", -3), ("+4.5", 4.5), ("12,50", 12.5), ("12.50", 12.5), ("1 234,5", 1234.5), ("1 234,5", 1234.5),
            ("1.234,56", 1234.56), ("1,234.56", 1234.56), ("1.234.567", 1234567), ("1,234,567", 1234567), ("12 zł", 12), ("12,5 zł", 12.5),
            ("$1,234.50", 1234.5), ("€ 5", 5), ("50%", 50), ("1e3", 1000), ("1.5E-3", 0.0015), ("007", 7), ("−5", -5), (".5", 0.5),
            ("  42  ", 42), ("1'234.5", 1234.5), ("-0,25", -0.25), ("12 PLN", 12),
        ];
        foreach (var (text, value) in good)
        {
            Check.True(CsvValue.TryParseNumber(text, CsvNumberStyle.Auto, out var v), "should parse: " + Check.Show(text));
            Check.True(Math.Abs(v - value) < 1e-9, $"{Check.Show(text)} = {v} (expected {value})");
        }

        string[] bad = ["", "abc", "1.2.3", "12,", "1,2,3", "1e", "--5", "12abc", "0x1F", "1 2", "NaN", "Infinity", "12.10.2026", "1,23,456", "."];
        foreach (var text in bad) Check.True(!CsvValue.TryParseNumber(text, CsvNumberStyle.Auto, out _), "should not parse: " + Check.Show(text));
    }

    [Case]
    public static void NumberStyles()
    {
        double V(string s, CsvNumberStyle st) => CsvValue.TryParseNumber(s, st, out var v) ? v : double.NaN;
        Check.Equal(1.234, V("1,234", CsvNumberStyle.Auto), "auto comma");
        Check.Equal(1.234, V("1.234", CsvNumberStyle.Auto), "auto dot");
        Check.Equal(1234.0, V("1,234", CsvNumberStyle.DotDecimal), "dot: comma groups");
        Check.Equal(1.234, V("1.234", CsvNumberStyle.DotDecimal), "dot: dot is the mark");
        Check.Equal(1.234, V("1,234", CsvNumberStyle.CommaDecimal), "comma: comma is the mark");
        Check.Equal(1234.0, V("1.234", CsvNumberStyle.CommaDecimal), "comma: dot groups");
        Check.True(double.IsNaN(V("12,5", CsvNumberStyle.DotDecimal)), "dot: 12,5 is not a valid grouping");
    }

    private static double Days(int y, int m, int d) => (new DateTime(y, m, d) - new DateTime(1970, 1, 1)).TotalDays;

    [Case]
    public static void Dates()
    {
        void Ok(string s, double expected, CsvDateOrder order = CsvDateOrder.DayFirst)
        {
            Check.True(CsvValue.TryParseDate(s, order, out var v), "should parse: " + s);
            Check.True(Math.Abs(v - expected) < 1e-9, $"{s} = {v} (expected {expected})");
        }

        Ok("2026-10-12", Days(2026, 10, 12));
        Ok("12.10.2026", Days(2026, 10, 12));
        Ok("12-10-2026", Days(2026, 10, 12));
        Ok("12/10/2026", Days(2026, 10, 12));
        Ok("12/10/2026", Days(2026, 12, 10), CsvDateOrder.MonthFirst);
        Ok("2026/10/12", Days(2026, 10, 12));
        Ok("2026-10-12 12:00", Days(2026, 10, 12) + 0.5);
        Ok("2026-10-12T06:00:00Z", Days(2026, 10, 12) + 0.25);
        Ok("2026-10-12T06:00:00+02:00", Days(2026, 10, 12) + 0.25);
        Ok("1.2.2026", Days(2026, 2, 1));
        string[] bad = ["2026-13-01", "2026-02-30", "31.04.2026", "12.10.26", "2026-10-12 25:00", "hello", "12:30", "2026-10", "1.234.567", "2026-10-12x"];
        foreach (var s in bad) Check.True(!CsvValue.TryParseDate(s, CsvDateOrder.DayFirst, out _), "should not parse: " + s);
    }

    [Case]
    public static void SlashOrderIsDecidedByTheData()
    {
        Check.Equal(CsvDateOrder.MonthFirst, CsvValue.DetectSlashOrder(["12/31/2025", "01/02/2025"]), "second part above 12");
        Check.Equal(CsvDateOrder.DayFirst, CsvValue.DetectSlashOrder(["31/12/2025", "01/02/2025"]), "first part above 12");
        Check.Equal(CsvDateOrder.DayFirst, CsvValue.DetectSlashOrder(["01/02/2025", "03/04/2025"]), "ambiguous: day first");
    }

    [Case]
    public static void Classification()
    {
        Check.Equal(CsvValueKind.Empty, CsvValue.Classify("  "));
        Check.Equal(CsvValueKind.Number, CsvValue.Classify("12,5"));
        Check.Equal(CsvValueKind.Date, CsvValue.Classify("12.10.2026"));
        Check.Equal(CsvValueKind.Date, CsvValue.Classify("2026-10-12"));
        Check.Equal(CsvValueKind.Text, CsvValue.Classify("Warszawa"));
        Check.Equal(CsvValueKind.Text, CsvValue.Classify("A1"));
    }

    [Case]
    public static void NaturalOrder()
    {
        var c = NaturalComparer.Ordinal;
        Check.True(c.Compare("item2", "item10") < 0, "2 < 10");
        Check.True(c.Compare("Item2", "item02") < 0, "fewer leading zeros first");
        Check.Equal(0, c.Compare("ABC", "abc"), "case-insensitive");
        Check.True(c.Compare("file9.txt", "file10.txt") < 0, "file9 < file10");
        Check.True(c.Compare("x1y2", "x1y10") < 0, "second digit run");
        Check.True(c.Compare("a", "a1") < 0, "prefix first");
        Check.True(c.Compare("10", "9") > 0, "pure numbers");
        Check.True(c.Compare("", "a") < 0, "empty first");
        var pl = new NaturalComparer(new CultureInfo("pl-PL").CompareInfo);
        Check.True(pl.Compare("Łukasz", "Lucjan") > 0 && pl.Compare("Łukasz", "Michał") < 0, "Polish alphabet puts Ł after L");
    }
}

internal static class ViewTests
{
    private static CsvTable T(string text, bool header = true, char delimiter = ',')
        => CsvTable.Parse(text, new CsvDialect { Delimiter = delimiter, HasHeader = header, LineEnding = "\n" });

    private static CsvColumnFilter F(int column, string text) => new(column, text);

    private static string[] Col(CsvView v, int col) => Enumerable.Range(0, v.RowCount).Select(i => v.GetCell(i, col)).ToArray();

    [Case]
    public static void IdentityViewHasNoOrderArray()
    {
        var t = T("h\na\nb\nc\n");
        var v = CsvView.Create(t);
        Check.True(v.IsIdentity, "identity");
        Check.Equal(3, v.RowCount, "rows");
        Check.Equal(1, v.SourceRow(0), "first data row");
        Check.Equal(2, v.DisplayIndexOf(3), "inverse");
        Check.Equal(-1, v.DisplayIndexOf(0), "header");
        Check.Throws<ArgumentOutOfRangeException>(() => v.SourceRow(3), "out of range");
        var h = T("a\nb\n", header: false);
        Check.Equal(2, CsvView.Create(h).RowCount, "headerless: all rows are data");
    }

    [Case]
    public static void NaturalTextSortBlanksLast()
    {
        var t = T("name\nitem10\nItem2\nitem1\n\nitem02\n");
        var asc = CsvView.Create(t, new CsvViewSpec { SortColumn = 0 });
        Check.SeqEqual(new[] { "item1", "Item2", "item02", "item10", "" }, Col(asc, 0), "ascending");
        var desc = CsvView.Create(t, new CsvViewSpec { SortColumn = 0, SortDescending = true });
        Check.SeqEqual(new[] { "item10", "item02", "Item2", "item1", "" }, Col(desc, 0), "descending: blanks still last");
        Check.Equal(CsvSortMode.Text, asc.ResolvedSortMode);
        Check.Equal("name", t.GetCell(0, 0), "the text is untouched");
    }

    [Case]
    public static void NumericSortWithPolishDecimals()
    {
        var t = T("v\n1 234,5\n12,5\n-3\n1.000,25\n\nabc\n7\n", delimiter: ';');
        var v = CsvView.Create(t, new CsvViewSpec { SortColumn = 0 });
        Check.Equal(CsvSortMode.Number, v.ResolvedSortMode, "auto picks numbers");
        Check.SeqEqual(new[] { "-3", "7", "12,5", "1.000,25", "1 234,5", "abc", "" }, Col(v, 0), "ascending: numbers, then text, then blanks");
        var d = CsvView.Create(t, new CsvViewSpec { SortColumn = 0, SortDescending = true });
        Check.SeqEqual(new[] { "1 234,5", "1.000,25", "12,5", "7", "-3", "abc", "" }, Col(d, 0), "descending");
        // forced text mode compares naturally instead
        var s = CsvView.Create(t, new CsvViewSpec { SortColumn = 0, SortMode = CsvSortMode.Text });
        Check.Equal(CsvSortMode.Text, s.ResolvedSortMode);
    }

    [Case]
    public static void DateSort()
    {
        var t = T("d\n12.10.2026\n01.02.2025\n2026-01-05\n31/12/2025\n");
        var v = CsvView.Create(t, new CsvViewSpec { SortColumn = 0 });
        Check.Equal(CsvSortMode.Date, v.ResolvedSortMode);
        Check.SeqEqual(new[] { "01.02.2025", "31/12/2025", "2026-01-05", "12.10.2026" }, Col(v, 0));
        var us = T("d\n12/31/2025\n01/02/2025\n");
        Check.SeqEqual(new[] { "01/02/2025", "12/31/2025" }, Col(CsvView.Create(us, new CsvViewSpec { SortColumn = 0 }), 0), "month-first proven by the data");
    }

    [Case]
    public static void SortIsStable()
    {
        var t = T("k,id\n1,a\n1,b\n1,c\n0,d\n1,e\n");
        var asc = CsvView.Create(t, new CsvViewSpec { SortColumn = 0 });
        Check.SeqEqual(new[] { "d", "a", "b", "c", "e" }, Col(asc, 1), "ascending keeps file order for equal keys");
        var desc = CsvView.Create(t, new CsvViewSpec { SortColumn = 0, SortDescending = true });
        Check.SeqEqual(new[] { "a", "b", "c", "e", "d" }, Col(desc, 1), "descending too");
    }

    [Case]
    public static void PolishAlphabetOrder()
    {
        var t = T("n\nZofia\nŁukasz\nAdam\nLucjan\nĆma\nCezary\n");
        var v = CsvView.Create(t, new CsvViewSpec { SortColumn = 0, Culture = new CultureInfo("pl-PL") });
        Check.SeqEqual(new[] { "Adam", "Cezary", "Ćma", "Lucjan", "Łukasz", "Zofia" }, Col(v, 0));
    }

    [Case]
    public static void FilterContains()
    {
        var t = T("name,city\nAnna,Warszawa\nJan,Kraków\nEwa,warszawa\nOla,\"Gdańsk, PL\"\nZed,\"say \"\"hi\"\"\"\n");
        CsvView V(params CsvColumnFilter[] f) => CsvView.Create(t, new CsvViewSpec { Filters = f });
        Check.SeqEqual(new[] { "Anna", "Ewa" }, Col(V(F(-1, "WARSZ")), 0), "any column, case-insensitive");
        Check.SeqEqual(new[] { "Anna", "Jan" }, Col(V(F(0, "an")), 0), "one column");
        Check.SeqEqual(Array.Empty<string>(), Col(V(F(1, "anna")), 0), "wrong column");
        Check.SeqEqual(new[] { "Ola" }, Col(V(F(-1, "Gdańsk, PL")), 0), "needle holding the delimiter");
        Check.SeqEqual(new[] { "Zed" }, Col(V(F(-1, "say \"hi\"")), 0), "needle holding quotes matches the decoded cell");
        Check.SeqEqual(new[] { "Anna" }, Col(V(F(-1, "warsz"), F(0, "ann")), 0), "filters are ANDed");
        Check.True(CsvView.Create(t, new CsvViewSpec { Filters = [new(0, "")] }).IsIdentity, "an empty filter text is ignored");
        Check.SeqEqual(new[] { "Ewa" }, Col(V(F(1, "warszawa"), F(0, "e")), 0), "column filters on both columns");
        Check.Equal(0, CsvView.Create(t, new CsvViewSpec { Filters = [new(9, "x")] }).RowCount);
    }

    [Case]
    public static void FilterAndSortTogether()
    {
        var t = T("city,n\nW,3\nK,10\nW,25\nW,2\n");
        var v = CsvView.Create(t, new CsvViewSpec { SortColumn = 1, SortDescending = true, Filters = [new(0, "w")] });
        Check.SeqEqual(new[] { "25", "3", "2" }, Col(v, 1));
        Check.Equal(v.SourceRow(0), 3, "source row of the first shown row");
        Check.Equal(0, v.DisplayIndexOf(3), "display index");
        Check.Equal(-1, v.DisplayIndexOf(2), "filtered out");
        Check.Equal(-1, v.DisplayIndexOf(0), "header");
    }

    [Case]
    public static void ViewsOnlyChangeTheViewAndFollowEdits()
    {
        var t = T("n,v\na,3\nb,1\nc,2\n");
        var v = CsvView.Create(t, new CsvViewSpec { SortColumn = 1 });
        Check.SeqEqual(new[] { "b", "c", "a" }, Col(v, 0), "sorted");
        Check.Equal("n,v\na,3\nb,1\nc,2\n", t.Text, "text unchanged by sorting");

        // edit the cell shown first in the view: it maps to the right place of the text
        var edit = t.SetCell(v.SourceRow(0), 1, "9");
        var t2 = t.WithEdit(edit);
        Check.Equal("n,v\na,3\nb,9\nc,2\n", t2.Text, "edit hit the right record");
        var v2 = v.Rebind(t2);
        Check.SeqEqual(new[] { "c", "a", "b" }, Col(v2, 0), "rebound view re-sorts");

        // delete the row shown last
        var del = t2.DeleteRow(v2.SourceRow(v2.RowCount - 1));
        var t3 = t2.WithEdit(del);
        Check.Equal("n,v\na,3\nc,2\n", t3.Text);
        Check.SeqEqual(new[] { "c", "a" }, Col(v2.Rebind(t3), 0));

        // changing the sort on the same table reuses cached keys
        var back = v.With(new CsvViewSpec());
        Check.True(back.IsIdentity, "back to file order");
    }

    [Case]
    public static void CsvDelimitedFilesReadQuotedThousandsCommas()
    {
        var t = T("v\n\"1,234\"\n\"12\"\n\"2,500\"\n");
        var v = CsvView.Create(t, new CsvViewSpec { SortColumn = 0 });
        Check.SeqEqual(new[] { "12", "1,234", "2,500" }, Col(v, 0), "in a comma file a lone comma groups thousands");
        var pl = T("v\n1,234\n12\n2,5\n", delimiter: ';');
        Check.SeqEqual(new[] { "1,234", "2,5", "12" }, Col(CsvView.Create(pl, new CsvViewSpec { SortColumn = 0 }), 0), "in a semicolon file it is the decimal mark");
    }
}
