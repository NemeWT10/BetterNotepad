using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;
using BetterNotepad.Editor.Highlighting;
using ICSharpCode.AvalonEdit.Document;

namespace BetterNotepad.Editor;

/// <summary>Line and text operations (VS Code semantics), each one undo step.</summary>
public static class EditorOps
{
    private static (DocumentLine First, DocumentLine Last) SelectedLines(CodeEditor e)
    {
        var doc = e.Document;
        var start = doc.GetLineByOffset(e.SelectionStart);
        var endOffset = e.SelectionStart + e.SelectionLength;
        var end = doc.GetLineByOffset(endOffset);
        // a selection ending at column 1 does not include that line
        if (e.SelectionLength > 0 && end.Offset == endOffset && end.LineNumber > start.LineNumber) end = end.PreviousLine;
        return (start, end);
    }

    private static string NewLine(TextDocument doc, DocumentLine line)
        => line.DelimiterLength > 0 ? doc.GetText(line.EndOffset, line.DelimiterLength) :
           line.PreviousLine is { DelimiterLength: > 0 } p ? doc.GetText(p.EndOffset, p.DelimiterLength) : Environment.NewLine;

    public static void MoveLines(CodeEditor e, int dir)
    {
        if (e.IsReadOnly) return;
        var doc = e.Document;
        var (first, last) = SelectedLines(e);
        if (dir < 0 && first.PreviousLine is null || dir > 0 && last.NextLine is null) return;
        var caret = e.CaretOffset;
        var selStart = e.SelectionStart;
        var selLen = e.SelectionLength;
        var blockStart = first.Offset;
        var blockEnd = last.EndOffset;
        var block = doc.GetText(blockStart, blockEnd - blockStart);
        using (doc.RunUpdate())
        {
            if (dir < 0)
            {
                var prev = first.PreviousLine!;
                var prevText = doc.GetText(prev.Offset, prev.Length);
                var nl = NewLine(doc, prev);
                doc.Replace(prev.Offset, blockEnd - prev.Offset, block + nl + prevText);
                var shift = -(prev.Length + nl.Length);
                Restore(e, caret + shift, selStart + shift, selLen);
            }
            else
            {
                var next = last.NextLine!;
                var nextText = doc.GetText(next.Offset, next.Length);
                var nl = NewLine(doc, last);
                doc.Replace(blockStart, next.EndOffset - blockStart, nextText + nl + block);
                var shift = next.Length + nl.Length;
                Restore(e, caret + shift, selStart + shift, selLen);
            }
        }
    }

    private static void Restore(CodeEditor e, int caret, int selStart, int selLen)
    {
        var len = e.Document.TextLength;
        if (selLen > 0) e.Select(Math.Clamp(selStart, 0, len), Math.Min(selLen, len - Math.Clamp(selStart, 0, len)));
        e.CaretOffset = Math.Clamp(caret, 0, len);
    }

    public static void CopyLines(CodeEditor e, int dir)
    {
        if (e.IsReadOnly) return;
        var doc = e.Document;
        var (first, last) = SelectedLines(e);
        var block = doc.GetText(first.Offset, last.EndOffset - first.Offset);
        var nl = NewLine(doc, last);
        var caret = e.CaretOffset;
        var selStart = e.SelectionStart;
        var selLen = e.SelectionLength;
        using (doc.RunUpdate())
        {
            doc.Insert(last.EndOffset, nl + block);
            if (dir > 0)
            {
                var shift = block.Length + nl.Length;
                Restore(e, caret + shift, selStart + shift, selLen);
            }
            else
            {
                Restore(e, caret, selStart, selLen);
            }
        }
    }

    public static void DeleteLines(CodeEditor e)
    {
        if (e.IsReadOnly) return;
        var doc = e.Document;
        var (first, last) = SelectedLines(e);
        var column = e.TextArea.Caret.Column;
        int start, end;
        if (last.NextLine is not null)
        {
            start = first.Offset;
            end = last.NextLine.Offset;
        }
        else if (first.PreviousLine is not null)
        {
            start = first.PreviousLine.EndOffset;
            end = last.EndOffset;
        }
        else
        {
            start = 0;
            end = doc.TextLength;
        }
        doc.Remove(start, end - start);
        var line = doc.GetLineByOffset(Math.Min(start, doc.TextLength));
        e.CaretOffset = line.Offset + Math.Min(column - 1, line.Length);
    }

    public static void InsertLine(CodeEditor e, bool above)
    {
        if (e.IsReadOnly) return;
        var doc = e.Document;
        var line = doc.GetLineByOffset(e.CaretOffset);
        var indent = LeadingWhitespace(doc, line);
        var nl = NewLine(doc, line);
        if (above)
        {
            doc.Insert(line.Offset, indent + nl);
            e.CaretOffset = line.Offset + indent.Length;
        }
        else
        {
            doc.Insert(line.EndOffset, nl + indent);
            e.CaretOffset = line.EndOffset + nl.Length + indent.Length;
        }
    }

    private static string LeadingWhitespace(TextDocument doc, DocumentLine line)
    {
        var seg = TextUtilities.GetLeadingWhitespace(doc, line);
        return doc.GetText(seg);
    }

    public static void SelectLine(CodeEditor e)
    {
        var doc = e.Document;
        var (first, last) = SelectedLines(e);
        var end = last.NextLine?.Offset ?? last.EndOffset;
        if (e.SelectionLength > 0 && e.SelectionStart == first.Offset && e.SelectionStart + e.SelectionLength == end && last.NextLine is not null)
        {
            // pressing again extends by one more line
            end = last.NextLine.NextLine?.Offset ?? last.NextLine.EndOffset;
        }
        e.Select(first.Offset, end - first.Offset);
    }

    /// <summary>Ctrl+/ — toggles line comments (block comments for languages without line comments).</summary>
    public static void ToggleLineComment(CodeEditor e, string languageId)
    {
        if (e.IsReadOnly) return;
        var lang = Languages.Get(languageId);
        if (lang.LineComment is null)
        {
            ToggleBlockComment(e, languageId);
            return;
        }
        var token = lang.LineComment;
        var doc = e.Document;
        var (first, last) = SelectedLines(e);
        var lines = Enumerable.Range(first.LineNumber, last.LineNumber - first.LineNumber + 1).Select(doc.GetLineByNumber).ToList();
        var nonEmpty = lines.Where(l => doc.GetText(l).Trim().Length > 0).ToList();
        if (nonEmpty.Count == 0) nonEmpty = lines;
        var allCommented = nonEmpty.All(l => doc.GetText(l).TrimStart().StartsWith(token.TrimEnd(), StringComparison.Ordinal));
        var minIndent = nonEmpty.Min(l => TextUtilities.GetLeadingWhitespace(doc, l).Length);
        using (doc.RunUpdate())
        {
            foreach (var l in lines.AsEnumerable().Reverse())
            {
                var text = doc.GetText(l);
                if (allCommented)
                {
                    var ws = TextUtilities.GetLeadingWhitespace(doc, l).Length;
                    var t = token.TrimEnd();
                    if (text.Length >= ws + t.Length && text.Substring(ws, t.Length) == t)
                    {
                        var removeLen = t.Length + (text.Length > ws + t.Length && text[ws + t.Length] == ' ' ? 1 : 0);
                        doc.Remove(l.Offset + ws, removeLen);
                    }
                }
                else if (text.Trim().Length > 0 || lines.Count == 1)
                {
                    doc.Insert(l.Offset + Math.Min(minIndent, text.Length), token.EndsWith(' ') ? token : token + " ");
                }
            }
        }
    }

    public static void ToggleBlockComment(CodeEditor e, string languageId)
    {
        if (e.IsReadOnly) return;
        var lang = Languages.Get(languageId);
        if (lang.BlockStart is null || lang.BlockEnd is null)
        {
            if (lang.LineComment is not null) ToggleLineComment(e, languageId);
            return;
        }
        var doc = e.Document;
        int start = e.SelectionStart, len = e.SelectionLength;
        if (len == 0)
        {
            var line = doc.GetLineByOffset(e.CaretOffset);
            start = line.Offset;
            len = line.Length;
        }
        var text = doc.GetText(start, len);
        var trimmed = text.Trim();
        using (doc.RunUpdate())
        {
            if (trimmed.StartsWith(lang.BlockStart, StringComparison.Ordinal) && trimmed.EndsWith(lang.BlockEnd, StringComparison.Ordinal))
            {
                var lead = text.IndexOf(lang.BlockStart, StringComparison.Ordinal);
                var tail = text.LastIndexOf(lang.BlockEnd, StringComparison.Ordinal);
                var inner = text.Substring(lead + lang.BlockStart.Length, tail - lead - lang.BlockStart.Length);
                if (inner.StartsWith(' ')) inner = inner[1..];
                if (inner.EndsWith(' ')) inner = inner[..^1];
                var replaced = text[..lead] + inner + text[(tail + lang.BlockEnd.Length)..];
                doc.Replace(start, len, replaced);
                e.Select(start, replaced.Length);
            }
            else
            {
                var wrapped = lang.BlockStart + " " + text + " " + lang.BlockEnd;
                doc.Replace(start, len, wrapped);
                e.Select(start, wrapped.Length);
            }
        }
    }

    public static void Indent(CodeEditor e, bool outdent)
    {
        if (e.IsReadOnly) return;
        var doc = e.Document;
        var (first, last) = SelectedLines(e);
        var size = e.Options.IndentationSize;
        var unit = e.Options.IndentationString;
        using (doc.RunUpdate())
        {
            for (var n = last.LineNumber; n >= first.LineNumber; n--)
            {
                var l = doc.GetLineByNumber(n);
                if (!outdent)
                {
                    if (l.Length > 0 || first == last) doc.Insert(l.Offset, unit);
                    continue;
                }
                var ws = doc.GetText(TextUtilities.GetLeadingWhitespace(doc, l));
                if (ws.Length == 0) continue;
                var remove = ws[0] == '\t' ? 1 : Math.Min(size, ws.TakeWhile(c => c == ' ').Count());
                if (remove > 0) doc.Remove(l.Offset, remove);
            }
        }
    }

    // ───────────── text transforms (palette tools) ─────────────

    /// <summary>Applies <paramref name="transform"/> to the selection, or to the whole document when nothing is selected.</summary>
    public static bool Transform(CodeEditor e, Func<string, string> transform, bool wholeLines = false)
    {
        if (e.IsReadOnly) return false;
        var doc = e.Document;
        int start, length;
        var hadSelection = e.SelectionLength > 0;
        if (e.SelectionLength == 0)
        {
            start = 0;
            length = doc.TextLength;
        }
        else if (wholeLines)
        {
            var (first, last) = SelectedLines(e);
            start = first.Offset;
            length = last.EndOffset - first.Offset;
        }
        else
        {
            start = e.SelectionStart;
            length = e.SelectionLength;
        }
        var input = doc.GetText(start, length);
        var output = transform(input);
        if (output == input) return true;
        doc.Replace(start, length, output);
        if (hadSelection) e.Select(start, output.Length);
        return true;
    }

    private static string DominantNewLine(string s) => s.Contains("\r\n") ? "\r\n" : s.Contains('\n') ? "\n" : Environment.NewLine;

    public static string SortLines(string s, bool descending)
    {
        var nl = DominantNewLine(s);
        var lines = s.Split(nl);
        var cmp = StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
        var sorted = descending ? lines.OrderByDescending(l => l, cmp) : lines.OrderBy(l => l, cmp);
        return string.Join(nl, sorted);
    }

    public static string UniqueLines(string s)
    {
        var nl = DominantNewLine(s);
        return string.Join(nl, s.Split(nl).Distinct(StringComparer.Ordinal));
    }

    public static string TrimTrailing(string s)
    {
        var nl = DominantNewLine(s);
        return string.Join(nl, s.Split(nl).Select(l => l.TrimEnd(' ', '\t')));
    }

    public static string TitleCase(string s) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.ToLower(CultureInfo.CurrentCulture));

    public static string Base64Encode(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    public static string Base64Decode(string s) => Encoding.UTF8.GetString(Convert.FromBase64String(s.Trim()));

    public static string UrlEncode(string s) => Uri.EscapeDataString(s);

    public static string UrlDecode(string s) => WebUtility.UrlDecode(s);

    public static string JsonFormat(string s, bool minify)
    {
        using var doc = JsonDocument.Parse(s, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
        {
            WriteIndented = !minify,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            IndentSize = 2,
        });
    }

    public static string XmlFormat(string s, bool minify)
    {
        if (minify)
        {
            var clean = XDocument.Parse(s, LoadOptions.None);
            return (clean.Declaration is { } d ? d + "" : "") + clean.ToString(SaveOptions.DisableFormatting);
        }
        var formatted = XDocument.Parse(s, LoadOptions.None);
        var body = formatted.ToString(SaveOptions.None);
        return formatted.Declaration is { } decl ? decl + Environment.NewLine + body : body;
    }
}
