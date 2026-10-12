using BetterNotepad.Editor.Highlighting;
using BetterNotepad.Editor.Text;
using ICSharpCode.AvalonEdit.Document;

namespace BetterNotepad.Editor.MultiCaret;

/// <summary>What typing does for the current document (settings + language).</summary>
public sealed record TypingContext(bool AutoClose, bool SmartIndent, FoldingKind Folding, string IndentUnit, int IndentSize, bool TabsToSpaces, CodeSyntax Syntax)
{
    public static readonly TypingContext Default = new(true, false, FoldingKind.None, "    ", 4, true, CodeSyntax.Plain);
}

/// <summary>
/// Per-caret edit rules: auto-close with type-over and wrap-selection, the empty-pair backspace, smart Enter.
/// Pure functions of (document, caret) so one caret and many carets behave the same.
/// </summary>
public static class Typing
{
    private const string WrapChars = "([{\"'`";

    private static char CloserOf(char c) => c switch { '(' => ')', '[' => ']', '{' => '}', _ => c };

    private static bool IsSpaceOrEnd(char c) => c is '\0' or ' ' or '\t' or '\r' or '\n';

    /// <summary>The edit for typing <paramref name="text"/> at one caret; Special = something besides a plain insert.</summary>
    public static (CaretEdit Edit, bool Special) Type(TextDocument doc, CaretRange r, string text, TypingContext ctx)
    {
        if (text is "\n" or "\r" or "\r\n") return (NewLine(doc, r, ctx), true);
        if (ctx.AutoClose && text.Length == 1)
        {
            var c = text[0];
            var next = r.End < doc.TextLength ? doc.GetCharAt(r.End) : '\0';
            var prev = r.Start > 0 ? doc.GetCharAt(r.Start - 1) : '\0';
            var isQuote = c is '"' or '\'' or '`';

            if (!r.IsEmpty && WrapChars.IndexOf(c) >= 0 && (!isQuote || ctx.Syntax.PairQuotes.Contains(c) || !doc.GetText(r.Start, r.Length).Contains('\n')))
            {
                // wrap the selection and keep it selected inside the pair
                var sel = doc.GetText(r.Start, r.Length);
                return (new CaretEdit(r.Start, r.Length, c + sel + CloserOf(c), 1, 1 + sel.Length), true);
            }
            if (r.IsEmpty)
            {
                // type over the closer that is already there
                if ((CodeScanner.IsClose(c) || isQuote && ctx.Syntax.PairQuotes.Contains(c)) && next == c && prev != '\\')
                    return (new CaretEdit(r.Caret, 0, "", 1, 1), true);
                if (CodeScanner.IsOpen(c) && (IsSpaceOrEnd(next) || ")]};,".Contains(next)))
                    return (new CaretEdit(r.Caret, 0, c.ToString() + CloserOf(c), 1, 1), true);
                if (isQuote && ctx.Syntax.PairQuotes.Contains(c) && !TextHelpers.IsWordChar(prev) && prev != c && prev != '\\'
                    && (IsSpaceOrEnd(next) || ")]},;:".Contains(next)))
                    return (new CaretEdit(r.Caret, 0, new string(c, 2), 1, 1), true);
            }
        }
        return (new CaretEdit(r.Start, r.Length, text, text.Length, text.Length), false);
    }

    /// <summary>Backspace between an auto-closed empty pair removes both halves.</summary>
    public static CaretEdit? DeletePair(TextDocument doc, CaretRange r, TypingContext ctx)
    {
        if (!ctx.AutoClose || !r.IsEmpty || r.Caret == 0 || r.Caret >= doc.TextLength) return null;
        var prev = doc.GetCharAt(r.Caret - 1);
        var next = doc.GetCharAt(r.Caret);
        var pair = CodeScanner.IsOpen(prev) && CloserOf(prev) == next || prev == next && ctx.Syntax.PairQuotes.Contains(prev) && prev is '"' or '\'' or '`';
        return pair ? new CaretEdit(r.Caret - 1, 2, "", 0, 0) : null;
    }

    /// <summary>Enter: keeps the indentation; in code indents after an opener and splits an empty {} pair over three lines.</summary>
    public static CaretEdit NewLine(TextDocument doc, CaretRange r, TypingContext ctx)
    {
        var line = doc.GetLineByOffset(r.Start);
        var nl = TextUtilities.GetNewLineFromDocument(doc, line.LineNumber);
        var col = r.Start - line.Offset;
        var head = doc.GetText(line.Offset, col);
        var wsLen = 0;
        while (wsLen < head.Length && head[wsLen] is ' ' or '\t') wsLen++;
        var indent = head[..wsLen];

        // whitespace right after the caret would only push the moved text to the right (but never eat the
        // indentation of a line when Enter is pressed inside it)
        var endLine = doc.GetLineByOffset(r.End);
        var removeAfter = 0;
        if (wsLen < head.Length)
            while (r.End + removeAfter < endLine.EndOffset && doc.GetCharAt(r.End + removeAfter) is ' ' or '\t') removeAfter++;
        var nextChar = r.End + removeAfter < doc.TextLength ? doc.GetCharAt(r.End + removeAfter) : '\0';

        var before = head.TrimEnd();
        var last = before.Length > 0 ? before[^1] : '\0';
        var opens = ctx.SmartIndent && (CodeScanner.IsOpen(last) || last == ':' && ctx.Folding == FoldingKind.Indent);
        if (opens && CodeScanner.IsOpen(last) && CloserOf(last) == nextChar)
        {
            var inner = indent + ctx.IndentUnit;
            var text = nl + inner + nl + indent;
            return new CaretEdit(r.Start, r.Length + removeAfter, text, nl.Length + inner.Length, nl.Length + inner.Length);
        }
        var body = nl + (opens ? indent + ctx.IndentUnit : indent);
        return new CaretEdit(r.Start, r.Length + removeAfter, body, body.Length, body.Length);
    }

    /// <summary>Tab at one caret: spaces up to the next tab stop (or a tab character).</summary>
    public static CaretEdit Tab(TextDocument doc, CaretRange r, TypingContext ctx)
    {
        if (!ctx.TabsToSpaces) return new CaretEdit(r.Start, r.Length, "\t", 1, 1);
        var line = doc.GetLineByOffset(r.Start);
        var cols = 0;
        for (var i = line.Offset; i < r.Start; i++) cols = doc.GetCharAt(i) == '\t' ? cols + ctx.IndentSize - cols % ctx.IndentSize : cols + 1;
        var n = ctx.IndentSize - cols % ctx.IndentSize;
        return new CaretEdit(r.Start, r.Length, new string(' ', n), n, n);
    }
}
