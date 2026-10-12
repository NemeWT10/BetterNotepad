using System.Globalization;
using System.IO;
using System.Text;

namespace BetterNotepad.Documents.Files;

public sealed record TextFileContent(string Text, Encoding Encoding, bool HasBom, LineEnding Eol, bool IsBinary, long Length, DateTime LastWriteUtc);

/// <summary>Encoding / line-ending detection and safe saving for text files.</summary>
public static class TextFileIO
{
    private static bool _providerRegistered;

    public static void EnsureCodePages()
    {
        if (_providerRegistered) return;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _providerRegistered = true;
    }

    public static readonly UTF8Encoding Utf8NoBom = new(false, false);

    /// <summary>The system's ANSI code page (Windows-1250 on Polish Windows).</summary>
    public static Encoding Ansi
    {
        get
        {
            EnsureCodePages();
            // the system code page (GetACP), not the thread culture: a Polish file stays 1250 under an English UI
            try { return Encoding.GetEncoding((int)GetACP()); }
            catch { return Encoding.GetEncoding(1252); }
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetACP();

    /// <summary>Encodings offered in the status bar (reopen with / convert to).</summary>
    public static IReadOnlyList<(string Id, string Label, Func<Encoding> Get, bool Bom)> Choices { get; } =
    [
        ("utf-8", "UTF-8", () => Utf8NoBom, false),
        ("utf-8-bom", "UTF-8 BOM", () => Utf8NoBom, true),
        ("utf-16le", "UTF-16 LE", () => new UnicodeEncoding(false, false), true),
        ("utf-16be", "UTF-16 BE", () => new UnicodeEncoding(true, false), true),
        ("windows-1250", "Windows-1250", () => Get(1250), false),
        ("windows-1252", "Windows-1252", () => Get(1252), false),
        ("iso-8859-2", "ISO-8859-2", () => Get(28592), false),
        ("ibm852", "DOS 852", () => Get(852), false),
        ("ascii", "ASCII", () => Encoding.ASCII, false),
    ];

    private static Encoding Get(int codePage)
    {
        EnsureCodePages();
        return Encoding.GetEncoding(codePage);
    }

    public static string Label(Encoding enc, bool bom)
    {
        return enc.CodePage switch
        {
            65001 => bom ? "UTF-8 BOM" : "UTF-8",
            1200 => "UTF-16 LE",
            1201 => "UTF-16 BE",
            12000 => "UTF-32 LE",
            12001 => "UTF-32 BE",
            1250 => "Windows-1250",
            1252 => "Windows-1252",
            28592 => "ISO-8859-2",
            852 => "DOS 852",
            20127 => "ASCII",
            _ => enc.WebName.ToUpperInvariant(),
        };
    }

    public static TextFileContent Read(string path, Encoding? forced = null)
    {
        var info = new FileInfo(path);
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
        {
            bytes = new byte[fs.Length];
            var read = 0;
            while (read < bytes.Length)
            {
                var n = fs.Read(bytes, read, bytes.Length - read);
                if (n == 0) break;
                read += n;
            }
            if (read < bytes.Length) Array.Resize(ref bytes, read);
        }
        return Decode(bytes, forced, info.LastWriteTimeUtc);
    }

    public static TextFileContent Decode(byte[] bytes, Encoding? forced = null, DateTime lastWriteUtc = default)
    {
        Encoding enc;
        bool bom;
        int skip;
        if (forced is not null)
        {
            enc = forced;
            skip = PreambleLength(bytes, forced);
            bom = skip > 0;
        }
        else
        {
            enc = Detect(bytes, out bom, out skip);
            // UTF-16 without BOM is full of NUL bytes by nature: judge its decoded text, not the raw bytes.
            var utf16 = !bom && enc.CodePage is 1200 or 1201;
            if (!bom && (utf16 ? enc.GetString(bytes).Contains('\0') : LooksBinary(bytes)))
                return new TextFileContent("", enc, false, LineEnding.CRLF, true, bytes.Length, lastWriteUtc);
        }
        var text = enc.GetString(bytes, skip, bytes.Length - skip);
        return new TextFileContent(text, enc, bom, DetectEol(text), false, bytes.Length, lastWriteUtc);
    }

    public static Encoding Detect(ReadOnlySpan<byte> b, out bool bom, out int preamble)
    {
        bom = true;
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) { preamble = 3; return Utf8NoBom; }
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0 && b[3] == 0) { preamble = 4; return new UTF32Encoding(false, false); }
        if (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF) { preamble = 4; return new UTF32Encoding(true, false); }
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) { preamble = 2; return new UnicodeEncoding(false, false); }
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) { preamble = 2; return new UnicodeEncoding(true, false); }

        bom = false;
        preamble = 0;

        // UTF-16 without BOM: lots of zero bytes in alternating positions.
        if (b.Length >= 4)
        {
            int evenZero = 0, oddZero = 0, sample = Math.Min(b.Length, 4096) & ~1;
            for (var i = 0; i < sample; i += 2)
            {
                if (b[i] == 0) evenZero++;
                if (b[i + 1] == 0) oddZero++;
            }
            var half = sample / 2;
            if (oddZero > half * 0.4 && evenZero < half * 0.05) return new UnicodeEncoding(false, false);
            if (evenZero > half * 0.4 && oddZero < half * 0.05) return new UnicodeEncoding(true, false);
        }

        return IsValidUtf8(b) ? Utf8NoBom : Ansi;
    }

    private static int PreambleLength(ReadOnlySpan<byte> bytes, Encoding enc)
    {
        var pre = enc.CodePage switch
        {
            65001 => new byte[] { 0xEF, 0xBB, 0xBF },
            1200 => [0xFF, 0xFE],
            1201 => [0xFE, 0xFF],
            _ => [],
        };
        return pre.Length > 0 && bytes.StartsWith(pre) ? pre.Length : 0;
    }

    /// <summary>Strict UTF-8 validation (pure ASCII counts as UTF-8). Vectorized in .NET.</summary>
    public static bool IsValidUtf8(ReadOnlySpan<byte> b) => System.Text.Unicode.Utf8.IsValid(b);

    /// <summary>NUL bytes in the first 8 KB (and not UTF-16) → binary.</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> b)
    {
        var n = Math.Min(b.Length, 8192);
        var control = 0;
        for (var i = 0; i < n; i++)
        {
            var c = b[i];
            if (c == 0) return true;
            if (c < 0x09 || c is > 0x0D and < 0x20 && c != 0x1B) control++;
        }
        return n > 0 && control > n / 10;
    }

    public static LineEnding DetectEol(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        var n = Math.Min(text.Length, 1 << 20);
        for (var i = 0; i < n; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (c == '\n') lf++;
        }
        if (crlf == 0 && lf == 0 && cr == 0) return LineEnding.CRLF;
        if (lf > crlf && lf >= cr) return LineEnding.LF;
        if (cr > crlf && cr > lf) return LineEnding.CR;
        return LineEnding.CRLF;
    }

    /// <summary>Encodes and writes atomically (temp file + replace, keeps attributes and ACLs).</summary>
    public static void Write(string path, string text, Encoding enc, bool bom)
    {
        var body = enc.GetBytes(text);
        byte[] bytes;
        var pre = bom ? enc.CodePage switch
        {
            65001 => new byte[] { 0xEF, 0xBB, 0xBF },
            1200 => [0xFF, 0xFE],
            1201 => [0xFE, 0xFF],
            12000 => [0xFF, 0xFE, 0, 0],
            12001 => [0, 0, 0xFE, 0xFF],
            _ => [],
        } : [];
        if (pre.Length > 0)
        {
            bytes = new byte[pre.Length + body.Length];
            pre.CopyTo(bytes, 0);
            body.CopyTo(bytes, pre.Length);
        }
        else
        {
            bytes = body;
        }
        Core.FileUtil.WriteAllBytesAtomic(path, bytes);
    }

    /// <summary>True if every character of <paramref name="text"/> survives <paramref name="enc"/>.</summary>
    public static bool CanEncode(string text, Encoding enc)
    {
        if (enc.CodePage is 65001 or 1200 or 1201 or 12000 or 12001) return true;
        var strict = (Encoding)enc.Clone();
        strict.EncoderFallback = EncoderFallback.ExceptionFallback;
        try
        {
            strict.GetByteCount(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
