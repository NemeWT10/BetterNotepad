using BetterNotepad.Editor.Highlighting;
using BetterNotepad.Editor.Text;
using ICSharpCode.AvalonEdit.Folding;
using AvalonDocument = ICSharpCode.AvalonEdit.Document.TextDocument;

namespace BetterNotepad.Editor.Folding;

/// <summary>
/// Fold regions per <see cref="FoldingKind"/>. Pure functions over a text snapshot, so they run off the UI thread.
/// Results are sorted by start offset (FoldingManager.UpdateFoldings requires it).
/// </summary>
public static class FoldingStrategies
{
    public const string Ellipsis = "…";

    public static List<NewFolding> Compute(string text, FoldingKind kind, CodeSyntax syntax, int tabSize, out int firstErrorOffset)
    {
        firstErrorOffset = -1;
        var list = kind switch
        {
            FoldingKind.Braces => Braces(text, syntax),
            FoldingKind.Indent => Indent(text, tabSize),
            FoldingKind.Markdown => Markdown(text),
            FoldingKind.Xml => Xml(text, out firstErrorOffset),
            _ => [],
        };
        list.Sort((a, b) => a.StartOffset != b.StartOffset ? a.StartOffset.CompareTo(b.StartOffset) : b.EndOffset.CompareTo(a.EndOffset));
        return list;
    }

    private static bool SpansLines(string text, int start, int end)
        => end > start && text.AsSpan(start, end - start).IndexOfAny('\r', '\n') >= 0;

    /// <summary>{ } and [ ] blocks over several lines, plus multi-line block comments; strings and comments are skipped.</summary>
    public static List<NewFolding> Braces(string text, CodeSyntax syntax)
    {
        var result = new List<NewFolding>();
        var stack = new List<(int Offset, char C)>();
        CodeScanner.Scan(text, 0, text.Length, syntax, (i, c) =>
        {
            if (c is '{' or '[')
            {
                stack.Add((i, c));
            }
            else if (c is '}' or ']')
            {
                // pop to the nearest matching opener (tolerates stray brackets)
                for (var k = stack.Count - 1; k >= 0; k--)
                {
                    if (CodeScanner.Partner(stack[k].C) != c) continue;
                    var open = stack[k].Offset;
                    stack.RemoveRange(k, stack.Count - k);
                    if (SpansLines(text, open, i)) result.Add(new NewFolding(AllmanStart(text, open), i + 1) { Name = c == '}' ? "{" + Ellipsis + "}" : "[" + Ellipsis + "]" });
                    break;
                }
            }
            return true;
        }, (start, end) =>
        {
            if (end > 0 && SpansLines(text, start, end) && syntax.BlockStart is { } bs && syntax.BlockEnd is { } be)
                result.Add(new NewFolding(start, end) { Name = bs + Ellipsis + be });
        });
        return result;
    }

    /// <summary>
    /// A brace alone at the start of its line (Allman style) folds from the end of the line above, so the
    /// chevron sits on the signature line ("void F()") and the folded view reads "void F(){…}".
    /// </summary>
    private static int AllmanStart(string text, int open)
    {
        var i = open - 1;
        while (i >= 0 && text[i] is ' ' or '\t') i--;
        if (i < 0 || text[i] is not ('\n' or '\r')) return open;
        if (i > 0 && text[i] == '\n' && text[i - 1] == '\r') i--;
        var prevEnd = i;
        i--;
        while (i >= 0 && text[i] is ' ' or '\t') i--;
        // blank line, or the line above ends a statement / block: keep the brace as the start
        if (i < 0 || text[i] is '\n' or '\r' or '{' or '}' or ';' or ',' or '[' or '(') return open;
        return prevEnd;
    }

    private readonly record struct Line(int Start, int End, int Indent);

    private static List<Line> Lines(string text, int tabSize)
    {
        var lines = new List<Line>();
        var start = 0;
        while (start <= text.Length)
        {
            var nl = text.IndexOfAny(['\r', '\n'], start);
            var end = nl < 0 ? text.Length : nl;
            lines.Add(new Line(start, end, TextHelpers.IndentColumns(text, start, end, tabSize)));
            if (nl < 0) break;
            start = nl + (text[nl] == '\r' && nl + 1 < text.Length && text[nl + 1] == '\n' ? 2 : 1);
        }
        return lines;
    }

    /// <summary>Python / YAML: a line followed by deeper-indented lines folds them (blank lines inside belong to the block).</summary>
    public static List<NewFolding> Indent(string text, int tabSize)
    {
        var result = new List<NewFolding>();
        var lines = Lines(text, tabSize);
        var stack = new List<(int Line, int Indent)>();
        var lastNonBlank = -1;
        void Close(int upTo)
        {
            var (open, _) = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (upTo > open) result.Add(new NewFolding(lines[open].End, lines[upTo].End) { Name = Ellipsis });
        }
        for (var i = 0; i < lines.Count; i++)
        {
            var indent = lines[i].Indent;
            if (indent < 0) continue; // blank
            while (stack.Count > 0 && indent <= stack[^1].Indent) Close(lastNonBlank);
            if (lastNonBlank >= 0 && indent > lines[lastNonBlank].Indent && (stack.Count == 0 || stack[^1].Line != lastNonBlank))
                stack.Add((lastNonBlank, lines[lastNonBlank].Indent));
            lastNonBlank = i;
        }
        while (stack.Count > 0) Close(lastNonBlank);
        return result;
    }

    /// <summary>Markdown: each heading folds up to the next heading of the same or a higher level; fenced code blocks fold too.</summary>
    public static List<NewFolding> Markdown(string text)
    {
        var result = new List<NewFolding>();
        var lines = Lines(text, 4);
        var headings = new List<(int Line, int Level)>();
        var lastNonBlank = -1;
        int? fence = null;
        string? fenceMark = null;
        void CloseHeadings(int level)
        {
            while (headings.Count > 0 && headings[^1].Level >= level)
            {
                var (h, _) = headings[^1];
                headings.RemoveAt(headings.Count - 1);
                if (lastNonBlank > h) result.Add(new NewFolding(lines[h].End, lines[lastNonBlank].End) { Name = Ellipsis });
            }
        }
        for (var i = 0; i < lines.Count; i++)
        {
            var (s, e, indent) = lines[i];
            if (indent < 0) continue;
            var t = text.AsSpan(s, e - s).TrimStart();
            if (fence is { } f)
            {
                if (t.StartsWith(fenceMark!, StringComparison.Ordinal))
                {
                    if (i > f) result.Add(new NewFolding(lines[f].End, e) { Name = Ellipsis });
                    fence = null;
                }
                lastNonBlank = i;
                continue;
            }
            if (indent < 4 && (t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal)))
            {
                fence = i;
                fenceMark = t[..3].ToString();
                lastNonBlank = i;
                continue;
            }
            if (indent < 4 && t.Length > 0 && t[0] == '#')
            {
                var level = 0;
                while (level < t.Length && t[level] == '#') level++;
                if (level <= 6 && (level == t.Length || t[level] == ' ' || t[level] == '\t'))
                {
                    CloseHeadings(level);
                    headings.Add((i, level));
                }
            }
            lastNonBlank = i;
        }
        CloseHeadings(1);
        return result;
    }

    /// <summary>XML / HTML: AvalonEdit's XmlFoldingStrategy on a private copy (thread-safe).</summary>
    public static List<NewFolding> Xml(string text, out int firstErrorOffset)
    {
        var doc = new AvalonDocument(text);
        var strategy = new XmlFoldingStrategy { ShowAttributesWhenFolded = true };
        return strategy.CreateNewFoldings(doc, out firstErrorOffset).ToList();
    }
}
