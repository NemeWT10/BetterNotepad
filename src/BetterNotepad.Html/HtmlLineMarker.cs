using System.Text;

namespace BetterNotepad.Html;

/// <summary>
/// Light HTML / SVG pre-pass: gives start tags a <c>data-bn-line="N"</c> attribute (1-based source line) so the preview can map
/// source lines to elements and back, and finds where the preview's config tag goes. It is a tokenizer, not a parser: comments,
/// doctype, CDATA, processing instructions, quoted attribute values and raw-text elements (script, style…) are skipped, nothing
/// else is changed — the document reaches the browser exactly as written plus one attribute per element.
/// </summary>
internal static class HtmlLineMarker
{
    /// <summary>Elements that never get the attribute (metadata, void elements without a box, the root).</summary>
    private static readonly HashSet<string> SkipHtml = new(StringComparer.OrdinalIgnoreCase)
    {
        "html", "head", "meta", "link", "script", "style", "title", "base", "noscript", "template",
        "br", "wbr", "source", "track", "param", "col", "colgroup", "area", "map",
    };

    private static readonly HashSet<string> SkipSvg = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "defs", "title", "desc", "metadata",
    };

    /// <summary>Elements whose content is raw text up to the matching end tag.</summary>
    private static readonly HashSet<string> RawText = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "textarea", "title", "xmp", "noembed", "noframes", "iframe", "noscript", "plaintext",
    };

    public readonly record struct Result(string Text, int HeadInsert, int Lines, int Marked);

    /// <param name="svg">SVG markup (different skip list; no HTML raw-text elements except script / style).</param>
    /// <param name="maxMarks">Stop marking after this many elements (huge generated files).</param>
    public static Result Mark(string src, bool svg, int maxMarks = 200_000)
    {
        var sb = new StringBuilder(src.Length + Math.Min(src.Length / 4, 1 << 20));
        var line = 1;
        var i = 0;
        var n = src.Length;
        var copied = 0; // src[copied..i) is still to be appended
        var headInsert = -1; // index in the OUTPUT where the config tag goes
        var afterDoctype = 0;
        var afterHtml = -1;
        var marked = 0;
        var skip = svg ? SkipSvg : SkipHtml;

        void Flush(int upTo)
        {
            if (upTo > copied) sb.Append(src, copied, upTo - copied);
            copied = upTo;
        }

        int CountLines(int from, int to)
        {
            var c = 0;
            for (var k = from; k < to; k++)
                if (src[k] == '\n') c++;
            return c;
        }

        // advances to just after the first occurrence of `end` (or the end of the text)
        int SkipPast(int from, string end)
        {
            var at = src.IndexOf(end, from, StringComparison.Ordinal);
            var stop = at < 0 ? n : at + end.Length;
            line += CountLines(from, stop);
            return stop;
        }

        while (i < n)
        {
            var c = src[i];
            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }
            if (c != '<' || i + 1 >= n)
            {
                i++;
                continue;
            }

            var next = src[i + 1];
            if (next == '!')
            {
                if (string.CompareOrdinal(src, i, "<!--", 0, 4) == 0) i = SkipPast(i + 4, "-->");
                else if (string.CompareOrdinal(src, i, "<![CDATA[", 0, 9) == 0) i = SkipPast(i + 9, "]]>");
                else
                {
                    i = SkipPast(i + 2, ">");
                    if (headInsert < 0 && afterHtml < 0 && marked == 0) afterDoctype = i;
                }
                continue;
            }
            if (next == '?')
            {
                i = SkipPast(i + 2, ">");
                if (headInsert < 0 && afterHtml < 0 && marked == 0) afterDoctype = i;
                continue;
            }
            if (next == '/')
            {
                i = SkipPast(i + 2, ">");
                continue;
            }
            if (!char.IsAsciiLetter(next))
            {
                i++;
                continue;
            }

            // start tag: name
            var tagLine = line;
            var nameStart = i + 1;
            var j = nameStart;
            while (j < n && (char.IsAsciiLetterOrDigit(src[j]) || src[j] is '-' or ':' or '_' or '.')) j++;
            var name = src.Substring(nameStart, j - nameStart);

            if (marked < maxMarks && !skip.Contains(name))
            {
                Flush(j);
                sb.Append(" data-bn-line=\"").Append(tagLine).Append('"');
                marked++;
            }

            // attributes up to the closing '>' (quotes may contain '>' and newlines)
            var quote = '\0';
            while (j < n)
            {
                var a = src[j];
                if (a == '\n') line++;
                if (quote != '\0')
                {
                    if (a == quote) quote = '\0';
                }
                else if (a is '"' or '\'')
                {
                    quote = a;
                }
                else if (a == '>')
                {
                    break;
                }
                j++;
            }
            var tagEnd = Math.Min(n, j + 1);
            i = tagEnd;

            if (!svg && name.Equals("head", StringComparison.OrdinalIgnoreCase) && headInsert < 0)
            {
                Flush(tagEnd);
                headInsert = sb.Length;
            }
            else if (!svg && name.Equals("html", StringComparison.OrdinalIgnoreCase) && afterHtml < 0)
            {
                Flush(tagEnd);
                afterHtml = sb.Length;
            }

            var selfClosed = tagEnd >= 2 && src[tagEnd - 2] == '/';
            if (!selfClosed && (svg ? name is "script" or "style" : RawText.Contains(name)))
            {
                // raw text: jump to the matching end tag (case-insensitive)
                var close = "</" + name;
                var at = src.IndexOf(close, i, StringComparison.OrdinalIgnoreCase);
                var stop = at < 0 ? n : at;
                line += CountLines(i, stop);
                i = stop;
            }
        }
        Flush(n);

        int insert;
        if (headInsert >= 0) insert = headInsert;
        else if (afterHtml >= 0) insert = afterHtml;
        else insert = MapToOutput(afterDoctype);
        return new Result(sb.ToString(), insert, line, marked);

        // before any marked tag the output equals the input, so a source index maps 1:1
        int MapToOutput(int srcIndex) => Math.Min(srcIndex, sb.Length);
    }
}
