using BetterNotepad.Editor.Highlighting;
using ICSharpCode.AvalonEdit.Document;

namespace BetterNotepad.Editor.Text;

/// <summary>How a language writes comments and strings (enough to tell code brackets from quoted ones).</summary>
public sealed record CodeSyntax(string? LineComment, string? BlockStart, string? BlockEnd, string Quotes, string PairQuotes)
{
    public static readonly CodeSyntax Plain = new(null, null, null, "", "");

    public static CodeSyntax For(string languageId)
    {
        var lang = Languages.Get(languageId);
        if (!lang.IsCode)
            // prose: apostrophes are not strings; backticks pair in Markdown (inline code)
            return new(null, lang.BlockStart, lang.BlockEnd, "", lang.Id == "markdown" ? "`" : "");
        var quotes = lang.Id switch
        {
            "rust" => "\"",                                   // 'a is a lifetime
            "html" or "xml" or "batch" or "ini" or "diff" => "", // apostrophes in text content
            "javascript" or "typescript" or "go" => "\"'`",
            _ => "\"'",
        };
        // asm and ini use ';' comments; our language table already knows the line comment token
        return new(lang.LineComment, lang.BlockStart, lang.BlockEnd, quotes, quotes);
    }
}

/// <summary>
/// A cheap forward scanner that reports brackets in code (skipping strings and comments). Used by
/// bracket matching and brace folding; never a full tokenizer.
/// </summary>
public static class CodeScanner
{
    /// <summary>Called for each bracket in code; return false to stop.</summary>
    public delegate bool BracketHandler(int offset, char c);

    /// <summary>Called for block comments that start in the scanned range (end = offset after the closing token, or -1).</summary>
    public delegate void BlockHandler(int start, int end);

    public static bool IsOpen(char c) => c is '(' or '[' or '{';

    public static bool IsClose(char c) => c is ')' or ']' or '}';

    public static char Partner(char c) => c switch
    {
        '(' => ')', ')' => '(', '[' => ']', ']' => '[', '{' => '}', '}' => '{', _ => '\0',
    };

    /// <summary>Scans text[from..to) (offsets are indices into <paramref name="text"/>).</summary>
    public static void Scan(string text, int from, int to, CodeSyntax syntax, BracketHandler onBracket, BlockHandler? onBlock = null)
    {
        var line = syntax.LineComment;
        var blockStart = syntax.BlockStart;
        var blockEnd = syntax.BlockEnd;
        var quotes = syntax.Quotes;
        var i = from;
        while (i < to)
        {
            var c = text[i];
            if (line is not null && c == line[0] && At(text, i, line, to))
            {
                i = LineEnd(text, i, to);
                continue;
            }
            if (blockStart is not null && blockEnd is not null && c == blockStart[0] && At(text, i, blockStart, to))
            {
                var end = text.IndexOf(blockEnd, Math.Min(i + blockStart.Length, to), to - Math.Min(i + blockStart.Length, to), StringComparison.Ordinal);
                var after = end < 0 ? -1 : end + blockEnd.Length;
                onBlock?.Invoke(i, after);
                i = after < 0 ? to : after;
                continue;
            }
            if (quotes.Length > 0 && quotes.IndexOf(c) >= 0)
            {
                i = SkipString(text, i, to, c);
                continue;
            }
            if ((IsOpen(c) || IsClose(c)) && !onBracket(i, c)) return;
            i++;
        }
    }

    private static bool At(string text, int i, string token, int to)
        => i + token.Length <= to && string.CompareOrdinal(text, i, token, 0, token.Length) == 0;

    private static int LineEnd(string text, int i, int to)
    {
        var n = text.IndexOfAny(['\r', '\n'], i, to - i);
        return n < 0 ? to : n;
    }

    /// <summary>Returns the offset after the closing quote (backticks may span lines, others end at the line end).</summary>
    private static int SkipString(string text, int i, int to, char quote)
    {
        var j = i + 1;
        while (j < to)
        {
            var ch = text[j];
            if (ch == '\\')
            {
                j += 2;
                continue;
            }
            if (ch == quote) return j + 1;
            if (quote != '`' && ch is '\n' or '\r') return j;
            j++;
        }
        return to;
    }

    /// <summary>
    /// The bracket pair at <paramref name="offset"/> (a bracket character), or null when it is in a string /
    /// comment or unmatched. Scans a window of the document, forward only.
    /// </summary>
    public static (int Open, int Close)? MatchBracket(TextDocument doc, int offset, CodeSyntax syntax, int window = 400_000)
    {
        if (offset < 0 || offset >= doc.TextLength) return null;
        var c = doc.GetCharAt(offset);
        if (!IsOpen(c) && !IsClose(c)) return null;
        // start at a line boundary well before the bracket (string / comment state is reliable at line starts)
        var start = offset <= window ? 0 : doc.GetLineByOffset(offset - window).Offset;
        var end = IsOpen(c) ? Math.Min(doc.TextLength, offset + window) : offset + 1;
        var text = doc.GetText(start, end - start);
        var target = offset - start;
        var stack = new List<int>();
        var targetDepth = -1;
        (int, int)? result = null;
        Scan(text, 0, text.Length, syntax, (i, ch) =>
        {
            if (IsOpen(ch))
            {
                stack.Add(i);
                if (i == target) targetDepth = stack.Count;
                return true;
            }
            var matches = stack.Count > 0 && Partner(text[stack[^1]]) == ch;
            if (i == target)
            {
                if (matches) result = (stack[^1] + start, i + start);
                return false;
            }
            if (!matches) return true; // stray closer: ignore
            if (targetDepth == stack.Count)
            {
                result = (stack[^1] + start, i + start);
                return false;
            }
            stack.RemoveAt(stack.Count - 1);
            return true;
        });
        return result;
    }
}
