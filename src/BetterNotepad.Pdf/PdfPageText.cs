namespace BetterNotepad.Pdf;

/// <summary>
/// Immutable text layer of one page: the extracted text (PDFium order, lines separated by "\r\n") plus one box per
/// UTF-16 character (page space, see <see cref="PdfRect"/>; empty for line breaks / chars without geometry).
/// Hit testing, selection rectangles and selected text are computed locally, so dragging a selection needs no engine round trip.
/// </summary>
public sealed class PdfPageText
{
    public int PageIndex { get; }
    public string Text { get; }
    public PdfRect[] CharBoxes { get; }
    /// <summary>Per-character font size in points; only when requested (conversion).</summary>
    public float[]? FontSizes { get; }
    /// <summary>Per-character bold flag; only when requested (conversion).</summary>
    public bool[]? Bold { get; }

    internal PdfPageText(int pageIndex, string text, PdfRect[] boxes, float[]? sizes, bool[]? bold)
    {
        PageIndex = pageIndex; Text = text; CharBoxes = boxes; FontSizes = sizes; Bold = bold;
    }

    public int Length => Text.Length;
    internal long EstimatedBytes => 64 + (long)Text.Length * (2 + 16 + (FontSizes != null ? 5 : 0));

    /// <summary>Index of the character whose box contains the point (± tolerance points), or -1.</summary>
    public int HitTestChar(double x, double y, double tolerance = 1.5)
    {
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < CharBoxes.Length; i++)
        {
            var b = CharBoxes[i];
            if (b.IsEmpty) continue;
            double dx = Math.Max(Math.Max(b.Left - x, x - b.Right), 0), dy = Math.Max(Math.Max(b.Top - y, y - b.Bottom), 0);
            if (dx > tolerance || dy > tolerance) continue;
            double d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = i; if (d == 0) break; }
        }
        return best;
    }

    /// <summary>
    /// Caret (insertion point) position 0..Length nearest to the point, for starting / extending a selection.
    /// A point left of a character gives its index, right of its middle gives index + 1. Returns -1 when the page has no text.
    /// </summary>
    public int HitTestCaret(double x, double y)
    {
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < CharBoxes.Length; i++)
        {
            var b = CharBoxes[i];
            if (b.IsEmpty) continue;
            double dx = Math.Max(Math.Max(b.Left - x, x - b.Right), 0), dy = Math.Max(Math.Max(b.Top - y, y - b.Bottom), 0);
            double d = dx * dx + 9 * dy * dy;     // stay on the line under the pointer rather than jump to a neighbour line
            if (d < bestD) { bestD = d; best = i; if (d == 0) break; }
        }
        if (best < 0) return -1;
        var bb = CharBoxes[best];
        return x > (bb.Left + bb.Right) * 0.5 ? best + 1 : best;
    }

    /// <summary>Text of the half-open caret range [start, end) (order-insensitive).</summary>
    public string GetSelectedText(int start, int end)
    {
        if (start > end) (start, end) = (end, start);
        start = Math.Clamp(start, 0, Text.Length);
        end = Math.Clamp(end, start, Text.Length);
        return Text[start..end].Replace("\u00AD\r\n", "").Replace("\u00AD", "");
    }

    /// <summary>Highlight rectangles for the caret range [start, end): one rectangle per line segment.</summary>
    public IReadOnlyList<PdfRect> GetSelectionRects(int start, int end)
    {
        if (start > end) (start, end) = (end, start);
        start = Math.Clamp(start, 0, CharBoxes.Length);
        end = Math.Clamp(end, start, CharBoxes.Length);
        var res = new List<PdfRect>();
        PdfRect cur = default;
        bool have = false;
        for (int i = start; i < end; i++)
        {
            var b = CharBoxes[i];
            if (b.IsEmpty) continue;
            if (have && SameLine(cur, b)) { cur = cur.Union(b); continue; }
            if (have) res.Add(cur);
            cur = b; have = true;
        }
        if (have) res.Add(cur);
        return res;
    }

    private static bool SameLine(PdfRect line, PdfRect c)
    {
        float overlap = Math.Min(line.Bottom, c.Bottom) - Math.Max(line.Top, c.Top);
        float minH = Math.Min(line.Height, c.Height);
        if (minH <= 0 || overlap < minH * 0.5f) return false;
        return c.Left - line.Right < Math.Max(line.Height, c.Height) * 2.5f && line.Left - c.Right < Math.Max(line.Height, c.Height) * 2.5f;
    }

    /// <summary>Word range [start, end) around a character (double-click selection).</summary>
    public (int Start, int End) GetWordRangeAt(int charIndex)
    {
        if (Text.Length == 0) return (0, 0);
        charIndex = Math.Clamp(charIndex, 0, Text.Length - 1);
        static bool W(char c) => char.IsLetterOrDigit(c) || c is '_' or '\u00AD';
        if (!W(Text[charIndex])) return (charIndex, charIndex + 1);
        int s = charIndex, e = charIndex + 1;
        while (s > 0 && W(Text[s - 1])) s--;
        while (e < Text.Length && W(Text[e])) e++;
        return (s, e);
    }

    /// <summary>Line range [start, end) around a character (triple-click selection), without the line break.</summary>
    public (int Start, int End) GetLineRangeAt(int charIndex)
    {
        if (Text.Length == 0) return (0, 0);
        charIndex = Math.Clamp(charIndex, 0, Text.Length - 1);
        int s = charIndex, e = charIndex;
        while (s > 0 && Text[s - 1] is not ('\n' or '\r')) s--;
        while (e < Text.Length && Text[e] is not ('\n' or '\r')) e++;
        return (s, e);
    }
}
