using System.IO.Compression;
using System.IO.Packaging;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterNotepad.Metadata.Sample;

/// <summary>Generates every test file from scratch (nothing from the user's disk is read).</summary>
internal static class Fixtures
{
    // ---------------------------------------------------------------- TIFF / EXIF builder (little- or big-endian)

    private sealed class Entry
    {
        public ushort Tag, Type; public uint Count; public byte[] Data = Array.Empty<byte>();
        public Ifd? Sub; public int Special;   // 1 = thumbnail offset, 2 = thumbnail length
    }
    private sealed class Ifd { public List<Entry> Entries = new(); public int Offset; }

    private static byte[] U16(bool le, params ushort[] v) { var b = new byte[v.Length * 2]; for (int i = 0; i < v.Length; i++) { if (le) { b[i * 2] = (byte)v[i]; b[i * 2 + 1] = (byte)(v[i] >> 8); } else { b[i * 2] = (byte)(v[i] >> 8); b[i * 2 + 1] = (byte)v[i]; } } return b; }
    private static byte[] U32(bool le, params uint[] v) { var b = new byte[v.Length * 4]; for (int i = 0; i < v.Length; i++) { var x = v[i]; if (le) { b[i * 4] = (byte)x; b[i * 4 + 1] = (byte)(x >> 8); b[i * 4 + 2] = (byte)(x >> 16); b[i * 4 + 3] = (byte)(x >> 24); } else { b[i * 4] = (byte)(x >> 24); b[i * 4 + 1] = (byte)(x >> 16); b[i * 4 + 2] = (byte)(x >> 8); b[i * 4 + 3] = (byte)x; } } return b; }
    private static Entry Ascii(ushort tag, string s) { var d = Encoding.UTF8.GetBytes(s + "\0"); return new Entry { Tag = tag, Type = 2, Count = (uint)d.Length, Data = d }; }
    private static Entry Short(bool le, ushort tag, ushort v) => new() { Tag = tag, Type = 3, Count = 1, Data = U16(le, v) };
    private static Entry Long(bool le, ushort tag, uint v) => new() { Tag = tag, Type = 4, Count = 1, Data = U32(le, v) };
    private static Entry Rat(bool le, ushort tag, params (uint n, uint d)[] v) => new() { Tag = tag, Type = 5, Count = (uint)v.Length, Data = U32(le, v.SelectMany(x => new[] { x.n, x.d }).ToArray()) };
    private static Entry Undef(ushort tag, byte[] d) => new() { Tag = tag, Type = 7, Count = (uint)d.Length, Data = d };
    private static Entry Bytes(ushort tag, byte[] d) => new() { Tag = tag, Type = 1, Count = (uint)d.Length, Data = d };

    private static byte[] BuildTiff(bool le, Ifd ifd0, Ifd? ifd1, byte[]? thumb)
    {
        var all = new List<Ifd> { ifd0 };
        void Collect(Ifd i) { foreach (var e in i.Entries) if (e.Sub != null) { all.Add(e.Sub); Collect(e.Sub); } }
        Collect(ifd0);
        if (ifd1 != null) all.Add(ifd1);
        int off = 8;
        foreach (var i in all) { i.Offset = off; off += 2 + 12 * i.Entries.Count + 4; }
        var dataOff = new Dictionary<Entry, int>();
        foreach (var i in all)
            foreach (var e in i.Entries.OrderBy(x => x.Tag))
                if (e.Sub == null && e.Special == 0 && e.Data.Length > 4) { if ((off & 1) == 1) off++; dataOff[e] = off; off += e.Data.Length; }
        int thumbOff = off;
        if (thumb != null) off += thumb.Length;
        var buf = new byte[off];
        buf[0] = buf[1] = (byte)(le ? 'I' : 'M');
        Bin(buf, 2, 42, 2, le); Bin(buf, 4, 8, 4, le);
        foreach (var i in all)
        {
            int p = i.Offset;
            Bin(buf, p, (uint)i.Entries.Count, 2, le); p += 2;
            foreach (var e in i.Entries.OrderBy(x => x.Tag))
            {
                Bin(buf, p, e.Tag, 2, le); Bin(buf, p + 2, e.Type, 2, le);
                if (e.Sub != null) { Bin(buf, p + 4, 1, 4, le); Bin(buf, p + 8, (uint)e.Sub.Offset, 4, le); }
                else if (e.Special == 1) { Bin(buf, p + 4, 1, 4, le); Bin(buf, p + 8, (uint)thumbOff, 4, le); }
                else if (e.Special == 2) { Bin(buf, p + 4, 1, 4, le); Bin(buf, p + 8, (uint)(thumb?.Length ?? 0), 4, le); }
                else
                {
                    Bin(buf, p + 4, e.Count, 4, le);
                    if (e.Data.Length <= 4) Array.Copy(e.Data, 0, buf, p + 8, e.Data.Length);
                    else { Bin(buf, p + 8, (uint)dataOff[e], 4, le); Array.Copy(e.Data, 0, buf, dataOff[e], e.Data.Length); }
                }
                p += 12;
            }
            Bin(buf, p, i == ifd0 && ifd1 != null ? (uint)ifd1.Offset : 0, 4, le);
        }
        if (thumb != null) Array.Copy(thumb, 0, buf, thumbOff, thumb.Length);
        return buf;
    }

    private static void Bin(byte[] b, int o, uint v, int n, bool le)
    {
        for (int i = 0; i < n; i++) b[o + (le ? i : n - 1 - i)] = (byte)(v >> (8 * i));
    }

    /// <summary>EXIF with camera data, GPS, an embedded thumbnail and an unknown MakerNote.</summary>
    public static byte[] BuildExif(int orientation, bool le, bool gps = true)
    {
        var exifIfd = new Ifd();
        exifIfd.Entries.Add(Rat(le, 0x829A, (1, 250)));
        exifIfd.Entries.Add(Rat(le, 0x829D, (28, 10)));
        exifIfd.Entries.Add(Short(le, 0x8827, 200));
        exifIfd.Entries.Add(Undef(0x9000, Encoding.ASCII.GetBytes("0230")));
        exifIfd.Entries.Add(Ascii(0x9003, "2024:05:17 14:29:58"));
        exifIfd.Entries.Add(Undef(0x9286, Encoding.ASCII.GetBytes("ASCII\0\0\0").Concat(Encoding.ASCII.GetBytes("Comment by fixture")).ToArray()));
        exifIfd.Entries.Add(Undef(0x927C, Enumerable.Range(0, 24).Select(i => (byte)(i * 7)).ToArray()));
        exifIfd.Entries.Add(Ascii(0xA431, "SN12345"));
        exifIfd.Entries.Add(Ascii(0xA434, "ACME 24-70mm"));

        var ifd0 = new Ifd();
        ifd0.Entries.Add(Ascii(0x010E, "Sample photo from Warsaw"));
        ifd0.Entries.Add(Ascii(0x010F, "ACME"));
        ifd0.Entries.Add(Ascii(0x0110, "SnapMaster 3000"));
        ifd0.Entries.Add(Short(le, 0x0112, (ushort)orientation));
        ifd0.Entries.Add(Ascii(0x0131, "BN Fixture 1.0"));
        ifd0.Entries.Add(Ascii(0x0132, "2024:05:17 14:30:00"));
        ifd0.Entries.Add(Ascii(0x013B, "Jan Kowalski"));
        ifd0.Entries.Add(Ascii(0x8298, "(c) Jan"));
        ifd0.Entries.Add(Bytes(0x9C9D, Encoding.Unicode.GetBytes("Jan K.\0")));
        ifd0.Entries.Add(new Entry { Tag = 0x8769, Type = 4, Count = 1, Sub = exifIfd });
        if (gps)
        {
            var g = new Ifd();
            g.Entries.Add(Bytes(0x0000, new byte[] { 2, 3, 0, 0 }));
            g.Entries.Add(Ascii(0x0001, "N"));
            g.Entries.Add(Rat(le, 0x0002, (52, 1), (13, 1), (4800, 100)));
            g.Entries.Add(Ascii(0x0003, "E"));
            g.Entries.Add(Rat(le, 0x0004, (21, 1), (0, 1), (3000, 100)));
            g.Entries.Add(new Entry { Tag = 0x0005, Type = 1, Count = 1, Data = new byte[] { 0 } });
            g.Entries.Add(Rat(le, 0x0006, (11050, 100)));
            g.Entries.Add(Rat(le, 0x0007, (14, 1), (29, 1), (58, 1)));
            g.Entries.Add(Ascii(0x001D, "2024:05:17"));
            ifd0.Entries.Add(new Entry { Tag = 0x8825, Type = 4, Count = 1, Sub = g });
        }
        var ifd1 = new Ifd();
        ifd1.Entries.Add(Short(le, 0x0103, 6));
        ifd1.Entries.Add(new Entry { Tag = 0x0201, Type = 4, Count = 1, Special = 1 });
        ifd1.Entries.Add(new Entry { Tag = 0x0202, Type = 4, Count = 1, Special = 2 });
        return BuildTiff(le, ifd0, ifd1, EncodeJpeg(Gradient(16, 12), 70));
    }

    // ---------------------------------------------------------------- images

    public static BitmapSource Gradient(int w, int h)
    {
        var px = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                px[i] = (byte)(x * 255 / Math.Max(1, w - 1));
                px[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                px[i + 2] = (byte)((x * 7 + y * 13) & 0xFF);
            }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, px, w * 3);
        bmp.Freeze();
        return bmp;
    }

    public static byte[] EncodeJpeg(BitmapSource src, int quality)
    {
        var enc = new JpegBitmapEncoder { QualityLevel = quality };
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static byte[] EncodePng(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public const string XmpPacket =
        "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
        "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:photoshop=\"http://ns.adobe.com/photoshop/1.0/\" xmlns:exif=\"http://ns.adobe.com/exif/1.0/\" " +
        "xmp:CreatorTool=\"Fixture Editor 9\" photoshop:City=\"Warszawa\" exif:GPSLatitude=\"52,13.8N\">" +
        "<dc:creator><rdf:Seq><rdf:li>Jan Kowalski</rdf:li></rdf:Seq></dc:creator>" +
        "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Zachód słońca</rdf:li></rdf:Alt></dc:title>" +
        "<dc:subject><rdf:Bag><rdf:li>sunset</rdf:li><rdf:li>sky</rdf:li></rdf:Bag></dc:subject>" +
        "</rdf:Description></rdf:RDF></x:xmpmeta>\n<?xpacket end=\"w\"?>";

    private static byte[] Seg(byte marker, byte[] payload)
    {
        var b = new byte[4 + payload.Length];
        b[0] = 0xFF; b[1] = marker; b[2] = (byte)((payload.Length + 2) >> 8); b[3] = (byte)(payload.Length + 2);
        Array.Copy(payload, 0, b, 4, payload.Length);
        return b;
    }

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] IptcSegment()
    {
        var iim = new List<byte>();
        void Ds(int rec, int ds, byte[] data) { iim.Add(0x1C); iim.Add((byte)rec); iim.Add((byte)ds); iim.Add((byte)(data.Length >> 8)); iim.Add((byte)data.Length); iim.AddRange(data); }
        Ds(1, 90, new byte[] { 0x1B, 0x25, 0x47 });
        Ds(2, 0, new byte[] { 0, 2 });
        Ds(2, 80, Encoding.UTF8.GetBytes("Jan Kowalski"));
        Ds(2, 90, Encoding.UTF8.GetBytes("Warszawa"));
        Ds(2, 25, Encoding.UTF8.GetBytes("sunset"));
        Ds(2, 25, Encoding.UTF8.GetBytes("sky"));
        Ds(2, 116, Encoding.UTF8.GetBytes("© Jan Kowalski"));
        Ds(2, 120, Encoding.UTF8.GetBytes("Zachód słońca nad Wisłą"));
        var res = new List<byte>();
        res.AddRange(Encoding.ASCII.GetBytes("8BIM"));
        res.Add(0x04); res.Add(0x04); res.Add(0); res.Add(0);
        res.Add(0); res.Add(0); res.Add((byte)(iim.Count >> 8)); res.Add((byte)iim.Count);
        res.AddRange(iim);
        if ((iim.Count & 1) == 1) res.Add(0);
        return Seg(0xED, Cat(Encoding.ASCII.GetBytes("Photoshop 3.0\0"), res.ToArray()));
    }

    /// <summary>JPEG: WPF-encoded picture + EXIF(GPS, thumbnail) + XMP + IPTC + COM inserted behind the JFIF header.</summary>
    public static byte[] BuildJpeg(int orientation = 6, bool le = true, bool gps = true)
    {
        var raw = EncodeJpeg(Gradient(64, 48), 90);
        int p = 2;   // after SOI; skip JFIF (APP0) if present
        if (raw[2] == 0xFF && raw[3] == 0xE0) p = 4 + (raw[4] << 8 | raw[5]);
        var exif = Seg(0xE1, Cat(Encoding.ASCII.GetBytes("Exif\0\0"), BuildExif(orientation, le, gps)));
        var xmp = Seg(0xE1, Cat(Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0"), Encoding.UTF8.GetBytes(XmpPacket)));
        var com = Seg(0xFE, Encoding.UTF8.GetBytes("Created by the fixture builder"));
        return Cat(raw[..p], exif, xmp, IptcSegment(), com, raw[p..]);
    }

    private static byte[] PngChunk(string type, byte[] data)
    {
        var b = new byte[12 + data.Length];
        b[0] = (byte)(data.Length >> 24); b[1] = (byte)(data.Length >> 16); b[2] = (byte)(data.Length >> 8); b[3] = (byte)data.Length;
        Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        data.CopyTo(b, 8);
        uint crc = Crc32Of(b, 4, 4 + data.Length);
        b[8 + data.Length] = (byte)(crc >> 24); b[9 + data.Length] = (byte)(crc >> 16); b[10 + data.Length] = (byte)(crc >> 8); b[11 + data.Length] = (byte)crc;
        return b;
    }

    private static uint Crc32Of(byte[] b, int off, int len)
    {
        uint c = 0xFFFFFFFF;
        for (int i = off; i < off + len; i++)
        {
            c ^= b[i];
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        return ~c;
    }

    public static byte[] BuildPng()
    {
        var raw = EncodePng(Gradient(40, 30));
        int ihdrEnd = 8 + 12 + 13;
        var extra = new List<byte[]>();
        extra.Add(PngChunk("tIME", new byte[] { 0x07, 0xE8, 5, 17, 14, 30, 0 }));
        extra.Add(PngChunk("tEXt", Encoding.Latin1.GetBytes("Author\0Anna Nowak")));
        extra.Add(PngChunk("tEXt", Encoding.Latin1.GetBytes("Software\0Fixture Paint 2")));
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(Encoding.Latin1.GetBytes("A secret compressed comment"));
            extra.Add(PngChunk("zTXt", Cat(Encoding.Latin1.GetBytes("Comment\0\0"), ms.ToArray())));
        }
        extra.Add(PngChunk("iTXt", Cat(Encoding.Latin1.GetBytes("Description\0\0\0pl\0Opis\0"), Encoding.UTF8.GetBytes("Zażółć gęślą jaźń"))));
        extra.Add(PngChunk("iTXt", Cat(Encoding.Latin1.GetBytes("XML:com.adobe.xmp\0\0\0\0\0"), Encoding.UTF8.GetBytes(XmpPacket))));
        extra.Add(PngChunk("eXIf", BuildExif(3, false)));
        return Cat(raw[..ihdrEnd], Cat(extra.ToArray()), raw[ihdrEnd..]);
    }

    public static byte[] BuildWebp()
    {
        // 1x1 lossy WebP from the public feature-detection snippet; wrapped into an extended (VP8X) container with EXIF + XMP.
        var basic = Convert.FromBase64String("UklGRiIAAABXRUJQVlA4IBYAAAAwAQCdASoBAAEADsD+JaQAA3AAAAAA");
        var vp8 = basic[12..];
        byte[] Chunk(string id, byte[] d) { var c = new byte[8 + d.Length + (d.Length & 1)]; Encoding.ASCII.GetBytes(id).CopyTo(c, 0); BitConverter.GetBytes((uint)d.Length).CopyTo(c, 4); d.CopyTo(c, 8); return c; }
        var vp8x = Chunk("VP8X", new byte[] { 0x08 | 0x04, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        var exif = Chunk("EXIF", BuildExif(6, true, gps: true));
        var xmp = Chunk("XMP ", Encoding.UTF8.GetBytes(XmpPacket));
        var body = Cat(Encoding.ASCII.GetBytes("WEBP"), vp8x, vp8, exif, xmp);
        return Cat(Encoding.ASCII.GetBytes("RIFF"), BitConverter.GetBytes((uint)body.Length), body);
    }

    // ---------------------------------------------------------------- PDF

    public const string PdfSecretAuthor = "Secret Author";
    public const string PdfSecretTitle = "Secret Title";

    private const string PdfXmp =
        "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
        "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" pdf:Producer=\"Fixture Producer 1\" xmp:CreatorTool=\"Fixture Writer\">" +
        "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">" + PdfSecretTitle + "</rdf:li></rdf:Alt></dc:title>" +
        "<dc:creator><rdf:Seq><rdf:li>" + PdfSecretAuthor + "</rdf:li></rdf:Seq></dc:creator></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    private sealed class PdfBuf
    {
        public readonly MemoryStream Ms = new();
        public readonly Dictionary<int, long> Offsets = new();
        public void Raw(string s) => Ms.Write(Encoding.Latin1.GetBytes(s));
        public void Raw(byte[] b) => Ms.Write(b);
        public void Obj(int n, string body) { Offsets[n] = Ms.Position; Raw($"{n} 0 obj\n{body}\nendobj\n"); }
        public void StreamObj(int n, string dict, byte[] data) { Offsets[n] = Ms.Position; Raw($"{n} 0 obj\n<< {dict} /Length {data.Length} >>\nstream\n"); Raw(data); Raw("\nendstream\nendobj\n"); }
        public long Pos => Ms.Position;
    }

    private static string ClassicXref(PdfBuf b, int size, string trailerExtra, long prev = -1)
    {
        long xr = b.Pos;
        b.Raw($"xref\n0 {size}\n0000000000 65535 f \n");
        for (int i = 1; i < size; i++) b.Raw(b.Offsets.TryGetValue(i, out var o) ? $"{o:D10} 00000 n \n" : "0000000000 00000 f \n");
        b.Raw($"trailer\n<< /Size {size} {trailerExtra}{(prev >= 0 ? $" /Prev {prev}" : "")} >>\nstartxref\n{xr}\n%%EOF\n");
        return xr.ToString();
    }

    /// <summary>Classic PDF with Info + XMP and an incremental revision that replaces Info (old strings stay in the file body).</summary>
    public static byte[] BuildPdfClassic(bool withRevision = true)
    {
        var b = new PdfBuf();
        b.Raw("%PDF-1.4\n%\u00E2\u00E3\u00CF\u00D3\n");
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R /Metadata 6 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        b.StreamObj(4, "", Encoding.ASCII.GetBytes("BT /F1 18 Tf 20 100 Td (Hello metadata) Tj ET"));
        b.Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.StreamObj(6, "/Type /Metadata /Subtype /XML", Encoding.UTF8.GetBytes(PdfXmp));
        b.Obj(7, $"<< /Title ({PdfSecretTitle}) /Author ({PdfSecretAuthor}) /Subject (OldSubject) /Keywords (alpha, beta) /Creator (Fixture Writer) /Producer (Fixture Producer 1) /CreationDate (D:20240517143000+02'00') /ModDate (D:20240518090000Z) /Company (Old Co) >>");
        long x1 = long.Parse(ClassicXref(b, 8, "/Root 1 0 R /Info 7 0 R /ID [<0123456789ABCDEF0123456789ABCDEF> <0123456789ABCDEF0123456789ABCDEF>]"));
        if (withRevision)
        {
            var title2 = BitConverter.ToString(Encoding.BigEndianUnicode.GetBytes("Zażółć Secret v2")).Replace("-", "");
            b.Offsets.Clear();
            b.Obj(7, $"<< /Title <FEFF{title2}> /Author (Revised Secret Author) /Producer (Fixture Producer 2) /Project (Top Secret Project) /CreationDate (D:20240517143000+02'00') >>");
            // the xref of this revision lists only object 7
            long xr = b.Pos;
            b.Raw($"xref\n7 1\n{b.Offsets[7]:D10} 00000 n \ntrailer\n<< /Size 8 /Root 1 0 R /Info 7 0 R /ID [<0123456789ABCDEF0123456789ABCDEF> <0123456789ABCDEF0123456789ABCDEF>] /Prev {x1} >>\nstartxref\n{xr}\n%%EOF\n");
        }
        return b.Ms.ToArray();
    }

    /// <summary>PDF 1.5: objects inside a Flate object stream, XMP stream compressed, xref stream with PNG predictor.</summary>
    public static byte[] BuildPdfXrefStream()
    {
        var b = new PdfBuf();
        b.Raw("%PDF-1.5\n%\u00E2\u00E3\u00CF\u00D3\n");
        b.StreamObj(4, "", Encoding.ASCII.GetBytes("BT /F1 18 Tf 20 100 Td (Hello compressed) Tj ET"));
        byte[] Z(byte[] d) { using var ms = new MemoryStream(); using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(d); return ms.ToArray(); }
        b.StreamObj(6, "/Type /Metadata /Subtype /XML /Filter /FlateDecode", Z(Encoding.UTF8.GetBytes(PdfXmp)));

        var inner = new (int Num, string Text)[]
        {
            (1, "<< /Type /Catalog /Pages 2 0 R /Metadata 6 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>"),
            (5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            (7, $"<< /Title ({PdfSecretTitle}) /Author ({PdfSecretAuthor}) /Producer (Compressed Producer) /CreationDate (D:20240101000000Z) >>"),
        };
        var header = new StringBuilder(); var body = new StringBuilder();
        foreach (var (num, text) in inner) { header.Append($"{num} {body.Length} "); body.Append(text).Append('\n'); }
        var objStm = Encoding.Latin1.GetBytes(header.ToString() + body);
        b.StreamObj(8, $"/Type /ObjStm /N {inner.Length} /First {header.Length} /Filter /FlateDecode", Z(objStm));

        // xref stream: W [1 4 2], PNG "Up" predictor
        var rows = new List<byte[]>();
        byte[] Row(int type, long a, int c) => new[] { (byte)type, (byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a, (byte)(c >> 8), (byte)c };
        long xrefOffset = b.Pos;
        for (int i = 0; i <= 9; i++)
        {
            int idx = Array.FindIndex(inner, t => t.Num == i);
            if (i == 0) rows.Add(Row(0, 0, 65535));
            else if (idx >= 0) rows.Add(Row(2, 8, idx));
            else if (i == 9) rows.Add(Row(1, xrefOffset, 0));
            else rows.Add(Row(1, b.Offsets[i], 0));
        }
        var enc = new MemoryStream();
        byte[] prev = new byte[7];
        foreach (var r in rows)
        {
            enc.WriteByte(2);
            for (int k = 0; k < 7; k++) enc.WriteByte((byte)(r[k] - prev[k]));
            prev = r;
        }
        var packed = Z(enc.ToArray());
        b.Offsets[9] = b.Pos;
        b.Raw($"9 0 obj\n<< /Type /XRef /Size 10 /W [1 4 2] /Index [0 10] /Root 1 0 R /Info 7 0 R /ID [<AABBCCDDEEFF00112233445566778899> <AABBCCDDEEFF00112233445566778899>] /Filter /FlateDecode /DecodeParms << /Predictor 12 /Columns 7 >> /Length {packed.Length} >>\nstream\n");
        b.Raw(packed);
        b.Raw($"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n");
        return b.Ms.ToArray();
    }

    /// <summary>Large PDF: many small objects (reachable through the catalog) plus one big random stream, with Info + XMP.</summary>
    public static byte[] BuildBigPdf(int objects, int streamMegabytes)
    {
        var b = new PdfBuf();
        b.Raw("%PDF-1.4\n%âãÏÓ\n");
        int first = 100;
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R /Metadata 6 0 R /Extra 9 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        b.StreamObj(4, "", Encoding.ASCII.GetBytes("BT /F1 18 Tf 20 100 Td (Big) Tj ET"));
        b.Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.StreamObj(6, "/Type /Metadata /Subtype /XML", Encoding.UTF8.GetBytes(PdfXmp));
        b.Obj(7, $"<< /Title ({PdfSecretTitle}) /Author ({PdfSecretAuthor}) >>");
        var big = new byte[streamMegabytes * 1024 * 1024];
        new Random(7).NextBytes(big);
        b.StreamObj(8, "/Type /BigData", big);
        b.Obj(9, "<< /Items [" + string.Join(" ", Enumerable.Range(first, objects).Select(i => $"{i} 0 R")) + " 8 0 R] >>");
        for (int i = 0; i < objects; i++) b.Obj(first + i, $"<< /Type /Filler /N {i} /Name (item {i}) >>");
        int size = first + objects;
        ClassicXref(b, size, "/Root 1 0 R /Info 7 0 R");
        return b.Ms.ToArray();
    }

    // ---------------------------------------------------------------- Office

    public static void BuildOffice(string path, string kind)
    {
        // kind: docx | docm | xlsx | pptx
        const string RelOfficeDoc = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
        const string RelExtended = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties";
        const string RelCustom = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/custom-properties";
        const string RelThumb = "http://schemas.openxmlformats.org/package/2006/relationships/metadata/thumbnail";
        if (File.Exists(path)) File.Delete(path);
        using var pkg = Package.Open(path, FileMode.Create);

        void AddXml(string uri, string ct, string xml, string? relFrom = null, string? relType = null)
        {
            var part = pkg.CreatePart(new Uri(uri, UriKind.Relative), ct, CompressionOption.Normal);
            using (var s = part.GetStream()) { var b = new UTF8Encoding(false).GetBytes(xml); s.Write(b, 0, b.Length); }
            if (relType != null) pkg.CreateRelationship(new Uri(uri, UriKind.Relative), TargetMode.Internal, relType);
        }

        switch (kind)
        {
            case "docx":
            case "docm":
                AddXml("/word/document.xml",
                    kind == "docx" ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml" : "application/vnd.ms-word.document.macroEnabled.main+xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Hello metadata</w:t></w:r></w:p></w:body></w:document>",
                    null, RelOfficeDoc);
                break;
            case "xlsx":
                AddXml("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" xmlns:x15ac=\"http://schemas.microsoft.com/office/spreadsheetml/2010/11/ac\" mc:Ignorable=\"x15ac\">" +
                    "<mc:AlternateContent><mc:Choice Requires=\"x15ac\"><x15ac:absPath url=\"C:\\Users\\SecretUser\\Documents\\\"/></mc:Choice></mc:AlternateContent><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>",
                    null, RelOfficeDoc);
                AddXml("/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Hello</t></is></c></row></sheetData></worksheet>");
                pkg.GetPart(new Uri("/xl/workbook.xml", UriKind.Relative)).CreateRelationship(new Uri("/xl/worksheets/sheet1.xml", UriKind.Relative), TargetMode.Internal,
                    "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", "rId1");
                break;
            case "pptx":
                AddXml("/ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"/>", null, RelOfficeDoc);
                break;
        }

        var pp = pkg.PackageProperties;
        pp.Creator = "Jan Kowalski"; pp.Title = "Quarterly secret report"; pp.Subject = "Finance"; pp.Keywords = "q3; secret";
        pp.Description = "internal comment"; pp.LastModifiedBy = "Anna Nowak"; pp.Revision = "7";
        pp.Created = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc); pp.Modified = new DateTime(2024, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        pp.Category = "Reports"; pp.ContentStatus = "Draft"; pp.Language = "en-US";

        AddXml("/docProps/app.xml", "application/vnd.openxmlformats-officedocument.extended-properties+xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">" +
            "<Application>Microsoft Office Fixture</Application><AppVersion>16.0000</AppVersion><Company>ACME Corp</Company><Manager>Big Boss</Manager><Template>Normal.dotm</Template><TotalTime>12</TotalTime><Pages>1</Pages><Words>3</Words></Properties>",
            null, RelExtended);
        AddXml("/docProps/custom.xml", "application/vnd.openxmlformats-officedocument.custom-properties+xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/custom-properties\" xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">" +
            "<property fmtid=\"{D5CDD505-2E9C-101B-9397-08002B2CF9AE}\" pid=\"2\" name=\"ProjectCode\"><vt:lpwstr>X-42</vt:lpwstr></property>" +
            "<property fmtid=\"{D5CDD505-2E9C-101B-9397-08002B2CF9AE}\" pid=\"3\" name=\"Reviewed\"><vt:bool>true</vt:bool></property></Properties>",
            null, RelCustom);

        var thumb = pkg.CreatePart(new Uri("/docProps/thumbnail.jpeg", UriKind.Relative), "image/jpeg", CompressionOption.NotCompressed);
        using (var s = thumb.GetStream()) { var j = EncodeJpeg(Gradient(32, 24), 60); s.Write(j, 0, j.Length); }
        pkg.CreateRelationship(new Uri("/docProps/thumbnail.jpeg", UriKind.Relative), TargetMode.Internal, RelThumb);
        pkg.Flush();
    }
}
