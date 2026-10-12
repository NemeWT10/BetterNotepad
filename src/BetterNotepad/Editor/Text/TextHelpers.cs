using ICSharpCode.AvalonEdit.Document;

namespace BetterNotepad.Editor.Text;

/// <summary>Word boundaries and small text queries shared by the editor features.</summary>
public static class TextHelpers
{
    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>The word touching <paramref name="offset"/> (prefers the word to the right), or null.</summary>
    public static (int Start, int End)? WordAt(TextDocument doc, int offset)
    {
        if (doc.TextLength == 0) return null;
        offset = Math.Clamp(offset, 0, doc.TextLength);
        var line = doc.GetLineByOffset(offset);
        var text = doc.GetText(line.Offset, line.Length);
        var col = offset - line.Offset;
        int s;
        if (col < text.Length && IsWordChar(text[col])) s = col;
        else if (col > 0 && IsWordChar(text[col - 1])) s = col - 1;
        else return null;
        var e = s;
        while (s > 0 && IsWordChar(text[s - 1])) s--;
        while (e < text.Length && IsWordChar(text[e])) e++;
        return (line.Offset + s, line.Offset + e);
    }

    /// <summary>True when text[start..start+length) is not glued to word characters on either side.</summary>
    public static bool IsWholeWord(string text, int start, int length)
        => (start == 0 || !IsWordChar(text[start - 1])) && (start + length >= text.Length || !IsWordChar(text[start + length]));

    public static bool IsWholeWord(TextDocument doc, int start, int length)
        => (start == 0 || !IsWordChar(doc.GetCharAt(start - 1))) && (start + length >= doc.TextLength || !IsWordChar(doc.GetCharAt(start + length)));

    /// <summary>Width of the leading whitespace in columns (tabs expanded to <paramref name="tabSize"/>), -1 for a blank line.</summary>
    public static int IndentColumns(string text, int start, int end, int tabSize)
    {
        var cols = 0;
        for (var i = start; i < end; i++)
        {
            var c = text[i];
            if (c == ' ') cols++;
            else if (c == '\t') cols += tabSize - cols % tabSize;
            else return cols;
        }
        return -1;
    }

    public static int IndentColumns(TextDocument doc, DocumentLine line, int tabSize)
    {
        var cols = 0;
        var end = line.EndOffset;
        for (var i = line.Offset; i < end; i++)
        {
            var c = doc.GetCharAt(i);
            if (c == ' ') cols++;
            else if (c == '\t') cols += tabSize - cols % tabSize;
            else return cols;
        }
        return -1;
    }
}
