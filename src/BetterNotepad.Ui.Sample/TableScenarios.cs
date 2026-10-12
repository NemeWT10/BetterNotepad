using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using BetterNotepad.Tabular;
using BetterNotepad.Tabular.Wpf;
using static BetterNotepad.Ui.Sample.Program;

namespace BetterNotepad.Ui.Sample;

internal static class TableScenarios
{
    private const int Rows = 100_000;
    private static readonly string[] Cities = ["Kraków", "Warszawa", "Gdańsk", "Wrocław", "Poznań", "Łódź", "Lublin", "Szczecin"];
    private static readonly string[] Names = ["Anna Nowak", "Jan Kowalski", "Ola Wiśniewska", "Piotr Zieliński", "Kasia Lewandowska", "Marek Wójcik"];

    [ModuleInitializer]
    internal static void Register() => Extra.Add(async only =>
    {
        if (only is "all" or "csv") await Run();
    });

    private static async Task Run()
    {
        var dir = Path.Combine(Out, "fixtures", "csv");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "orders.csv");
        var text = MakeCsv();
        File.WriteAllText(path, text, new UTF8Encoding(false));

        foreach (var theme in new[] { "aurora", "daylight" })
        {
            UseTheme(theme);
            var p = new CsvTablePreview();
            var edits = new List<IReadOnlyList<TextEdit>>();
            var scrolls = new List<(int, double)>();
            var lines = new List<int>();
            p.EditRequested += edits.Add;
            p.Scrolled += (l, f) => scrolls.Add((l, f));
            p.LineRequested += lines.Add;
            var win = Host(p, 1100, 640);
            var t0 = Environment.TickCount64;
            p.Update(text, path);
            await p.Pending;
            await Task.Delay(60);
            var first = Environment.TickCount64 - t0;
            Check(p.Table is { DataRowCount: Rows, ColumnCount: 7 }, $"[{theme}] parsed {p.Table?.DataRowCount} rows × {p.Table?.ColumnCount} cols in {first} ms");
            Check(p.Dialect is { Delimiter: ';', HasHeader: true }, $"[{theme}] dialect ; + header detected");
            Snapshot(p, $"csv_{theme}.png");

            // selection + copy as TSV
            p.SelectForTest(0, 0, 2, 2);
            var tsv = p.CopySelection() ?? "";
            var tsvLines = tsv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Check(tsvLines.Length == 3 && tsvLines[0].Split('\t').Length == 3 && tsvLines[0].StartsWith("1\t", StringComparison.Ordinal), $"[{theme}] copy 3×3 as TSV: {tsvLines.FirstOrDefault()}");
            Snapshot(p, $"csv_selection_{theme}.png");

            // inline editor
            p.BeginEditForTest(3, 2);
            await Task.Delay(40);
            Snapshot(p, $"csv_editing_{theme}.png");
            p.CancelEditForTest();

            // edit → text edits for the buffer (value needs quoting: contains the delimiter)
            p.EditForTest(1, 2, "Nowa; wartość " + theme);
            Check(edits.Count == 1, $"[{theme}] one edit emitted");
            if (edits.Count == 1)
            {
                var newText = TextEdit.ApplyAll(text, edits[0]);
                var again = CsvTable.Parse(newText, p.Dialect);
                Check(again.GetCell(2, 2) == "Nowa; wartość " + theme && newText.Contains("\"Nowa; wartość " + theme + "\"", StringComparison.Ordinal), $"[{theme}] edit applied with quoting");
                Check(p.Table?.GetCell(2, 2) == "Nowa; wartość " + theme, $"[{theme}] table shows the edit at once");
                p.Update(newText, path); // the buffer echo: no reparse needed
                text = newText;
            }

            // sort (view only) by amount, descending
            p.SortForTest(4, true);
            await p.Pending;
            var top = p.View!.GetCell(0, 4);
            var max = Enumerable.Range(1, Rows).Max(r => double.Parse(p.Table!.GetCell(r, 4).Replace(',', '.'), CultureInfo.InvariantCulture));
            Check(double.Parse(top.Replace(',', '.'), CultureInfo.InvariantCulture) == max, $"[{theme}] sorted by amount desc: top {top}");
            Snapshot(p, $"csv_sorted_{theme}.png");
            p.SortForTest(-1, false);
            await p.Pending;

            // filter
            p.FilterForTest("Gdańsk");
            await Task.Delay(260);
            await p.Pending;
            var expect = Enumerable.Range(1, Rows).Count(r => p.Table!.GetRow(r).Any(v => v.Contains("Gdańsk", StringComparison.OrdinalIgnoreCase)));
            Check(p.View!.RowCount == expect, $"[{theme}] filter 'Gdańsk' → {p.View.RowCount} rows (expected {expect})");
            Snapshot(p, $"csv_filtered_{theme}.png");
            p.FilterForTest("");
            await Task.Delay(260);
            await p.Pending;
            Check(p.View!.RowCount == Rows, $"[{theme}] filter cleared");

            // editor → table and back
            var line = LineOfRow(text, 5000);
            p.ScrollToLine(line, 0, 0.5);
            var expectY = (5000 - 1) * CsvGrid.RowHeight;
            Check(Math.Abs(p.ScrollYForTest - expectY) < 1, $"[{theme}] editor→table: line {line} → row 5000 at the top (y {p.ScrollYForTest:0} vs {expectY:0})");
            scrolls.Clear();
            p.ScrollByUser((20000 - 1) * CsvGrid.RowHeight);
            var expLine = LineOfRow(text, 20000);
            Check(scrolls.Count == 1 && scrolls[0].Item1 == expLine, $"[{theme}] table→editor: Scrolled({(scrolls.Count > 0 ? scrolls[0].Item1 : -1)}) = line {expLine}");

            // header toggle re-parses
            p.HasHeader = false;
            await p.Pending;
            await Task.Delay(50);
            Check(p.Table?.DataRowCount == Rows + 1, $"[{theme}] header off: {p.Table?.DataRowCount} data rows");
            p.HasHeader = true;
            await p.Pending;

            p.ReleaseContent();
            Check(p.Table is null, $"[{theme}] release drops the table");
            p.Visibility = System.Windows.Visibility.Collapsed;
            p.Visibility = System.Windows.Visibility.Visible;
            await p.Pending;
            Check(p.Table?.DataRowCount == Rows, $"[{theme}] shown again: parsed anew");
            win.Close();
            p.Dispose();
        }
        UseTheme("aurora");
    }

    /// <summary>1-based source line of data row <paramref name="dataRow"/> (1-based).</summary>
    private static int LineOfRow(string text, int dataRow)
    {
        var t = CsvTable.Parse(text);
        var start = t.RowStart(dataRow);
        return text.AsSpan(0, start).Count('\n') + 1;
    }

    private static string MakeCsv()
    {
        var rnd = new Random(7);
        var sb = new StringBuilder(Rows * 70);
        sb.Append("Id;Klient;Miasto;Data;Kwota;Status;Uwagi\r\n");
        for (var i = 1; i <= Rows; i++)
        {
            var amount = (rnd.Next(100, 2_000_000) / 100.0).ToString("0.00", CultureInfo.GetCultureInfo("pl-PL"));
            var date = new DateTime(2025, 1, 1).AddDays(rnd.Next(0, 640)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var status = (i % 7) switch { 0 => "anulowane", 1 or 2 => "w drodze", _ => "dostarczone" };
            var note = (i % 11) switch
            {
                0 => "\"Pilne; zadzwonić przed dostawą\"",
                3 => "\"Prosi o \"\"fakturę\"\" na firmę\"",
                5 => "",
                _ => "—",
            };
            sb.Append(i).Append(';').Append(Names[rnd.Next(Names.Length)]).Append(';').Append(Cities[rnd.Next(Cities.Length)]).Append(';')
              .Append(date).Append(';').Append(amount).Append(';').Append(status).Append(';').Append(note).Append("\r\n");
        }
        return sb.ToString();
    }
}
