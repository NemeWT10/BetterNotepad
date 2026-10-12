using BetterNotepad.Tabular;

namespace BetterNotepad.Tabular.Sample;

internal static class RandomTests
{
    /// <summary>
    /// Random files (delimiter, line ending, BOM, quoting, blank lines, multi-line cells) go through chains of cell edits,
    /// row inserts / deletes and arbitrary text splices (including ones that open or close quotes across rows). After every
    /// step the incrementally derived table must equal a full parse, and the edit API must read back what it wrote.
    /// </summary>
    [Case]
    public static void RandomEditsMatchAFullParseAndReadBack()
    {
        var rnd = new Random(int.TryParse(Environment.GetEnvironmentVariable("BN_FUZZ_SEED"), out var seed) ? seed : 20261012);
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("BN_FUZZ_ITER"), out var it) ? it : 1500;
        string[] pool = ["", "a", "bb", "x y", "12", "1,5", "q\"uote", "multi\nline", "semi;colon", "  sp  ", "ł", "t\tab", "cr\r\nlf"];
        string[] junk = ["\"", ",", ";", "\n", "\r", "\r\n", "a", "\"\"", ",\"", "\",", ""];
        char[] delims = [',', ';', '\t'];
        string[] eols = ["\n", "\r\n", "\r"];
        var checks = 0;
        for (var iter = 0; iter < iterations; iter++)
        {
            var d = new CsvDialect { Delimiter = delims[rnd.Next(3)], HasHeader = rnd.Next(2) == 0, LineEnding = eols[rnd.Next(3)], QuoteStyle = CsvQuoteStyle.Minimal };
            var rows = rnd.Next(0, 8);
            var cols = rnd.Next(1, 5);
            var sb = new System.Text.StringBuilder();
            if (rnd.Next(8) == 0) sb.Append('﻿');
            for (var r = 0; r < rows; r++)
            {
                if (r > 0) sb.Append(d.LineEnding);
                for (var c = 0; c < cols; c++)
                {
                    if (c > 0) sb.Append(d.Delimiter);
                    sb.Append(CsvWriter.Encode(pool[rnd.Next(pool.Length)], d, forceQuote: rnd.Next(5) == 0));
                }
            }

            if (rows > 0 && rnd.Next(2) == 0) sb.Append(d.LineEnding);
            var table = CsvTable.Parse(sb.ToString(), d);

            for (var step = 0; step < 12; step++)
            {
                var ctx = $"iter {iter} step {step} ({Check.Show(table.Text)})";
                var kind = rnd.Next(6);
                TextEdit edit;
                Action<CsvTable>? verify = null;
                var old = table;
                var mixed = HasMixedBreaks(old);
                if (kind <= 1 && table.RowCount > 0)
                {
                    var row = rnd.Next(table.RowCount);
                    var col = rnd.Next(table.ColumnCount + 2);
                    var value = pool[rnd.Next(pool.Length)];
                    edit = table.SetCell(row, col, value);
                    verify = after =>
                    {
                        if (mixed || old.HasUnterminatedQuote || after.HasUnterminatedQuote) return;
                        Check.Equal(old.RowCount, after.RowCount, ctx + " set: row count");
                        for (var r = 0; r < old.RowCount; r++)
                        {
                            var was = old.GetRow(r);
                            var now = after.GetRow(r);
                            if (r != row) Check.SeqEqual(was, now, ctx + " set: other row " + r);
                            else
                            {
                                Check.Equal(value, after.GetCell(row, col), ctx + " set: value read back");
                                for (var c = 0; c < was.Length; c++) if (c != col) Check.Equal(was[c], now[c], ctx + " set: neighbour " + c);
                            }
                        }
                    };
                }
                else if (kind == 2)
                {
                    var index = rnd.Next(table.RowCount + 1);
                    var values = Enumerable.Range(0, rnd.Next(0, 5)).Select(_ => pool[rnd.Next(pool.Length)]).ToArray();
                    edit = table.AddRow(index, values);
                    verify = after =>
                    {
                        if (mixed || old.HasUnterminatedQuote || after.HasUnterminatedQuote) return;
                        Check.Equal(old.RowCount + 1, after.RowCount, ctx + " add: row count");
                        for (var i = 0; i < values.Length; i++) Check.Equal(values[i], after.GetCell(index, i), ctx + " add: cell " + i);
                        for (var r = 0; r < old.RowCount; r++) Check.SeqEqual(old.GetRow(r), after.GetRow(r < index ? r : r + 1), ctx + " add: old row " + r);
                    };
                }
                else if (kind == 3 && table.RowCount > 0)
                {
                    var row = rnd.Next(table.RowCount);
                    edit = table.DeleteRow(row);
                    verify = after =>
                    {
                        if (mixed || old.HasUnterminatedQuote || after.HasUnterminatedQuote) return;
                        Check.Equal(old.RowCount - 1, after.RowCount, ctx + " delete: row count");
                        for (var r = 0; r < after.RowCount; r++) Check.SeqEqual(old.GetRow(r < row ? r : r + 1), after.GetRow(r), ctx + " delete: row " + r);
                    };
                }
                else
                {
                    // an arbitrary text edit, as typed in the editor
                    var len = table.Text.Length;
                    var o = Math.Max(table.ContentStart, rnd.Next(len + 1)); // the editor never shows (or lets the user touch) the BOM
                    var l = rnd.Next(Math.Min(6, len - o + 1));
                    edit = new TextEdit(o, l, junk[rnd.Next(junk.Length)] + (rnd.Next(3) == 0 ? junk[rnd.Next(junk.Length)] : ""));
                }

                var newText = edit.ApplyTo(table.Text);
                var next = table.WithEdit(edit, newText);
                Check.Equal(newText, next.Text, ctx + " text");
                TableTests.SameAsFullParse(next, ctx);
                verify?.Invoke(next);
                table = next;
                checks++;
            }
        }

        Check.True(checks >= iterations * 12, "ran " + checks);
    }

    /// <summary>A CR next to an LF can fuse into one break, so row bookkeeping by index is only exact when all breaks are alike.</summary>
    private static bool HasMixedBreaks(CsvTable t)
    {
        var kinds = new HashSet<string>();
        for (var r = 0; r < t.RowCount; r++) kinds.Add(t.Text[t.RowEnd(r)..t.RowNext(r)]);
        kinds.Remove("");
        return kinds.Count > 1;
    }

    /// <summary>Deleting many rows at once: the batch edit equals deleting them from the original text, whatever the line breaks.</summary>
    [Case]
    public static void RandomMultiRowDeletes()
    {
        var rnd = new Random(77);
        string[] eols = ["\n", "\r\n"];
        for (var iter = 0; iter < 500; iter++)
        {
            var eol = eols[rnd.Next(2)];
            var n = rnd.Next(1, 9);
            var lines = Enumerable.Range(0, n).Select(i => rnd.Next(5) == 0 ? "" : "r" + i + ",x").ToList();
            var text = string.Join(eol, lines) + (rnd.Next(2) == 0 ? eol : "");
            var d = new CsvDialect { LineEnding = eol, HasHeader = false };
            var t = CsvTable.Parse(text, d);
            var drop = Enumerable.Range(0, t.RowCount).Where(_ => rnd.Next(2) == 0).ToList();
            var expectedRows = Enumerable.Range(0, t.RowCount).Where(r => !drop.Contains(r)).Select(t.GetRow).ToList();
            var result = TextEdit.ApplyAll(text, t.DeleteRows(drop));
            var after = CsvTable.Parse(result, d);
            if (after.RowCount != expectedRows.Count) throw new CheckFailed($"iter {iter}: {Check.Show(text)} minus rows [{string.Join(",", drop)}] gave {Check.Show(result)} with {after.RowCount} rows, expected {expectedRows.Count}");
            for (var r = 0; r < expectedRows.Count; r++) Check.SeqEqual(expectedRows[r], after.GetRow(r), $"iter {iter} row {r}");
            TableTests.SameAsFullParse(t.WithEdits(t.DeleteRows(drop)), $"iter {iter} WithEdits");
        }
    }
}
