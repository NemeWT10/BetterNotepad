using System.Globalization;
using System.Text;

namespace BetterNotepad.Hex;

/// <summary>How bytes are shown in the text column.</summary>
public enum HexTextEncoding
{
    /// <summary>Windows-1250 (Central European): Polish letters in the 0x80-0xFF range.</summary>
    Windows1250,

    /// <summary>ISO 8859-1.</summary>
    Latin1,

    /// <summary>Printable ASCII only.</summary>
    Ascii,
}

/// <summary>Byte to character table of the text column: printable characters as themselves, everything else is a dot.</summary>
public sealed class HexTextTable
{
    public const char Dot = '.';

    private static readonly Lazy<HexTextTable> Cp1250 = new(() => Build(HexTextEncoding.Windows1250));
    private static readonly Lazy<HexTextTable> Latin = new(() => Build(HexTextEncoding.Latin1));
    private static readonly Lazy<HexTextTable> AsciiOnly = new(() => Build(HexTextEncoding.Ascii));

    private readonly char[] _chars = new char[256];
    private readonly bool[] _printable = new bool[256];

    public static HexTextTable For(HexTextEncoding encoding) => encoding switch
    {
        HexTextEncoding.Latin1 => Latin.Value,
        HexTextEncoding.Ascii => AsciiOnly.Value,
        _ => Cp1250.Value,
    };

    /// <summary>The character shown for a byte (<see cref="Dot"/> when it is not printable).</summary>
    public char Map(byte b) => _chars[b];

    public bool IsPrintable(byte b) => _printable[b];

    private static HexTextTable Build(HexTextEncoding kind)
    {
        var t = new HexTextTable();
        Encoding? enc = null;
        if (kind == HexTextEncoding.Windows1250)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            enc = Encoding.GetEncoding(1250);
        }
        else if (kind == HexTextEncoding.Latin1) enc = Encoding.Latin1;

        for (var i = 0; i < 256; i++)
        {
            var c = Dot;
            var printable = false;
            if (i is >= 0x20 and <= 0x7E)
            {
                c = (char)i;
                printable = true;
            }
            else if (i >= 0x80 && enc is not null)
            {
                var s = enc.GetString([(byte)i]);
                if (s.Length == 1)
                {
                    var ch = s[0];
                    // controls, the replacement character, no-break and soft hyphen do not make a readable glyph in a fixed grid
                    var category = char.GetUnicodeCategory(ch);
                    if (ch != '�' && ch != ' ' && ch != '­' && category != UnicodeCategory.Control && category != UnicodeCategory.OtherNotAssigned)
                    {
                        c = ch;
                        printable = true;
                    }
                }
            }

            t._chars[i] = c;
            t._printable[i] = printable;
        }

        return t;
    }
}
