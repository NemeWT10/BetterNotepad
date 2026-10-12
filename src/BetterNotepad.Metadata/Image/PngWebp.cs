using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BetterNotepad.Metadata;

internal readonly record struct PngChunk(string Type, int Start, int End, int DataStart, int DataLen);

internal static class PngFormat
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private const string XmpKeyword = "XML:com.adobe.xmp";

    public static List<PngChunk> Walk(ReadOnlySpan<byte> d)
    {
        if (d.Length < 8 || !d.Slice(0, 8).SequenceEqual(Signature)) throw new MetadataException(MetadataErrorCode.Corrupt, "Not a PNG file.");
        var list = new List<PngChunk>();
        int pos = 8;
        while (pos + 12 <= d.Length)
        {
            uint len = Bin.U32(d, pos, false);
            if (len > int.MaxValue || pos + 12L + len > d.Length) throw new MetadataException(MetadataErrorCode.Corrupt, "Truncated PNG chunk.");
            string type = Encoding.Latin1.GetString(d.Slice(pos + 4, 4));
            int end = pos + 12 + (int)len;
            list.Add(new PngChunk(type, pos, end, pos + 8, (int)len));
            pos = end;
            if (type == "IEND") break;
        }
        if (pos < d.Length) list.Add(new PngChunk("", pos, d.Length, pos, d.Length - pos));   // trailing data
        return list;
    }

    private static bool IsXmp(ReadOnlySpan<byte> d, in PngChunk c)
    {
        var data = d.Slice(c.DataStart, c.DataLen);
        int nul = data.IndexOf((byte)0);
        return nul > 0 && Encoding.Latin1.GetString(data.Slice(0, nul)) == XmpKeyword;
    }

    // ---------------- read ----------------

    private static readonly HashSet<string> KnownKeywords = new()
    { "Title", "Author", "Description", "Copyright", "Creation Time", "Software", "Disclaimer", "Warning", "Source", "Comment" };
    private static readonly HashSet<string> SensitiveKeywords = new() { "Author", "Software", "Source" };

    public static void Read(MetadataReport report, ReadOnlySpan<byte> d)
    {
        List<PngChunk> chunks;
        try { chunks = Walk(d); }
        catch (MetadataException) { report.Warnings.Add("read.partial"); return; }

        var used = new HashSet<string>();
        bool exifDone = false, xmpDone = false;
        foreach (var c in chunks)
        {
            var data = d.Slice(c.DataStart, c.DataLen);
            switch (c.Type)
            {
                case "tEXt":
                {
                    int nul = data.IndexOf((byte)0);
                    if (nul <= 0) break;
                    AddText(report, used, Encoding.Latin1.GetString(data.Slice(0, nul)), Encoding.Latin1.GetString(data.Slice(nul + 1)));
                    break;
                }
                case "zTXt":
                {
                    int nul = data.IndexOf((byte)0);
                    if (nul <= 0 || nul + 2 > data.Length) break;
                    var text = Encoding.Latin1.GetString(Txt.Inflate(data.Slice(nul + 2), 8 * 1024 * 1024));
                    AddText(report, used, Encoding.Latin1.GetString(data.Slice(0, nul)), text);
                    break;
                }
                case "iTXt":
                {
                    int nul = data.IndexOf((byte)0);
                    if (nul <= 0 || nul + 3 > data.Length) break;
                    string kw = Encoding.Latin1.GetString(data.Slice(0, nul));
                    bool compressed = data[nul + 1] == 1;
                    var rest = data.Slice(nul + 3);
                    int n1 = rest.IndexOf((byte)0); if (n1 < 0) break;
                    rest = rest.Slice(n1 + 1);
                    int n2 = rest.IndexOf((byte)0); if (n2 < 0) break;
                    rest = rest.Slice(n2 + 1);
                    byte[] bytes = compressed ? Txt.Inflate(rest, 16 * 1024 * 1024) : rest.ToArray();
                    if (kw == XmpKeyword)
                    {
                        if (xmpDone) break;
                        xmpDone = true;
                        var props = XmpReader.Read(bytes);
                        if (props == null) report.Warnings.Add("xmp.unreadable");
                        else if (props.Count > 0) XmpReader.AddEntries(report.Group(MetadataGroupId.Xmp), props);
                    }
                    else AddText(report, used, kw, Encoding.UTF8.GetString(bytes));
                    break;
                }
                case "eXIf":
                {
                    if (exifDone) break;
                    exifDone = true;
                    int skip = Bin.StartsWith(data, 0, "Exif\0\0") ? 6 : 0;
                    var t = TiffBlock.Parse(data.Slice(skip).ToArray());
                    if (t != null) ExifReader.Build(report, t, editable: false);
                    else report.Warnings.Add("exif.unreadable");
                    break;
                }
                case "tIME":
                    if (c.DataLen == 7)
                    {
                        try
                        {
                            var dt = new DateTime(Bin.U16(data, 0, false), data[2], data[3], data[4], data[5], Math.Min((int)data[6], 59), DateTimeKind.Utc);
                            report.Group(MetadataGroupId.ImageText).Entries.Add(new MetadataEntry("text.tIME", "meta.text.tIME", Txt.Iso(dt), MetadataValueKind.DateTime, false, false, "Last modification time"));
                        }
                        catch (ArgumentOutOfRangeException) { }
                    }
                    break;
            }
        }
    }

    private static void AddText(MetadataReport report, HashSet<string> used, string keyword, string text)
    {
        string baseKey = "text." + keyword;
        string key = baseKey; int n = 2;
        while (!used.Add(key)) key = baseKey + "#" + n++;
        bool known = KnownKeywords.Contains(keyword);
        string label = known ? "meta.text." + keyword.Replace(" ", "") : "meta.text.Custom";
        if (keyword.StartsWith("Raw profile type", StringComparison.OrdinalIgnoreCase)) text = $"({text.Length} chars)";
        report.Group(MetadataGroupId.ImageText).Entries.Add(new MetadataEntry(key, label, Txt.Clip(text.TrimEnd('\0')), MetadataValueKind.Text, false, SensitiveKeywords.Contains(keyword) || !known, keyword));
    }

    // ---------------- strip ----------------

    public static List<Chunk>? PlanStrip(ReadOnlySpan<byte> d, StripOptions o, List<string> removed)
    {
        var chunks = Walk(d);
        var drop = new bool[chunks.Count];
        int firstExif = -1;
        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            switch (c.Type)
            {
                case "tEXt": case "zTXt": case "tIME":
                    if (o.TextAndComments) { drop[i] = true; Note(removed, "png." + c.Type); }
                    break;
                case "iTXt":
                    if (IsXmp(d, c)) { if (o.Xmp) { drop[i] = true; Note(removed, "png.xmp"); } }
                    else if (o.TextAndComments) { drop[i] = true; Note(removed, "png.iTXt"); }
                    break;
                case "eXIf":
                    if (firstExif < 0) firstExif = i;
                    if (o.Exif && !(i == firstExif && IsOrientationStub(d, c))) { drop[i] = true; Note(removed, "png.exif"); }
                    break;
                case "caBX":
                    if (o.OtherEmbedded) { drop[i] = true; Note(removed, "png.other"); }
                    break;
            }
        }
        byte[]? stub = null;
        if (o.Exif && o.KeepOrientation && firstExif >= 0)
        {
            var c = chunks[firstExif];
            var data = d.Slice(c.DataStart, c.DataLen);
            int skip = Bin.StartsWith(data, 0, "Exif\0\0") ? 6 : 0;
            int ori = TiffBlock.Parse(data.Slice(skip).ToArray())?.Orientation() ?? 1;
            if (ori > 1) stub = BuildExifChunk(TiffBlock.BuildOrientationOnly(ori));
        }
        if (!drop.Any(x => x)) return null;

        var plan = new List<Chunk>();
        plan.Add(Chunk.Range(0, 8));
        for (int i = 0; i < chunks.Count; i++)
        {
            if (i == firstExif && drop[i] && stub != null) { plan.Add(Chunk.Literal(stub)); continue; }
            if (drop[i]) continue;
            Chunk.AddRange(plan, chunks[i].Start, chunks[i].End);
        }
        return plan;
    }

    private static void Note(List<string> l, string s) { if (!l.Contains(s)) l.Add(s); }

    /// <summary>True when the eXIf chunk is already exactly what a previous strip left behind (orientation only).</summary>
    private static bool IsOrientationStub(ReadOnlySpan<byte> d, in PngChunk c)
    {
        var data = d.Slice(c.DataStart, c.DataLen);
        int skip = Bin.StartsWith(data, 0, "Exif\0\0") ? 6 : 0;
        int ori = TiffBlock.Parse(data.Slice(skip).ToArray())?.Orientation() ?? 1;
        return ori > 1 && d.Slice(c.Start, c.End - c.Start).SequenceEqual(BuildExifChunk(TiffBlock.BuildOrientationOnly(ori)));
    }

    public static byte[] BuildExifChunk(byte[] tiff)
    {
        var b = new byte[12 + tiff.Length];
        Bin.PutU32(b, 0, (uint)tiff.Length, false);
        b[4] = (byte)'e'; b[5] = (byte)'X'; b[6] = (byte)'I'; b[7] = (byte)'f';
        Buffer.BlockCopy(tiff, 0, b, 8, tiff.Length);
        Bin.PutU32(b, 8 + tiff.Length, Crc32.Compute(b.AsSpan(4, 4 + tiff.Length)), false);
        return b;
    }

    /// <summary>Everything except the metadata chunk types: chunk-by-chunk equality is what "no recompression" means for PNG.</summary>
    public static bool IsMetadataChunk(string type) => type is "tEXt" or "zTXt" or "iTXt" or "tIME" or "eXIf" or "caBX";

    public static string CoreHash(ReadOnlySpan<byte> d)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var c in Walk(d))
        {
            if (IsMetadataChunk(c.Type)) continue;
            h.AppendData(d.Slice(c.Start, c.End - c.Start));
        }
        return Convert.ToHexString(h.GetHashAndReset());
    }

    /// <summary>Number of chunks whose stored CRC does not match (optionally ignoring the metadata chunks a strip removes or rewrites).</summary>
    public static int CrcFailures(ReadOnlySpan<byte> d, bool ignoreMetadataChunks)
    {
        int bad = 0;
        foreach (var c in Walk(d))
        {
            if (c.Type.Length == 0) continue;
            if (ignoreMetadataChunks && IsMetadataChunk(c.Type)) continue;
            uint stored = Bin.U32(d, c.End - 4, false);
            if (Crc32.Compute(d.Slice(c.Start + 4, 4 + c.DataLen)) != stored) bad++;
        }
        return bad;
    }
}

internal static class WebpFormat
{
    private readonly record struct Riff(string Id, int Start, int End, int DataStart, int DataLen);

    private static List<Riff> Walk(ReadOnlySpan<byte> d)
    {
        if (d.Length < 12 || !Bin.StartsWith(d, 0, "RIFF") || !Bin.StartsWith(d, 8, "WEBP")) throw new MetadataException(MetadataErrorCode.Corrupt, "Not a WebP file.");
        var list = new List<Riff>();
        int pos = 12;
        uint riffSize = Bin.U32(d, 4, true);
        int limit = (int)Math.Min((long)d.Length, 8L + riffSize);
        while (pos + 8 <= limit)
        {
            uint len = Bin.U32(d, pos + 4, true);
            long end = pos + 8L + len + (len & 1);
            if (pos + 8L + len > limit) throw new MetadataException(MetadataErrorCode.Corrupt, "Truncated WebP chunk.");
            list.Add(new Riff(Encoding.ASCII.GetString(d.Slice(pos, 4)), pos, (int)Math.Min(end, limit), pos + 8, (int)len));
            pos = (int)Math.Min(end, limit);
        }
        return list;
    }

    public static void Read(MetadataReport report, ReadOnlySpan<byte> d)
    {
        List<Riff> chunks;
        try { chunks = Walk(d); }
        catch (MetadataException) { report.Warnings.Add("read.partial"); return; }
        foreach (var c in chunks)
        {
            var data = d.Slice(c.DataStart, c.DataLen);
            if (c.Id == "EXIF")
            {
                int skip = Bin.StartsWith(data, 0, "Exif\0\0") ? 6 : 0;
                var t = TiffBlock.Parse(data.Slice(skip).ToArray());
                if (t != null) ExifReader.Build(report, t, editable: false); else report.Warnings.Add("exif.unreadable");
            }
            else if (c.Id == "XMP ")
            {
                var props = XmpReader.Read(data);
                if (props == null) report.Warnings.Add("xmp.unreadable");
                else if (props.Count > 0) XmpReader.AddEntries(report.Group(MetadataGroupId.Xmp), props);
            }
        }
    }

    private static byte[] BuildOrientationChunk(int ori)
    {
        var tiff = TiffBlock.BuildOrientationOnly(ori);
        var stub = new byte[8 + tiff.Length + (tiff.Length & 1)];
        Encoding.ASCII.GetBytes("EXIF", 0, 4, stub, 0);
        Bin.PutU32(stub, 4, (uint)tiff.Length, true);
        Buffer.BlockCopy(tiff, 0, stub, 8, tiff.Length);
        return stub;
    }

    private static bool IsOrientationStub(ReadOnlySpan<byte> d, in Riff c)
    {
        var data = d.Slice(c.DataStart, c.DataLen);
        int skip = Bin.StartsWith(data, 0, "Exif\0\0") ? 6 : 0;
        int ori = TiffBlock.Parse(data.Slice(skip).ToArray())?.Orientation() ?? 1;
        return ori > 1 && d.Slice(c.Start, c.End - c.Start).SequenceEqual(BuildOrientationChunk(ori));
    }

    public static string CoreHash(ReadOnlySpan<byte> d)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var c in Walk(d))
        {
            if (c.Id is "EXIF" or "XMP " or "VP8X") continue;
            h.AppendData(d.Slice(c.Start, c.End - c.Start));
        }
        return Convert.ToHexString(h.GetHashAndReset());
    }

    public static List<Chunk>? PlanStrip(ReadOnlySpan<byte> d, StripOptions o, List<string> removed)
    {
        var chunks = Walk(d);
        var drop = new bool[chunks.Count];
        int firstExif = -1;
        for (int i = 0; i < chunks.Count; i++)
        {
            if (chunks[i].Id == "EXIF")
            {
                if (firstExif < 0) firstExif = i;
                if (o.Exif && !(i == firstExif && IsOrientationStub(d, chunks[i]))) { drop[i] = true; if (!removed.Contains("webp.exif")) removed.Add("webp.exif"); }
            }
            else if (chunks[i].Id == "XMP ") { if (o.Xmp) { drop[i] = true; if (!removed.Contains("webp.xmp")) removed.Add("webp.xmp"); } }
        }
        if (!drop.Any(x => x)) return null;

        byte[]? stub = null;
        if (o.Exif && o.KeepOrientation && firstExif >= 0)
        {
            var c = chunks[firstExif];
            var data = d.Slice(c.DataStart, c.DataLen);
            int skip = Bin.StartsWith(data, 0, "Exif\0\0") ? 6 : 0;
            int ori = TiffBlock.Parse(data.Slice(skip).ToArray())?.Orientation() ?? 1;
            if (ori > 1) stub = BuildOrientationChunk(ori);
        }

        bool hasExifLeft = false, hasXmpLeft = false;
        var body = new List<Chunk>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            if (i == firstExif && drop[i] && stub != null) { body.Add(Chunk.Literal(stub)); hasExifLeft = true; continue; }
            if (drop[i]) continue;
            if (c.Id == "EXIF") hasExifLeft = true;
            if (c.Id == "XMP ") hasXmpLeft = true;
            if (c.Id == "VP8X" && c.DataLen >= 10)
            {
                var copy = d.Slice(c.Start, c.End - c.Start).ToArray();
                body.Add(Chunk.Literal(copy));   // flags patched below
                continue;
            }
            Chunk.AddRange(body, c.Start, c.End);
        }
        // patch VP8X flags (EXIF 0x08, XMP 0x04) and RIFF size
        for (int i = 0; i < body.Count; i++)
        {
            if (body[i].Data is { Length: >= 18 } lit && lit[0] == 'V' && lit[1] == 'P' && lit[2] == '8' && lit[3] == 'X')
            {
                if (!hasExifLeft) lit[8] &= unchecked((byte)~0x08);
                if (!hasXmpLeft) lit[8] &= unchecked((byte)~0x04);
            }
        }
        long total = 4 + body.Sum(b => (long)b.Length);
        var header = new byte[12];
        Encoding.ASCII.GetBytes("RIFF", 0, 4, header, 0);
        Bin.PutU32(header, 4, (uint)total, true);
        Encoding.ASCII.GetBytes("WEBP", 0, 4, header, 8);
        var plan = new List<Chunk> { Chunk.Literal(header) };
        plan.AddRange(body);
        return plan;
    }
}
