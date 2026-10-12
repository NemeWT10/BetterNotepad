using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterNotepad.Pdf.Sample;

/// <summary>Writes small PDFs in raw PDF syntax (own generator, correct xref offsets) - the only test input of the sample.</summary>
internal sealed class SamplePdfBuilder
{
    private readonly SortedDictionary<int, byte[]> _objs = new();
    private readonly Func<int, byte[], byte[]>? _encrypt;

    public SamplePdfBuilder(Func<int, byte[], byte[]>? encrypt = null) => _encrypt = encrypt;

    private static byte[] L1(string s) => Encoding.Latin1.GetBytes(s);

    public void Add(int num, string body) => _objs[num] = L1(body);

    public void AddStream(int num, string dictBody, byte[] data)
    {
        if (_encrypt != null) data = _encrypt(num, data);
        using var ms = new MemoryStream();
        ms.Write(L1($"<< {dictBody} /Length {data.Length} >>\nstream\n"));
        ms.Write(data);
        ms.Write(L1("\nendstream"));
        _objs[num] = ms.ToArray();
    }

    public byte[] Build(string trailerExtra)
    {
        using var ms = new MemoryStream();
        ms.Write(L1("%PDF-1.4\n%âãÏÓ\n"));
        int max = _objs.Keys.Max();
        var offsets = new long[max + 1];
        foreach (var (num, body) in _objs)
        {
            offsets[num] = ms.Position;
            ms.Write(L1($"{num} 0 obj\n"));
            ms.Write(body);
            ms.Write(L1("\nendobj\n"));
        }
        long xref = ms.Position;
        var sb = new StringBuilder();
        sb.Append($"xref\n0 {max + 1}\n");
        sb.Append("0000000000 65535 f \n");
        for (int i = 1; i <= max; i++)
            sb.Append(_objs.ContainsKey(i) ? $"{offsets[i]:D10} 00000 n \n" : "0000000000 65535 f \n");
        sb.Append($"trailer\n<< /Size {max + 1} /Root 1 0 R {trailerExtra} >>\nstartxref\n{xref}\n%%EOF\n");
        ms.Write(L1(sb.ToString()));
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ the sample documents

    private static byte[] Jpeg64()
    {
        const int N = 64;
        var px = new byte[N * N * 3];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                int i = (y * N + x) * 3;
                px[i] = (byte)(x * 4); px[i + 1] = (byte)(y * 4); px[i + 2] = (byte)(255 - x * 2 - y * 2); // BGR
            }
        var bs = BitmapSource.Create(N, N, 96, 96, PixelFormats.Bgr24, null, px, N * 3);
        var enc = new JpegBitmapEncoder { QualityLevel = 90 };
        enc.Frames.Add(BitmapFrame.Create(bs));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static string RawChecker16Hex()
    {
        var sb = new StringBuilder();
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                sb.Append(((x / 4 + y / 4) & 1) == 0 ? "E8A200" : "1B4F72");
        return sb + ">";
    }

    /// <summary>3 pages: A4 text/graphics, Letter with two images, A4 with /Rotate 90.</summary>
    public static byte[] BuildSample()
    {
        var b = new SamplePdfBuilder();
        b.Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Add(2, "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>");
        const string Fonts = "/Font << /F1 6 0 R /F2 7 0 R /F3 8 0 R /F4 9 0 R >>";
        b.Add(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 10 0 R /Resources << {Fonts} >> >>");
        b.Add(4, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 11 0 R /Resources << {Fonts} /XObject << /Im1 13 0 R /Im2 15 0 R >> >> >>");
        b.Add(5, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Rotate 90 /Contents 12 0 R /Resources << {Fonts} >> >>");
        b.Add(6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        b.Add(7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        b.Add(8, "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>");
        b.Add(9, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>");

        const string P1 = @"
0.12 0.31 0.55 rg
50 770 495 40 re f
1 1 1 rg
BT /F2 22 Tf 62 782 Td (BetterNotepad PDF test page 1) Tj ET
0 g
BT /F1 12 Tf 50 735 Td 16 TL
(Hello world. This is the first paragraph of the sample document.) Tj T*
(It wraps over several lines so the converter can merge the lines into) Tj T*
(one paragraph. The word Helvetica appears here, and the number 12345.) Tj ET
BT /F1 12 Tf 50 665 Td 16 TL
(Second paragraph starts after a gap. It lists pages and a Needle,) Tj T*
(with an accented line: caf\351 na\357ve \374ber \361.) Tj ET
BT /F3 14 Tf 50 600 Td (Times-Roman line: The quick brown fox jumps over the lazy dog.) Tj ET
BT /F4 11 Tf 50 570 Td (Courier line: int main() { return 0; }) Tj ET
0.8 0.2 0.2 RG 2 w 50 545 m 545 545 l S
0.2 0.6 0.3 rg 50 440 200 80 re f
0 0 0 RG 1 w 50 440 200 80 re S
1 1 1 rg
BT /F1 10 Tf 60 475 Td (Text in the rectangle) Tj ET
0 0 0 RG 0.5 w 300 440 m 545 520 l S
";
        b.AddStream(10, "", L1(P1));

        const string P2 = @"
BT /F2 18 Tf 50 740 Td (Page 2 of the sample) Tj ET
q 150 0 0 150 50 560 cm /Im1 Do Q
q 80 0 0 80 260 600 cm /Im2 Do Q
BT /F1 12 Tf 50 520 Td 16 TL
(Images above: a JPEG gradient and a raw RGB checker.) Tj T*
(Another line with the word Helvetica and a hyphen-) Tj T*
(ated word that continues here.) Tj ET
";
        b.AddStream(11, "", L1(P2));

        const string P3 = @"
BT /F2 20 Tf 100 700 Td (Rotated page three) Tj ET
BT /F1 14 Tf 100 400 Td (Find the needle in the haystack.) Tj ET
0 0 1 RG 3 w 100 380 m 400 380 l S
";
        b.AddStream(12, "", L1(P3));

        b.AddStream(13, "/Type /XObject /Subtype /Image /Width 64 /Height 64 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", Jpeg64());
        b.Add(14, "<< /Title (BetterNotepad PDF sample) /Author (Sample Generator) /Subject (Test) /Keywords (pdf, test) /Creator (SamplePdfBuilder) /Producer (hand written) /CreationDate (D:20260101120000+01'00') >>");
        b.AddStream(15, "/Type /XObject /Subtype /Image /Width 16 /Height 16 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /ASCIIHexDecode", L1(RawChecker16Hex()));
        return b.Build("/Info 14 0 R");
    }

    private static readonly byte[] Pad =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private static byte[] PadPw(string pw)
    {
        var r = new byte[32];
        var p = Encoding.Latin1.GetBytes(pw);
        int n = Math.Min(32, p.Length);
        Array.Copy(p, r, n);
        Array.Copy(Pad, 0, r, n, 32 - n);
        return r;
    }

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++) { j = (j + s[i] + key[i % key.Length]) & 255; (s[i], s[j]) = (s[j], s[i]); }
        var o = new byte[data.Length];
        for (int k = 0, i = 0, j = 0; k < data.Length; k++)
        {
            i = (i + 1) & 255; j = (j + s[i]) & 255; (s[i], s[j]) = (s[j], s[i]);
            o[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 255]);
        }
        return o;
    }

    /// <summary>One page, standard security handler V1/R2 (RC4 40 bit), user password given, empty owner = same.</summary>
    public static byte[] BuildEncrypted(string userPassword, string ownerPassword)
    {
        byte[] id = Enumerable.Range(1, 16).Select(i => (byte)(i * 7)).ToArray();
        var ownerKey = MD5.HashData(PadPw(ownerPassword))[..5];
        byte[] o = Rc4(ownerKey, PadPw(userPassword));
        int perm = -4;
        var seed = PadPw(userPassword).Concat(o).Concat(BitConverter.GetBytes(perm)).Concat(id).ToArray();
        byte[] fileKey = MD5.HashData(seed)[..5];
        byte[] u = Rc4(fileKey, Pad);

        byte[] ObjEncrypt(int num, byte[] data)
        {
            var k = fileKey.Concat(new[] { (byte)num, (byte)(num >> 8), (byte)(num >> 16), (byte)0, (byte)0 }).ToArray();
            return Rc4(MD5.HashData(k)[..10], data);
        }

        var b = new SamplePdfBuilder(ObjEncrypt);
        b.Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Add(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Add(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Contents 5 0 R /Resources << /Font << /F1 4 0 R >> >> >>");
        b.Add(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.AddStream(5, "", L1("BT /F1 18 Tf 20 100 Td (Encrypted page secret text) Tj ET"));
        string Hex(byte[] x) => "<" + Convert.ToHexString(x) + ">";
        b.Add(6, $"<< /Filter /Standard /V 1 /R 2 /O {Hex(o)} /U {Hex(u)} /P {perm} >>");
        return b.Build($"/Encrypt 6 0 R /ID [{Hex(id)}{Hex(id)}]");
    }
}
