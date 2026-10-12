using System.Security.Cryptography;
using System.Text;

namespace BetterNotepad.Metadata;

/// <summary>Output piece: either a byte range of the source or literal bytes.</summary>
internal readonly record struct Chunk(int Start, int End, byte[]? Data)
{
    public int Length => Data?.Length ?? End - Start;
    public static Chunk Range(int s, int e) => new(s, e, null);
    public static Chunk Literal(byte[] b) => new(0, 0, b);

    /// <summary>Adds a source range, merging with the previous one when contiguous.</summary>
    public static void AddRange(List<Chunk> list, int s, int e)
    {
        if (e <= s) return;
        if (list.Count > 0)
        {
            var last = list[^1];
            if (last.Data == null && last.End == s) { list[^1] = Range(last.Start, e); return; }
        }
        list.Add(Range(s, e));
    }

    public static long Write(Stream dest, ReadOnlySpan<byte> src, List<Chunk> chunks)
    {
        long total = 0;
        foreach (var c in chunks)
        {
            if (c.Data != null) dest.Write(c.Data);
            else dest.Write(src.Slice(c.Start, c.End - c.Start));
            total += c.Length;
        }
        return total;
    }
}

internal enum JpegKind { Keep, Exif, Xmp, XmpExt, Iptc, Comment, Other, Mpf }

/// <summary>Marker -1 = entropy-coded scan data, -2 = bytes after EOI. Start..End is the whole piece incl. marker and length.</summary>
internal readonly record struct JpegPiece(int Marker, int Start, int End, int DataStart);

internal static class JpegFormat
{
    private const string ExifSig = "Exif\0\0";
    private const string XmpSig = "http://ns.adobe.com/xap/1.0/\0";
    private const string XmpExtSig = "http://ns.adobe.com/xmp/extension/\0";

    public static List<JpegPiece> Walk(ReadOnlySpan<byte> d)
    {
        var list = new List<JpegPiece>();
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) throw new MetadataException(MetadataErrorCode.Corrupt, "Not a JPEG file.");
        list.Add(new JpegPiece(0xD8, 0, 2, 2));
        int pos = 2;
        while (pos < d.Length)
        {
            if (d[pos] != 0xFF) { list.Add(new JpegPiece(-2, pos, d.Length, pos)); break; }
            int p = pos;
            while (p < d.Length && d[p] == 0xFF) p++;
            if (p >= d.Length) { list.Add(new JpegPiece(-2, pos, d.Length, pos)); break; }
            int m = d[p++];
            if (m == 0x00) throw new MetadataException(MetadataErrorCode.Corrupt, "Invalid JPEG marker.");
            if (m == 0xD9) { list.Add(new JpegPiece(m, pos, p, p)); pos = p; if (pos < d.Length) list.Add(new JpegPiece(-2, pos, d.Length, pos)); break; }
            if (m == 0x01 || (m >= 0xD0 && m <= 0xD8)) { list.Add(new JpegPiece(m, pos, p, p)); pos = p; continue; }
            if (p + 2 > d.Length) throw new MetadataException(MetadataErrorCode.Corrupt, "Truncated JPEG segment.");
            int len = d[p] << 8 | d[p + 1];
            if (len < 2 || p + len > d.Length) throw new MetadataException(MetadataErrorCode.Corrupt, "Truncated JPEG segment.");
            int end = p + len;
            list.Add(new JpegPiece(m, pos, end, p + 2));
            pos = end;
            if (m == 0xDA)
            {
                int q = pos;
                while (true)
                {
                    int idx = d.Slice(q).IndexOf((byte)0xFF);
                    if (idx < 0) { q = d.Length; break; }
                    q += idx;
                    if (q + 1 >= d.Length) { q = d.Length; break; }
                    byte b = d[q + 1];
                    if (b == 0x00 || (b >= 0xD0 && b <= 0xD7) || b == 0xFF) { q += b == 0xFF ? 1 : 2; continue; }
                    break;
                }
                if (q > pos) list.Add(new JpegPiece(-1, pos, q, pos));
                pos = q;
            }
        }
        return list;
    }

    public static JpegKind Classify(ReadOnlySpan<byte> d, in JpegPiece p)
    {
        int m = p.Marker;
        if (m == 0xFE) return JpegKind.Comment;
        if (m < 0xE0 || m > 0xEF) return JpegKind.Keep;
        var payload = d.Slice(p.DataStart, p.End - p.DataStart);
        switch (m)
        {
            case 0xE0: return JpegKind.Keep;
            case 0xE1:
                if (Bin.StartsWith(payload, 0, ExifSig)) return JpegKind.Exif;
                if (Bin.StartsWith(payload, 0, XmpSig)) return JpegKind.Xmp;
                if (Bin.StartsWith(payload, 0, XmpExtSig)) return JpegKind.XmpExt;
                return JpegKind.Other;
            case 0xE2:
                if (Bin.StartsWith(payload, 0, "ICC_PROFILE\0")) return JpegKind.Keep;
                if (Bin.StartsWith(payload, 0, "MPF\0")) return JpegKind.Mpf;
                return JpegKind.Other;
            case 0xED: return Bin.StartsWith(payload, 0, "Photoshop 3.0\0") ? JpegKind.Iptc : JpegKind.Keep;
            case 0xEE: return JpegKind.Keep;   // Adobe colour transform: needed to decode
            default: return JpegKind.Other;
        }
    }

    // ---------------- read ----------------

    public static void Read(MetadataReport report, ReadOnlySpan<byte> d)
    {
        List<JpegPiece> pieces;
        try { pieces = Walk(d); }
        catch (MetadataException) { report.Warnings.Add("read.partial"); return; }

        bool exifDone = false, xmpDone = false, iptcDone = false;
        int comments = 0;
        foreach (var p in pieces)
        {
            switch (Classify(d, p))
            {
                case JpegKind.Exif when !exifDone:
                    exifDone = true;
                    var tiff = d.Slice(p.DataStart + 6, p.End - p.DataStart - 6).ToArray();
                    var t = TiffBlock.Parse(tiff);
                    if (t != null) ExifReader.Build(report, t, editable: true);
                    else report.Warnings.Add("exif.unreadable");
                    break;
                case JpegKind.Xmp when !xmpDone:
                    xmpDone = true;
                    var props = XmpReader.Read(d.Slice(p.DataStart + XmpSig.Length, p.End - p.DataStart - XmpSig.Length));
                    if (props == null) report.Warnings.Add("xmp.unreadable");
                    else if (props.Count > 0) XmpReader.AddEntries(report.Group(MetadataGroupId.Xmp), props);
                    break;
                case JpegKind.Iptc when !iptcDone:
                    iptcDone = true;
                    var g = new MetadataGroup(MetadataGroupId.Iptc);
                    IptcReader.AddEntries(g, d.Slice(p.DataStart, p.End - p.DataStart));
                    if (g.Entries.Count > 0) report.Groups.Add(g);
                    break;
                case JpegKind.Comment:
                    comments++;
                    var raw = d.Slice(p.DataStart, p.End - p.DataStart);
                    string txt;
                    try { txt = new UTF8Encoding(false, true).GetString(raw); } catch { txt = Encoding.Latin1.GetString(raw); }
                    string key = comments == 1 ? "text.Comment" : "text.Comment#" + comments;
                    report.Group(MetadataGroupId.ImageText).Entries.Add(new MetadataEntry(key, "meta.text.Comment", Txt.Clip(txt.TrimEnd('\0')), MetadataValueKind.Text, false, false, "Comment"));
                    break;
            }
        }
    }

    // ---------------- strip ----------------

    public static List<Chunk>? PlanStrip(ReadOnlySpan<byte> d, StripOptions o, List<string> removed)
    {
        var pieces = Walk(d);
        int n = pieces.Count;
        var kinds = new JpegKind[n];
        var drop = new bool[n];
        var repl = new byte[]?[n];
        int mpf = -1, firstExif = -1;
        for (int i = 0; i < n; i++)
        {
            kinds[i] = Classify(d, pieces[i]);
            if (kinds[i] == JpegKind.Exif && firstExif < 0) firstExif = i;
            if (kinds[i] == JpegKind.Mpf) mpf = i;
        }

        // Keep only the Orientation tag so the image is not displayed rotated (and make a second run a no-op).
        byte[]? orientationStub = null;
        bool stubAlreadyThere = false;
        if (o.Exif && o.KeepOrientation && firstExif >= 0)
        {
            var p = pieces[firstExif];
            var t = TiffBlock.Parse(d.Slice(p.DataStart + 6, p.End - p.DataStart - 6).ToArray());
            int ori = t?.Orientation() ?? 1;
            if (ori > 1)
            {
                orientationStub = BuildExifSegment(TiffBlock.BuildOrientationOnly(ori));
                stubAlreadyThere = d.Slice(p.Start, p.End - p.Start).SequenceEqual(orientationStub);
            }
        }

        for (int i = 0; i < n; i++)
        {
            switch (kinds[i])
            {
                case JpegKind.Exif:
                    if (o.Exif)
                    {
                        if (i == firstExif && stubAlreadyThere) break;
                        drop[i] = true; Note(removed, "jpeg.exif");
                    }
                    else if (o.GpsOnly)
                    {
                        var p = pieces[i];
                        var tiff = d.Slice(p.DataStart + 6, p.End - p.DataStart - 6).ToArray();
                        if (TiffEditor.RemoveGps(tiff))
                        {
                            var seg = d.Slice(p.Start, p.End - p.Start).ToArray();
                            Buffer.BlockCopy(tiff, 0, seg, p.DataStart - p.Start + 6, tiff.Length);   // skip marker + length + "Exif\0\0"
                            repl[i] = seg;
                            Note(removed, "jpeg.gps");
                        }
                    }
                    break;
                case JpegKind.Xmp: case JpegKind.XmpExt:
                    if (o.Xmp) { drop[i] = true; Note(removed, "jpeg.xmp"); }
                    break;
                case JpegKind.Iptc:
                    if (o.Iptc) { drop[i] = true; Note(removed, "jpeg.iptc"); }
                    break;
                case JpegKind.Comment:
                    if (o.TextAndComments) { drop[i] = true; Note(removed, "jpeg.comment"); }
                    break;
                case JpegKind.Other:
                    if (o.OtherEmbedded) { drop[i] = true; Note(removed, "jpeg.other"); }
                    break;
            }
        }
        // MPF stores offsets relative to its own segment: dropping anything behind it would corrupt them.
        if (mpf >= 0 && !drop[mpf])
            for (int i = mpf + 1; i < n; i++)
                if (drop[i]) { drop[mpf] = true; Note(removed, "jpeg.mpf"); break; }

        if (!drop.Any(x => x) && !repl.Any(x => x != null)) return null;

        var chunks = new List<Chunk>();
        for (int i = 0; i < n; i++)
        {
            var p = pieces[i];
            if (i == firstExif && drop[i] && orientationStub != null) { chunks.Add(Chunk.Literal(orientationStub)); continue; }
            if (drop[i]) continue;
            if (repl[i] != null) { chunks.Add(Chunk.Literal(repl[i]!)); continue; }
            Chunk.AddRange(chunks, p.Start, p.End);
        }
        return chunks;
    }

    private static void Note(List<string> l, string s) { if (!l.Contains(s)) l.Add(s); }

    public static byte[] BuildExifSegment(byte[] tiff)
    {
        var seg = new byte[2 + 2 + 6 + tiff.Length];
        seg[0] = 0xFF; seg[1] = 0xE1;
        int len = 2 + 6 + tiff.Length;
        seg[2] = (byte)(len >> 8); seg[3] = (byte)len;
        Encoding.ASCII.GetBytes(ExifSig, 0, 6, seg, 4);
        Buffer.BlockCopy(tiff, 0, seg, 10, tiff.Length);
        return seg;
    }

    /// <summary>Hash of every piece that must survive any metadata operation unchanged (image data, tables, ICC ...).</summary>
    public static string CoreHash(ReadOnlySpan<byte> d)
    {
        var pieces = Walk(d);
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var p in pieces)
        {
            if (p.Marker == -2) continue;   // trailing bytes are not touched either, but are not part of the picture
            if (Classify(d, p) != JpegKind.Keep) continue;
            h.AppendData(d.Slice(p.Start, p.End - p.Start));
        }
        return Convert.ToHexString(h.GetHashAndReset());
    }

    // ---------------- edit EXIF text ----------------

    public static List<Chunk>? PlanSetExif(ReadOnlySpan<byte> d, IReadOnlyDictionary<string, string?> changes, SetResult result)
    {
        var pieces = Walk(d);
        int idx = -1;
        for (int i = 0; i < pieces.Count; i++) if (Classify(d, pieces[i]) == JpegKind.Exif) { idx = i; break; }
        if (idx < 0)
        {
            foreach (var k in changes.Keys) result.Rejected[k] = "notPresent";
            return null;
        }
        var p = pieces[idx];
        var tiff = d.Slice(p.DataStart + 6, p.End - p.DataStart - 6).ToArray();
        var applied = new List<string>();
        foreach (var (key, value) in changes)
        {
            var t = TiffBlock.Parse(tiff);
            if (t == null) { result.Rejected[key] = "notPresent"; continue; }
            string name = key.Substring("exif.".Length);
            if (!ExifNames.EditableText.Contains(name)) { result.Rejected[key] = "notEditable"; continue; }
            TiffEntry? hit = null;
            foreach (var e in t.Entries)
                if ((e.Kind == IfdKind.Ifd0 || e.Kind == IfdKind.Exif) && e.ValuePos >= 0 && ExifNames.Name(e.Kind, e.Tag) == name) { hit = e; break; }
            if (hit == null) { result.Rejected[key] = "notPresent"; continue; }

            byte[] bytes;
            string v = value ?? "";
            if (name.StartsWith("XP", StringComparison.Ordinal))
            {
                if (hit.Value.Type != 1) { result.Rejected[key] = "notEditable"; continue; }
                var u = Encoding.Unicode.GetBytes(v);
                bytes = new byte[u.Length + 2];
                Buffer.BlockCopy(u, 0, bytes, 0, u.Length);
            }
            else
            {
                if (hit.Value.Type != 2) { result.Rejected[key] = "notEditable"; continue; }
                if (ExifNames.DateTags.Contains(name) && v.Length > 0)
                {
                    if (!Txt.TryParseIso(v, out var dt)) { result.Rejected[key] = "invalidValue"; continue; }
                    v = dt.DateTime.ToString("yyyy:MM:dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                }
                var u = Encoding.UTF8.GetBytes(v);
                bytes = new byte[u.Length + 1];
                Buffer.BlockCopy(u, 0, bytes, 0, u.Length);
            }
            var next = TiffEditor.SetEntryBytes(tiff, hit.Value, bytes);
            if (next.Length + 8 > 65535) { result.Rejected[key] = "tooLong"; continue; }
            tiff = next;
            applied.Add(key);
        }
        if (applied.Count == 0) return null;
        result.Applied.AddRange(applied);
        var chunks = new List<Chunk>();
        Chunk.AddRange(chunks, 0, p.Start);
        chunks.Add(Chunk.Literal(BuildExifSegment(tiff)));
        Chunk.AddRange(chunks, p.End, d.Length);
        return chunks;
    }
}
