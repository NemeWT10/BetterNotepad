using System.Diagnostics;
using System.Globalization;
using System.Text;
using BetterNotepad.Tabular;

namespace BetterNotepad.Tabular.Sample;

/// <summary>Generates the sample files under tests\output\tabular (our own files, nothing from outside the project).</summary>
internal static class Fixtures
{
    public static string Dir { get; set; } = "";

    public static string Path(string name) => System.IO.Path.Combine(Dir, name);

    /// <summary>Reads the file like the app would (decode the bytes, keep a BOM as U+FEFF in the text).</summary>
    public static string ReadText(string name) => Encoding.UTF8.GetString(File.ReadAllBytes(Path(name)));

    private static void Write(string name, string text, bool bom = false)
        => File.WriteAllBytes(Path(name), (bom ? Encoding.UTF8.GetPreamble() : []).Concat(Encoding.UTF8.GetBytes(text)).ToArray());

    public static void WriteAll()
    {
        Directory.CreateDirectory(Dir);

        // what Excel with a Polish locale saves: ';' delimiter, decimal commas, CRLF, UTF-8 with BOM
        var pl = new StringBuilder("Imię;Nazwisko;Miasto;Kwota;Data;Uwagi\r\n");
        string[] first = ["Anna", "Jan", "Zofia", "Łukasz", "Michał", "Ćwik", "Ewa", "Żaneta"];
        string[] last = ["Kowalska", "Nowak", "Wiśniewska", "Zieliński", "Wójcik", "Kamiński"];
        string[] city = ["Warszawa", "Kraków", "Łódź", "Gdańsk", "Wrocław", "Poznań", "Szczecin"];
        for (var i = 0; i < 40; i++)
        {
            var amount = ((i * 7919) % 100000 / 100.0).ToString("#,##0.00", CultureInfo.GetCultureInfo("pl-PL")).Replace(' ', ' ');
            pl.Append(first[i % first.Length]).Append(';').Append(last[i % last.Length]).Append(';').Append(city[i % city.Length]).Append(';')
              .Append(amount).Append(';').Append(new DateTime(2026, 1, 1).AddDays(i * 9).ToString("dd.MM.yyyy")).Append(';')
              .Append(i % 6 == 0 ? "\"zamówienie; pilne\"" : i % 11 == 0 ? "\"powiedział \"\"tak\"\"\"" : "").Append("\r\n");
        }

        Write("pl-excel.csv", pl.ToString(), bom: true);

        Write("data.tsv", "id\tname\tnote\n1\tAnna\tprosta notatka, z przecinkami\n2\tJan\t\"wiersz 1\nwiersz 2\"\n3\tEwa\t\n4\tŁukasz\tostatni");
        Write("multiline.csv", "id,opis,cena\n1,\"Linia pierwsza\nLinia druga\",10.50\n2,\"Cytat: \"\"zażółć gęślą jaźń\"\"\",7\n3,zwykły,3.25\n");
        Write("bom-comma.csv", "Name,Age,City\nAnna,30,Warszawa\nJan,41,Kraków\n", bom: true);
        Write("empty-last-line.csv", "a,b,c\r\n1,2,3\r\n4,5,6\r\n\r\n");
        Write("no-trailing-newline.csv", "a;b\r\n1;2\r\n3;4");
        Write("pipe.psv", "k|v|w\nx|1|2\ny|3|4\n");

        var sw = Stopwatch.StartNew();
        Write("big-200k.csv", Generate(200_000));
        Console.WriteLine($"  fixtures written to {Dir} (big-200k.csv generated in {sw.ElapsedMilliseconds} ms)");
    }

    /// <summary>A Polish-Excel style file: ';', CRLF, decimal commas, dd.MM.yyyy dates and some quoted notes.</summary>
    public static string Generate(int rows, int seed = 7)
    {
        var rnd = new Random(seed);
        string[] city = ["Warszawa", "Kraków", "Łódź", "Gdańsk", "Wrocław", "Poznań", "Szczecin", "Lublin", "Katowice", "Białystok"];
        var sb = new StringBuilder(rows * 80);
        sb.Append("Id;Nazwa;Miasto;Kwota;Data;Ilość;Uwagi;Aktywny\r\n");
        for (var i = 0; i < rows; i++)
        {
            sb.Append(i + 1).Append(";Produkt ").Append(rnd.Next(1, 5000)).Append(';').Append(city[rnd.Next(city.Length)]).Append(';')
              .Append(rnd.Next(0, 99999)).Append(',').Append(rnd.Next(0, 99).ToString("00")).Append(';')
              .Append(new DateTime(2020, 1, 1).AddDays(rnd.Next(0, 2500)).ToString("dd.MM.yyyy")).Append(';')
              .Append(rnd.Next(1, 500)).Append(';');
            if (i % 7 == 0) sb.Append("\"uwaga; numer ").Append(i).Append("\"");
            sb.Append(';').Append(rnd.Next(2) == 0 ? "TAK" : "NIE").Append("\r\n");
        }

        return sb.ToString();
    }
}

internal static class FixtureTests
{
    [Case]
    public static void PolishExcelFile()
    {
        var bytes = File.ReadAllBytes(Fixtures.Path("pl-excel.csv"));
        Check.True(bytes is [0xEF, 0xBB, 0xBF, ..], "file really starts with a UTF-8 BOM");
        var text = Fixtures.ReadText("pl-excel.csv");
        Check.Equal('﻿', text[0], "BOM char is kept in the text");
        var t = CsvTable.Parse(text, fileName: "pl-excel.csv");
        Check.Equal(';', t.Dialect.Delimiter, "delimiter");
        Check.True(t.Dialect.HasHeader, "header");
        Check.Equal("\r\n", t.Dialect.LineEnding, "eol");
        Check.Equal(41, t.RowCount, "rows");
        Check.Equal(6, t.ColumnCount, "columns");
        Check.Equal("Imię", t.GetCell(0, 0), "header cell is free of the BOM");
        Check.Equal("zamówienie; pilne", t.GetCell(1, 5), "quoted note with the delimiter");
        Check.True(t.GetCell(2, 3).Contains(','), "decimal comma kept as text");
        var amounts = CsvView.Create(t, new CsvViewSpec { SortColumn = 3 });
        Check.Equal(CsvSortMode.Number, amounts.ResolvedSortMode, "amounts sort as numbers");
        var sorted = Enumerable.Range(0, amounts.RowCount).Select(i => amounts.GetCell(i, 3)).ToArray();
        var values = sorted.Select(s => { CsvValue.TryParseNumber(s, CsvNumberStyle.Auto, out var v); return v; }).ToArray();
        Check.True(values.SequenceEqual(values.OrderBy(v => v)), "ascending numeric order");
        var dates = CsvView.Create(t, new CsvViewSpec { SortColumn = 4, SortDescending = true });
        Check.Equal(CsvSortMode.Date, dates.ResolvedSortMode, "dates sort as dates");
        // editing a note keeps the delimiter, CRLF and BOM
        var e = t.SetCell(3, 5, "nowa; notatka");
        var edited = e.ApplyTo(text);
        Check.True(edited.StartsWith('﻿'), "BOM kept");
        Check.True(edited.Contains(";\"nowa; notatka\"\r\n"), "quoted because of the delimiter");
        Check.Equal(text.Length + e.Delta, edited.Length);
    }

    [Case]
    public static void TsvWithQuotedMultilineCell()
    {
        var text = Fixtures.ReadText("data.tsv");
        var t = CsvTable.Parse(text, fileName: "data.tsv");
        Check.Equal('\t', t.Dialect.Delimiter);
        Check.True(t.Dialect.HasHeader, "header");
        Check.Equal(5, t.RowCount, "rows");
        Check.Equal("wiersz 1\nwiersz 2", t.GetCell(2, 2), "multi-line cell");
        Check.Equal("prosta notatka, z przecinkami", t.GetCell(1, 2), "comma is plain text");
        Check.Equal("", t.GetCell(3, 2), "empty last cell");
        Check.True(!t.EndsWithLineBreak, "no trailing newline");
        var added = t.AddRow(t.RowCount, ["5", "Olek", "tab\tw środku"]).ApplyTo(text);
        Check.True(added.EndsWith("\n5\tOlek\t\"tab\tw środku\""), "appended without adding a trailing newline");
    }

    [Case]
    public static void MultilineCsv()
    {
        var text = Fixtures.ReadText("multiline.csv");
        var t = CsvTable.Parse(text);
        Check.Equal(4, t.RowCount, "rows");
        Check.Equal("Linia pierwsza\nLinia druga", t.GetCell(1, 1));
        Check.Equal("Cytat: \"zażółć gęślą jaźń\"", t.GetCell(2, 1));
        Check.Equal("10.50", t.GetCell(1, 2));
        var rebuilt = string.Concat(Enumerable.Range(0, t.RowCount).Select(r => text[t.RowStart(r)..t.RowNext(r)]));
        Check.Equal(text, rebuilt, "records tile the text exactly");
    }

    [Case]
    public static void BomCommaAndOtherSmallFiles()
    {
        var bom = Fixtures.ReadText("bom-comma.csv");
        var t = CsvTable.Parse(bom);
        Check.Equal(1, t.ContentStart);
        Check.Equal("Name", t.GetCell(0, 0));
        Check.Equal(3, t.RowCount);

        var blank = CsvTable.Parse(Fixtures.ReadText("empty-last-line.csv"));
        Check.Equal(4, blank.RowCount, "the empty last line is a (blank) record");
        Check.Equal(1, blank.GetFieldCount(3), "one empty cell");
        Check.Equal("\r\n", blank.Dialect.LineEnding);
        // the table can drop it again without touching the rest
        Check.Equal("a,b,c\r\n1,2,3\r\n4,5,6\r\n", blank.DeleteRow(3).ApplyTo(blank.Text));

        var open = CsvTable.Parse(Fixtures.ReadText("no-trailing-newline.csv"));
        Check.Equal(';', open.Dialect.Delimiter);
        Check.Equal(3, open.RowCount);
        Check.Equal("a;b\r\n1;2\r\n3;4\r\n9;8", open.AddRow(3, ["9", "8"]).ApplyTo(open.Text), "append to a file without trailing newline");

        Check.Equal('|', CsvTable.Parse(Fixtures.ReadText("pipe.psv"), fileName: "pipe.psv").Dialect.Delimiter);
    }

    [Case]
    public static void BigFilePerformance()
    {
        var text = Fixtures.ReadText("big-200k.csv");
        var sw = Stopwatch.StartNew();
        var table = CsvTable.Parse(text, fileName: "big-200k.csv");
        var parse = sw.ElapsedMilliseconds;
        Check.Equal(200_001, table.RowCount, "rows");
        Check.Equal(';', table.Dialect.Delimiter);
        Check.True(table.Dialect.HasHeader);
        sw.Restart();
        Check.Equal(8, table.ColumnCount);
        var columns = sw.ElapsedMilliseconds;
        sw.Restart();
        var byAmount = CsvView.Create(table, new CsvViewSpec { SortColumn = 3 });
        var sortNumber = sw.ElapsedMilliseconds;
        Check.Equal(CsvSortMode.Number, byAmount.ResolvedSortMode);
        sw.Restart();
        var byName = CsvView.Create(table, new CsvViewSpec { SortColumn = 2, SortDescending = true });
        var sortText = sw.ElapsedMilliseconds;
        sw.Restart();
        var filtered = CsvView.Create(table, new CsvViewSpec { Filters = [new(-1, "kraków")] });
        var filter = sw.ElapsedMilliseconds;
        Check.True(filtered.RowCount > 10000 && filtered.RowCount < 40000, "filter found " + filtered.RowCount);
        sw.Restart();
        var edit = table.SetCell(150_000, 6, "zmienione; w teście");
        var next = table.WithEdit(edit);
        var incremental = sw.ElapsedMilliseconds;
        sw.Restart();
        var full = CsvTable.Parse(next.Text, next.Dialect);
        var fullParse = sw.ElapsedMilliseconds;
        Check.Equal(full.RowCount, next.RowCount);
        Console.WriteLine($"        200k rows ({text.Length / 1048576.0:0.0} MB): parse {parse} ms, columns {columns} ms, sort number {sortNumber} ms, sort text {sortText} ms, filter {filter} ms, cell edit incl. text copy {incremental} ms (full re-parse {fullParse} ms)");
        Check.True(parse < 800, "parse took " + parse + " ms");
        Check.True(sortNumber < 3000 && sortText < 6000, "sorts took " + sortNumber + " / " + sortText + " ms");
        Check.True(filter < 1500, "filter took " + filter + " ms");

        // a screenful of rows is cheap
        sw.Restart();
        for (var i = 0; i < 60; i++) _ = byName.GetRow(100_000 + i);
        Check.True(sw.ElapsedMilliseconds < 50, "60 visible rows took " + sw.ElapsedMilliseconds + " ms");
    }
}
