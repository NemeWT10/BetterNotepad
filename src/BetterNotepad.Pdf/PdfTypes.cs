using System.Globalization;
using System.Windows;

namespace BetterNotepad.Pdf;

/// <summary>Page size in PDF points (1/72 inch), already including the page /Rotate.</summary>
public readonly record struct PdfPageSize(double Width, double Height);

/// <summary>
/// Rectangle in page space: points, origin at the TOP-LEFT of the page as displayed (y grows downwards),
/// i.e. multiply by the render scale to get bitmap pixels. Rotation / crop box are already applied.
/// </summary>
public readonly record struct PdfRect(float Left, float Top, float Right, float Bottom)
{
    public static readonly PdfRect Empty = new(0, 0, 0, 0);
    public float Width => Right - Left;
    public float Height => Bottom - Top;
    public bool IsEmpty => !(Right > Left && Bottom > Top);
    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
    public PdfRect Union(PdfRect o) => IsEmpty ? o : o.IsEmpty ? this : new(Math.Min(Left, o.Left), Math.Min(Top, o.Top), Math.Max(Right, o.Right), Math.Max(Bottom, o.Bottom));
    public PdfRect Inflate(float d) => new(Left - d, Top - d, Right + d, Bottom + d);

    /// <summary>WPF rectangle scaled by <paramref name="scale"/> (pixels per point).</summary>
    public Rect ToRect(double scale = 1.0) => new(Left * scale, Top * scale, Math.Max(0, Width) * scale, Math.Max(0, Height) * scale);
}

/// <summary>Document information dictionary (strings as stored; dates parsed when well-formed).</summary>
public sealed class PdfMetadata
{
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Subject { get; init; }
    public string? Keywords { get; init; }
    public string? Creator { get; init; }
    public string? Producer { get; init; }
    public string? CreationDateRaw { get; init; }
    public string? ModificationDateRaw { get; init; }
    public DateTimeOffset? CreationDate { get; init; }
    public DateTimeOffset? ModificationDate { get; init; }
    /// <summary>PDF header version, e.g. 17 = PDF 1.7 (0 when unknown).</summary>
    public int FileVersion { get; init; }

    internal static DateTimeOffset? ParseDate(string? s)
    {
        // D:YYYYMMDDHHmmSSOHH'mm'  (everything after the year is optional)
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal)) s = s[2..];
        static int Num(string t, int pos, int len, int def) =>
            pos + len <= t.Length && int.TryParse(t.AsSpan(pos, len), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : def;
        if (s.Length < 4) return null;
        int y = Num(s, 0, 4, -1);
        if (y < 1) return null;
        int mo = Num(s, 4, 2, 1), d = Num(s, 6, 2, 1), h = Num(s, 8, 2, 0), mi = Num(s, 10, 2, 0), sec = Num(s, 12, 2, 0);
        TimeSpan off = TimeSpan.Zero;
        if (s.Length > 14)
        {
            char z = s[14];
            if (z == '+' || z == '-')
            {
                int oh = Num(s, 15, 2, 0);
                int om = s.Length >= 20 ? Num(s, 18, 2, 0) : 0;
                off = new TimeSpan(oh, om, 0);
                if (z == '-') off = -off;
            }
        }
        try
        {
            if (y > 9999) return null;
            mo = Math.Clamp(mo, 1, 12);
            d = Math.Clamp(d, 1, DateTime.DaysInMonth(y, mo));
            return new DateTimeOffset(y, mo, d, Math.Clamp(h, 0, 23), Math.Clamp(mi, 0, 59), Math.Clamp(sec, 0, 59), off);
        }
        catch { return null; }
    }
}

/// <summary>One search match on a page.</summary>
public sealed class PdfSearchHit
{
    public int PageIndex { get; init; }
    /// <summary>Index of the first matching character in the page text (see <see cref="PdfPageText"/>).</summary>
    public int CharIndex { get; init; }
    public int CharCount { get; init; }
    /// <summary>Highlight rectangles (one per text line piece), page space - see <see cref="PdfRect"/>.</summary>
    public IReadOnlyList<PdfRect> Rects { get; init; } = [];
    /// <summary>Text around the match on one line, for result lists.</summary>
    public string Context { get; init; } = "";
    /// <summary>Position of the match inside <see cref="Context"/>.</summary>
    public int ContextMatchStart { get; init; }
}

/// <summary>Render options for pages. Defaults favour crisp, theme-neutral grayscale-antialiased text.</summary>
public sealed class PdfRenderOptions
{
    public static readonly PdfRenderOptions Default = new();
    /// <summary>Draw annotations (links borders, highlights, stamps...).</summary>
    public bool Annotations { get; init; } = true;
    /// <summary>LCD (ClearType-like) text. Only crisp when the bitmap is shown 1:1; default off.</summary>
    public bool LcdText { get; init; }
    public bool Grayscale { get; init; }

    internal int Flags => Pdfium.RenderLimitedImageCache
        | (Annotations ? Pdfium.RenderAnnot : 0)
        | (LcdText ? Pdfium.RenderLcdText : 0)
        | (Grayscale ? Pdfium.RenderGrayscale : 0);
}

/// <summary>An image embedded in a page (used by the DOCX converter).</summary>
public sealed class PdfPageImage
{
    public PdfRect Bounds { get; init; }
    public byte[] Data { get; init; } = [];
    /// <summary>"image/jpeg" or "image/png".</summary>
    public string ContentType { get; init; } = "image/png";
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
}

public enum PdfOpenError
{
    Unknown,
    FileNotFound,
    AccessDenied,
    InvalidFormat,
    /// <summary>The document is encrypted and needs a password (none was supplied).</summary>
    PasswordRequired,
    /// <summary>A password was supplied but it is wrong.</summary>
    IncorrectPassword,
    UnsupportedSecurity,
}

public class PdfException : Exception
{
    public PdfException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class PdfOpenException : PdfException
{
    public PdfOpenError Error { get; }
    public PdfOpenException(PdfOpenError error, string message, Exception? inner = null) : base(message, inner) => Error = error;
    public bool IsPasswordProblem => Error is PdfOpenError.PasswordRequired or PdfOpenError.IncorrectPassword;
}
