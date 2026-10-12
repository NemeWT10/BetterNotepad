using System.Globalization;
using System.Text;

namespace BetterNotepad.Metadata;

internal sealed class PdfFormatException : Exception
{
    public PdfFormatException(string message) : base(message) { }
}

/// <summary>PDF value with the source range [S, E) it was parsed from.</summary>
internal abstract class PV { public int S, E; }
internal sealed class PNull : PV { }
internal sealed class PBool : PV { public bool V; }
internal sealed class PNum : PV { public double D; public bool IsInt; public long L; }
internal sealed class PStr : PV { public byte[] B = Array.Empty<byte>(); }
internal sealed class PName : PV { public string N = ""; }
internal sealed class PArr : PV { public List<PV> Items = new(); }
internal sealed class PRef : PV { public int Num, Gen; }
internal readonly record struct PEntry(string Key, PV Value, int KeyStart);

internal sealed class PDict : PV
{
    public List<PEntry> Entries = new();
    public PV? Get(string key)
    {
        for (int i = Entries.Count - 1; i >= 0; i--) if (Entries[i].Key == key) return Entries[i].Value;
        return null;
    }
    public bool Has(string key) => Get(key) != null;
    public string? NameOf(string key) => (Get(key) as PName)?.N;
}

internal ref struct PdfLexer
{
    private readonly ReadOnlySpan<byte> _d;
    public int Pos;

    public PdfLexer(ReadOnlySpan<byte> data, int pos) { _d = data; Pos = pos; }

    public readonly bool AtEnd => Pos >= _d.Length;
    public static bool IsWhite(byte b) => b == 0 || b == 9 || b == 10 || b == 12 || b == 13 || b == 32;
    public static bool IsDelim(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
    private static bool IsRegular(byte b) => !IsWhite(b) && !IsDelim(b);

    public void SkipWs()
    {
        while (Pos < _d.Length)
        {
            byte b = _d[Pos];
            if (IsWhite(b)) Pos++;
            else if (b == (byte)'%') { while (Pos < _d.Length && _d[Pos] != 10 && _d[Pos] != 13) Pos++; }
            else break;
        }
    }

    /// <summary>Consumes the keyword if it is next (followed by a delimiter / white space / end).</summary>
    public bool TryKeyword(string kw)
    {
        SkipWs();
        if (Pos + kw.Length > _d.Length) return false;
        for (int i = 0; i < kw.Length; i++) if (_d[Pos + i] != (byte)kw[i]) return false;
        int e = Pos + kw.Length;
        if (e < _d.Length && IsRegular(_d[e])) return false;
        Pos = e;
        return true;
    }

    public bool PeekIs(string s)
    {
        SkipWs();
        if (Pos + s.Length > _d.Length) return false;
        for (int i = 0; i < s.Length; i++) if (_d[Pos + i] != (byte)s[i]) return false;
        return true;
    }

    public long ReadInt()
    {
        SkipWs();
        int s = Pos;
        bool neg = false;
        if (Pos < _d.Length && (_d[Pos] == '+' || _d[Pos] == '-')) { neg = _d[Pos] == '-'; Pos++; }
        long v = 0; int digits = 0;
        while (Pos < _d.Length && _d[Pos] >= '0' && _d[Pos] <= '9')
        {
            v = checked(v * 10 + (_d[Pos] - '0'));
            if (++digits > 18) throw new PdfFormatException("Integer too long");
            Pos++;
        }
        if (digits == 0) { Pos = s; throw new PdfFormatException("Integer expected"); }
        return neg ? -v : v;
    }

    public PV ParseValue(int depth = 0)
    {
        if (depth > 128) throw new PdfFormatException("Nesting too deep");
        SkipWs();
        if (Pos >= _d.Length) throw new PdfFormatException("Unexpected end");
        int start = Pos;
        byte c = _d[Pos];
        PV v;
        switch (c)
        {
            case (byte)'/':
                v = new PName { N = ReadName() };
                break;
            case (byte)'(':
                v = new PStr { B = ReadLiteralString() };
                break;
            case (byte)'<':
                if (Pos + 1 < _d.Length && _d[Pos + 1] == '<') v = ParseDict(depth);
                else v = new PStr { B = ReadHexString() };
                break;
            case (byte)'[':
            {
                Pos++;
                var a = new PArr();
                while (true)
                {
                    SkipWs();
                    if (Pos >= _d.Length) throw new PdfFormatException("Unterminated array");
                    if (_d[Pos] == ']') { Pos++; break; }
                    a.Items.Add(ParseValue(depth + 1));
                }
                v = a;
                break;
            }
            default:
                if (c == '+' || c == '-' || c == '.' || (c >= '0' && c <= '9')) { v = ParseNumberOrRef(); break; }
                if (TryKeyword("true")) { v = new PBool { V = true }; break; }
                if (TryKeyword("false")) { v = new PBool { V = false }; break; }
                if (TryKeyword("null")) { v = new PNull(); break; }
                throw new PdfFormatException("Unexpected token at " + Pos);
        }
        v.S = start;
        v.E = Pos;
        return v;
    }

    private PDict ParseDict(int depth)
    {
        int s = Pos;
        Pos += 2;
        var d = new PDict();
        while (true)
        {
            SkipWs();
            if (Pos >= _d.Length) throw new PdfFormatException("Unterminated dictionary");
            if (_d[Pos] == '>' && Pos + 1 < _d.Length && _d[Pos + 1] == '>') { Pos += 2; break; }
            if (_d[Pos] != '/') throw new PdfFormatException("Dictionary key expected at " + Pos);
            int keyStart = Pos;
            string key = ReadName();
            SkipWs();
            if (Pos < _d.Length && _d[Pos] == '>' && Pos + 1 < _d.Length && _d[Pos + 1] == '>')
            {
                d.Entries.Add(new PEntry(key, new PNull { S = Pos, E = Pos }, keyStart));
                Pos += 2; break;
            }
            var val = ParseValue(depth + 1);
            d.Entries.Add(new PEntry(key, val, keyStart));
        }
        d.S = s; d.E = Pos;
        return d;
    }

    private PV ParseNumberOrRef()
    {
        int s = Pos;
        bool neg = false;
        while (Pos < _d.Length && (_d[Pos] == '+' || _d[Pos] == '-')) { neg |= _d[Pos] == '-'; Pos++; }
        long ip = 0; double frac = 0, scale = 0.1; bool isReal = false; int digits = 0;
        while (Pos < _d.Length && _d[Pos] >= '0' && _d[Pos] <= '9')
        {
            if (digits < 18) ip = ip * 10 + (_d[Pos] - '0');
            digits++; Pos++;
        }
        if (Pos < _d.Length && _d[Pos] == '.')
        {
            isReal = true; Pos++;
            while (Pos < _d.Length && _d[Pos] >= '0' && _d[Pos] <= '9') { frac += (_d[Pos] - '0') * scale; scale /= 10; Pos++; digits++; }
        }
        if (digits == 0) throw new PdfFormatException("Bad number at " + s);
        if (!isReal && !neg && digits <= 10 && Pos < _d.Length && IsWhite(_d[Pos]))
        {
            // indirect reference?  <int> <int> R
            int save = Pos;
            SkipWs();
            int g0 = Pos;
            long gen = 0; int gd = 0;
            while (Pos < _d.Length && _d[Pos] >= '0' && _d[Pos] <= '9' && gd < 6) { gen = gen * 10 + (_d[Pos] - '0'); Pos++; gd++; }
            if (gd > 0 && Pos < _d.Length && IsWhite(_d[Pos]))
            {
                SkipWs();
                if (Pos < _d.Length && _d[Pos] == 'R' && (Pos + 1 >= _d.Length || !IsRegular(_d[Pos + 1])))
                {
                    Pos++;
                    return new PRef { Num = (int)ip, Gen = (int)gen };
                }
            }
            Pos = save;
        }
        double dv = ip + frac;
        return new PNum { D = neg ? -dv : dv, IsInt = !isReal, L = neg ? -ip : ip };
    }

    private string ReadName()
    {
        Pos++;   // '/'
        var sb = new StringBuilder();
        while (Pos < _d.Length && IsRegular(_d[Pos]))
        {
            byte b = _d[Pos];
            if (b == '#' && Pos + 2 < _d.Length && Hex(_d[Pos + 1]) >= 0 && Hex(_d[Pos + 2]) >= 0)
            {
                sb.Append((char)(Hex(_d[Pos + 1]) << 4 | Hex(_d[Pos + 2])));
                Pos += 3;
            }
            else { sb.Append((char)b); Pos++; }
        }
        return sb.ToString();
    }

    private static int Hex(byte b) => b >= '0' && b <= '9' ? b - '0' : b >= 'a' && b <= 'f' ? b - 'a' + 10 : b >= 'A' && b <= 'F' ? b - 'A' + 10 : -1;

    private byte[] ReadHexString()
    {
        Pos++;   // '<'
        var list = new List<byte>();
        int hi = -1;
        while (Pos < _d.Length && _d[Pos] != '>')
        {
            int h = Hex(_d[Pos++]);
            if (h < 0) continue;
            if (hi < 0) hi = h; else { list.Add((byte)(hi << 4 | h)); hi = -1; }
        }
        if (hi >= 0) list.Add((byte)(hi << 4));
        if (Pos < _d.Length) Pos++;
        return list.ToArray();
    }

    private byte[] ReadLiteralString()
    {
        Pos++;   // '('
        var list = new List<byte>();
        int depth = 1;
        while (Pos < _d.Length)
        {
            byte b = _d[Pos++];
            if (b == '\\')
            {
                if (Pos >= _d.Length) break;
                byte n = _d[Pos++];
                switch (n)
                {
                    case (byte)'n': list.Add(10); break;
                    case (byte)'r': list.Add(13); break;
                    case (byte)'t': list.Add(9); break;
                    case (byte)'b': list.Add(8); break;
                    case (byte)'f': list.Add(12); break;
                    case 13: if (Pos < _d.Length && _d[Pos] == 10) Pos++; break;
                    case 10: break;
                    default:
                        if (n >= '0' && n <= '7')
                        {
                            int v = n - '0';
                            for (int i = 0; i < 2 && Pos < _d.Length && _d[Pos] >= '0' && _d[Pos] <= '7'; i++) v = v * 8 + (_d[Pos++] - '0');
                            list.Add((byte)v);
                        }
                        else list.Add(n);
                        break;
                }
            }
            else if (b == '(') { depth++; list.Add(b); }
            else if (b == ')') { if (--depth == 0) return list.ToArray(); list.Add(b); }
            else if (b == 13) { list.Add(10); if (Pos < _d.Length && _d[Pos] == 10) Pos++; }
            else list.Add(b);
        }
        return list.ToArray();
    }
}

/// <summary>PDF text-string / date helpers.</summary>
internal static class PdfText
{
    private static readonly char[] DocEnc80 =
    {
        '•', '†', '‡', '…', '—', '–', 'ƒ', '⁄', '‹', '›', '−', '‰', '„', '“', '”', '‘',
        '’', '‚', '™', 'ﬁ', 'ﬂ', 'Ł', 'Œ', 'Š', 'Ÿ', 'Ž', 'ı', 'ł', 'œ', 'š', 'ž', '�',
        '€',
    };
    private static readonly char[] DocEnc18 = { '˘', 'ˇ', 'ˆ', '˙', '˝', '˛', '˚', '˜' };

    public static string Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b.Slice(2));
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b.Slice(2));
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b.Slice(3));
        var sb = new StringBuilder(b.Length);
        foreach (byte x in b)
        {
            if (x >= 0x18 && x <= 0x1F) sb.Append(DocEnc18[x - 0x18]);
            else if (x >= 0x80 && x <= 0xA0) sb.Append(DocEnc80[x - 0x80]);
            else sb.Append((char)x);
        }
        return sb.ToString();
    }

    /// <summary>Serialises a text string: literal for plain ASCII, UTF-16BE hex string otherwise.</summary>
    public static string Serialize(string s)
    {
        bool ascii = true;
        foreach (char c in s) if (c < 0x20 || c > 0x7E) { ascii = false; break; }
        if (ascii)
        {
            var sb = new StringBuilder("(");
            foreach (char c in s)
            {
                if (c == '(' || c == ')' || c == '\\') sb.Append('\\');
                sb.Append(c);
            }
            return sb.Append(')').ToString();
        }
        var bytes = Encoding.BigEndianUnicode.GetBytes(s);
        return "<FEFF" + Convert.ToHexString(bytes) + ">";
    }

    public static DateTimeOffset? ParseDate(string s)
    {
        s = s.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal)) s = s.Substring(2);
        int p = 0;
        int Num(int len, int def)
        {
            if (p + len > s.Length) return def;
            for (int i = 0; i < len; i++) if (!char.IsAsciiDigit(s[p + i])) return def;
            int v = int.Parse(s.AsSpan(p, len), NumberStyles.None, CultureInfo.InvariantCulture);
            p += len;
            return v;
        }
        if (s.Length < 4) return null;
        int y = Num(4, -1); if (y < 0) return null;
        int mo = Num(2, 1), d = Num(2, 1), h = Num(2, 0), mi = Num(2, 0), se = Num(2, 0);
        TimeSpan? off = null;
        if (p < s.Length)
        {
            char o = s[p];
            if (o == 'Z') off = TimeSpan.Zero;
            else if (o == '+' || o == '-')
            {
                p++;
                int oh = Num(2, 0);
                if (p < s.Length && s[p] == '\'') p++;
                int om = Num(2, 0);
                off = new TimeSpan(oh, om, 0);
                if (o == '-') off = -off;
            }
        }
        try
        {
            var dt = new DateTime(y, mo, d, Math.Min(h, 23), Math.Min(mi, 59), Math.Min(se, 59), DateTimeKind.Unspecified);
            return new DateTimeOffset(dt, off ?? TimeZoneInfo.Local.GetUtcOffset(dt));
        }
        catch (ArgumentException) { return null; }
    }

    public static string FormatDate(DateTimeOffset d)
    {
        var o = d.Offset;
        string tz = o == TimeSpan.Zero ? "Z" : (o < TimeSpan.Zero ? "-" : "+") + Math.Abs(o.Hours).ToString("00") + "'" + Math.Abs(o.Minutes).ToString("00") + "'";
        return "D:" + d.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + tz;
    }
}
