using System.Globalization;

namespace BetterNotepad.Tabular;

public enum CsvValueKind { Empty, Number, Date, Text }

/// <summary>How a lone "," or "." in a number is read.</summary>
public enum CsvNumberStyle
{
    /// <summary>A single separator is the decimal mark ("12,50" and "12.50" are both 12.5); several equal separators are thousands ("1.234.567").</summary>
    Auto,

    /// <summary>"." is the decimal mark, "," groups thousands ("1,234" = 1234).</summary>
    DotDecimal,

    /// <summary>"," is the decimal mark, "." groups thousands ("1.234" = 1234).</summary>
    CommaDecimal,
}

/// <summary>Order of the day and month in "a/b/yyyy" dates.</summary>
public enum CsvDateOrder { DayFirst, MonthFirst }

/// <summary>Culture independent recognition of numbers (Polish "12,50 zł", "1 234,5" as well as "1,234.5") and dates.</summary>
public static class CsvValue
{
    private static readonly string[] Units = ["%", "zł", "zl", "PLN", "EUR", "USD", "GBP", "CHF", "$", "€", "£"];
    private static readonly DateTime Epoch = new(1970, 1, 1);

    public static CsvValueKind Classify(ReadOnlySpan<char> s, CsvNumberStyle style = CsvNumberStyle.Auto)
    {
        s = Trim(s);
        if (s.Length == 0) return CsvValueKind.Empty;
        if (TryParseNumber(s, style, out _)) return CsvValueKind.Number;
        if (TryParseDate(s, CsvDateOrder.DayFirst, out _)) return CsvValueKind.Date;
        return CsvValueKind.Text;
    }

    internal static ReadOnlySpan<char> Trim(ReadOnlySpan<char> s)
    {
        var a = 0;
        var b = s.Length;
        while (a < b && IsSpace(s[a])) a++;
        while (b > a && IsSpace(s[b - 1])) b--;
        return s[a..b];
    }

    private static bool IsSpace(char c) => char.IsWhiteSpace(c) || c == ' ' || c == ' ';

    // ------------------------------------------------------------------ numbers

    public static bool TryParseNumber(ReadOnlySpan<char> s, CsvNumberStyle style, out double value)
    {
        value = 0;
        s = Trim(s);
        if (s.Length == 0 || s.Length > 48) return false;

        // unit suffix / prefix (%, zł, PLN, $ ...)
        s = StripUnit(s, suffix: true);
        var negative = false;
        var signed = false;
        s = StripSign(s, ref negative, ref signed);
        s = StripUnit(s, suffix: false);
        if (!signed) s = StripSign(s, ref negative, ref signed); // "$-5" as well as "-$5"
        if (s.Length == 0) return false;

        // optional exponent
        var exp = ReadOnlySpan<char>.Empty;
        var e = s.IndexOfAny('e', 'E');
        if (e >= 0)
        {
            exp = s[(e + 1)..];
            s = s[..e];
            if (exp.Length == 0 || exp.Length > 4) return false;
            var k = exp[0] is '+' or '-' ? 1 : 0;
            if (k == exp.Length) return false;
            for (var x = k; x < exp.Length; x++) if (exp[x] is < '0' or > '9') return false;
        }

        Span<char> digits = stackalloc char[48];
        Span<int> dots = stackalloc int[8];
        Span<int> commas = stackalloc int[8];
        Span<int> groups = stackalloc int[16];
        int nd = 0, nDots = 0, nCommas = 0, nGroups = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is >= '0' and <= '9') digits[nd++] = c;
            else if (c == '.')
            {
                if (nDots == dots.Length) return false;
                dots[nDots++] = nd;
            }
            else if (c == ',')
            {
                if (nCommas == commas.Length) return false;
                commas[nCommas++] = nd;
            }
            else if (c is ' ' or ' ' or ' ' or '\'' or '’')
            {
                if (nd == 0 || i + 1 >= s.Length || s[i + 1] is < '0' or > '9') return false;
                if (nGroups == groups.Length) return false;
                groups[nGroups++] = nd;
            }
            else return false;
        }

        if (nd == 0) return false;

        // roles of the separators: at most one decimal mark, the rest group thousands
        var dec = -1; // position (count of digits before it) of the decimal mark
        Span<int> thousands = stackalloc int[32];
        var nt = 0;
        for (var i = 0; i < nGroups; i++) thousands[nt++] = groups[i];

        var decimalIsDot = false;
        var hasDecimal = false;
        if (nDots > 0 && nCommas > 0)
        {
            decimalIsDot = dots[nDots - 1] > commas[nCommas - 1];
            hasDecimal = true;
        }
        else if (nDots > 0 || nCommas > 0)
        {
            var isDot = nDots > 0;
            var count = isDot ? nDots : nCommas;
            if (count > 1) hasDecimal = false;
            else
            {
                hasDecimal = style switch
                {
                    CsvNumberStyle.DotDecimal => isDot,
                    CsvNumberStyle.CommaDecimal => !isDot,
                    _ => true,
                };
                decimalIsDot = isDot;
            }
        }

        for (var i = 0; i < nDots; i++)
        {
            if (hasDecimal && decimalIsDot && i == nDots - 1) dec = dots[i];
            else if (nt < thousands.Length) thousands[nt++] = dots[i];
            else return false;
        }

        for (var i = 0; i < nCommas; i++)
        {
            if (hasDecimal && !decimalIsDot && i == nCommas - 1) dec = commas[i];
            else if (nt < thousands.Length) thousands[nt++] = commas[i];
            else return false;
        }

        var intDigits = dec >= 0 ? dec : nd;
        if (dec >= 0 && nd - dec == 0) return false; // "12." has no fraction

        if (nt > 0)
        {
            var t = thousands[..nt];
            t.Sort();
            if (t[0] is < 1 or > 3) return false;
            for (var i = 1; i < nt; i++) if (t[i] - t[i - 1] != 3) return false;
            if (intDigits - t[nt - 1] != 3) return false;
        }

        // normalized text -> double
        Span<char> norm = stackalloc char[nd + 12];
        var n = 0;
        if (negative) norm[n++] = '-';
        if (intDigits == 0) norm[n++] = '0';
        for (var i = 0; i < nd; i++)
        {
            if (i == dec) norm[n++] = '.';
            norm[n++] = digits[i];
        }

        if (exp.Length > 0)
        {
            norm[n++] = 'E';
            exp.CopyTo(norm[n..]);
            n += exp.Length;
        }

        return double.TryParse(norm[..n], NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static ReadOnlySpan<char> StripSign(ReadOnlySpan<char> s, scoped ref bool negative, scoped ref bool signed)
    {
        if (s.Length > 0 && s[0] is '+' or '-' or '−')
        {
            negative = s[0] != '+';
            signed = true;
            return Trim(s[1..]);
        }

        return s;
    }

    private static ReadOnlySpan<char> StripUnit(ReadOnlySpan<char> s, bool suffix)
    {
        foreach (var u in Units)
        {
            if (s.Length <= u.Length) continue;
            if (suffix && s.EndsWith(u, StringComparison.OrdinalIgnoreCase)) return Trim(s[..^u.Length]);
            if (!suffix && s.StartsWith(u, StringComparison.OrdinalIgnoreCase) && u.Length == 1 && !char.IsLetter(u[0])) return Trim(s[u.Length..]);
        }

        return s;
    }

    // ------------------------------------------------------------------ dates

    /// <summary>
    /// ISO "2026-10-12", "2026-10-12 14:30:05", "2026-10-12T14:30Z", "12.10.2026", "12-10-2026" and "12/10/2026"
    /// (order per <paramref name="order"/>). The value is days since 1970-01-01 (fraction = time of day); time zones are ignored.
    /// </summary>
    public static bool TryParseDate(ReadOnlySpan<char> s, CsvDateOrder order, out double days)
    {
        days = 0;
        s = Trim(s);
        if (s.Length < 8 || s.Length > 40) return false;

        var p = 0;
        if (!ReadInt(s, ref p, 4, out var n1, out var d1)) return false;
        if (p >= s.Length || s[p] is not ('-' or '.' or '/')) return false;
        var sep = s[p++];
        if (!ReadInt(s, ref p, 2, out var n2, out var d2)) return false;
        if (p >= s.Length || s[p] != sep) return false;
        p++;
        if (!ReadInt(s, ref p, 4, out var n3, out var d3)) return false;

        int y, m, d;
        if (d1 == 4 && d2 <= 2 && d3 <= 2)
        {
            y = n1;
            m = n2;
            d = n3;
        }
        else if (d3 == 4 && d1 <= 2 && d2 <= 2)
        {
            y = n3;
            if (sep == '/' && order == CsvDateOrder.MonthFirst)
            {
                m = n1;
                d = n2;
            }
            else
            {
                d = n1;
                m = n2;
            }
        }
        else return false;

        if (y < 1 || y > 9999 || m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;

        var fraction = 0.0;
        if (p < s.Length)
        {
            if (s[p] is not (' ' or 'T')) return false;
            p++;
            if (!ReadInt(s, ref p, 2, out var hh, out _) || p >= s.Length || s[p] != ':') return false;
            p++;
            if (!ReadInt(s, ref p, 2, out var mm, out _)) return false;
            var ss = 0;
            var frac = 0.0;
            if (p < s.Length && s[p] == ':')
            {
                p++;
                if (!ReadInt(s, ref p, 2, out ss, out _)) return false;
                if (p < s.Length && s[p] is '.' or ',')
                {
                    p++;
                    var scale = 0.1;
                    var any = false;
                    while (p < s.Length && s[p] is >= '0' and <= '9')
                    {
                        frac += (s[p] - '0') * scale;
                        scale /= 10;
                        p++;
                        any = true;
                    }

                    if (!any) return false;
                }
            }

            if (hh > 23 || mm > 59 || ss > 59) return false;
            fraction = (hh * 3600 + mm * 60 + ss + frac) / 86400.0;

            // optional zone (ignored): Z, +hh:mm, -hhmm
            if (p < s.Length && s[p] == 'Z') p++;
            else if (p < s.Length && s[p] is '+' or '-')
            {
                p++;
                var z = 0;
                while (p < s.Length && (s[p] is >= '0' and <= '9' || s[p] == ':') && z < 5)
                {
                    p++;
                    z++;
                }
            }

            if (p != s.Length) return false;
        }

        days = (new DateTime(y, m, d) - Epoch).TotalDays + fraction;
        return true;
    }

    private static bool ReadInt(ReadOnlySpan<char> s, ref int p, int maxDigits, out int value, out int digits)
    {
        value = 0;
        digits = 0;
        while (p < s.Length && s[p] is >= '0' and <= '9' && digits < maxDigits)
        {
            value = value * 10 + (s[p] - '0');
            p++;
            digits++;
        }

        return digits > 0;
    }

    /// <summary>Picks month-first only when the values prove it (a second component above 12 and no first component above 12).</summary>
    public static CsvDateOrder DetectSlashOrder(IEnumerable<string> values)
    {
        var firstOver12 = false;
        var secondOver12 = false;
        foreach (var v in values)
        {
            if (string.IsNullOrEmpty(v)) continue;
            var s = Trim(v);
            var a = s.IndexOf('/');
            if (a < 1) continue;
            var b = s[(a + 1)..].IndexOf('/');
            if (b < 1) continue;
            if (int.TryParse(s[..a], out var x) && x > 12) firstOver12 = true;
            if (int.TryParse(s.Slice(a + 1, b), out var y) && y > 12) secondOver12 = true;
        }

        return secondOver12 && !firstOver12 ? CsvDateOrder.MonthFirst : CsvDateOrder.DayFirst;
    }
}

/// <summary>
/// Natural ordering: digit runs compare by numeric value ("item2" &lt; "item10"), the rest case-insensitively.
/// Ordinal for plain ASCII text; culture aware (Polish ą ć ę ł ń ó ś ź ż) when <paramref name="culture"/> is set.
/// </summary>
public sealed class NaturalComparer(CompareInfo? culture) : IComparer<string?>
{
    public static NaturalComparer Ordinal { get; } = new(null);

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        return Compare(x.AsSpan(), y.AsSpan());
    }

    public int Compare(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            var da = IsDigit(a[i]);
            var db = IsDigit(b[j]);
            if (da && db)
            {
                var si = i;
                var sj = j;
                while (i < a.Length && IsDigit(a[i])) i++;
                while (j < b.Length && IsDigit(b[j])) j++;
                var ra = a[si..i].TrimStart('0');
                var rb = b[sj..j].TrimStart('0');
                if (ra.Length != rb.Length) return ra.Length < rb.Length ? -1 : 1;
                var c = ra.CompareTo(rb, StringComparison.Ordinal);
                if (c != 0) return c < 0 ? -1 : 1;
                // equal value: fewer leading zeros first
                var za = i - si;
                var zb = j - sj;
                if (za != zb) return za < zb ? -1 : 1;
                continue;
            }

            var ei = i;
            var ej = j;
            while (ei < a.Length && !IsDigit(a[ei])) ei++;
            while (ej < b.Length && !IsDigit(b[ej])) ej++;
            var sa = a[i..ei];
            var sb = b[j..ej];
            var r = culture is null ? sa.CompareTo(sb, StringComparison.OrdinalIgnoreCase) : culture.Compare(sa, sb, CompareOptions.IgnoreCase);
            if (r != 0) return r < 0 ? -1 : 1;
            i = ei;
            j = ej;
        }

        var left = a.Length - i;
        var right = b.Length - j;
        if (left != right) return left < right ? -1 : 1;
        // equal ignoring case (the sort adds the original row order as the final tiebreak)
        return 0;
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';
}
