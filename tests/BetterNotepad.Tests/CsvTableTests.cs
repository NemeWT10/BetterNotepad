using System.Globalization;
using BetterNotepad.Tabular;
using BetterNotepad.Tabular.Wpf;

namespace BetterNotepad.Tests;

/// <summary>Dialect and header detection of the CSV / TSV table.</summary>
public sealed class CsvDialectTests
{
    [Theory]
    [InlineData("a,b,c\r\n1,2,3\r\n4,5,6\r\n", ',')]
    [InlineData("a;b;c\r\n1,5;2,5;3\r\n4;5;6\r\n", ';')] // Polish decimals: the commas inside numbers do not win
    [InlineData("a\tb\tc\n1\t2\t3\n4\t5\t6\n", '\t')]
    [InlineData("a|b|c\n1|2|3\n4|5|6\n", '|')]
    [InlineData("name,email\r\n\"Kowalski, Jan\",jan@example.com\r\n\"Nowak, Anna\",anna@example.com\r\n", ',')]
    public void DetectsTheSeparator(string text, char expected)
    {
        var d = CsvDialect.Detect(text);
        Assert.Equal(expected, d.Delimiter);
        Assert.True(d.LooksTabular);
    }

    [Fact]
    public void FileExtensionDecidesWhenTheTextHasNoStructure()
    {
        Assert.Equal('\t', CsvDialect.Detect("hello\nworld\n", "data.tsv").Delimiter);
        Assert.Equal('|', CsvDialect.Detect("hello\nworld\n", "data.psv").Delimiter);
        Assert.Equal(',', CsvDialect.Detect("hello\nworld\n", "data.csv").Delimiter);
        Assert.False(CsvDialect.Detect("hello\nworld\n", "data.csv").LooksTabular);
    }

    [Fact]
    public void HeaderOfNamesOverNumbersIsDetected()
    {
        Assert.True(CsvDialect.Detect("name,score\r\nAnna,87\r\nJan,92.5\r\nOla,78\r\n").HasHeader);
        Assert.True(CsvDialect.Detect("Id;Kwota;Data\r\n1;12,50;2026-01-05\r\n2;8,00;2026-01-06\r\n").HasHeader);
    }

    [Fact]
    public void NumbersInTheFirstRowMeanNoHeader()
    {
        Assert.False(CsvDialect.Detect("1,2026-01-05,12.50\r\n2,2026-01-06,8.00\r\n3,2026-01-09,23.75\r\n").HasHeader);
    }

    [Fact]
    public void SingleQuotesAreDetectedWhenTheyWrapFields()
    {
        var d = CsvDialect.Detect("id,name\r\n1,'Kowalski, Jan'\r\n2,'Nowak, Anna'\r\n3,'Mazur, Zofia'\r\n");
        Assert.Equal('\'', d.Quote);
        Assert.Equal(',', d.Delimiter);
    }

    [Theory]
    [InlineData("a,b\n1,2\n", "\n")]
    [InlineData("a,b\r\n1,2\r\n", "\r\n")]
    [InlineData("a,b\r1,2\r", "\r")]
    public void DetectsTheLineEnding(string text, string expected) => Assert.Equal(expected, CsvDialect.Detect(text).LineEnding);

    [Fact]
    public void ByteOrderMarkIsNotPartOfTheFirstCell()
    {
        var t = CsvTable.Parse("﻿name,city\r\nAnna,Gdańsk\r\n");
        Assert.Equal(1, t.ContentStart);
        Assert.Equal("name", t.GetHeaders()[0]);
    }
}

/// <summary>Cell edits: the separator and the quoting style stay, the text is the source of truth.</summary>
public sealed class CsvEditTests
{
    private static string Apply(CsvTable t, TextEdit e) => e.ApplyTo(t.Text);

    [Fact]
    public void QuotedCellStaysQuoted()
    {
        var t = CsvTable.Parse("name,city\r\n\"Kowalski, Jan\",Kraków\r\n");
        Assert.Equal("name,city\r\n\"Nowak\",Kraków\r\n", Apply(t, t.SetCell(1, 0, "Nowak")));
    }

    [Theory]
    [InlineData("Kraków, centrum", "\"Kraków, centrum\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\r\nlines", "\"two\r\nlines\"")]
    [InlineData("Gdańsk", "Gdańsk")]
    public void BareCellIsQuotedOnlyWhenTheValueNeedsIt(string value, string written)
    {
        var t = CsvTable.Parse("name,city\r\nAnna,Kraków\r\n");
        var text = Apply(t, t.SetCell(1, 1, value));
        Assert.Equal("name,city\r\nAnna," + written + "\r\n", text);
        Assert.Equal(value, CsvTable.Parse(text, t.Dialect).GetCell(1, 1)); // and it reads back the same
    }

    [Fact]
    public void SemicolonFileKeepsItsSeparatorAndLeavesCommasBare()
    {
        var t = CsvTable.Parse("a;b\r\n1;2\r\n");
        Assert.Equal(';', t.Dialect.Delimiter);
        Assert.Equal("a;b\r\n1;3,5\r\n", Apply(t, t.SetCell(1, 1, "3,5")));
        Assert.Equal("a;b\r\n1;\"3;4\"\r\n", Apply(t, t.SetCell(1, 1, "3;4")));
    }

    [Fact]
    public void UnchangedValueIsANoOp()
    {
        var t = CsvTable.Parse("name,city\r\n\"Kowalski, Jan\",Kraków\r\n");
        Assert.True(t.SetCell(1, 1, "Kraków").IsNoOp);
        Assert.True(t.SetCell(1, 0, "Kowalski, Jan").IsNoOp);
    }

    [Fact]
    public void ShortRowIsPaddedWithSeparators()
    {
        var t = CsvTable.Parse("a,b,c\r\n1\r\n");
        Assert.Equal("a,b,c\r\n1,,x\r\n", Apply(t, t.SetCell(1, 2, "x")));
        Assert.True(t.SetCell(1, 2, "").IsNoOp);
    }

    [Fact]
    public void EmptyingTheLastUnterminatedRecordKeepsTheRecord()
    {
        var t = CsvTable.Parse("a\r\nb", CsvDialect.Default with { HasHeader = true });
        var text = Apply(t, t.SetCell(1, 0, ""));
        Assert.Equal("a\r\n\"\"", text);
        Assert.Equal(2, CsvTable.Parse(text, t.Dialect).RowCount);
    }

    [Fact]
    public void IncrementalTableAfterAnEditEqualsAFullParse()
    {
        var rnd = new Random(5);
        string[] values = ["", "x", "a,b", "q\"q", "line\r\nbreak", "  spaced ", "ąę", "12,5", "\"", "\r\n"];
        var dialect = CsvDialect.Default;
        var table = CsvTable.Parse("h1,h2,h3\r\n1,2,3\r\n\"a,b\",c,d\r\n4,5\r\n\r\n7,8,9\r\n", dialect);
        for (var i = 0; i < 400; i++)
        {
            var row = rnd.Next(table.RowCount);
            var col = rnd.Next(4);
            var edit = table.SetCell(row, col, values[rnd.Next(values.Length)]);
            var next = table.WithEdit(edit);
            var full = CsvTable.Parse(next.Text, dialect);
            Assert.Equal(full.RowCount, next.RowCount);
            for (var r = 0; r < full.RowCount; r++)
            {
                Assert.Equal(full.RowStart(r), next.RowStart(r));
                Assert.Equal(full.RowEnd(r), next.RowEnd(r));
            }
            Assert.Equal(full.ColumnCount, next.ColumnCount);
            table = next;
        }
    }

    [Fact]
    public void SeveralEditsApplyInAnyOrder()
    {
        var t = CsvTable.Parse("a,b\r\n1,2\r\n3,4\r\n");
        TextEdit[] edits = [t.SetCell(1, 0, "one"), t.SetCell(2, 1, "four, really"), t.SetCell(1, 1, "")];
        var expected = "a,b\r\none,\r\n3,\"four, really\"\r\n";
        Assert.Equal(expected, TextEdit.ApplyAll(t.Text, edits));
        Assert.Equal(expected, TextEdit.ApplyAll(t.Text, edits.AsEnumerable().Reverse()));
        Assert.Equal(expected, t.WithEdits(edits).Text);
    }

    [Theory]
    [InlineData("a\nb\r\nc\rd", "\r\n", "a\r\nb\r\nc\r\nd")]
    [InlineData("a\nb\r\nc\rd", "\n", "a\nb\nc\nd")]
    [InlineData("a\r\n\r\nb", "\r", "a\r\rb")]
    public void LineBreaksTypedIntoACellFollowTheFile(string value, string eol, string expected)
        => Assert.Equal(expected, CsvWriter.NormalizeLineBreaks(value, eol));

    [Fact]
    public void ValueWithoutLineBreaksIsReturnedAsIs()
    {
        const string v = "no breaks here";
        Assert.Same(v, CsvWriter.NormalizeLineBreaks(v, "\r\n"));
    }

    [Fact]
    public void OffsetsMapToTheirCells()
    {
        var t = CsvTable.Parse("a,b\r\n\"x\r\ny\",z\r\n");
        Assert.True(t.TryFindCell(t.Text.IndexOf('y'), out var row, out var col));
        Assert.Equal((1, 0), (row, col)); // the second line of a quoted cell
        Assert.True(t.TryFindCell(t.Text.IndexOf(",z", StringComparison.Ordinal), out row, out col));
        Assert.Equal((1, 0), (row, col)); // a delimiter belongs to the cell on its left
        Assert.True(t.TryFindCell(t.Text.IndexOf('z'), out row, out col));
        Assert.Equal((1, 1), (row, col));
    }
}

/// <summary>Sorting and filtering change only the view; the order understands numbers, Polish decimals and dates.</summary>
public sealed class CsvViewTests
{
    private static string[] Column(CsvView v, int col) => Enumerable.Range(0, v.RowCount).Select(i => v.GetCell(i, col)).ToArray();

    [Fact]
    public void PolishDecimalsSortAsNumbersWithEmptyCellsLast()
    {
        var t = CsvTable.Parse("k;v\r\na;10,5\r\nb;2,25\r\nc;100\r\nd;\r\ne;-3\r\n");
        var asc = CsvView.Create(t, new CsvViewSpec { SortColumn = 1 });
        Assert.Equal(CsvSortMode.Number, asc.ResolvedSortMode);
        Assert.Equal(["e", "b", "a", "c", "d"], Column(asc, 0));
        var desc = CsvView.Create(t, new CsvViewSpec { SortColumn = 1, SortDescending = true });
        Assert.Equal(["c", "a", "b", "e", "d"], Column(desc, 0)); // empty stays last
    }

    [Fact]
    public void CommaFileReadsCommasInNumbersAsThousands()
    {
        var t = CsvTable.Parse("k,v\r\na,\"1,234\"\r\nb,999\r\nc,10.5\r\nd,1e3\r\n");
        var v = CsvView.Create(t, new CsvViewSpec { SortColumn = 1 });
        Assert.Equal(["c", "b", "d", "a"], Column(v, 0));
    }

    [Fact]
    public void DatesSortChronologicallyAcrossFormats()
    {
        var t = CsvTable.Parse("k;d\r\na;2026-03-01\r\nb;15.01.2026\r\nc;2025-12-31\r\nd;2026-01-15 08:30\r\n");
        var v = CsvView.Create(t, new CsvViewSpec { SortColumn = 1 });
        Assert.Equal(CsvSortMode.Date, v.ResolvedSortMode);
        Assert.Equal(["c", "b", "d", "a"], Column(v, 0));
    }

    [Fact]
    public void TextSortsNaturallyAndInPolishOrder()
    {
        var t = CsvTable.Parse("name\r\nitem10\r\nitem2\r\nItem1\r\n");
        Assert.Equal(["Item1", "item2", "item10"], Column(CsvView.Create(t, new CsvViewSpec { SortColumn = 0 }), 0));
        var cities = CsvTable.Parse("city\r\nŁódź\r\nLublin\r\nŻory\r\nKraków\r\nOpole\r\n");
        var v = CsvView.Create(cities, new CsvViewSpec { SortColumn = 0, Culture = CultureInfo.GetCultureInfo("pl-PL") });
        Assert.Equal(["Kraków", "Lublin", "Łódź", "Opole", "Żory"], Column(v, 0));
    }

    [Fact]
    public void EqualValuesKeepTheFileOrder()
    {
        var t = CsvTable.Parse("k,v\r\na,1\r\nb,2\r\nc,1\r\nd,2\r\ne,1\r\n");
        Assert.Equal(["a", "c", "e", "b", "d"], Column(CsvView.Create(t, new CsvViewSpec { SortColumn = 1 }), 0));
        Assert.Equal(["b", "d", "a", "c", "e"], Column(CsvView.Create(t, new CsvViewSpec { SortColumn = 1, SortDescending = true }), 0));
    }

    [Fact]
    public void FilterIsCaseInsensitiveAndCanBeLimitedToOneColumn()
    {
        var t = CsvTable.Parse("name,city\r\nAnna,Kraków\r\nKrak,Gdańsk\r\nJan,KRAKÓW\r\n");
        var any = CsvView.Create(t, new CsvViewSpec { Filters = [new CsvColumnFilter(-1, "krak")] });
        Assert.Equal(["Anna", "Krak", "Jan"], Column(any, 0));
        var city = CsvView.Create(t, new CsvViewSpec { Filters = [new CsvColumnFilter(1, "kraków")] });
        Assert.Equal(["Anna", "Jan"], Column(city, 0));
        Assert.Equal(-1, city.DisplayIndexOf(2)); // filtered out
        Assert.Equal(-1, city.DisplayIndexOf(0)); // the header is never a row of the view
    }

    [Fact]
    public void SortAndFilterLeaveTheTextAlone()
    {
        const string text = "k;v\r\na;3\r\nb;1\r\nc;2\r\n";
        var t = CsvTable.Parse(text);
        var v = CsvView.Create(t, new CsvViewSpec { SortColumn = 1, Filters = [new CsvColumnFilter(-1, "b")] });
        Assert.Same(text, v.Table.Text);
        Assert.Equal(2, v.SourceRow(0));
    }

    [Fact]
    public void RetainKeepsTheOrderAfterACellEdit()
    {
        var t = CsvTable.Parse("k,v\r\na,3\r\nb,1\r\nc,2\r\n");
        var v = CsvView.Create(t, new CsvViewSpec { SortColumn = 1 });
        Assert.Equal(["b", "c", "a"], Column(v, 0));
        var edited = t.WithEdit(t.SetCell(2, 1, "99")); // b would now sort last
        var kept = v.Retain(edited);
        Assert.Same(edited, kept.Table);
        Assert.Equal(["b", "c", "a"], Column(kept, 0));
        Assert.Equal("99", kept.GetCell(0, 1));
        Assert.Equal(["c", "a", "b"], Column(kept.Rebind(edited), 0)); // sorting again moves it
    }

    [Fact]
    public void RetainSortsAgainWhenRowsWereAddedOrRemoved()
    {
        var t = CsvTable.Parse("k,v\r\na,3\r\nb,1\r\n");
        var v = CsvView.Create(t, new CsvViewSpec { SortColumn = 1 });
        var grown = t.WithEdit(t.AddRow(t.RowCount, ["c", "2"]));
        Assert.Equal(["b", "c", "a"], Column(v.Retain(grown), 0));
    }
}

/// <summary>Ragged rows: which records differ from the expected cell count.</summary>
public sealed class CsvShapeTests
{
    [Fact]
    public void RowsWithAnotherCellCountThanTheHeaderAreListed()
    {
        var t = CsvTable.Parse("kod,nazwa,ilość,cena\r\nA1,x,1,2\r\nA2,y,3\r\nA3,z,4,5\r\nA4,w,6,7,8\r\n");
        var s = t.AnalyzeShape();
        Assert.Equal(4, s.ExpectedColumns);
        Assert.Equal(2, s.RaggedCount);
        Assert.Equal([2, 4], s.RaggedRows);
        Assert.Equal(5, t.ColumnCount);
    }

    [Fact]
    public void BlankLinesAreNotRagged()
    {
        var s = CsvTable.Parse("a,b\r\n1,2\r\n\r\n3,4\r\n\r\n").AnalyzeShape();
        Assert.False(s.IsRagged);
        Assert.Equal(2, s.ExpectedColumns);
    }

    [Fact]
    public void WithoutAHeaderTheMostCommonCountIsExpected()
    {
        var t = CsvTable.Parse("1,2,3\n4,5\n6,7,8\n9,10,11\n", CsvDialect.Default with { HasHeader = false });
        var s = t.AnalyzeShape();
        Assert.Equal(3, s.ExpectedColumns);
        Assert.Equal([1], s.RaggedRows);
    }

    [Fact]
    public void TheHeaderSetsTheExpectationEvenWhenMostRowsDiffer()
    {
        var s = CsvTable.Parse("a,b\n1,2,3\n4,5,6\n").AnalyzeShape();
        Assert.Equal(2, s.ExpectedColumns);
        Assert.Equal(2, s.RaggedCount);
    }

    [Fact]
    public void EmptyTextHasNoShape()
    {
        Assert.Same(CsvShape.Empty, CsvTable.Parse("").AnalyzeShape());
    }

    [Fact]
    public void UnclosedQuoteSwallowsTheRestIntoTheLastRecord()
    {
        var t = CsvTable.Parse("id,opis,wartość\r\n1,a,10\r\n3,\"bez końca,30\r\n4,b,40\r\n");
        Assert.True(t.HasUnterminatedQuote);
        Assert.Equal(3, t.RowCount);
        Assert.Contains("4,b,40", t.GetCell(2, 1), StringComparison.Ordinal);
    }
}

/// <summary>The user's separator / quote / header choice over the detected dialect.</summary>
public sealed class CsvDialectOverrideTests
{
    private static readonly CsvDialect Detected = new() { Delimiter = '\t', Quote = '"', HasHeader = true, LineEnding = "\n", Confidence = 0.9 };

    [Fact]
    public void NoChoiceKeepsTheDetection()
    {
        Assert.True(CsvDialectOverride.None.IsEmpty);
        Assert.Same(Detected, CsvDialectOverride.None.ApplyTo(Detected));
    }

    [Fact]
    public void EachChoiceReplacesOnlyItsOwnPart()
    {
        var d = new CsvDialectOverride(Delimiter: ',').ApplyTo(Detected);
        Assert.Equal((',', '"', true, "\n"), (d.Delimiter, d.Quote, d.HasHeader, d.LineEnding));
        var h = new CsvDialectOverride(HasHeader: false).ApplyTo(Detected);
        Assert.Equal(('\t', false), (h.Delimiter, h.HasHeader));
        var q = new CsvDialectOverride(Quote: '\'').ApplyTo(Detected);
        Assert.Equal('\'', q.Quote);
    }

    [Fact]
    public void ChoicesCombineAndCompareByValue()
    {
        var o = CsvDialectOverride.None with { Delimiter = ';' };
        o = o with { HasHeader = false };
        Assert.Equal(new CsvDialectOverride(';', null, false), o);
        Assert.False(o.IsEmpty);
        var d = o.ApplyTo(Detected);
        Assert.Equal((';', false), (d.Delimiter, d.HasHeader));
    }

    [Fact]
    public void ASeparatorEqualToTheQuoteIsIgnored()
    {
        Assert.Same(Detected, new CsvDialectOverride(Delimiter: '"').ApplyTo(Detected));
    }
}
