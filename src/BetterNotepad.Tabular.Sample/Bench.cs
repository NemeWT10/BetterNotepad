using System.Diagnostics;
using BetterNotepad.Tabular;

namespace BetterNotepad.Tabular.Sample;

/// <summary>Timings for big files (printed, no assertions). Memory is the managed heap after a forced collection.</summary>
internal static class Bench
{
    public static void Run()
    {
        foreach (var rows in new[] { 200_000, 1_000_000 })
        {
            Console.WriteLine($"--- {rows:N0} rows");
            var sw = Stopwatch.StartNew();
            var text = Fixtures.Generate(rows);
            Console.WriteLine($"  generate           {sw.ElapsedMilliseconds,6} ms   ({text.Length / 1048576.0:0.0} MB, {text.Length * 2 / 1048576.0:0.0} MB as UTF-16)");

            GC.Collect();
            var before = GC.GetTotalMemory(true);
            sw.Restart();
            var dialect = CsvDialect.Detect(text, "big.csv");
            Console.WriteLine($"  detect dialect     {sw.ElapsedMilliseconds,6} ms   delimiter '{dialect.Delimiter}', header {dialect.HasHeader}, confidence {dialect.Confidence:0.00}");
            sw.Restart();
            var table = CsvTable.Parse(text, dialect);
            Console.WriteLine($"  index records      {sw.ElapsedMilliseconds,6} ms   {table.RowCount:N0} records");
            GC.Collect();
            Console.WriteLine($"  table memory       {(GC.GetTotalMemory(true) - before) / 1048576.0,6:0.0} MB   (the text itself excluded: {text.Length * 2 / 1048576.0:0.0} MB)");
            sw.Restart();
            _ = table.ColumnCount;
            Console.WriteLine($"  column count       {sw.ElapsedMilliseconds,6} ms   {table.ColumnCount} columns");

            sw.Restart();
            for (var i = 0; i < 1000; i++) _ = table.GetRow(table.RowCount / 2 + i);
            Console.WriteLine($"  1000 rows decoded  {sw.ElapsedMilliseconds,6} ms");

            Time("sort number (Kwota)", () => CsvView.Create(table, new CsvViewSpec { SortColumn = 3 }));
            Time("sort date (Data)", () => CsvView.Create(table, new CsvViewSpec { SortColumn = 4, SortDescending = true }));
            Time("sort text (Miasto)", () => CsvView.Create(table, new CsvViewSpec { SortColumn = 2 }));
            Time("sort again (cached keys)", () => CsvView.Create(table, new CsvViewSpec { SortColumn = 3, SortDescending = true }));
            Time("filter any column", () => CsvView.Create(table, new CsvViewSpec { Filters = [new(-1, "kraków")] }));
            Time("filter column + sort", () => CsvView.Create(table, new CsvViewSpec { SortColumn = 3, Filters = [new(2, "wroc"), new(7, "tak")] }));

            var mid = table.RowCount / 2;
            sw.Restart();
            var edit = table.SetCell(mid, 6, "zmiana; w komórce");
            var edited = edit.ApplyTo(text);
            var copy = sw.ElapsedMilliseconds;
            sw.Restart();
            var next = table.WithEdit(edit, edited);
            var inc = sw.ElapsedMilliseconds;
            sw.Restart();
            _ = CsvTable.Parse(edited, dialect);
            Console.WriteLine($"  edit one cell      text copy {copy} ms + incremental table {inc} ms  (full re-parse would be {sw.ElapsedMilliseconds} ms)");
        }
    }

    private static void Time(string name, Func<CsvView> make)
    {
        var sw = Stopwatch.StartNew();
        var view = make();
        Console.WriteLine($"  {name,-18} {sw.ElapsedMilliseconds,6} ms   {view.RowCount:N0} rows shown ({view.ResolvedSortMode})");
    }
}
