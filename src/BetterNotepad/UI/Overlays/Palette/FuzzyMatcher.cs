using System.Globalization;
using System.Text;

namespace BetterNotepad.UI.Palette;

/// <summary>A successful fuzzy match: score (higher is better) and the matched character positions.</summary>
public readonly record struct FuzzyMatch(double Score, int[] Positions);

/// <summary>
/// Fuzzy matching in the spirit of fzy: every query character must appear in order; contiguous runs and
/// characters at word starts (after a space, _ - . / \ or a camelCase hump, and the very first character)
/// score higher, gaps cost a little. Case and Polish diacritics are ignored ("zamien" finds "Zamień").
/// A query with spaces also matches when every word matches on its own ("as save" → "Save as…").
/// </summary>
public static class FuzzyMatcher
{
    private const double Min = double.NegativeInfinity;
    private const double GapLeading = -0.005;
    private const double GapTrailing = -0.005;
    private const double GapInner = -0.01;
    private const double Consecutive = 1.0;
    private const double BonusSlash = 0.9;
    private const double BonusWord = 0.8;
    private const double BonusCapital = 0.7;
    private const double BonusDot = 0.6;
    private const int MaxTarget = 256;

    [ThreadStatic] private static double[]? _d;
    [ThreadStatic] private static double[]? _m;

    /// <summary>Case- and accent-insensitive form of one character (1:1, so positions stay valid).</summary>
    public static char Fold(char c)
    {
        if (c < 128) return c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        if (c < FoldTable.Length) return FoldTable[c];
        return char.ToLowerInvariant(c);
    }

    private static readonly char[] FoldTable = BuildFoldTable();

    private static char[] BuildFoldTable()
    {
        var t = new char[0x250];
        for (var i = 0; i < t.Length; i++)
        {
            var c = char.ToLowerInvariant((char)i);
            var d = c.ToString().Normalize(NormalizationForm.FormD);
            if (d.Length > 0 && CharUnicodeInfo.GetUnicodeCategory(d[0]) != UnicodeCategory.NonSpacingMark) c = d[0];
            t[i] = c;
        }
        t['ł'] = 'l';
        t['Ł'] = 'l';
        t['ß'] = 's';
        t['đ'] = 'd';
        t['Đ'] = 'd';
        return t;
    }

    /// <summary>
    /// Matches <paramref name="query"/> against <paramref name="target"/>; null when it does not match or when a
    /// word of the query scores below <paramref name="minPerChar"/> per character (scattered letters).
    /// </summary>
    public static FuzzyMatch? Match(string query, string? target, double minPerChar = double.NegativeInfinity)
    {
        if (string.IsNullOrEmpty(target)) return null;
        query = query.Trim();
        if (query.Length == 0) return new FuzzyMatch(0, []);

        var whole = MatchTerm(query, target, minPerChar);
        if (whole is not null || query.IndexOf(' ') < 0) return whole;

        // several words: each must match somewhere (any order)
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double score = 0;
        var positions = new SortedSet<int>();
        foreach (var term in terms)
        {
            var m = MatchTerm(term, target, minPerChar);
            if (m is null) return null;
            score += m.Value.Score;
            foreach (var p in m.Value.Positions) positions.Add(p);
        }
        // a little below an in-order match of the whole query
        return new FuzzyMatch(score - 0.5, positions.ToArray());
    }

    private static FuzzyMatch? MatchTerm(string query, string target, double minPerChar)
    {
        var n = query.Length;
        var m = Math.Min(target.Length, MaxTarget);
        if (n > m) return null;

        // quick subsequence check before the quadratic part
        var qi = 0;
        for (var j = 0; j < m && qi < n; j++)
            if (Fold(query[qi]) == Fold(target[j])) qi++;
        if (qi < n) return null;

        if (n == m)
        {
            var all = new int[n];
            for (var i = 0; i < n; i++) all[i] = i;
            return new FuzzyMatch(1000, all);
        }

        var size = n * m;
        if (_d is null || _d.Length < size)
        {
            _d = new double[Math.Max(size, 1024)];
            _m = new double[Math.Max(size, 1024)];
        }
        var D = _d;
        var M = _m!;

        Span<double> bonus = m <= 512 ? stackalloc double[m] : new double[m];
        var prev = '/';
        for (var j = 0; j < m; j++)
        {
            var c = target[j];
            bonus[j] = Bonus(prev, c);
            prev = c;
        }

        for (var i = 0; i < n; i++)
        {
            var q = Fold(query[i]);
            var prevScore = Min;
            var gap = i == n - 1 ? GapTrailing : GapInner;
            var row = i * m;
            for (var j = 0; j < m; j++)
            {
                if (q == Fold(target[j]))
                {
                    var score = Min;
                    if (i == 0) score = j * GapLeading + bonus[j];
                    else if (j > 0)
                    {
                        var up = row - m + j - 1;
                        score = Math.Max(M[up] + bonus[j], D[up] + Consecutive);
                    }
                    D[row + j] = score;
                    M[row + j] = prevScore = Math.Max(score, prevScore + gap);
                }
                else
                {
                    D[row + j] = Min;
                    M[row + j] = prevScore += gap;
                }
            }
        }

        // backtrack the best positions
        var positions = new int[n];
        var matchRequired = false;
        var col = m - 1;
        for (var i = n - 1; i >= 0; i--)
        {
            var row = i * m;
            for (; col >= 0; col--)
            {
                var d = D[row + col];
                if (d != Min && (matchRequired || d == M[row + col]))
                {
                    matchRequired = i > 0 && col > 0 && M[row + col] == D[row - m + col - 1] + Consecutive;
                    positions[i] = col--;
                    break;
                }
            }
        }

        var total = M[(n - 1) * m + m - 1];
        // prefix and exact-word starts read as "what I meant"
        if (positions[0] == 0 && IsContiguous(positions)) total += 1.5;
        else if (IsContiguous(positions) && bonus[positions[0]] > 0) total += 0.75;
        return total / n < minPerChar ? null : new FuzzyMatch(total, positions);
    }

    private static bool IsContiguous(int[] p)
    {
        for (var i = 1; i < p.Length; i++)
            if (p[i] != p[i - 1] + 1) return false;
        return true;
    }

    private static double Bonus(char prev, char c)
    {
        if (!char.IsLetterOrDigit(c)) return 0;
        return prev switch
        {
            '/' or '\\' => BonusSlash,
            '-' or '_' or ' ' or ':' or '(' or '[' or '"' or '\'' => BonusWord,
            '.' => BonusDot,
            _ when char.IsLower(prev) && char.IsUpper(c) => BonusCapital,
            _ when !char.IsLetterOrDigit(prev) => BonusWord,
            _ => 0,
        };
    }
}
