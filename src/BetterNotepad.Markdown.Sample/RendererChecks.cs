using System.Text;
using System.Windows;
using System.Windows.Documents;

namespace BetterNotepad.Markdown.Sample;

/// <summary>Layout-free checks: incremental patching equals a fresh render, line mapping of rendered elements.</summary>
internal static class RendererChecks
{
    public static void Run(string outDir)
    {
        Console.WriteLine("--- renderer checks");
        var plain = SampleText.Document
            .Replace("Przypis[^1], ", "")
            .Replace("[^1]: To jest treść przypisu dolnego, z `kodem` i [linkiem](https://example.com).", "")
            .Replace("[docs]: https://example.com/docs \"Dokumentacja\"", "")
            .Replace("[home]: https://github.com/NemeWT10/BetterNotepad", "")
            .Replace("[dokumentację][docs]", "dokumentację")
            .Replace("[stronę projektu][home]", "stronę projektu");

        IncrementalEquality("no footnotes / no references", plain, outDir, 250, 7);
        IncrementalEquality("with footnotes + references", SampleText.Document, outDir, 60, 11);
        LineMapping(outDir);
    }

    private static string Fingerprint(MarkdownRenderer r)
    {
        var sb = new StringBuilder();
        sb.Append(new TextRange(r.Document.ContentStart, r.Document.ContentEnd).Text).Append('|');
        foreach (var b in r.Blocks) sb.Append(b.Line).Append('-').Append(b.EndLine).Append(':').Append(b.Kind).Append(b.Element is null ? "0" : "1").Append(';');
        foreach (var h in r.Headings) sb.Append('#').Append(h.Level).Append(h.Line).Append(h.Slug);
        sb.Append(r.RichContent);
        return sb.ToString();
    }

    private static void IncrementalEquality(string name, string start, string baseFolder, int steps, int seed)
    {
        var rnd = new Random(seed);
        var inc = new MarkdownRenderer();
        var text = start;
        inc.Render(text, baseFolder);
        var failures = 0;
        long reused = 0, rebuilt = 0;
        var incremental = 0;
        for (var i = 0; i < steps; i++)
        {
            text = Mutate(text, rnd);
            var result = inc.Render(text, baseFolder);
            reused += result.Reused;
            rebuilt += result.Rebuilt;
            if (result.Incremental) incremental++;

            using var fresh = new MarkdownRenderer();
            fresh.Render(text, baseFolder);
            if (Fingerprint(inc) != Fingerprint(fresh))
            {
                failures++;
                if (failures <= 3)
                {
                    Console.WriteLine($"  MISMATCH at step {i}");
                    var a = Fingerprint(inc);
                    var b = Fingerprint(fresh);
                    var k = 0;
                    while (k < a.Length && k < b.Length && a[k] == b[k]) k++;
                    Console.WriteLine($"    first difference at {k}: inc=…{Excerpt(a, k)}…  fresh=…{Excerpt(b, k)}…");
                }
            }
        }

        Console.WriteLine($"  {name}: {steps} random edits, mismatches={failures}, incremental renders={incremental}, reused blocks={reused}, rebuilt={rebuilt}");
        inc.Dispose();
    }

    private static string Excerpt(string s, int at) => s.Substring(Math.Max(0, at - 20), Math.Min(60, s.Length - Math.Max(0, at - 20))).Replace('\n', '~').Replace('\r', '~');

    private static string Mutate(string text, Random rnd)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        switch (rnd.Next(7))
        {
            case 0:
            {
                var p = rnd.Next(text.Length);
                return text.Insert(p, "x");
            }
            case 1:
            {
                var p = rnd.Next(Math.Max(1, text.Length - 10));
                return text.Remove(p, rnd.Next(1, 8));
            }
            case 2:
            {
                var i = rnd.Next(lines.Count);
                lines.Insert(i, lines[rnd.Next(lines.Count)]);
                break;
            }
            case 3:
            {
                if (lines.Count > 5) lines.RemoveAt(rnd.Next(lines.Count));
                break;
            }
            case 4:
            {
                var i = rnd.Next(lines.Count);
                lines.Insert(i, "");
                break;
            }
            case 5:
            {
                var i = rnd.Next(lines.Count);
                lines.Insert(i, "## Nowy nagłówek " + rnd.Next(100));
                break;
            }
            default:
            {
                var i = rnd.Next(lines.Count);
                lines[i] = "> " + lines[i];
                break;
            }
        }

        return string.Join("\n", lines);
    }

    private static void LineMapping(string baseFolder)
    {
        using var r = new MarkdownRenderer();
        r.Render(SampleText.Document, baseFolder);
        var lines = SampleText.Document.Replace("\r\n", "\n").Split('\n');
        var bad = 0;
        var checkedCount = 0;
        foreach (var b in r.Blocks)
        {
            if (b.Element is null) continue;
            checkedCount++;
            var line = r.SourceLineOf(b.Element);
            if (line != b.Line)
            {
                bad++;
                Console.WriteLine($"  block {b.Kind} expected line {b.Line} got {line}");
            }
        }

        Console.WriteLine($"  top-level SourceLineOf: {checkedCount} blocks, wrong={bad}");

        // nested element: a task checkbox must report its own line
        var task = FindLogical<System.Windows.Controls.CheckBox>(r.Document);
        if (task is not null)
        {
            var l = r.SourceLineOf(task);
            Console.WriteLine($"  first checkbox resolves to line {l}: '{lines[(l ?? 1) - 1].Trim()}'");
        }

        var table = FindLogical<System.Windows.Documents.Table>(r.Document);
        if (table is not null)
        {
            var row = table.RowGroups[0].Rows[2];
            var l = r.SourceLineOf(row.Cells[0].Blocks.FirstBlock);
            Console.WriteLine($"  table row 3 resolves to line {l}: '{lines[(l ?? 1) - 1].Trim()}'");
        }

        var h = r.Headings.First(x => x.Text == "Tabele");
        var blk = r.FindBlock(h.Line);
        Console.WriteLine($"  FindBlock({h.Line}) = {blk?.Kind} lines {blk?.Line}-{blk?.EndLine}");
        var inside = r.FindBlock(h.Line + 3);
        Console.WriteLine($"  FindBlock({h.Line + 3}) = {inside?.Kind} lines {inside?.Line}-{inside?.EndLine}");
    }

    private static T? FindLogical<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is T t) return t;
            if (child is DependencyObject d && FindLogical<T>(d) is { } f) return f;
        }

        return null;
    }
}
