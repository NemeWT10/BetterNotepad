using System.Text;

namespace BetterNotepad.Metadata;

internal readonly record struct XEntry(byte Type, long A, int B);   // 0 free, 1 in use (A = offset, B = gen), 2 compressed (A = object stream, B = index)

internal sealed class PObj
{
    public int Num, Gen;
    public PV Value = null!;
    /// <summary>Buffer the value's ranges refer to: the file (Data) or a decoded object stream.</summary>
    public ReadOnlyMemory<byte> Buf;
    public bool Compressed;
    public bool IsStream;
    /// <summary>Stream data range in Data (valid when IsStream).</summary>
    public int DataStart, DataLen;
    /// <summary>The declared /Length was missing or wrong; DataLen is the verified length.</summary>
    public bool LengthWrong;
}

/// <summary>
/// Read-only PDF object model: cross-reference tables / streams (incl. hybrid + /Prev chains), object streams and a
/// fallback that rebuilds the table by scanning when the file is damaged.
/// </summary>
internal sealed class PdfDocument
{
    private sealed class ObjStm
    {
        public byte[] Buf = Array.Empty<byte>();
        public int[] Nums = Array.Empty<int>();
        public int[] Offsets = Array.Empty<int>();
        public int First;
    }

    private readonly bool _allowRebuild;
    private readonly Dictionary<int, ObjStm?> _objStms = new();
    private int _loadDepth;

    public ReadOnlyMemory<byte> Data { get; }
    /// <summary>Bytes before "%PDF-" (offsets in the file are relative to the header).</summary>
    public int HeaderOffset { get; }
    public string Version { get; private set; } = "1.4";
    public Dictionary<int, XEntry> Xref { get; } = new();
    public Dictionary<string, PV> Trailer { get; private set; } = new();
    public bool Rebuilt { get; private set; }
    public bool LastIsXrefStream { get; private set; }
    public long LastStartXref { get; private set; } = -1;
    public bool Encrypted => Trailer.ContainsKey("Encrypt");

    private PdfDocument(ReadOnlyMemory<byte> file, bool allowRebuild)
    {
        _allowRebuild = allowRebuild;
        var span = file.Span;
        int h = span.Slice(0, Math.Min(span.Length, 4096)).IndexOf("%PDF-"u8);
        if (h < 0) throw new MetadataException(MetadataErrorCode.Corrupt, "Not a PDF file.");
        HeaderOffset = h;
        Data = file.Slice(h);
        var ds = Data.Span;
        int e = 5;
        while (e < ds.Length && e < 16 && (char.IsAsciiDigit((char)ds[e]) || ds[e] == '.')) e++;
        if (e > 5) Version = Encoding.ASCII.GetString(ds.Slice(5, e - 5));
    }

    public static PdfDocument Open(ReadOnlyMemory<byte> file, bool allowRebuild = true)
    {
        var doc = new PdfDocument(file, allowRebuild);
        bool ok = false;
        try
        {
            long sx = doc.FindStartXref();
            if (sx >= 0) { doc.ReadXrefChain(sx); ok = doc.Trailer.TryGetValue("Root", out var r) && r is PRef rr && doc.Load(rr.Num) is { Value: PDict }; }
        }
        catch (Exception ex) when (ex is PdfFormatException or OverflowException or ArgumentOutOfRangeException or InvalidDataException) { ok = false; }
        if (ok && doc.Rebuilt && !doc.Trailer.ContainsKey("Root")) ok = false;
        if (!ok)
        {
            if (!allowRebuild) throw new MetadataException(MetadataErrorCode.Corrupt, "Cross-reference data is damaged.");
            if (!doc.Rebuilt) doc.Rebuild();
            if (!doc.Trailer.ContainsKey("Root")) throw new MetadataException(MetadataErrorCode.Corrupt, "PDF structure is damaged (no catalog).");
        }
        return doc;
    }

    // ---------------- cross-reference ----------------

    private long FindStartXref()
    {
        var d = Data.Span;
        int from = Math.Max(0, d.Length - 65536);
        int idx = d.Slice(from).LastIndexOf("startxref"u8);
        if (idx < 0) return -1;
        var lx = new PdfLexer(d, from + idx + 9);
        try { return lx.ReadInt(); } catch (PdfFormatException) { return -1; }
    }

    private void ReadXrefChain(long start)
    {
        var visited = new HashSet<long>();
        long pos = start;
        bool first = true;
        while (pos >= 0 && pos < Data.Length && visited.Add(pos))
        {
            var sec = ReadSection(pos);
            if (first)
            {
                Trailer = sec.Trailer; LastIsXrefStream = sec.IsStream; LastStartXref = pos; first = false;
            }
            else
            {
                foreach (var k in new[] { "Root", "Info", "ID", "Encrypt" })
                    if (!Trailer.ContainsKey(k) && sec.Trailer.TryGetValue(k, out var v)) Trailer[k] = v;
            }
            if (sec.XRefStm >= 0 && visited.Add(sec.XRefStm)) ReadSection(sec.XRefStm);
            pos = sec.Prev;
        }
        if (first) throw new PdfFormatException("No cross-reference section");
    }

    private readonly record struct Section(Dictionary<string, PV> Trailer, bool IsStream, long Prev, long XRefStm);

    private Section ReadSection(long pos)
    {
        var lx = new PdfLexer(Data.Span, (int)pos);
        if (lx.TryKeyword("xref"))
        {
            while (true)
            {
                lx.SkipWs();
                if (lx.TryKeyword("trailer")) break;
                long first = lx.ReadInt();
                long count = lx.ReadInt();
                if (count < 0 || count > 50_000_000) throw new PdfFormatException("Bad xref subsection");
                for (long i = 0; i < count; i++)
                {
                    long off = lx.ReadInt();
                    long gen = lx.ReadInt();
                    lx.SkipWs();
                    if (lx.AtEnd) throw new PdfFormatException("Truncated xref");
                    char kind = (char)Data.Span[lx.Pos];
                    lx.Pos++;
                    if (i == 0 && first == 1 && kind == 'f' && off == 0 && gen == 65535) first = 0;   // common off-by-one in the wild
                    int num = (int)(first + i);
                    if (Xref.ContainsKey(num)) continue;
                    if (kind == 'n') { if (off > 0) Xref[num] = new XEntry(1, off, (int)gen); }
                    else if (kind == 'f') Xref[num] = new XEntry(0, off, (int)gen);
                    else throw new PdfFormatException("Bad xref entry");
                }
            }
            var td = lx.ParseValue() as PDict ?? throw new PdfFormatException("Trailer dictionary expected");
            var map = ToMap(td);
            return new Section(map, false, Num(td.Get("Prev"), -1), Num(td.Get("XRefStm"), -1));
        }

        // cross-reference stream
        var obj = ParseIndirectAt(lx.Pos, expectedNum: -1);
        if (obj == null || obj.Value is not PDict dict || dict.NameOf("Type") != "XRef") throw new PdfFormatException("Cross-reference stream expected");
        var bytes = DecodeStream(obj) ?? throw new PdfFormatException("Unsupported xref stream filter");
        var w = (dict.Get("W") as PArr)?.Items.Select(x => (int)Num(x, 0)).ToArray() ?? throw new PdfFormatException("Missing /W");
        if (w.Length < 3 || w.Any(x => x < 0 || x > 8)) throw new PdfFormatException("Bad /W");
        long size = Num(dict.Get("Size"), 0);
        var index = (dict.Get("Index") as PArr)?.Items.Select(x => Num(x, 0)).ToArray() ?? new[] { 0L, size };
        int rowLen = w[0] + w[1] + w[2];
        if (rowLen <= 0) throw new PdfFormatException("Bad /W");
        int p = 0;
        for (int s = 0; s + 1 < index.Length; s += 2)
        {
            for (long i = 0; i < index[s + 1]; i++)
            {
                if (p + rowLen > bytes.Length) break;
                long t = w[0] == 0 ? 1 : ReadBe(bytes, p, w[0]);
                long a = ReadBe(bytes, p + w[0], w[1]);
                long b = ReadBe(bytes, p + w[0] + w[1], w[2]);
                p += rowLen;
                int num = (int)(index[s] + i);
                if (Xref.ContainsKey(num)) continue;
                if (t == 0) Xref[num] = new XEntry(0, a, (int)b);
                else if (t == 1) Xref[num] = new XEntry(1, a, (int)b);
                else if (t == 2) Xref[num] = new XEntry(2, a, (int)b);
            }
        }
        return new Section(ToMap(dict), true, Num(dict.Get("Prev"), -1), -1);
    }

    private static Dictionary<string, PV> ToMap(PDict d)
    {
        var m = new Dictionary<string, PV>();
        foreach (var e in d.Entries) m[e.Key] = e.Value;
        return m;
    }

    private static long ReadBe(byte[] b, int off, int len)
    {
        long v = 0;
        for (int i = 0; i < len; i++) v = v << 8 | b[off + i];
        return v;
    }

    public static long Num(PV? v, long def) => v is PNum n ? n.L : def;

    // ---------------- objects ----------------

    public PObj? Load(int num)
    {
        if (!Xref.TryGetValue(num, out var x) || x.Type == 0) return null;
        if (_loadDepth > 24) return null;
        _loadDepth++;
        try
        {
            if (x.Type == 1)
            {
                PObj? o = null;
                try { o = ParseIndirectAt(x.A, num); } catch (PdfFormatException) { }
                if (o == null && _allowRebuild && !Rebuilt)
                {
                    Rebuild();
                    return Load(num);
                }
                if (o != null) o.Gen = x.B;
                return o;
            }
            return LoadCompressed(num, (int)x.A, x.B);
        }
        finally { _loadDepth--; }
    }

    public PV? Resolve(PV? v)
    {
        for (int i = 0; i < 8 && v is PRef r; i++) v = Load(r.Num)?.Value;
        return v;
    }

    public PDict? ResolveDict(PV? v) => Resolve(v) as PDict;

    private PObj? ParseIndirectAt(long offset, int expectedNum)
    {
        if (offset < 0 || offset >= Data.Length) return null;
        var d = Data.Span;
        var lx = new PdfLexer(d, (int)offset);
        int num, gen;
        try
        {
            num = (int)lx.ReadInt(); gen = (int)lx.ReadInt();
            if (!lx.TryKeyword("obj")) return null;
        }
        catch (PdfFormatException) { return null; }
        if (expectedNum >= 0 && num != expectedNum) return null;
        var value = lx.ParseValue();
        var o = new PObj { Num = num, Gen = gen, Value = value, Buf = Data };
        int save = lx.Pos;
        if (value is PDict dict && lx.TryKeyword("stream"))
        {
            int p = lx.Pos;
            if (p < d.Length && d[p] == 13) { p++; if (p < d.Length && d[p] == 10) p++; }
            else if (p < d.Length && d[p] == 10) p++;
            o.IsStream = true;
            o.DataStart = p;
            long len = -1;
            var lv = dict.Get("Length");
            if (lv is PNum ln) len = ln.L;
            else if (lv is PRef lr && Load(lr.Num)?.Value is PNum lrn) len = lrn.L;
            bool ok = len >= 0 && p + len <= d.Length && EndStreamFollows(d, (int)(p + len));
            if (ok) o.DataLen = (int)len;
            else
            {
                int idx = d.Slice(p).IndexOf("endstream"u8);
                int end = idx < 0 ? d.Length : p + idx;
                if (end - p >= 2 && d[end - 2] == 13 && d[end - 1] == 10) end -= 2;
                else if (end - p >= 1 && (d[end - 1] == 10 || d[end - 1] == 13)) end -= 1;
                o.DataLen = end - p;
                o.LengthWrong = true;
            }
        }
        else lx.Pos = save;
        return o;
    }

    private static bool EndStreamFollows(ReadOnlySpan<byte> d, int pos)
    {
        while (pos < d.Length && PdfLexer.IsWhite(d[pos])) pos++;
        return Bin.StartsWith(d, pos, "endstream");
    }

    private PObj? LoadCompressed(int num, int stmNum, int index)
    {
        var os = GetObjStm(stmNum);
        if (os == null) return null;
        int i = index >= 0 && index < os.Nums.Length && os.Nums[index] == num ? index : Array.IndexOf(os.Nums, num);
        if (i < 0) return null;
        try
        {
            var lx = new PdfLexer(os.Buf, os.First + os.Offsets[i]);
            var v = lx.ParseValue();
            return new PObj { Num = num, Gen = 0, Value = v, Buf = os.Buf, Compressed = true };
        }
        catch (PdfFormatException) { return null; }
    }

    private ObjStm? GetObjStm(int num)
    {
        if (_objStms.TryGetValue(num, out var c)) return c;
        ObjStm? result = null;
        if (Xref.TryGetValue(num, out var x) && x.Type == 1)
        {
            try
            {
                var o = ParseIndirectAt(x.A, num);
                if (o is { IsStream: true, Value: PDict d } && d.NameOf("Type") == "ObjStm")
                {
                    var bytes = DecodeStream(o);
                    long n = Num(d.Get("N"), 0), first = Num(d.Get("First"), 0);
                    if (bytes != null && n >= 0 && n <= bytes.Length / 2 && first >= 0 && first <= bytes.Length)
                    {
                        var os = new ObjStm { Buf = bytes, First = (int)first, Nums = new int[n], Offsets = new int[n] };
                        var lx = new PdfLexer(bytes, 0);
                        for (int i = 0; i < n; i++) { os.Nums[i] = (int)lx.ReadInt(); os.Offsets[i] = (int)lx.ReadInt(); }
                        result = os;
                    }
                }
            }
            catch (PdfFormatException) { }
        }
        _objStms[num] = result;
        return result;
    }

    // ---------------- streams ----------------

    /// <summary>Decodes Flate (+ PNG / TIFF predictors) streams; null for other filters.</summary>
    public byte[]? DecodeStream(PObj o)
    {
        if (o.Value is not PDict dict) return null;
        var raw = Data.Span.Slice(o.DataStart, o.DataLen);
        var filters = new List<string>();
        var fv = Resolve(dict.Get("Filter"));
        if (fv is PName fn) filters.Add(fn.N);
        else if (fv is PArr fa) foreach (var it in fa.Items) { if (Resolve(it) is PName n) filters.Add(n.N); }
        var pv = Resolve(dict.Get("DecodeParms") ?? dict.Get("DP"));
        var parms = new List<PDict?>();
        if (pv is PDict pd) parms.Add(pd);
        else if (pv is PArr pa) foreach (var it in pa.Items) parms.Add(Resolve(it) as PDict);

        byte[] data = raw.ToArray();
        for (int i = 0; i < filters.Count; i++)
        {
            if (filters[i] is "FlateDecode" or "Fl")
            {
                data = Txt.Inflate(data);
                var pp = i < parms.Count ? parms[i] : null;
                if (pp != null)
                {
                    long pred = Num(Resolve(pp.Get("Predictor")), 1);
                    if (pred >= 10) data = PngPredictor(data, (int)Num(Resolve(pp.Get("Columns")), 1), (int)Num(Resolve(pp.Get("Colors")), 1), (int)Num(Resolve(pp.Get("BitsPerComponent")), 8));
                    else if (pred == 2) data = TiffPredictor(data, (int)Num(Resolve(pp.Get("Columns")), 1), (int)Num(Resolve(pp.Get("Colors")), 1), (int)Num(Resolve(pp.Get("BitsPerComponent")), 8));
                }
            }
            else return null;
        }
        return data;
    }

    private static byte[] PngPredictor(byte[] src, int columns, int colors, int bpc)
    {
        int rowBytes = (colors * bpc * columns + 7) / 8;
        int bpp = Math.Max(1, colors * bpc / 8);
        int rows = src.Length / (rowBytes + 1);
        var dst = new byte[rows * rowBytes];
        for (int r = 0; r < rows; r++)
        {
            int si = r * (rowBytes + 1), di = r * rowBytes;
            byte ft = src[si++];
            for (int i = 0; i < rowBytes; i++)
            {
                int a = i >= bpp ? dst[di + i - bpp] : 0;
                int b = r > 0 ? dst[di - rowBytes + i] : 0;
                int c = r > 0 && i >= bpp ? dst[di - rowBytes + i - bpp] : 0;
                int x = src[si + i];
                int v = ft switch
                {
                    0 => x,
                    1 => x + a,
                    2 => x + b,
                    3 => x + ((a + b) >> 1),
                    4 => x + Paeth(a, b, c),
                    _ => x,
                };
                dst[di + i] = (byte)v;
            }
        }
        return dst;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] TiffPredictor(byte[] src, int columns, int colors, int bpc)
    {
        if (bpc != 8) return src;
        int rowBytes = colors * columns;
        if (rowBytes <= 0) return src;
        var dst = (byte[])src.Clone();
        for (int r = 0; (r + 1) * rowBytes <= dst.Length; r++)
            for (int i = colors; i < rowBytes; i++) dst[r * rowBytes + i] += dst[r * rowBytes + i - colors];
        return dst;
    }

    // ---------------- rebuild ----------------

    private void Rebuild()
    {
        Xref.Clear(); _objStms.Clear();
        Trailer = new Dictionary<string, PV>();
        Rebuilt = true;
        LastStartXref = -1;
        var d = Data.Span;

        // 1. every "N G obj"
        int from = 0;
        while (true)
        {
            int idx = d.Slice(from).IndexOf("obj"u8);
            if (idx < 0) break;
            int p = from + idx;
            from = p + 3;
            if (p >= 3 && d[p - 3] == 'e' && d[p - 2] == 'n' && d[p - 1] == 'd') continue;
            if (p + 3 < d.Length && !PdfLexer.IsWhite(d[p + 3]) && !PdfLexer.IsDelim(d[p + 3])) continue;
            int q = p - 1;
            int ws1 = 0; while (q >= 0 && PdfLexer.IsWhite(d[q])) { q--; ws1++; }
            int genEnd = q; while (q >= 0 && char.IsAsciiDigit((char)d[q])) q--;
            if (genEnd == q || ws1 == 0) continue;
            int gen = ParseDigits(d, q + 1, genEnd);
            int ws2 = 0; while (q >= 0 && PdfLexer.IsWhite(d[q])) { q--; ws2++; }
            int numEnd = q; while (q >= 0 && char.IsAsciiDigit((char)d[q])) q--;
            if (numEnd == q || ws2 == 0) continue;
            if (numEnd - q > 9) continue;
            int num = ParseDigits(d, q + 1, numEnd);
            Xref[num] = new XEntry(1, q + 1, gen);
        }

        // 2. trailers
        from = 0;
        while (true)
        {
            int idx = d.Slice(from).IndexOf("trailer"u8);
            if (idx < 0) break;
            int p = from + idx + 7;
            from = p;
            try
            {
                var lx = new PdfLexer(d, p);
                if (lx.ParseValue() is PDict td) MergeTrailer(td);
            }
            catch (PdfFormatException) { }
        }

        // 3. xref streams / object streams
        var nums = Xref.Keys.OrderBy(k => k).ToList();
        var stms = new List<int>();
        foreach (int n in nums)
        {
            var x = Xref[n];
            if (x.Type != 1) continue;
            try
            {
                var lx = new PdfLexer(d, (int)x.A);
                lx.ReadInt(); lx.ReadInt(); lx.TryKeyword("obj");
                lx.SkipWs();
                if (lx.PeekIs("<<") && lx.ParseValue() is PDict od)
                {
                    string? t = od.NameOf("Type");
                    if (t == "XRef") MergeTrailer(od);
                    else if (t == "ObjStm") stms.Add(n);
                }
            }
            catch (PdfFormatException) { }
        }
        foreach (int sn in stms)
        {
            var os = GetObjStm(sn);
            if (os == null) continue;
            for (int i = 0; i < os.Nums.Length; i++)
                if (!Xref.ContainsKey(os.Nums[i])) Xref[os.Nums[i]] = new XEntry(2, sn, i);
        }

        // 4. catalog
        if (!Trailer.ContainsKey("Root") || !(Trailer["Root"] is PRef rr && Load(rr.Num)?.Value is PDict))
        {
            foreach (int n in Xref.Keys.OrderByDescending(k => k).ToList())
            {
                var o = Load(n);
                if (o?.Value is PDict od && od.NameOf("Type") == "Catalog") { Trailer["Root"] = new PRef { Num = n, Gen = o.Gen }; break; }
            }
        }
    }

    private void MergeTrailer(PDict td)
    {
        foreach (var k in new[] { "Root", "Info", "ID", "Encrypt" })
            if (td.Get(k) is { } v) Trailer[k] = v;
    }

    private static int ParseDigits(ReadOnlySpan<byte> d, int from, int toInclusive)
    {
        long v = 0;
        for (int i = from; i <= toInclusive; i++) v = Math.Min(v * 10 + (d[i] - '0'), int.MaxValue);
        return (int)v;
    }

    // ---------------- convenience ----------------

    public PRef? RootRef => Trailer.TryGetValue("Root", out var v) ? v as PRef : null;
    public PRef? InfoRef => Trailer.TryGetValue("Info", out var v) ? v as PRef : null;

    public PDict? Catalog => RootRef != null ? Load(RootRef.Num)?.Value as PDict : null;

    public int PageCount()
    {
        try
        {
            var pages = ResolveDict(Catalog?.Get("Pages"));
            return pages != null ? (int)Num(Resolve(pages.Get("Count")), 0) : 0;
        }
        catch (PdfFormatException) { return 0; }
    }

    /// <summary>Decoded XMP stream referenced by the catalog /Metadata, or null.</summary>
    public (int Num, byte[] Bytes)? ReadCatalogXmp()
    {
        if (Catalog?.Get("Metadata") is not PRef r) return null;
        var o = Load(r.Num);
        if (o is not { IsStream: true }) return null;
        var b = DecodeStream(o);
        return b == null ? null : (r.Num, b);
    }
}
