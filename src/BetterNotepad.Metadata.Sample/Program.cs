using System.Diagnostics;
using System.IO.Packaging;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace BetterNotepad.Metadata.Sample;

internal static class Program
{
    private static string _root = "", _out = "", _fix = "", _work = "";
    private static readonly StringBuilder Log = new();
    private static int _ok, _fail;

    [STAThread]
    private static int Main()
    {
        _root = FindRoot();
        _out = Path.Combine(_root, "tests", "output", "metadata");
        _fix = Path.Combine(_out, "fixtures");
        _work = Path.Combine(_out, "work");
        foreach (var d in new[] { _fix, _work }) { if (Directory.Exists(d)) Directory.Delete(d, true); Directory.CreateDirectory(d); }

        var sw = Stopwatch.StartNew();
        Section("JPEG", TestJpeg);
        Section("PNG", TestPng);
        Section("WebP", TestWebp);
        Section("PDF (classic xref + incremental revision)", TestPdfClassic);
        Section("PDF (xref stream + object stream + flate XMP)", TestPdfXrefStream);
        Section("PDF (damaged xref)", TestPdfDamaged);
        Section("Office (docx, docm, xlsx, pptx)", TestOffice);
        Section("File level (times, attributes, Zone.Identifier, backup, read-only)", TestFileLevel);
        Section("Hash", TestHash);
        Section("Edge cases (JPEG segments, encrypted PDF, broken zip)", TestEdgeCases);
        Section("Scale (large PDF / JPEG)", TestScale);
        Section("Fuzz (random corruption of every format; originals must stay intact)", TestFuzz);

        // no temp files may be left behind
        var leftovers = Directory.GetFiles(_work, ".bnmeta-*", SearchOption.AllDirectories).Length + Directory.GetFiles(_fix, ".bnmeta-*", SearchOption.AllDirectories).Length;
        Check("no temp files left behind", leftovers == 0, leftovers + " found");

        Log.AppendLine();
        Log.AppendLine($"RESULT: {_ok} passed, {_fail} failed, {sw.Elapsed.TotalSeconds:0.0} s");
        File.WriteAllText(Path.Combine(_out, "report.txt"), Log.ToString(), new UTF8Encoding(false));
        Console.WriteLine(Log.ToString());
        try { Directory.Delete(_work, true); } catch { }   // derived copies are scratch; the (well-formed) fixtures stay for inspection
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ harness

    private static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "BetterNotepad.sln"))) d = d.Parent;
        if (d == null) throw new InvalidOperationException("Repository root not found.");
        return d.FullName;
    }

    private static void Section(string name, Action a)
    {
        Log.AppendLine();
        Log.AppendLine("== " + name);
        try { a(); }
        catch (Exception ex) { Check("section completed without exception", false, ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); }
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) _ok++; else _fail++;
        Log.AppendLine((ok ? "  [ok]   " : "  [FAIL] ") + name + (ok || detail.Length == 0 ? "" : "  -> " + detail));
    }

    private static void Note(string s) => Log.AppendLine("  " + s);

    private static string Fix(string name, byte[] data) { var p = Path.Combine(_fix, name); File.WriteAllBytes(p, data); return p; }
    private static string Copy(string src, string name) { var p = Path.Combine(_work, name); File.Copy(src, p, true); File.SetAttributes(p, FileAttributes.Normal); return p; }

    private static bool Has(byte[] data, string s)
    {
        foreach (var enc in new Encoding[] { Encoding.UTF8, Encoding.Latin1, Encoding.Unicode, Encoding.BigEndianUnicode })
            if (data.AsSpan().IndexOf(enc.GetBytes(s)) >= 0) return true;
        return false;
    }

    private static string Summary(MetadataReport r) =>
        string.Join(", ", r.Groups.Select(g => g.Id + ":" + g.Entries.Count)) + (r.Warnings.Count > 0 ? "  warnings=" + string.Join("|", r.Warnings) : "");

    private static (int W, int H, byte[] Px) Pixels(string path)
    {
        using var fs = File.OpenRead(path);
        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var conv = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgra32, null, 0);
        var px = new byte[conv.PixelWidth * conv.PixelHeight * 4];
        conv.CopyPixels(px, conv.PixelWidth * 4, 0);
        return (conv.PixelWidth, conv.PixelHeight, px);
    }

    private static bool SamePixels(string a, string b)
    {
        var x = Pixels(a); var y = Pixels(b);
        return x.W == y.W && x.H == y.H && x.Px.AsSpan().SequenceEqual(y.Px);
    }

    private static string? Wic(string path, string query)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var md = dec.Frames[0].Metadata as BitmapMetadata;
            return md?.GetQuery(query)?.ToString();
        }
        catch { return null; }
    }

    private static string Val(MetadataReport r, string key) => r.Find(key)?.Value ?? "<missing>";
    private static bool Near(string v, double expect, double tol = 0.0001) => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && Math.Abs(d - expect) < tol;

    // ------------------------------------------------------------------ JPEG

    private static void TestJpeg()
    {
        var src = Fix("photo.jpg", Fixtures.BuildJpeg());
        var orig = File.ReadAllBytes(src);
        var rep = MetadataReader.Read(src);
        Note("read: " + Summary(rep));
        Check("format detected as JPEG", rep.Format == MetadataFormat.Jpeg);
        Check("EXIF Make / Model / Orientation", Val(rep, "exif.Make") == "ACME" && Val(rep, "exif.Model") == "SnapMaster 3000" && Val(rep, "exif.Orientation") == "6");
        Check("EXIF exposure values (1/250, f2.8, ISO200)", Val(rep, "exif.ExposureTime") == "1/250" && Val(rep, "exif.FNumber") == "2.8" && Val(rep, "exif.ISO") == "200");
        Check("EXIF date parsed to ISO", Val(rep, "exif.DateTimeOriginal") == "2024-05-17T14:29:58" && rep.Find("exif.DateTimeOriginal")!.Kind == MetadataValueKind.DateTime);
        Check("EXIF XPAuthor (UTF-16) + UserComment + ExifVersion", Val(rep, "exif.XPAuthor") == "Jan K." && Val(rep, "exif.UserComment") == "Comment by fixture" && Val(rep, "exif.ExifVersion") == "0230");
        Check("EXIF thumbnail reported (sensitive)", rep.Find("exif.Thumbnail") is { Sensitive: true });
        Check("GPS lat/lon/alt decimal", Near(Val(rep, "gps.Latitude"), 52.23) && Near(Val(rep, "gps.Longitude"), 21.008333, 0.00001) && Val(rep, "gps.Altitude") == "110.5");
        Check("GPS entries are sensitive", rep.Groups.First(g => g.Id == MetadataGroupId.Gps).Entries.All(e => e.Sensitive));
        Check("Artist/Software sensitive, Make editable", rep.Find("exif.Artist")!.Sensitive && rep.Find("exif.Software")!.Sensitive && rep.Find("exif.Make")!.Editable && !rep.Find("exif.Orientation")!.Editable);
        Check("XMP flattened (creator, title with Polish chars, subject bag, attributes)",
            Val(rep, "xmp.dc.creator") == "Jan Kowalski" && Val(rep, "xmp.dc.title") == "Zachód słońca" && Val(rep, "xmp.dc.subject") == "sunset; sky"
            && Val(rep, "xmp.xmp.CreatorTool") == "Fixture Editor 9" && Val(rep, "xmp.photoshop.City") == "Warszawa" && rep.Find("xmp.dc.creator")!.Sensitive);
        Check("IPTC (by-line, city, keywords, copyright, caption)",
            Val(rep, "iptc.ByLine") == "Jan Kowalski" && Val(rep, "iptc.City") == "Warszawa" && Val(rep, "iptc.Keywords") == "sunset; sky"
            && Val(rep, "iptc.CopyrightNotice") == "© Jan Kowalski" && Val(rep, "iptc.CaptionAbstract") == "Zachód słońca nad Wisłą");
        Check("COM comment", Val(rep, "text.Comment") == "Created by the fixture builder");
        Check("label keys are stable (meta.exif.Model)", rep.Find("exif.Model")!.LabelKey == "meta.exif.Model" && rep.Find("gps.Latitude")!.LabelKey == "meta.gps.Latitude");
        Check("independent reader (WPF/WIC) sees Make and GPS ref", Wic(src, "/app1/ifd/{ushort=271}") == "ACME" && Wic(src, "/app1/ifd/gps/{ushort=1}") == "N",
              $"make={Wic(src, "/app1/ifd/{ushort=271}")} ref={Wic(src, "/app1/ifd/gps/{ushort=1}")}");

        // big-endian EXIF variant is read the same way
        var be = Fix("photo_be.jpg", Fixtures.BuildJpeg(1, le: false));
        var rbe = MetadataReader.Read(be);
        Check("big-endian EXIF read", Val(rbe, "exif.Make") == "ACME" && Near(Val(rbe, "gps.Latitude"), 52.23));

        // ---- edit
        var ed = Copy(src, "photo_edit.jpg");
        var changes = new Dictionary<string, string?>
        {
            ["exif.Artist"] = "Maria Nowak-Wisniewska",   // longer than the old value: relocated
            ["exif.Model"] = "X",                          // shorter: in place
            ["exif.DateTimeOriginal"] = "2020-01-02T03:04:05",
            ["exif.XPAuthor"] = "Zażółć gęślą",
            ["exif.Software"] = null,                      // clear
            ["exif.Orientation"] = "1",                    // not editable
            ["exif.Bogus"] = "x",
            ["file.Hidden"] = "false",
        };
        var res = MetadataWriter.Set(ed, changes);
        Note("applied: " + string.Join(",", res.Applied) + "  rejected: " + string.Join(",", res.Rejected.Select(kv => kv.Key + "=" + kv.Value)));
        var r2 = MetadataReader.Read(ed);
        Check("EXIF edits applied and read back",
            Val(r2, "exif.Artist") == "Maria Nowak-Wisniewska" && Val(r2, "exif.Model") == "X" && Val(r2, "exif.DateTimeOriginal") == "2020-01-02T03:04:05"
            && Val(r2, "exif.XPAuthor") == "Zażółć gęślą" && Val(r2, "exif.Software") == "");
        Check("non-editable / unknown keys rejected", res.Rejected.TryGetValue("exif.Orientation", out var why) && why == "notEditable" && res.Rejected.ContainsKey("exif.Bogus"));
        Check("other EXIF intact after relocation (Make, GPS, thumbnail, Orientation)",
            Val(r2, "exif.Make") == "ACME" && Near(Val(r2, "gps.Latitude"), 52.23) && r2.Find("exif.Thumbnail") != null && Val(r2, "exif.Orientation") == "6");
        Check("old Artist value is gone from the EXIF block", !Has(File.ReadAllBytes(ed), "Jan Kowalski\0"));
        Check("edited JPEG: pixels identical", SamePixels(src, ed));
        Check("independent reader sees new Artist", Wic(ed, "/app1/ifd/{ushort=315}") == "Maria Nowak-Wisniewska", "got " + Wic(ed, "/app1/ifd/{ushort=315}"));

        // ---- GPS only
        var gp = Copy(src, "photo_gps.jpg");
        var sr = MetadataWriter.Strip(gp, new StripOptions { Exif = false, GpsOnly = true, Xmp = false, Iptc = false, TextAndComments = false, OtherEmbedded = false, ZoneIdentifier = false });
        var rg = MetadataReader.Read(gp);
        Check("GPS-only strip: location gone, rest kept", sr.ContentChanged && sr.Removed.Contains("jpeg.gps") && rg.Groups.All(g => g.Id != MetadataGroupId.Gps)
            && Val(rg, "exif.Make") == "ACME" && rg.Find("exif.Thumbnail") != null && rg.Groups.Any(g => g.Id == MetadataGroupId.Xmp) && rg.Groups.Any(g => g.Id == MetadataGroupId.Iptc));
        Check("GPS-only strip: file size unchanged, pixels identical, WIC still reads Make", new FileInfo(gp).Length == orig.Length && SamePixels(src, gp) && Wic(gp, "/app1/ifd/{ushort=271}") == "ACME");

        // ---- full strip (keeps Orientation)
        var st = Copy(src, "photo_strip.jpg");
        var before = new FileInfo(st).Length;
        var sa = MetadataWriter.Strip(st);
        var rs = MetadataReader.Read(st);
        Note($"strip: removed [{string.Join(",", sa.Removed)}] {sa.BytesBefore} -> {sa.BytesAfter} bytes; read: " + Summary(rs));
        Check("strip removes EXIF/GPS/XMP/IPTC/COM", sa.ContentChanged && rs.Groups.All(g => g.Id is MetadataGroupId.File or MetadataGroupId.Exif));
        var exifLeft = rs.Groups.FirstOrDefault(g => g.Id == MetadataGroupId.Exif)?.Entries ?? new();
        Check("only the Orientation tag survives", exifLeft.Count == 1 && exifLeft[0].Key == "exif.Orientation" && exifLeft[0].Value == "6");
        Check("pixels identical after strip (decoded)", SamePixels(src, st));
        var o1 = File.ReadAllBytes(src); var o2 = File.ReadAllBytes(st);
        int sos1 = IndexOfSos(o1), sos2 = IndexOfSos(o2);
        Check("entropy-coded data byte-identical (no recompression)", sos1 > 0 && sos2 > 0 && o1.AsSpan(sos1).SequenceEqual(o2.AsSpan(sos2)));
        Check("independent reader: no Make, Orientation 6 kept", Wic(st, "/app1/ifd/{ushort=271}") == null && Wic(st, "/app1/ifd/{ushort=274}") == "6", $"make={Wic(st, "/app1/ifd/{ushort=271}")} ori={Wic(st, "/app1/ifd/{ushort=274}")}");
        Check("no personal strings left in the bytes", !Has(o2, "Kowalski") && !Has(o2, "ACME") && !Has(o2, "Warszawa") && !Has(o2, "fixture builder") && !Has(o2, "SN12345"));
        Check("file got smaller", sa.BytesAfter < before);
        var again = MetadataWriter.Strip(st);
        Check("second strip is a no-op (nothing rewritten)", !again.ContentChanged);

        var st2 = Copy(src, "photo_strip_noori.jpg");
        MetadataWriter.Strip(st2, new StripOptions { KeepOrientation = false });
        Check("KeepOrientation=false leaves no EXIF at all", MetadataReader.Read(st2).Groups.All(g => g.Id == MetadataGroupId.File) && SamePixels(src, st2));

        // ---- robustness: truncated / junk
        var rj = MemRead(orig.Take(300).ToArray());   // truncated copy, memory only
        Check("truncated JPEG: reader does not throw, reports partial", rj.Format == MetadataFormat.Jpeg && rj.Warnings.Contains("read.partial"));
        Note("truncated: " + Summary(rj));
    }

    private static int IndexOfSos(byte[] d)
    {
        for (int i = 2; i + 1 < d.Length; i++)
        {
            if (d[i] != 0xFF) continue;
            byte m = d[i + 1];
            if (m == 0xDA) return i;
            if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7) || m == 0xFF || m == 0x00) continue;
            i += 1 + (d[i + 2] << 8 | d[i + 3]);
        }
        return -1;
    }

    // ------------------------------------------------------------------ PNG

    private static List<(string Type, byte[] Bytes)> PngChunks(byte[] d)
    {
        var l = new List<(string, byte[])>();
        int p = 8;
        while (p + 12 <= d.Length)
        {
            int len = d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3];
            l.Add((Encoding.ASCII.GetString(d, p + 4, 4), d.AsSpan(p, 12 + len).ToArray()));
            p += 12 + len;
        }
        return l;
    }

    private static void TestPng()
    {
        var src = Fix("image.png", Fixtures.BuildPng());
        var rep = MetadataReader.Read(src);
        Note("read: " + Summary(rep));
        Check("format detected as PNG", rep.Format == MetadataFormat.Png);
        Check("tEXt / zTXt / iTXt (UTF-8) / tIME",
            Val(rep, "text.Author") == "Anna Nowak" && Val(rep, "text.Software") == "Fixture Paint 2" && Val(rep, "text.Comment") == "A secret compressed comment"
            && Val(rep, "text.Description") == "Zażółć gęślą jaźń" && Val(rep, "text.tIME") == "2024-05-17T14:30:00");
        Check("text keywords: known -> label key, Author sensitive", rep.Find("text.Author")!.LabelKey == "meta.text.Author" && rep.Find("text.Author")!.Sensitive);
        Check("eXIf (big-endian) + GPS + XMP read", Val(rep, "exif.Make") == "ACME" && Val(rep, "exif.Orientation") == "3" && Near(Val(rep, "gps.Latitude"), 52.23) && Val(rep, "xmp.dc.creator") == "Jan Kowalski");

        var st = Copy(src, "image_strip.png");
        var sr = MetadataWriter.Strip(st);
        var rs = MetadataReader.Read(st);
        Note($"strip: removed [{string.Join(",", sr.Removed)}] {sr.BytesBefore} -> {sr.BytesAfter}; read: " + Summary(rs));
        var left = rs.Groups.FirstOrDefault(g => g.Id == MetadataGroupId.Exif)?.Entries ?? new();
        Check("only Orientation survives, no text / XMP / GPS", sr.ContentChanged && left.Count == 1 && left[0].Value == "3" && rs.Groups.All(g => g.Id is MetadataGroupId.File or MetadataGroupId.Exif));
        var a = PngChunks(File.ReadAllBytes(src)).Where(c => c.Type is "IHDR" or "IDAT" or "IEND" or "PLTE" or "pHYs").Select(c => c.Type + Convert.ToHexString(SHA256.HashData(c.Bytes))).ToList();
        var b = PngChunks(File.ReadAllBytes(st)).Where(c => c.Type is "IHDR" or "IDAT" or "IEND" or "PLTE" or "pHYs").Select(c => c.Type + Convert.ToHexString(SHA256.HashData(c.Bytes))).ToList();
        Check("IHDR/IDAT/IEND chunks byte-identical", a.Count >= 3 && a.SequenceEqual(b));
        Check("PNG still decodes with identical pixels", SamePixels(src, st));
        var bytes = File.ReadAllBytes(st);
        Check("no personal strings left", !Has(bytes, "Anna") && !Has(bytes, "secret") && !Has(bytes, "Kowalski") && !Has(bytes, "ACME") && !Has(bytes, "Zażółć"));
        Check("second strip is a no-op", !MetadataWriter.Strip(st).ContentChanged);

        var st2 = Copy(src, "image_strip2.png");
        MetadataWriter.Strip(st2, new StripOptions { KeepOrientation = false });
        Check("KeepOrientation=false: no eXIf chunk", PngChunks(File.ReadAllBytes(st2)).All(c => c.Type != "eXIf") && SamePixels(src, st2));
    }

    // ------------------------------------------------------------------ WebP

    private static void TestWebp()
    {
        var src = Fix("image.webp", Fixtures.BuildWebp());
        var rep = MetadataReader.Read(src);
        Note("read: " + Summary(rep));
        Check("format detected as WebP + EXIF/XMP/GPS read", rep.Format == MetadataFormat.WebP && Val(rep, "exif.Make") == "ACME" && Val(rep, "xmp.dc.creator") == "Jan Kowalski" && Near(Val(rep, "gps.Latitude"), 52.23));
        var st = Copy(src, "image_strip.webp");
        var sr = MetadataWriter.Strip(st);
        var d = File.ReadAllBytes(st);
        var rs = MetadataReader.Read(st);
        Note($"strip: removed [{string.Join(",", sr.Removed)}] {sr.BytesBefore} -> {sr.BytesAfter}; read: " + Summary(rs));
        uint riff = BitConverter.ToUInt32(d, 4);
        Check("RIFF size consistent", riff == d.Length - 8, $"riff={riff} len={d.Length}");
        Check("VP8X flags: XMP cleared, EXIF kept for the orientation stub", (d[20] & 0x04) == 0 && (d[20] & 0x08) != 0, "flags=" + d[20].ToString("X2"));
        var left = rs.Groups.FirstOrDefault(g => g.Id == MetadataGroupId.Exif)?.Entries ?? new();
        Check("only Orientation survives", left.Count == 1 && left[0].Value == "6" && rs.Groups.All(g => g.Id is MetadataGroupId.File or MetadataGroupId.Exif));
        var vp8 = Convert.FromBase64String("UklGRiIAAABXRUJQVlA4IBYAAAAwAQCdASoBAAEADsD+JaQAA3AAAAAA")[12..];
        Check("VP8 bitstream chunk untouched", d.AsSpan().IndexOf(vp8) >= 0);
        Check("second strip is a no-op", !MetadataWriter.Strip(st).ContentChanged);
        var st2 = Copy(src, "image_strip2.webp");
        MetadataWriter.Strip(st2, new StripOptions { KeepOrientation = false });
        var d2 = File.ReadAllBytes(st2);
        Check("KeepOrientation=false: flags 0, no EXIF/XMP chunk", d2[20] == 0 && !Has(d2, "EXIF") && !Has(d2, "XMP ") && BitConverter.ToUInt32(d2, 4) == d2.Length - 8);
        try { var px = Pixels(st2); Note($"WIC decoded the stripped WebP ({px.W}x{px.H})"); } catch (Exception ex) { Note("WIC WebP decoder unavailable here (" + ex.GetType().Name + "); structure verified instead"); }
    }

    // ------------------------------------------------------------------ PDF

    private static void CheckPdfOpens(string label, string path, int pages = 1)
    {
        var info = Pdfium.Open(path);
        Check(label + ": PDFium opens it, " + pages + " page(s)", info.Pages == pages, "pages=" + info.Pages);
    }

    private static void TestPdfClassic()
    {
        var src = Fix("doc.pdf", Fixtures.BuildPdfClassic());
        var orig = File.ReadAllBytes(src);
        CheckPdfOpens("fixture", src);
        var rep = MetadataReader.Read(src);
        Note("read: " + Summary(rep));
        Check("format detected as PDF, version/pages", rep.Format == MetadataFormat.Pdf && Val(rep, "pdf.Version") == "1.4" && Val(rep, "pdf.Pages") == "1" && Val(rep, "pdf.Encrypted") == "false");
        Check("newest Info revision wins (incremental update)", Val(rep, "pdf.Title") == "Zażółć Secret v2" && Val(rep, "pdf.Author") == "Revised Secret Author" && Val(rep, "pdf.Producer") == "Fixture Producer 2");
        Check("custom Info key listed (read-only, sensitive)", rep.Find("pdf.Custom.Project") is { Editable: false, Sensitive: true, Value: "Top Secret Project" });
        Check("dates parsed", Val(rep, "pdf.CreationDate").StartsWith("2024-05-17T14:30:00+02:00"), Val(rep, "pdf.CreationDate"));
        Check("XMP of the catalog read", Val(rep, "xmp.dc.title") == Fixtures.PdfSecretTitle && Val(rep, "xmp.dc.creator") == Fixtures.PdfSecretAuthor && Val(rep, "xmp.pdf.Producer") == "Fixture Producer 1");

        // ---- edit (incremental update)
        var ed = Copy(src, "doc_edit.pdf");
        var res = MetadataWriter.Set(ed, new Dictionary<string, string?>
        {
            ["pdf.Title"] = "Nowy tytuł (z nawiasami)", ["pdf.Author"] = "Ala Ma Kota", ["pdf.Keywords"] = "a, b, c", ["pdf.Subject"] = null,
            ["pdf.CreationDate"] = "2021-03-04T05:06:07+01:00", ["pdf.Trapped"] = "True", ["pdf.Nope"] = "x",
        });
        Note("applied: " + string.Join(",", res.Applied) + "  rejected: " + string.Join(",", res.Rejected.Select(kv => kv.Key + "=" + kv.Value)));
        var r2 = MetadataReader.Read(ed);
        Check("PDF edit applied and read back",
            Val(r2, "pdf.Title") == "Nowy tytuł (z nawiasami)" && Val(r2, "pdf.Author") == "Ala Ma Kota" && Val(r2, "pdf.Keywords") == "a, b, c" && Val(r2, "pdf.Subject") == ""
            && Val(r2, "pdf.CreationDate").StartsWith("2021-03-04T05:06:07+01:00") && Val(r2, "pdf.Producer") == "Fixture Producer 2" && Val(r2, "pdf.Custom.Project") == "Top Secret Project");
        Check("unknown / read-only keys rejected", res.Rejected.ContainsKey("pdf.Trapped") && res.Rejected.ContainsKey("pdf.Nope"));
        Check("edit is a pure append (original bytes untouched)", File.ReadAllBytes(ed).AsSpan(0, orig.Length).SequenceEqual(orig));
        Check("XMP kept in sync with Info (dc:title, dc:creator)", Val(r2, "xmp.dc.title") == "Nowy tytuł (z nawiasami)" && Val(r2, "xmp.dc.creator") == "Ala Ma Kota", $"{Val(r2, "xmp.dc.title")} / {Val(r2, "xmp.dc.creator")}");
        CheckPdfOpens("edited", ed);
        var info = Pdfium.Open(ed);
        Check("PDFium sees the new Title / Author", info.Meta["Title"] == "Nowy tytuł (z nawiasami)" && info.Meta["Author"] == "Ala Ma Kota", $"{info.Meta["Title"]} / {info.Meta["Author"]}");
        var ed2 = MetadataWriter.Set(ed, new Dictionary<string, string?> { ["pdf.Producer"] = "Second edit" });
        Check("second edit stacks a further revision", MetadataReader.Read(ed).Find("pdf.Producer")!.Value == "Second edit" && ed2.Applied.Count == 1);

        // ---- strip (full rewrite, old strings must disappear from the bytes)
        Check("fixture really contains the old strings", Has(orig, "Secret Author") && Has(orig, "Fixture Producer 1") && Has(orig, "Top Secret"));
        var st = Copy(src, "doc_strip.pdf");
        var sr = MetadataWriter.Strip(st);
        var d = File.ReadAllBytes(st);
        var rs = MetadataReader.Read(st);
        Note($"strip: removed [{string.Join(",", sr.Removed)}] {sr.BytesBefore} -> {sr.BytesAfter}; read: " + Summary(rs));
        Check("strip removed Info + XMP", sr.ContentChanged && sr.Removed.Contains("pdf.info") && sr.Removed.Contains("pdf.xmp"));
        Check("reader finds no Info values and no XMP", rs.Groups.All(g => g.Id is MetadataGroupId.File or MetadataGroupId.Pdf) && (rs.Find("pdf.Title")?.Value ?? "") == "" && (rs.Find("pdf.Author")?.Value ?? "") == "" && rs.Find("pdf.Custom.Project") == null);
        Check("no old string anywhere in the file (old revisions rewritten away)",
            !Has(d, "Secret") && !Has(d, "Producer") && !Has(d, "Top") && !Has(d, "Old Co") && !Has(d, "OldSubject") && !Has(d, "Fixture") && !Has(d, "xpacket"));
        Check("single revision, classic xref, page content kept", Has(d, "Hello metadata") && Count(d, "startxref") == 1 && Count(d, "%%EOF") == 1);
        CheckPdfOpens("stripped", st);
        var si = Pdfium.Open(st);
        Check("PDFium sees empty metadata", si.Meta.Values.All(v => v == ""), string.Join("|", si.Meta.Values));
        Check("second strip is a no-op", !MetadataWriter.Strip(st).ContentChanged);

        // partial strip: keep Info, drop XMP only
        var part = Copy(src, "doc_strip_xmp_only.pdf");
        MetadataWriter.Strip(part, new StripOptions { DocumentProperties = false });
        var rp = MetadataReader.Read(part);
        Check("DocumentProperties=false keeps Info, drops XMP", Val(rp, "pdf.Author") == "Revised Secret Author" && rp.Groups.All(g => g.Id != MetadataGroupId.Xmp));
        CheckPdfOpens("partial strip", part);
    }

    private static int Count(byte[] d, string s)
    {
        int n = 0, i = 0; var needle = Encoding.ASCII.GetBytes(s);
        while (true) { int x = d.AsSpan(i).IndexOf(needle); if (x < 0) break; n++; i += x + needle.Length; }
        return n;
    }

    private static void TestPdfXrefStream()
    {
        var src = Fix("doc_xrefstm.pdf", Fixtures.BuildPdfXrefStream());
        var orig = File.ReadAllBytes(src);
        CheckPdfOpens("fixture", src);
        var rep = MetadataReader.Read(src);
        Note("read: " + Summary(rep));
        Check("Info inside object stream + flate XMP read", Val(rep, "pdf.Title") == Fixtures.PdfSecretTitle && Val(rep, "pdf.Producer") == "Compressed Producer" && Val(rep, "xmp.dc.creator") == Fixtures.PdfSecretAuthor && Val(rep, "pdf.Version") == "1.5");
        Check("Info is not in the file as plain text", !Has(orig, "Compressed Producer"));

        var ed = Copy(src, "doc_xrefstm_edit.pdf");
        var res = MetadataWriter.Set(ed, new Dictionary<string, string?> { ["pdf.Title"] = "Edited in xref-stream file", ["pdf.Subject"] = "Subj" });
        var r2 = MetadataReader.Read(ed);
        Check("edit appended with an xref-stream revision", res.Applied.Count == 2 && Val(r2, "pdf.Title") == "Edited in xref-stream file" && Val(r2, "pdf.Subject") == "Subj" && Val(r2, "pdf.Producer") == "Compressed Producer" && Val(r2, "xmp.dc.title") == "Edited in xref-stream file", Summary(r2));
        CheckPdfOpens("edited", ed);
        Check("PDFium sees new Title", Pdfium.Open(ed).Meta["Title"] == "Edited in xref-stream file");

        var st = Copy(src, "doc_xrefstm_strip.pdf");
        var sr = MetadataWriter.Strip(st);
        var d = File.ReadAllBytes(st);
        var rs = MetadataReader.Read(st);
        Note($"strip: removed [{string.Join(",", sr.Removed)}] {sr.BytesBefore} -> {sr.BytesAfter}; read: " + Summary(rs));
        Check("strip expanded object streams, dropped Info + XMP", sr.ContentChanged && !Has(d, "ObjStm") && !Has(d, "/Type /XRef") && Has(d, "xref\n0 ") && (rs.Find("pdf.Title")?.Value ?? "") == "" && rs.Groups.All(g => g.Id != MetadataGroupId.Xmp));
        Check("page content kept", Has(d, "Hello compressed"));
        CheckPdfOpens("stripped", st);
        Check("PDFium sees empty metadata", Pdfium.Open(st).Meta.Values.All(v => v == ""));
    }

    private static void TestPdfDamaged()
    {
        // Malformed input is handled strictly in memory: it is never written to disk and never given to a system decoder.
        var good = Fixtures.BuildPdfClassic(withRevision: false);
        var txt = Encoding.Latin1.GetString(good);

        // (a) wrong startxref
        int sx = txt.LastIndexOf("startxref", StringComparison.Ordinal);
        var bad = Encoding.Latin1.GetBytes(txt.Substring(0, sx) + "startxref\n999999\n%%EOF\n");
        var ra = MemRead(bad);
        Check("wrong startxref: table rebuilt, Info still read", Val(ra, "pdf.Author") == Fixtures.PdfSecretAuthor && ra.Warnings.Contains("pdf.xrefRebuilt"), Summary(ra));
        var outA = MemStrip(bad, out var remA);
        Check("rewritten from damaged file: valid again (PDFium opens it, 1 page)", outA != null && Pdfium.Open(outA).Pages == 1);
        Check("rewritten file is clean and has a valid xref", outA != null && !Has(outA, "Secret") && !MemRead(outA).Warnings.Contains("pdf.xrefRebuilt") && remA.Contains("pdf.info"));
        try { MemSet(bad, new Dictionary<string, string?> { ["pdf.Title"] = "x" }); Check("edit of a damaged file is refused", false); }
        catch (MetadataException ex) { Check("edit of a damaged file is refused (Corrupt)", ex.Code == MetadataErrorCode.Corrupt, ex.Code.ToString()); }

        // (b) all offsets shifted (junk inserted after the header)
        var shifted = Encoding.Latin1.GetBytes(txt.Insert(txt.IndexOf('\n') + 1, "% inserted junk line\n"));
        var rb = MemRead(shifted);
        Check("shifted offsets: rebuilt and readable", Val(rb, "pdf.Title") == Fixtures.PdfSecretTitle && rb.Warnings.Contains("pdf.xrefRebuilt"), Summary(rb));
        var outB = MemStrip(shifted, out _);
        Check("rewritten from shifted file: PDFium opens it", outB != null && Pdfium.Open(outB).Pages == 1);

        // (c) garbage prefix before %PDF- (offsets are relative to the header)
        var prefixed = Encoding.Latin1.GetBytes("GARBAGE-BEFORE-HEADER\r\n").Concat(good).ToArray();
        var rc = MemRead(prefixed);
        Check("junk before the header: still read without rebuild", Val(rc, "pdf.Title") == Fixtures.PdfSecretTitle && !rc.Warnings.Contains("pdf.xrefRebuilt"), Summary(rc));
        var outC = MemSet(prefixed, new Dictionary<string, string?> { ["pdf.Title"] = "Prefixed ok" });
        Check("junk before the header: incremental edit offsets are relative to the header", outC != null && Val(MemRead(outC), "pdf.Title") == "Prefixed ok");

        // (d) nothing / not really a PDF
        var re = MemRead(Array.Empty<byte>());
        Check("empty input: unknown format, nothing read", re.Format == MetadataFormat.Unknown && re.Groups.Count == 0);
        var fake = Encoding.ASCII.GetBytes("%PDF-1.4\nthis is not really a pdf\n");
        var rf = MemRead(fake);
        Check("bogus PDF: reader survives with a warning", rf.Format == MetadataFormat.Pdf && rf.Warnings.Contains("read.partial"));
        try { MemStrip(fake, out _); Check("bogus PDF strip is refused", false); }
        catch (MetadataException ex) { Check("bogus PDF strip is refused", ex.Code == MetadataErrorCode.Corrupt, ex.Code.ToString()); }
    }

    private static void TestEdgeCases()
    {
        // All hand-made JPEG variants stay in memory (well-formed or not, they are never written to disk or decoded by a system codec).
        var baseJpeg = Fixtures.BuildJpeg();
        var icc = AppSeg(0xE2, Encoding.ASCII.GetBytes("ICC_PROFILE\0").Concat(new byte[] { 1, 1 }).Concat(Enumerable.Repeat((byte)0x5A, 40)).ToArray());
        var mpf = AppSeg(0xE2, Encoding.ASCII.GetBytes("MPF\0").Concat(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }).ToArray());
        var vendor = AppSeg(0xEB, Encoding.ASCII.GetBytes("JUMBF-C2PA-manifest-secret"));
        int com = FindMarker(baseJpeg, 0xFE);
        Check("fixture markers located", com > 0 && FindMarker(baseJpeg, 0xDA) > com);
        bool SameScan(byte[] x, byte[] y) { int a = IndexOfSos(x), b = IndexOfSos(y); return a > 0 && b > 0 && x.AsSpan(a).SequenceEqual(y.AsSpan(b)); }

        // ICC kept, vendor APP11 dropped, MPF dropped because a segment behind it (the COM) is removed
        var a = InsertBefore(InsertBefore(InsertBefore(baseJpeg, com, icc), com, vendor), com, mpf);
        var da = MemStrip(a, out var remA);
        Check("ICC profile kept", da != null && Has(da, "ICC_PROFILE"));
        Check("vendor / C2PA APP11 removed", da != null && !Has(da, "JUMBF-C2PA") && remA.Contains("jpeg.other"));
        Check("MPF removed together with segments behind it (its offsets would break)", da != null && remA.Contains("jpeg.mpf") && !Has(da, "MPF"));
        Check("scan data identical", da != null && SameScan(a, da));

        // MPF as the last header segment: nothing behind it is removed, so it stays
        var b = InsertBefore(baseJpeg, FindMarker(baseJpeg, 0xDA), mpf);
        var db = MemStrip(b, out var remB);
        Check("MPF kept when no removed segment follows it", db != null && !remB.Contains("jpeg.mpf") && Has(db, "MPF") && SameScan(b, db));

        // COM after the scan data must be found and removed
        int eoi = baseJpeg.Length - 2;
        Check("fixture ends with EOI", baseJpeg[eoi] == 0xFF && baseJpeg[eoi + 1] == 0xD9);
        var tailCom = InsertBefore(baseJpeg, eoi, AppSeg(0xFE, Encoding.ASCII.GetBytes("trailing-comment-after-scan")));
        Check("comment after the scan is read", MemRead(tailCom).Groups.SelectMany(g => g.Entries).Any(e => e.Value == "trailing-comment-after-scan"));
        var dc = MemStrip(tailCom, out _);
        Check("comment after the scan is removed, scan intact", dc != null && !Has(dc, "trailing-comment") && IndexOfSos(dc) > 0 && dc.AsSpan(IndexOfSos(dc)).SequenceEqual(baseJpeg.AsSpan(IndexOfSos(baseJpeg))));

        // data after EOI is preserved byte for byte
        var withTrailer = baseJpeg.Concat(Encoding.ASCII.GetBytes("TRAILER-DATA-AFTER-EOI")).ToArray();
        var dd = MemStrip(withTrailer, out _);
        Check("bytes after EOI preserved", dd != null && Has(dd, "TRAILER-DATA-AFTER-EOI"));

        // segment length below 2 / longer than the file: refused cleanly, never decoded
        var zeroLenCom = InsertBefore(baseJpeg, com, new byte[] { 0xFF, 0xFE, 0x00, 0x00 });
        var rz = MemRead(zeroLenCom);
        Check("COM segment with length 0: reader reports a partial read, no exception", rz.Warnings.Contains("read.partial"));
        try { MemStrip(zeroLenCom, out _); Check("COM segment with length 0: strip refused", false); }
        catch (MetadataException ex) { Check("COM segment with length 0: strip refused (Corrupt)", ex.Code == MetadataErrorCode.Corrupt); }
        var longSeg = InsertBefore(baseJpeg, com, new byte[] { 0xFF, 0xFE, 0xFF, 0xFF, 1, 2, 3 });
        try { MemStrip(longSeg, out _); Check("oversized segment length: strip refused", false); }
        catch (MetadataException ex) { Check("oversized segment length: strip refused (Corrupt)", ex.Code == MetadataErrorCode.Corrupt); }

        // encrypted PDF (structure only): reader warns, strip / edit refuse
        var good = Encoding.Latin1.GetString(Fixtures.BuildPdfClassic(withRevision: false));
        var enc = Encoding.Latin1.GetBytes(good.Replace("/Root 1 0 R", "/Root 1 0 R /Encrypt 7 0 R"));
        var re = MemRead(enc);
        Check("encrypted PDF: warning + basic entries only", re.Warnings.Contains("pdf.encrypted") && Val(re, "pdf.Encrypted") == "true" && re.Find("pdf.Title") == null);
        try { MemStrip(enc, out _); Check("encrypted PDF strip refused", false); }
        catch (MetadataException ex) { Check("encrypted PDF strip refused (Encrypted)", ex.Code == MetadataErrorCode.Encrypted); }
        try { MemSet(enc, new Dictionary<string, string?> { ["pdf.Title"] = "x" }); Check("encrypted PDF edit refused", false); }
        catch (MetadataException ex) { Check("encrypted PDF edit refused (Encrypted)", ex.Code == MetadataErrorCode.Encrypted); }

        // broken ZIP posing as docx
        var zip = File.ReadAllBytes(Path.Combine(_fix, "sample.docx"));
        var rb = MemRead(zip.Take(zip.Length / 2).ToArray());
        Check("truncated docx: reader survives", rb.Format == MetadataFormat.Unknown && rb.Groups.Count == 0);

        // an ordinary zip is not an Office package
        using var plain = new MemoryStream();
        using (var zf = new System.IO.Compression.ZipArchive(plain, System.IO.Compression.ZipArchiveMode.Create, true)) { var en = zf.CreateEntry("a.txt"); using var w = new StreamWriter(en.Open()); w.Write("hi"); }
        Check("plain zip is not treated as Office", MemRead(plain.ToArray()).Format == MetadataFormat.Unknown);

        // content wins over extension (valid file, safe on disk)
        var misnamed = Copy(Path.Combine(_fix, "photo.jpg"), "really_a_jpeg.txt");
        Check("format is sniffed from content, not from the extension", MetadataReader.Read(misnamed).Format == MetadataFormat.Jpeg && MetadataWriter.Strip(misnamed).ContentChanged);
    }

    // ------------------------------------------------------------------ scale

    private static void TestScale()
    {
        var proc = Process.GetCurrentProcess();
        var big = Path.Combine(_fix, "big.pdf");
        File.WriteAllBytes(big, Fixtures.BuildBigPdf(30000, 40));
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long mem0 = GC.GetTotalMemory(true);
        var sw = Stopwatch.StartNew();
        var rep = MetadataReader.Read(big);
        Note($"big PDF ({new FileInfo(big).Length / 1048576} MB, 30k objects) read in {sw.ElapsedMilliseconds} ms");
        Check("big PDF read", Val(rep, "pdf.Author") == Fixtures.PdfSecretAuthor && rep.Warnings.Count == 0);
        var work = Copy(big, "big_work.pdf");
        sw.Restart();
        var sr = MetadataWriter.Strip(work);
        long ms = sw.ElapsedMilliseconds;
        proc.Refresh();
        Note($"big PDF strip in {ms} ms, managed heap delta {(GC.GetTotalMemory(false) - mem0) / 1048576} MB, peak working set {proc.PeakWorkingSet64 / 1048576} MB");
        var after = MetadataReader.Read(work);
        Check("big PDF stripped (Info + XMP gone)", sr.ContentChanged && (after.Find("pdf.Author")?.Value ?? "") == "" && after.Groups.All(g => g.Id != MetadataGroupId.Xmp));
        Check("big PDF strip finished in reasonable time", ms < 20000, ms + " ms");
        Check("big PDF opens in PDFium", Pdfium.Open(work).Pages == 1);

        // JPEG with 60 MB of trailing filler (bytes < 0xFE only: no marker-like sequences anywhere)
        var jpg = Path.Combine(_fix, "big.jpg");
        var jb = Fixtures.BuildJpeg();
        var tail = new byte[60 * 1024 * 1024]; new Random(3).NextBytes(tail);
        for (int i = 0; i < tail.Length; i++) tail[i] = (byte)(tail[i] % 0xFE);
        using (var fs = File.Create(jpg)) { fs.Write(jb); fs.Write(tail); }
        var jw = Copy(jpg, "big_work.jpg");
        sw.Restart();
        var jr = MetadataWriter.Strip(jw);
        Note($"big JPEG ({new FileInfo(jpg).Length / 1048576} MB) strip in {sw.ElapsedMilliseconds} ms");
        Check("big JPEG stripped, trailing data preserved", jr.ContentChanged && jr.BytesAfter > 60L * 1024 * 1024 && jr.BytesBefore - jr.BytesAfter > 1000);
        File.Delete(big); File.Delete(jpg); File.Delete(work); File.Delete(jw);
    }

    // ------------------------------------------------------------------ fuzz (memory only)

    private static void TestFuzz()
    {
        // Corrupted files are NEVER written to disk (antivirus engines flag deliberately malformed JPEG / PDF / Office files) and never reach a
        // system decoder: the exact production code path (detect -> read / build -> verify) is driven with byte arrays.
        var rnd = new Random(20261012);
        var targets = new (string File, int Rounds)[]
        {
            ("photo.jpg", 800), ("image.png", 600), ("image.webp", 400), ("doc.pdf", 1200), ("doc_xrefstm.pdf", 1200), ("sample.docx", 300), ("sample.xlsx", 200),
        };
        var inner = new Dictionary<string, int>();
        var codes = new Dictionary<string, int>();
        var verif = new Dictionary<string, int>();
        int unexpected = 0, mutatedInput = 0, slow = 0, readThrows = 0, strips = 0, stripOk = 0, sets = 0, setOk = 0;
        string firstUnexpected = "";
        foreach (var (file, rounds) in targets)
        {
            var orig = File.ReadAllBytes(Path.Combine(_fix, file));
            string ext = Path.GetExtension(file);
            bool pdf = ext == ".pdf", jpeg = ext == ".jpg", office = ext is ".docx" or ".xlsx";
            for (int round = 0; round < rounds; round++)
            {
                var m = (byte[])orig.Clone();
                switch (rnd.Next(5))
                {
                    case 0: for (int k = 0, n = rnd.Next(1, 9); k < n; k++) m[rnd.Next(m.Length)] = (byte)rnd.Next(256); break;
                    case 1: m = m[..rnd.Next(1, m.Length)]; break;
                    case 2: { int at = rnd.Next(m.Length), len = rnd.Next(1, Math.Min(64, m.Length - at) + 1); Array.Clear(m, at, len); break; }
                    case 3: { int at = rnd.Next(m.Length), len = Math.Min(rnd.Next(1, 200), m.Length - at); m = m[..at].Concat(m[at..(at + len)]).Concat(m[at..]).ToArray(); break; }
                    default:
                        for (int k = 0, n = rnd.Next(1, 5); k < n; k++) { int idx = rnd.Next(2) == 0 ? rnd.Next(Math.Min(m.Length, 400)) : m.Length - 1 - rnd.Next(Math.Min(m.Length, 300)); m[idx] ^= (byte)(1 << rnd.Next(8)); }
                        break;
                }
                var fingerprint = SHA256.HashData(m);
                var sw = Stopwatch.StartNew();

                try { ContentOps.ReadBytes(m); }
                catch (Exception ex) { readThrows++; if (firstUnexpected.Length == 0) firstUnexpected = "Read: " + file + " #" + round + " " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace; }

                strips++;
                try { ContentOps.StripBytes(m, new StripOptions(), new List<string>()); stripOk++; }
                catch (MetadataException ex)
                {
                    if (ex.Code == MetadataErrorCode.VerificationFailed)
                    {
                        var vk = file + ": " + ex.Message; verif[vk] = verif.GetValueOrDefault(vk) + 1;
                    }
                    codes[ex.Code.ToString()] = codes.GetValueOrDefault(ex.Code.ToString()) + 1;
                    if (ex.InnerException != null) { var k = ex.InnerException.GetType().Name; inner[k] = inner.GetValueOrDefault(k) + 1; }
                }
                catch (Exception ex) { unexpected++; if (firstUnexpected.Length == 0) firstUnexpected = "Strip: " + file + " #" + round + " " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace; }

                if (pdf || jpeg || office)
                {
                    sets++;
                    var ch = new Dictionary<string, string?>();
                    if (pdf) { ch["pdf.Title"] = "Fuzz title"; ch["pdf.Author"] = null; }
                    else if (jpeg) { ch["exif.Artist"] = "Fuzz artist that is long enough to relocate"; ch["exif.Model"] = "Z"; }
                    else { ch["office.Title"] = "Fuzz title"; ch["office.Author"] = null; ch["office.Company"] = "Fuzz"; }
                    try { ContentOps.SetBytes(m, ch, new SetResult()); setOk++; }
                    catch (MetadataException ex)
                    {
                        codes["set." + ex.Code] = codes.GetValueOrDefault("set." + ex.Code) + 1;
                        if (ex.Code == MetadataErrorCode.VerificationFailed) { var vk = "set " + file + ": " + ex.Message; verif[vk] = verif.GetValueOrDefault(vk) + 1; }
                        if (ex.InnerException != null) { var k = "set:" + ex.InnerException.GetType().Name; inner[k] = inner.GetValueOrDefault(k) + 1; }
                    }
                    catch (Exception ex) { unexpected++; if (firstUnexpected.Length == 0) firstUnexpected = "Set: " + file + " #" + round + " " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace; }
                }
                if (!SHA256.HashData(m).AsSpan().SequenceEqual(fingerprint)) mutatedInput++;
                if (sw.ElapsedMilliseconds > 5000) slow++;
            }
        }
        Note($"{strips} strip runs ({stripOk} succeeded), {sets} set runs ({setOk} succeeded)");
        Note("refusal codes: " + string.Join(", ", codes.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value)));
        Note("strip verification refusals: " + string.Join(" | ", verif.OrderByDescending(k => k.Value).Take(12).Select(k => k.Key + " x" + k.Value)));
        Note("wrapped inner exceptions: " + string.Join(", ", inner.OrderByDescending(k => k.Value).Select(k => k.Key + "=" + k.Value)));
        Check("reader never throws on corrupted input", readThrows == 0, firstUnexpected);
        Check("writers only ever throw MetadataException", unexpected == 0, firstUnexpected);
        Check("input buffers are never modified", mutatedInput == 0, mutatedInput + " violations");
        Check("no hang (every iteration < 5 s)", slow == 0, slow + " slow iterations");
    }

    // ------------------------------------------------------------------ Office

    private static (string? Creator, string? Title, int ThumbRels, int Parts) PackageInfo(string path)
    {
        using var pkg = Package.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        int parts = 0;
        foreach (var part in pkg.GetParts())
        {
            parts++;
            if (part.ContentType.EndsWith("xml", StringComparison.OrdinalIgnoreCase))
            {
                using var s = part.GetStream();
                XDocument.Load(s);   // must be well-formed
            }
        }
        return (pkg.PackageProperties.Creator, pkg.PackageProperties.Title,
                pkg.GetRelationshipsByType("http://schemas.openxmlformats.org/package/2006/relationships/metadata/thumbnail").Count(), parts);
    }

    private static void TestOffice()
    {
        foreach (var kind in new[] { "docx", "docm", "xlsx", "pptx" })
        {
            Log.AppendLine("  -- " + kind);
            var src = Path.Combine(_fix, "sample." + kind);
            Fixtures.BuildOffice(src, kind);
            var info0 = PackageInfo(src);
            Check(kind + ": fixture opens with System.IO.Packaging", info0.Creator == "Jan Kowalski" && info0.ThumbRels == 1);
            var rep = MetadataReader.Read(src);
            Note("read: " + Summary(rep));
            Check(kind + ": format detected as Office", rep.Format == MetadataFormat.Office);
            Check(kind + ": core properties", Val(rep, "office.Title") == "Quarterly secret report" && Val(rep, "office.Author") == "Jan Kowalski" && Val(rep, "office.LastModifiedBy") == "Anna Nowak"
                && Val(rep, "office.Keywords") == "q3; secret" && Val(rep, "office.Revision") == "7" && Val(rep, "office.Category") == "Reports" && Val(rep, "office.Comments") == "internal comment");
            Check(kind + ": dates", DateTimeOffset.TryParse(Val(rep, "office.Created"), out var cr) && cr.UtcDateTime == new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), Val(rep, "office.Created"));
            Check(kind + ": extended + custom properties + thumbnail", Val(rep, "office.Company") == "ACME Corp" && Val(rep, "office.Manager") == "Big Boss" && Val(rep, "office.Application") == "Microsoft Office Fixture"
                && Val(rep, "office.Words") == "3" && Val(rep, "office.custom.ProjectCode") == "X-42" && rep.Find("office.custom.Reviewed")!.Kind == MetadataValueKind.Boolean && rep.Find("office.Thumbnail") != null);
            Check(kind + ": editable / sensitive flags", rep.Find("office.Title")!.Editable && rep.Find("office.Author")!.Sensitive && !rep.Find("office.Words")!.Editable && rep.Find("office.custom.ProjectCode")!.Editable);

            var ed = Copy(src, "edit." + kind);
            var res = MetadataWriter.Set(ed, new Dictionary<string, string?>
            {
                ["office.Title"] = "Zmieniony tytuł & <znaki>", ["office.Author"] = "Ola", ["office.Company"] = "NewCo", ["office.custom.ProjectCode"] = "Y-7",
                ["office.Modified"] = "2030-01-01T00:00:00Z", ["office.Revision"] = "x", ["office.Words"] = "9", ["office.Keywords"] = null,
            });
            Note("applied: " + string.Join(",", res.Applied) + "  rejected: " + string.Join(",", res.Rejected.Select(kv => kv.Key + "=" + kv.Value)));
            var r2 = MetadataReader.Read(ed);
            Check(kind + ": edits applied", Val(r2, "office.Title") == "Zmieniony tytuł & <znaki>" && Val(r2, "office.Author") == "Ola" && Val(r2, "office.Company") == "NewCo" && Val(r2, "office.custom.ProjectCode") == "Y-7"
                && Val(r2, "office.Keywords") == "" && Val(r2, "office.LastModifiedBy") == "Anna Nowak" && res.Rejected.ContainsKey("office.Revision") && res.Rejected.ContainsKey("office.Words"));
            var pi = PackageInfo(ed);
            Check(kind + ": edited file opens with Packaging and sees the new values", pi.Creator == "Ola" && pi.Title == "Zmieniony tytuł & <znaki>");

            var st = Copy(src, "strip." + kind);
            var sr = MetadataWriter.Strip(st);
            var rs = MetadataReader.Read(st);
            Note($"strip: removed [{string.Join(",", sr.Removed)}] {sr.BytesBefore} -> {sr.BytesAfter}; read: " + Summary(rs));
            var pk = PackageInfo(st);
            Check(kind + ": core/app/custom cleared, thumbnail removed", sr.ContentChanged && sr.Removed.Contains("office.core") && sr.Removed.Contains("office.app") && sr.Removed.Contains("office.custom") && sr.Removed.Contains("office.thumbnail"));
            Check(kind + ": Packaging opens it; Creator/Title empty; no thumbnail relationship", string.IsNullOrEmpty(pk.Creator) && string.IsNullOrEmpty(pk.Title) && pk.ThumbRels == 0, $"creator={pk.Creator}");
            Check(kind + ": reader finds no personal values", Val(rs, "office.Author") == "" && Val(rs, "office.Company") == "" && rs.Find("office.custom.ProjectCode") == null && rs.Find("office.Thumbnail") == null);
            var all = ReadAllZipText(st);
            Check(kind + ": no personal strings left in any part", !all.Contains("Kowalski") && !all.Contains("ACME") && !all.Contains("Big Boss") && !all.Contains("X-42") && !all.Contains("secret report") && !all.Contains("SecretUser"));
            var diff = ChangedParts(src, st);
            Check(kind + ": every non-property part byte-identical (CRC)", diff.Count == 0, string.Join(",", diff));
            Check(kind + ": second strip is a no-op", !MetadataWriter.Strip(st).ContentChanged);
        }
    }

    private static List<string> ChangedParts(string before, string after)
    {
        var changed = new List<string>();
        using var a = System.IO.Compression.ZipFile.OpenRead(before);
        using var b = System.IO.Compression.ZipFile.OpenRead(after);
        var skip = new[] { "docProps/", "[Content_Types].xml", "_rels/.rels", "package/services/metadata/", "xl/workbook.xml" };
        foreach (var e in a.Entries)
        {
            if (skip.Any(x => e.FullName.StartsWith(x, StringComparison.OrdinalIgnoreCase))) continue;
            var f = b.GetEntry(e.FullName);
            if (f == null || f.Crc32 != e.Crc32 || f.Length != e.Length) changed.Add(e.FullName);
        }
        return changed;
    }

    private static string ReadAllZipText(string path)
    {
        var sb = new StringBuilder();
        using var z = System.IO.Compression.ZipFile.OpenRead(path);
        foreach (var e in z.Entries)
        {
            if (e.FullName.EndsWith(".jpeg")) continue;
            using var s = e.Open(); using var sr = new StreamReader(s);
            sb.AppendLine(sr.ReadToEnd());
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ file level

    private static void TestFileLevel()
    {
        // plain text file with a Zone.Identifier stream
        var txt = Path.Combine(_work, "note.txt");
        File.WriteAllText(txt, "hello\n");
        File.WriteAllText(txt + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://example.test/page\r\nHostUrl=https://example.test/file.txt\r\n");
        var rep = MetadataReader.Read(txt);
        Note("read: " + Summary(rep));
        Check("unknown format still gets the File group", rep.Format == MetadataFormat.Unknown && rep.Groups.Count == 1 && !rep.CanStripContent);
        Check("Zone.Identifier parsed (zone id, referrer, host - sensitive)", Val(rep, "file.Zone") == "true" && Val(rep, "file.ZoneId") == "3" && rep.Find("file.ReferrerUrl")!.Sensitive && Val(rep, "file.HostUrl") == "https://example.test/file.txt");
        Check("file attributes / dates present", Val(rep, "file.ReadOnly") == "false" && rep.Find("file.Created")!.Kind == MetadataValueKind.DateTime && rep.Find("file.Modified")!.Editable);

        var res = MetadataWriter.Set(txt, new Dictionary<string, string?>
        {
            ["file.Created"] = "2001-02-03T04:05:06", ["file.Modified"] = "2002-03-04T05:06:07", ["file.Accessed"] = "2003-04-05T06:07:08",
            ["file.Hidden"] = "true", ["file.Zone"] = "false", ["file.System"] = "maybe",
        });
        Note("applied: " + string.Join(",", res.Applied) + "  rejected: " + string.Join(",", res.Rejected.Select(kv => kv.Key + "=" + kv.Value)));
        Check("times set", File.GetCreationTime(txt) == new DateTime(2001, 2, 3, 4, 5, 6) && File.GetLastWriteTime(txt) == new DateTime(2002, 3, 4, 5, 6, 7));
        Check("hidden attribute set, Zone.Identifier removed (unblock), bad bool rejected", (File.GetAttributes(txt) & FileAttributes.Hidden) != 0 && !File.Exists(txt + ":Zone.Identifier") && res.Rejected.ContainsKey("file.System"));
        File.SetAttributes(txt, FileAttributes.Normal);

        // image with Zone + custom times: strip must keep zone unless asked, keep times unless asked
        var jpg = Copy(Path.Combine(_fix, "photo.jpg"), "zone.jpg");
        File.WriteAllText(jpg + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.test/p.jpg\r\n");
        var t = new DateTime(2020, 1, 1, 12, 0, 0);
        File.SetCreationTime(jpg, t); File.SetLastWriteTime(jpg, t); File.SetLastAccessTime(jpg, t);
        var s1 = MetadataWriter.Strip(jpg, new StripOptions { ZoneIdentifier = false });
        Check("content strip keeps Zone.Identifier when not asked to remove it", s1.ContentChanged && !s1.ZoneRemoved && File.Exists(jpg + ":Zone.Identifier"));
        Check("content strip keeps original file times", File.GetLastWriteTime(jpg) == t && File.GetCreationTime(jpg) == t, File.GetLastWriteTime(jpg).ToString("o"));

        var jpg2 = Copy(Path.Combine(_fix, "photo.jpg"), "zone2.jpg");
        File.WriteAllText(jpg2 + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        File.SetLastWriteTime(jpg2, t);
        var s2 = MetadataWriter.Strip(jpg2, new StripOptions { ResetFileTimes = true });
        Check("full strip removes Zone.Identifier and resets times to now", s2.ZoneRemoved && !File.Exists(jpg2 + ":Zone.Identifier") && (DateTime.Now - File.GetLastWriteTime(jpg2)).TotalMinutes < 5);

        // zone/time-only strip on a file without embedded metadata (e.g. a text file)
        File.WriteAllText(txt + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var s3 = MetadataWriter.Strip(txt);
        Check("strip on an unknown format still clears the Zone.Identifier", !s3.ContentChanged && s3.ZoneRemoved && !File.Exists(txt + ":Zone.Identifier"));

        // backup
        var bk = Copy(Path.Combine(_fix, "image.png"), "backup.png");
        var origBytes = File.ReadAllBytes(bk);
        MetadataWriter.Strip(bk, new StripOptions { KeepBackup = true });
        Check("KeepBackup writes name.ext.bak with the original bytes", File.Exists(bk + ".bak") && File.ReadAllBytes(bk + ".bak").AsSpan().SequenceEqual(origBytes) && File.ReadAllBytes(bk).Length < origBytes.Length);
        var nb = Copy(Path.Combine(_fix, "image.png"), "nobackup.png");
        MetadataWriter.Strip(nb);
        Check("no .bak by default", !File.Exists(nb + ".bak"));

        // read-only
        var ro = Copy(Path.Combine(_fix, "photo.jpg"), "readonly.jpg");
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        try { MetadataWriter.Strip(ro); Check("read-only file is refused", false); }
        catch (MetadataException ex) { Check("read-only file is refused and left intact", ex.Code == MetadataErrorCode.ReadOnly && File.ReadAllBytes(ro).Length == File.ReadAllBytes(Path.Combine(_fix, "photo.jpg")).Length); }
        var r3 = MetadataWriter.Set(ro, new Dictionary<string, string?> { ["file.ReadOnly"] = "false", ["exif.Artist"] = "Cleared RO" });
        Check("Set may clear read-only and edit in one call", r3.Applied.Contains("exif.Artist") && (File.GetAttributes(ro) & FileAttributes.ReadOnly) == 0 && Val(MetadataReader.Read(ro), "exif.Artist") == "Cleared RO");

        // missing file / cancellation
        try { MetadataReader.Read(Path.Combine(_work, "nope.jpg")); Check("missing file -> NotFound", false); }
        catch (MetadataException ex) { Check("missing file -> NotFound", ex.Code == MetadataErrorCode.NotFound); }
        try
        {
            using var cts = new CancellationTokenSource(); cts.Cancel();
            MetadataWriter.StripAsync(Copy(Path.Combine(_fix, "photo.jpg"), "cancel.jpg"), null, cts.Token).GetAwaiter().GetResult();
            Check("cancelled strip throws", false);
        }
        catch (OperationCanceledException) { Check("cancelled strip throws OperationCanceledException and leaves the file", File.ReadAllBytes(Path.Combine(_work, "cancel.jpg")).Length == File.ReadAllBytes(Path.Combine(_fix, "photo.jpg")).Length); }

        // async API sanity
        var asyncCopy = Copy(Path.Combine(_fix, "photo.jpg"), "async.jpg");
        var ar = MetadataReader.ReadAsync(asyncCopy).GetAwaiter().GetResult();
        var asr = MetadataWriter.StripAsync(asyncCopy).GetAwaiter().GetResult();
        Check("async API works", ar.EntryCount > 10 && asr.ContentChanged);
    }

    private static byte[] InsertBefore(byte[] data, int pos, byte[] seg) => data[..pos].Concat(seg).Concat(data[pos..]).ToArray();
    private static byte[] AppSeg(byte marker, byte[] payload) { var b = new byte[4 + payload.Length]; b[0] = 0xFF; b[1] = marker; b[2] = (byte)((payload.Length + 2) >> 8); b[3] = (byte)(payload.Length + 2); payload.CopyTo(b, 4); return b; }
    private static int FindMarker(byte[] d, byte marker) { for (int i = 2; i + 1 < d.Length; i++) { if (d[i] != 0xFF) continue; if (d[i + 1] == marker) return i; if (d[i + 1] == 0xDA) return -1; if (d[i + 1] is 0xD8 or 0x01 or 0 or 0xFF) continue; i += 1 + (d[i + 2] << 8 | d[i + 3]); } return -1; }

    // ------------------------------------------------------------------ in-memory pipeline (same code path as the file-based API)

    private static MetadataReport MemRead(byte[] data) => ContentOps.ReadBytes(data);
    private static byte[]? MemStrip(byte[] data, out List<string> removed) { removed = new List<string>(); return ContentOps.StripBytes(data, new StripOptions(), removed); }
    private static byte[]? MemSet(byte[] data, Dictionary<string, string?> changes) => ContentOps.SetBytes(data, changes, new SetResult());

    // ------------------------------------------------------------------ hash

    private static void TestHash()
    {
        var path = Path.Combine(_work, "big.bin");
        var rnd = new Random(12345);
        var data = new byte[5 * 1024 * 1024 + 123];
        rnd.NextBytes(data);
        File.WriteAllBytes(path, data);
        double last = 0; int reports = 0;
        var sha = FileHasher.HashAsync(path, HashAlgorithmKind.Sha256, new SyncProgress(v => { last = v; reports++; })).GetAwaiter().GetResult();
        var md5 = FileHasher.HashAsync(path, HashAlgorithmKind.Md5).GetAwaiter().GetResult();
        var sha1 = FileHasher.HashAsync(path, HashAlgorithmKind.Sha1).GetAwaiter().GetResult();
        Check("SHA-256 matches", sha == Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
        Check("MD5 matches", md5 == Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant());
        Check("SHA-1 matches", sha1 == Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant());
        Check("progress reaches 1.0 and is monotonic-ish", last == 1.0 && reports >= 3, $"last={last} reports={reports}");
        var multi = FileHasher.HashAsync(path, new[] { HashAlgorithmKind.Sha256, HashAlgorithmKind.Md5 }).GetAwaiter().GetResult();
        Check("multi-algorithm single pass", multi[HashAlgorithmKind.Sha256] == sha && multi[HashAlgorithmKind.Md5] == md5);
        var empty = Path.Combine(_work, "empty.bin"); File.WriteAllBytes(empty, Array.Empty<byte>());
        Check("empty file hash", FileHasher.HashAsync(empty, HashAlgorithmKind.Sha256).GetAwaiter().GetResult() == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        try
        {
            using var cts = new CancellationTokenSource(); cts.Cancel();
            FileHasher.HashAsync(path, HashAlgorithmKind.Sha256, null, cts.Token).GetAwaiter().GetResult();
            Check("cancelled hash throws", false);
        }
        catch (OperationCanceledException) { Check("cancelled hash throws OperationCanceledException", true); }
        try { FileHasher.HashAsync(Path.Combine(_work, "missing.bin"), HashAlgorithmKind.Sha256).GetAwaiter().GetResult(); Check("missing file -> NotFound", false); }
        catch (MetadataException ex) { Check("missing file -> NotFound", ex.Code == MetadataErrorCode.NotFound); }
        // file open in another process-like handle (shared read/write) must still hash
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            Check("hashing a file that is open for writing elsewhere", FileHasher.HashAsync(path, HashAlgorithmKind.Sha256).GetAwaiter().GetResult() == sha);
    }

    private sealed class SyncProgress : IProgress<double>
    {
        private readonly Action<double> _a;
        public SyncProgress(Action<double> a) { _a = a; }
        public void Report(double value) => _a(value);
    }
}
