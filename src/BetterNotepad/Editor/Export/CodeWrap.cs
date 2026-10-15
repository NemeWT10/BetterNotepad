namespace BetterNotepad.Editor.Export;

/// <summary>
/// One drawn row of a code image: the runs, how many columns a continuation is indented by, and whether it is the
/// first row of its source line (only that one gets a line number).
/// </summary>
public sealed record WrappedRow(int Source, bool First, int Indent, IReadOnlyList<CodeRun> Runs)
{
    public string Text => string.Concat(Runs.Select(r => r.Text));
}

/// <summary>
/// Soft wrapping for code images: a line longer than the column limit breaks after the last space that fits (a long
/// token breaks where it must); continuation rows hang two columns under the line's own indentation, as in an editor.
/// </summary>
public static class CodeWrap
{
    /// <summary>The rows of a block, at most <paramref name="maxRows"/>; <paramref name="sourceLines"/> = source lines drawn (the last may be cut).</summary>
    public static List<WrappedRow> Rows(CodeBlock block, int columns, int maxRows, out bool truncated, out int sourceLines)
    {
        var rows = new List<WrappedRow>();
        truncated = false;
        sourceLines = 0;
        for (var i = 0; i < block.Lines.Count; i++)
        {
            var room = maxRows - rows.Count;
            if (room <= 0)
            {
                truncated = true;
                break;
            }
            var parts = Line(block.Lines[i], i, columns);
            sourceLines++;
            if (parts.Count > room)
            {
                rows.AddRange(parts.Take(room));
                truncated = true;
                break;
            }
            rows.AddRange(parts);
        }
        return rows;
    }

    public static List<WrappedRow> Line(IReadOnlyList<CodeRun> runs, int source, int columns)
    {
        columns = Math.Max(20, columns);
        var text = string.Concat(runs.Select(r => r.Text));
        if (text.Length <= columns) return [new WrappedRow(source, true, 0, runs)];

        var lead = 0;
        while (lead < text.Length && text[lead] == ' ') lead++;
        var hang = Math.Min(lead + 2, columns / 2);
        var rows = new List<WrappedRow>();
        var pos = 0;
        while (pos < text.Length)
        {
            var first = rows.Count == 0;
            var room = first ? columns : columns - hang;
            int end, next;
            if (text.Length - pos <= room)
            {
                end = next = text.Length;
            }
            else
            {
                // the row may end at a space in its second half (never inside the indentation)
                var brk = -1;
                var lowest = Math.Max(pos + room / 2, first ? lead : pos);
                for (var k = pos + room; k > lowest; k--)
                {
                    if (text[k] != ' ') continue;
                    brk = k;
                    break;
                }
                if (brk < 0)
                {
                    end = next = pos + room;
                    if (char.IsHighSurrogate(text[end - 1]) && end - 1 > pos) end = next = end - 1; // keep a surrogate pair together
                }
                else
                {
                    end = brk;
                    while (end > pos + 1 && text[end - 1] == ' ') end--;
                    next = brk;
                    while (next < text.Length && text[next] == ' ') next++;
                }
            }
            rows.Add(new WrappedRow(source, first, first ? 0 : hang, Slice(runs, pos, end)));
            pos = next;
        }
        return rows;
    }

    private static List<CodeRun> Slice(IReadOnlyList<CodeRun> runs, int from, int to)
    {
        var list = new List<CodeRun>();
        var offset = 0;
        foreach (var r in runs)
        {
            var a = Math.Max(from, offset);
            var b = Math.Min(to, offset + r.Text.Length);
            if (b > a) list.Add(r with { Text = r.Text.Substring(a - offset, b - a) });
            offset += r.Text.Length;
            if (offset >= to) break;
        }
        return list;
    }
}
