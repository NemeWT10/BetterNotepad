namespace BetterNotepad.Hex;

public enum HexPane { Hex, Text }

/// <summary>
/// Column layout of one row in DIPs: offset | bytes in hex, grouped by 8 | text. Pure arithmetic (no WPF objects), used
/// for drawing and for hit testing, so both always agree.
/// </summary>
public readonly struct HexGeometry
{
    private const double Pad = 14;
    private const double Gutter = 2.5; // characters between the columns; the divider line sits in the middle

    public HexGeometry(double charWidth, double rowHeight, int bytesPerRow, long length)
    {
        CharWidth = charWidth;
        RowHeight = rowHeight;
        BytesPerRow = bytesPerRow;
        OffsetDigits = HexFormat.OffsetDigits(length);
        XOffset = Pad;
        XHex = XOffset + OffsetDigits * charWidth + Gutter * charWidth;
        XText = XHex + HexChars * charWidth + Gutter * charWidth;
        ContentWidth = XText + bytesPerRow * charWidth + Pad;
    }

    public double CharWidth { get; }

    public double RowHeight { get; }

    public int BytesPerRow { get; }

    public int OffsetDigits { get; }

    public double XOffset { get; }

    public double XHex { get; }

    public double XText { get; }

    /// <summary>Width of the three columns with their paddings (without a scroll bar).</summary>
    public double ContentWidth { get; }

    /// <summary>Characters of the hex column: three per byte (two digits and a space), one more between groups of 8, none trailing.</summary>
    public int HexChars => BytesPerRow * 3 - 1 + (BytesPerRow / 8 - 1);

    /// <summary>First character cell of byte <paramref name="i"/> inside the hex column.</summary>
    public static int HexCol(int i) => i * 3 + i / 8;

    public double HexX(int i) => XHex + HexCol(i) * CharWidth;

    public double TextX(int i) => XText + i * CharWidth;

    /// <summary>X of the divider between the offset and the hex column.</summary>
    public double Divider1 => XHex - Gutter / 2 * CharWidth;

    /// <summary>X of the divider between the hex and the text column.</summary>
    public double Divider2 => XText - Gutter / 2 * CharWidth;

    /// <summary>Horizontal extent of the highlight for bytes i..j (inclusive) of one row in the hex column.</summary>
    public (double Left, double Right) HexSpan(int i, int j)
        => (HexX(i) - 0.45 * CharWidth, HexX(j) + 2.45 * CharWidth);

    public (double Left, double Right) TextSpan(int i, int j)
        => (TextX(i) - 0.05 * CharWidth, TextX(j) + 1.05 * CharWidth);

    /// <summary>The pane under an x coordinate (the offset column and the area between the panes count for the nearer pane).</summary>
    public HexPane PaneAt(double x) => x >= Divider2 ? HexPane.Text : HexPane.Hex;

    /// <summary>Byte index (0..BytesPerRow-1) under x in the hex column; the gaps belong to the byte on their left.</summary>
    public int HitHex(double x)
    {
        var col = (int)Math.Floor((x - XHex) / CharWidth);
        if (col < 0) return 0;
        var group = col / 25; // 8 bytes * 3 + 1 extra space
        var within = col % 25;
        var i = group * 8 + Math.Min(7, within / 3);
        return Math.Clamp(i, 0, BytesPerRow - 1);
    }

    /// <summary>Byte index under x in the text column.</summary>
    public int HitText(double x) => Math.Clamp((int)Math.Floor((x - XText) / CharWidth), 0, BytesPerRow - 1);
}
