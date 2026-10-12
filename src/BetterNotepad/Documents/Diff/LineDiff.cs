using System.Text;

namespace BetterNotepad.Documents.Diff;

public enum DiffOp
{
    Equal,
    Delete,
    Insert,
}

public readonly record struct DiffLine(DiffOp Op, string Text, int OldLine, int NewLine);

/// <summary>Line diff (Myers O(ND)) with a unified-diff renderer.</summary>
public static class LineDiff
{
    /// <summary>More edits than this and Myers is skipped (files shown as fully replaced).</summary>
    private const int MaxEdits = 3000;

    public static string[] SplitLines(string text) => text.Length == 0 ? [] : text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    public static List<DiffLine> Compute(string oldText, string newText)
    {
        var a = SplitLines(oldText);
        var b = SplitLines(newText);

        // trim common prefix/suffix (fast path for typical edits)
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;

        var result = new List<DiffLine>(a.Length + b.Length);
        for (var i = 0; i < prefix; i++) result.Add(new DiffLine(DiffOp.Equal, a[i], i + 1, i + 1));

        var midA = a[prefix..(a.Length - suffix)];
        var midB = b[prefix..(b.Length - suffix)];
        foreach (var d in Myers(midA, midB))
            result.Add(d with { OldLine = d.OldLine < 0 ? -1 : d.OldLine + prefix + 1, NewLine = d.NewLine < 0 ? -1 : d.NewLine + prefix + 1 });

        for (var i = 0; i < suffix; i++)
        {
            var ia = a.Length - suffix + i;
            var ib = b.Length - suffix + i;
            result.Add(new DiffLine(DiffOp.Equal, a[ia], ia + 1, ib + 1));
        }
        return result;
    }

    private static List<DiffLine> Myers(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length, max = n + m;
        var output = new List<DiffLine>();
        if (n == 0)
        {
            for (var j = 0; j < m; j++) output.Add(new DiffLine(DiffOp.Insert, b[j], -1, j));
            return output;
        }
        if (m == 0)
        {
            for (var i = 0; i < n; i++) output.Add(new DiffLine(DiffOp.Delete, a[i], i, -1));
            return output;
        }

        var v = new int[2 * max + 2];
        var trace = new List<int[]>();
        for (var d = 0; d <= max; d++)
        {
            // very many edits: fall back to "replace all" rather than burning memory (the trace is O(D^2))
            if (d > MaxEdits)
            {
                for (var i = 0; i < n; i++) output.Add(new DiffLine(DiffOp.Delete, a[i], i, -1));
                for (var j = 0; j < m; j++) output.Add(new DiffLine(DiffOp.Insert, b[j], -1, j));
                return output;
            }
            // only v[max-d-1 .. max+d+1] is read when backtracking from step d
            var lo = Math.Max(0, max - d - 1);
            trace.Add(v[lo..(Math.Min(v.Length - 1, max + d + 1) + 1)]);
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || k != d && v[max + k - 1] < v[max + k + 1]) x = v[max + k + 1];
                else x = v[max + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[max + k] = x;
                if (x >= n && y >= m) return Backtrack(trace, a, b, max);
            }
        }
        return output;
    }

    private static List<DiffLine> Backtrack(List<int[]> trace, string[] a, string[] b, int max)
    {
        var result = new List<DiffLine>();
        int x = a.Length, y = b.Length;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var v = trace[d];
            var lo = Math.Max(0, max - d - 1); // v is the slice of the full array starting here
            var k = x - y;
            int prevK;
            if (k == -d || k != d && v[max + k - 1 - lo] < v[max + k + 1 - lo]) prevK = k + 1;
            else prevK = k - 1;
            var prevX = v[max + prevK - lo];
            var prevY = prevX - prevK;
            while (x > prevX && y > prevY)
            {
                x--;
                y--;
                result.Add(new DiffLine(DiffOp.Equal, a[x], x, y));
            }
            if (d > 0)
            {
                if (x == prevX) result.Add(new DiffLine(DiffOp.Insert, b[prevY], -1, prevY));
                else result.Add(new DiffLine(DiffOp.Delete, a[prevX], prevX, -1));
            }
            x = prevX;
            y = prevY;
        }
        result.Reverse();
        return result;
    }

    /// <summary>Unified diff with 3 lines of context.</summary>
    public static string Unified(string oldText, string newText, string oldName, string newName, int context = 3)
    {
        var lines = Compute(oldText, newText);
        var sb = new StringBuilder();
        sb.Append("--- ").AppendLine(oldName);
        sb.Append("+++ ").AppendLine(newName);
        var changes = lines.Select((l, i) => (l, i)).Where(t => t.l.Op != DiffOp.Equal).Select(t => t.i).ToList();
        if (changes.Count == 0) return sb.ToString();

        var i0 = 0;
        while (i0 < changes.Count)
        {
            var start = Math.Max(0, changes[i0] - context);
            var end = changes[i0];
            var i1 = i0;
            while (i1 + 1 < changes.Count && changes[i1 + 1] - end <= context * 2 + 1) end = changes[++i1];
            end = Math.Min(lines.Count - 1, end + context);

            int oldStart = 0, newStart = 0, oldCount = 0, newCount = 0;
            for (var i = start; i <= end; i++)
            {
                var l = lines[i];
                if (l.Op != DiffOp.Insert) { if (oldCount++ == 0) oldStart = l.OldLine; }
                if (l.Op != DiffOp.Delete) { if (newCount++ == 0) newStart = l.NewLine; }
            }
            sb.AppendLine($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@");
            for (var i = start; i <= end; i++)
            {
                var l = lines[i];
                sb.Append(l.Op switch { DiffOp.Insert => '+', DiffOp.Delete => '-', _ => ' ' }).AppendLine(l.Text);
            }
            i0 = i1 + 1;
        }
        return sb.ToString();
    }
}
