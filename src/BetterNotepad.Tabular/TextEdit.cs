using System.Text;

namespace BetterNotepad.Tabular;

/// <summary>
/// One replacement in the file text: remove <see cref="Length"/> characters at <see cref="Offset"/> and insert
/// <see cref="Replacement"/>. The table never touches the editor itself; the host applies edits to its buffer.
/// All edits returned by one call refer to the ORIGINAL text and do not overlap.
/// </summary>
public readonly record struct TextEdit(int Offset, int Length, string Replacement)
{
    /// <summary>First offset after the replaced range (in the text the edit was made against).</summary>
    public int End => Offset + Length;

    /// <summary>Change of the text length caused by the edit.</summary>
    public int Delta => Replacement.Length - Length;

    /// <summary>True for an edit that changes nothing (e.g. a cell set to the value it already had).</summary>
    public bool IsNoOp => Length == 0 && Replacement.Length == 0;

    public string ApplyTo(string text)
    {
        if (Offset < 0 || Length < 0 || Offset + Length > text.Length) throw new ArgumentOutOfRangeException(nameof(text));
        return string.Concat(text.AsSpan(0, Offset), Replacement, text.AsSpan(Offset + Length));
    }

    /// <summary>Applies non-overlapping edits that all refer to the same original text, in any order.</summary>
    public static string ApplyAll(string text, IEnumerable<TextEdit> edits)
    {
        var list = edits.Where(e => !e.IsNoOp).OrderBy(e => e.Offset).ToList();
        if (list.Count == 0) return text;
        var sb = new StringBuilder(text.Length + 64);
        var pos = 0;
        foreach (var e in list)
        {
            if (e.Offset < pos) throw new ArgumentException("Edits overlap.", nameof(edits));
            sb.Append(text, pos, e.Offset - pos).Append(e.Replacement);
            pos = e.End;
        }

        return sb.Append(text, pos, text.Length - pos).ToString();
    }
}
