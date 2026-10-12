using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BetterNotepad.Modules.Pdf;

/// <summary>
/// Small PDFs written by hand (correct xref offsets) for the self-test and the UI snapshots: a text manual with N pages
/// and a password-protected page (standard security handler V1 / R2, RC4 40 bit). Test input only.
/// </summary>
internal static class PdfFixtures
{
    public const string Password = "open sesame";

    /// <summary>A manual (Helvetica text): the 3 classic pages, then filler chapters up to <paramref name="pages"/>.</summary>
    public static void WriteManual(string path, int pages = 3)
    {
        var texts = new List<string>
        {
            "BetterNotepad manual|Page one: open PDF files in a tab.|Search with Ctrl+F, copy text with Ctrl+C.",
            "Chapter 2|Zoom with Ctrl + mouse wheel or the buttons.|BetterNotepad renders only the visible pages.",
            "Chapter 3|Print the document or convert it to Word.|The end.",
        };
        for (var i = texts.Count; i < pages; i++)
            texts.Add($"Chapter {i + 1}|" + string.Join("|", Enumerable.Range(1, 30).Select(l => $"Line {l} of chapter {i + 1}: lorem ipsum dolor sit amet, consectetur.")));
        var b = new Builder();
        b.Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Add(2, "<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(0, texts.Count).Select(i => $"{4 + i * 2} 0 R")) + $"] /Count {texts.Count} >>");
        b.Add(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        for (var i = 0; i < texts.Count; i++)
        {
            b.Add(4 + i * 2, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + i * 2} 0 R >>");
            b.AddStream(5 + i * 2, Content(texts[i]));
        }
        File.WriteAllBytes(path, b.Build(""));
    }

    private static string Content(string text)
    {
        var lines = text.Split('|');
        var sb = new StringBuilder("BT /F1 26 Tf 72 740 Td (" + lines[0] + ") Tj /F1 13 Tf 0 -40 Td 18 TL");
        foreach (var l in lines.Skip(1)) sb.Append(" (" + l + ") ' ");
        sb.Append(" ET");
        return sb.ToString();
    }

    /// <summary>One A4 page that opens only with <see cref="Password"/> (user password; the owner password is the same).</summary>
    public static void WriteProtected(string path)
    {
        var id = Enumerable.Range(1, 16).Select(i => (byte)(i * 7)).ToArray();
        var ownerKey = MD5.HashData(Pad(Password))[..5];
        var o = Rc4(ownerKey, Pad(Password));
        const int perm = -4;
        var fileKey = MD5.HashData([.. Pad(Password), .. o, .. BitConverter.GetBytes(perm), .. id])[..5];
        var u = Rc4(fileKey, PadBytes);

        var b = new Builder((num, data) => Rc4(MD5.HashData([.. fileKey, (byte)num, (byte)(num >> 8), (byte)(num >> 16), 0, 0])[..10], data));
        b.Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Add(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Add(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 5 0 R /Resources << /Font << /F1 4 0 R >> >> >>");
        b.Add(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.AddStream(5, Content("Quarterly figures|Encrypted page secret text.|Opened with the right password."));
        static string Hex(byte[] x) => "<" + Convert.ToHexString(x) + ">";
        b.Add(6, $"<< /Filter /Standard /V 1 /R 2 /O {Hex(o)} /U {Hex(u)} /P {perm} >>");
        File.WriteAllBytes(path, b.Build($"/Encrypt 6 0 R /ID [{Hex(id)}{Hex(id)}]"));
    }

    private static readonly byte[] PadBytes =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private static byte[] Pad(string password)
    {
        var r = new byte[32];
        var p = Encoding.Latin1.GetBytes(password);
        var n = Math.Min(32, p.Length);
        Array.Copy(p, r, n);
        Array.Copy(PadBytes, 0, r, n, 32 - n);
        return r;
    }

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (var i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 255;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var o = new byte[data.Length];
        for (int k = 0, i = 0, j = 0; k < data.Length; k++)
        {
            i = (i + 1) & 255;
            j = (j + s[i]) & 255;
            (s[i], s[j]) = (s[j], s[i]);
            o[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 255]);
        }
        return o;
    }

    private sealed class Builder(Func<int, byte[], byte[]>? encrypt = null)
    {
        private readonly SortedDictionary<int, byte[]> _objs = new();

        private static byte[] L1(string s) => Encoding.Latin1.GetBytes(s);

        public void Add(int num, string body) => _objs[num] = L1(body);

        public void AddStream(int num, string content)
        {
            var data = L1(content);
            if (encrypt is not null) data = encrypt(num, data);
            _objs[num] = [.. L1($"<< /Length {data.Length} >>\nstream\n"), .. data, .. L1("\nendstream")];
        }

        public byte[] Build(string trailerExtra)
        {
            using var ms = new MemoryStream();
            ms.Write(L1("%PDF-1.4\n"));
            var max = _objs.Keys.Max();
            var offsets = new long[max + 1];
            foreach (var (num, body) in _objs)
            {
                offsets[num] = ms.Position;
                ms.Write(L1($"{num} 0 obj\n"));
                ms.Write(body);
                ms.Write(L1("\nendobj\n"));
            }
            var xref = ms.Position;
            var sb = new StringBuilder($"xref\n0 {max + 1}\n0000000000 65535 f \n");
            for (var i = 1; i <= max; i++) sb.Append(_objs.ContainsKey(i) ? $"{offsets[i]:D10} 00000 n \n" : "0000000000 65535 f \n");
            sb.Append($"trailer\n<< /Size {max + 1} /Root 1 0 R {trailerExtra} >>\nstartxref\n{xref}\n%%EOF\n");
            ms.Write(L1(sb.ToString()));
            return ms.ToArray();
        }
    }
}
