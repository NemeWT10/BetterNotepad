using System.Globalization;
using System.Text;

namespace BetterNotepad.Hex;

/// <summary>What "copy as hex" produces.</summary>
public enum HexCopyFormat
{
    /// <summary>DE AD BE EF</summary>
    Spaced,

    /// <summary>DEADBEEF</summary>
    Compact,

    /// <summary>0xDE, 0xAD, 0xBE, 0xEF</summary>
    CArray,

    /// <summary>Offset, hex and text lines like the view shows them.</summary>
    Dump,
}

public static class HexFormat
{
    private const string Digits = "0123456789ABCDEF";

    /// <summary>Hex digits needed for an offset column: at least 8, grown by two for files over 4 GB.</summary>
    public static int OffsetDigits(long length)
    {
        var digits = 8;
        var max = Math.Max(0, length - 1);
        while (digits < 16 && (max >> (digits * 4)) != 0) digits += 2;
        return digits;
    }

    public static string FormatOffset(long offset, int digits)
    {
        Span<char> buf = stackalloc char[16];
        for (var i = digits - 1; i >= 0; i--)
        {
            buf[i] = Digits[(int)(offset & 0xF)];
            offset >>= 4;
        }

        return new string(buf[..digits]);
    }

    public static string ToHex(ReadOnlySpan<byte> bytes, HexCopyFormat format = HexCopyFormat.Spaced)
    {
        if (bytes.IsEmpty) return string.Empty;
        var sb = new StringBuilder(bytes.Length * 5);
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            switch (format)
            {
                case HexCopyFormat.Compact:
                    sb.Append(Digits[b >> 4]).Append(Digits[b & 0xF]);
                    break;
                case HexCopyFormat.CArray:
                    if (i > 0) sb.Append(", ");
                    sb.Append("0x").Append(Digits[b >> 4]).Append(Digits[b & 0xF]);
                    break;
                default:
                    if (i > 0) sb.Append(' ');
                    sb.Append(Digits[b >> 4]).Append(Digits[b & 0xF]);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Hex dump of <paramref name="bytes"/> that start at file offset <paramref name="startOffset"/>; rows follow the file's grid (a partial first row is padded).</summary>
    public static string ToDump(ReadOnlySpan<byte> bytes, long startOffset, int bytesPerRow, HexTextTable text, long fileLength)
    {
        if (bytes.IsEmpty) return string.Empty;
        var digits = OffsetDigits(fileLength);
        var sb = new StringBuilder();
        var first = startOffset / bytesPerRow * bytesPerRow;
        var end = startOffset + bytes.Length;
        for (var rowStart = first; rowStart < end; rowStart += bytesPerRow)
        {
            sb.Append(FormatOffset(rowStart, digits)).Append("  ");
            for (var i = 0; i < bytesPerRow; i++)
            {
                if (i > 0 && i % 8 == 0) sb.Append(' ');
                var o = rowStart + i;
                if (o >= startOffset && o < end)
                {
                    var b = bytes[(int)(o - startOffset)];
                    sb.Append(Digits[b >> 4]).Append(Digits[b & 0xF]);
                }
                else sb.Append("  ");

                sb.Append(' ');
            }

            sb.Append(' ');
            for (var i = 0; i < bytesPerRow; i++)
            {
                var o = rowStart + i;
                if (o >= startOffset && o < end) sb.Append(text.Map(bytes[(int)(o - startOffset)]));
                else sb.Append(' ');
            }

            if (rowStart + bytesPerRow < end) sb.Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>The text column's characters for a run of bytes (dots for the unprintable ones).</summary>
    public static string ToText(ReadOnlySpan<byte> bytes, HexTextTable text)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < chars.Length; i++) chars[i] = text.Map(bytes[i]);
        return new string(chars);
    }

    /// <summary>
    /// Parses a "go to" offset: <c>0x1F4</c>, <c>1F4h</c>, <c>$1F4</c> are hex; a plain number is hex when <paramref name="defaultHex"/> is set,
    /// else decimal. Spaces and underscores inside are ignored. Negative or non numeric input fails.
    /// </summary>
    public static bool TryParseOffset(string? text, bool defaultHex, out long offset)
    {
        offset = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().Replace(" ", "").Replace("_", "");
        var hex = defaultHex;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = true;
            s = s[2..];
        }
        else if (s.StartsWith('$'))
        {
            hex = true;
            s = s[1..];
        }
        else if (s.EndsWith('h') || s.EndsWith('H'))
        {
            hex = true;
            s = s[..^1];
        }

        if (s.Length == 0 || s.StartsWith('-') || s.StartsWith('+')) return false;
        return long.TryParse(s, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out offset) && offset >= 0;
    }
}

/// <summary>Cheap "is this file binary" test on the first bytes of a file, for hosts that choose between a text editor and the hex view.</summary>
public static class HexSniffer
{
    public static bool LooksBinary(ReadOnlySpan<byte> sample)
    {
        if (sample.IsEmpty) return false;
        if (sample.StartsWith(new byte[] { 0xFF, 0xFE }) || sample.StartsWith(new byte[] { 0xFE, 0xFF })) return false; // UTF-16 / UTF-32 BOM
        if (sample.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) sample = sample[3..];
        if (sample.IsEmpty) return false;

        int nul = 0, control = 0, nulOdd = 0, nulEven = 0;
        for (var i = 0; i < sample.Length; i++)
        {
            var b = sample[i];
            if (b == 0)
            {
                nul++;
                if ((i & 1) == 0) nulEven++;
                else nulOdd++;
            }
            else if (b < 0x20 && b is not (9 or 10 or 12 or 13 or 27)) control++;
        }

        if (nul > 0)
        {
            // UTF-16 text without a BOM: NULs sit almost only on one parity
            var half = sample.Length / 2;
            var utf16 = half >= 4 && (nulOdd >= half * 0.3 && nulEven <= half * 0.05 || nulEven >= half * 0.3 && nulOdd <= half * 0.05);
            return !utf16;
        }

        if (control > sample.Length * 0.05) return true;

        // no NULs and few controls: still binary when it is mostly high bytes that are not UTF-8 (compressed data, encrypted data)
        var high = 0;
        foreach (var b in sample) if (b >= 0x80) high++;
        if (high > sample.Length * 0.3)
        {
            var body = sample[..Math.Max(0, sample.Length - 3)]; // a cut-off multi-byte character at the end is not an error
            return !System.Text.Unicode.Utf8.IsValid(body);
        }

        return false;
    }
}
