using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace BetterNotepad.Metadata;

/// <summary>
/// Full rewrite that really removes metadata: walks the object graph from the catalog, drops the Info dictionary and every
/// /Metadata (XMP) and /PieceInfo entry, and writes only the objects still reachable (so the old strings are gone from the
/// file, not merely unreferenced). Objects keep their numbers; object streams are expanded; a classic xref table is written.
/// </summary>
internal static class PdfStripper
{
    private readonly record struct Edit(int Start, int End, byte[]? Replacement);

    private sealed class Plan
    {
        public int Gen;
        public bool Skip;
        public readonly List<Edit> Edits = new();
    }

    /// <returns>False when there was nothing to remove (nothing written).</returns>
    public static bool Strip(PdfDocument doc, StripOptions o, Stream output, List<string> removed)
    {
        if (doc.Encrypted) throw new MetadataException(MetadataErrorCode.Encrypted, "The PDF is encrypted.");
        var root = doc.RootRef ?? throw new MetadataException(MetadataErrorCode.Corrupt, "PDF has no catalog.");
        var info = doc.InfoRef;

        var plans = new SortedDictionary<int, Plan>();
        var queue = new Queue<int>();
        bool anything = false;

        void Visit(int n)
        {
            if (n <= 0 || plans.ContainsKey(n)) return;
            plans[n] = new Plan();
            queue.Enqueue(n);
        }

        Visit(root.Num);
        if (info != null)
        {
            if (o.DocumentProperties) { removed.Add("pdf.info"); anything = true; }
            else Visit(info.Num);
        }

        while (queue.Count > 0)
        {
            int n = queue.Dequeue();
            var plan = plans[n];
            var obj = doc.Load(n);
            if (obj == null) { plan.Skip = true; continue; }
            plan.Gen = obj.Compressed ? 0 : obj.Gen;
            if (obj.Value is PDict d && d.Has("Linearized") && !obj.IsStream) { plan.Skip = true; continue; }
            var refs = new List<int>();
            Collect(doc, obj.Value, plan.Edits, refs, o, removed, ref anything);
            if (obj.IsStream && obj.LengthWrong && obj.Value is PDict sd) FixLength(obj, sd, plan.Edits);
            foreach (int r in refs) Visit(r);
        }
        if (!anything) return false;

        var data = doc.Data.Span;
        void Write(ReadOnlySpan<byte> b) => output.Write(b);
        void WriteAscii(string s) => output.Write(Encoding.ASCII.GetBytes(s));

        WriteAscii("%PDF-" + doc.Version + "\n");
        output.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

        var offsets = new Dictionary<int, long>();
        var gens = new Dictionary<int, int>();
        foreach (var (n, plan) in plans)
        {
            if (plan.Skip) continue;
            var obj = doc.Load(n);
            if (obj == null) continue;
            offsets[n] = output.Position;
            gens[n] = plan.Gen;
            WriteAscii($"{n} {plan.Gen} obj\n");
            var buf = obj.Buf.Span;
            int cursor = obj.Value.S;
            foreach (var e in plan.Edits.OrderBy(x => x.Start))
            {
                if (e.Start < cursor) continue;
                Write(buf.Slice(cursor, e.Start - cursor));
                if (e.Replacement != null) Write(e.Replacement);
                cursor = e.End;
            }
            Write(buf.Slice(cursor, obj.Value.E - cursor));
            if (obj.IsStream)
            {
                WriteAscii("\nstream\n");
                Write(data.Slice(obj.DataStart, obj.DataLen));
                WriteAscii("\nendstream");
            }
            WriteAscii("\nendobj\n");
        }

        if (!offsets.ContainsKey(root.Num)) throw new MetadataException(MetadataErrorCode.Corrupt, "Catalog could not be read.");
        int size = offsets.Keys.Max() + 1;
        long xrefPos = output.Position;
        WriteAscii($"xref\n0 {size}\n");

        // free list: 0 -> first free -> ... -> 0
        var nextFree = new int[size];
        int prevFree = 0;
        for (int i = 1; i < size; i++)
        {
            if (offsets.ContainsKey(i)) continue;
            nextFree[prevFree] = i;
            prevFree = i;
        }
        var table = new StringBuilder(size * 20);
        for (int i = 0; i < size; i++)
        {
            if (offsets.TryGetValue(i, out long off))
                table.Append($"{off:D10} {gens[i]:D5} n\r\n");
            else
            {
                int gen = i == 0 ? 65535 : (doc.Xref.TryGetValue(i, out var xe) && xe.Type == 1 ? Math.Min(xe.B + 1, 65535) : 0);
                table.Append($"{nextFree[i]:D10} {gen:D5} f\r\n");
            }
        }
        WriteAscii(table.ToString());
        var sb = new StringBuilder();
        sb.Append($"trailer\n<< /Size {size} /Root {root.Num} {gens[root.Num]} R");
        if (!o.DocumentProperties && info != null && offsets.ContainsKey(info.Num)) sb.Append($" /Info {info.Num} {gens[info.Num]} R");
        WriteAscii(sb.ToString());
        if (doc.Trailer.TryGetValue("ID", out var id) && id is PArr && id.E <= data.Length && id.E > id.S)
        {
            WriteAscii(" /ID ");
            Write(data.Slice(id.S, id.E - id.S));
        }
        WriteAscii($" >>\nstartxref\n{xrefPos}\n%%EOF\n");
        return true;
    }

    private static void FixLength(PObj obj, PDict dict, List<Edit> edits)
    {
        var lv = dict.Get("Length");
        var text = Encoding.ASCII.GetBytes(obj.DataLen.ToString(CultureInfo.InvariantCulture));
        if (lv != null && lv.E > lv.S) edits.Add(new Edit(lv.S, lv.E, text));
        else edits.Add(new Edit(dict.E - 2, dict.E - 2, Encoding.ASCII.GetBytes(" /Length " + obj.DataLen.ToString(CultureInfo.InvariantCulture) + " ")));
    }

    private static void Collect(PdfDocument doc, PV v, List<Edit> edits, List<int> refs, StripOptions o, List<string> removed, ref bool anything)
    {
        switch (v)
        {
            case PRef r:
                refs.Add(r.Num);
                break;
            case PArr a:
                foreach (var it in a.Items) Collect(doc, it, edits, refs, o, removed, ref anything);
                break;
            case PDict d:
                foreach (var e in d.Entries)
                {
                    if (e.Key == "Metadata" && o.Xmp && e.Value is PRef mr && IsXmpStream(doc, mr))
                    {
                        edits.Add(new Edit(e.KeyStart, e.Value.E, null));
                        if (!removed.Contains("pdf.xmp")) removed.Add("pdf.xmp");
                        anything = true;
                        continue;
                    }
                    if (e.Key == "PieceInfo" && o.OtherEmbedded)
                    {
                        edits.Add(new Edit(e.KeyStart, e.Value.E, null));
                        if (!removed.Contains("pdf.pieceInfo")) removed.Add("pdf.pieceInfo");
                        anything = true;
                        continue;
                    }
                    Collect(doc, e.Value, edits, refs, o, removed, ref anything);
                }
                break;
        }
    }

    private static bool IsXmpStream(PdfDocument doc, PRef r)
    {
        var t = doc.Load(r.Num);
        return t is { IsStream: true, Value: PDict td } && (td.NameOf("Subtype") == "XML" || td.NameOf("Type") == "Metadata");
    }
}

/// <summary>Strict structural check of a produced PDF (no xref repair allowed).</summary>
internal static class PdfVerifier
{
    /// <summary>
    /// Strict check (no xref repair): the output must open, have a catalog, every object must load and the page count must match.
    /// With <paramref name="tolerate"/> (used for append-only edits) damage the original already had is not held against the result.
    /// </summary>
    public static void Verify(ReadOnlyMemory<byte> output, int expectedPages, ReadOnlyMemory<byte>? tolerate = null)
    {
        var baseline = new HashSet<string>();
        if (tolerate != null) { baseline = Problems(tolerate.Value, out int basePages); expectedPages = basePages; }
        var problems = Problems(output, out int pages);
        problems.ExceptWith(baseline);
        if (problems.Count > 0) throw new MetadataException(MetadataErrorCode.VerificationFailed, "Output PDF failed verification: " + problems.First());
        if (expectedPages >= 0 && pages != expectedPages) throw new MetadataException(MetadataErrorCode.VerificationFailed, "Output PDF page count differs.");
    }

    private static HashSet<string> Problems(ReadOnlyMemory<byte> data, out int pages)
    {
        var problems = new HashSet<string>();
        pages = -1;
        PdfDocument doc;
        try { doc = PdfDocument.Open(data, allowRebuild: false); }
        catch (MetadataException) { problems.Add("cross-reference data is damaged"); return problems; }
        if (doc.Rebuilt) problems.Add("cross-reference data is damaged");
        if (doc.Catalog == null) problems.Add("no catalog");
        foreach (var (num, x) in doc.Xref)
        {
            if (x.Type == 0) continue;
            if (doc.Load(num) == null) problems.Add($"object {num} cannot be read");
        }
        pages = doc.PageCount();
        return problems;
    }
}

/// <summary>Appends a new revision (new Info dictionary and, when present, an updated XMP stream); original bytes stay untouched.</summary>
internal static class PdfEditor
{
    public static readonly Dictionary<string, string> InfoKeys = new()
    {
        ["pdf.Title"] = "Title", ["pdf.Author"] = "Author", ["pdf.Subject"] = "Subject", ["pdf.Keywords"] = "Keywords",
        ["pdf.Creator"] = "Creator", ["pdf.Producer"] = "Producer", ["pdf.CreationDate"] = "CreationDate", ["pdf.ModDate"] = "ModDate",
    };

    public static bool Apply(PdfDocument doc, ReadOnlySpan<byte> file, IReadOnlyDictionary<string, string?> changes, Stream output, SetResult result)
    {
        // validate
        var plain = new Dictionary<string, string?>();     // Info key -> plain value (dates as ISO), null = clear
        var pdfValue = new Dictionary<string, string?>();  // Info key -> serialized PDF value, null = clear
        var keyOf = new Dictionary<string, string>();      // Info key -> original change key
        foreach (var (key, value) in changes)
        {
            if (!InfoKeys.TryGetValue(key, out var ik))
            {
                result.Rejected[key] = key.StartsWith("pdf.", StringComparison.Ordinal) || key.StartsWith("xmp.", StringComparison.Ordinal) ? "notEditable" : "unknown";
                continue;
            }
            string v = value?.Trim() ?? "";
            keyOf[ik] = key;
            if (v.Length == 0) { plain[ik] = null; pdfValue[ik] = null; continue; }
            if (ik is "CreationDate" or "ModDate")
            {
                if (!Txt.TryParseIso(v, out var dto)) { result.Rejected[key] = "invalidValue"; keyOf.Remove(ik); continue; }
                plain[ik] = Txt.Iso(dto);
                pdfValue[ik] = "(" + PdfText.FormatDate(dto) + ")";
            }
            else { plain[ik] = v; pdfValue[ik] = PdfText.Serialize(v); }
        }
        if (plain.Count == 0) return false;
        if (doc.Encrypted) throw new MetadataException(MetadataErrorCode.Encrypted, "The PDF is encrypted.");
        if (doc.Rebuilt) throw new MetadataException(MetadataErrorCode.Corrupt, "The PDF cross-reference table is damaged; the file cannot be updated in place.");
        if (doc.RootRef == null) throw new MetadataException(MetadataErrorCode.Corrupt, "PDF has no catalog.");

        int header = doc.HeaderOffset;
        var infoRef = doc.InfoRef;
        var infoObj = infoRef != null ? doc.Load(infoRef.Num) : null;
        var infoDict = infoObj?.Value as PDict;
        long oldSize = PdfDocument.Num(doc.Trailer.GetValueOrDefault("Size"), 0);
        int nextNum = (int)Math.Max(oldSize, doc.Xref.Count == 0 ? 1 : doc.Xref.Keys.Max() + 1);
        int infoNum = infoRef?.Num ?? nextNum++;
        int infoGen = infoRef != null ? (infoObj?.Gen ?? infoRef.Gen) : 0;

        // new Info body
        var body = new MemoryStream();
        void W(string s) => body.Write(Encoding.ASCII.GetBytes(s));
        W("<<\n");
        if (infoDict != null && infoObj != null)
        {
            var buf = infoObj.Buf.Span;
            foreach (var e in infoDict.Entries)
            {
                if (pdfValue.ContainsKey(e.Key)) continue;
                body.Write(buf.Slice(e.KeyStart, e.Value.E - e.KeyStart));
                W("\n");
            }
        }
        foreach (var (k, v) in pdfValue)
            if (v != null) W("/" + k + " " + v + "\n");
        W(">>");

        var newObjs = new List<(int Num, int Gen, byte[] Body, byte[]? Stream)>();
        newObjs.Add((infoNum, infoGen, body.ToArray(), null));

        // keep XMP consistent with Info
        var xmp = doc.ReadCatalogXmp();
        if (xmp != null)
        {
            var patched = PdfXmpPatcher.Patch(xmp.Value.Bytes, plain);
            if (patched != null)
            {
                int xgen = doc.Xref.TryGetValue(xmp.Value.Num, out var xe) && xe.Type == 1 ? xe.B : 0;
                newObjs.Add((xmp.Value.Num, xgen, Encoding.ASCII.GetBytes($"<< /Type /Metadata /Subtype /XML /Length {patched.Length} >>"), patched));
            }
        }

        // an object load may have triggered a lazy xref rebuild: then there is no valid previous section to chain to
        if (doc.Rebuilt) throw new MetadataException(MetadataErrorCode.Corrupt, "The PDF cross-reference table is damaged; the file cannot be updated in place.");

        // write
        output.Write(file);
        if (file.Length > 0 && file[^1] != 10 && file[^1] != 13) output.Write("\n"u8);
        var entries = new List<(int Num, long Off, int Gen)>();
        foreach (var (num, gen, b, s) in newObjs)
        {
            entries.Add((num, output.Position - header, gen));
            output.Write(Encoding.ASCII.GetBytes($"{num} {gen} obj\n"));
            output.Write(b);
            if (s != null)
            {
                output.Write("\nstream\n"u8);
                output.Write(s);
                output.Write("\nendstream"u8);
            }
            output.Write("\nendobj\n"u8);
        }

        int newSize = (int)Math.Max(oldSize, entries.Max(x => x.Num) + 1);
        var trailerExtra = new StringBuilder();
        trailerExtra.Append($" /Root {doc.RootRef.Num} {doc.RootRef.Gen} R /Info {infoNum} {infoGen} R");
        if (doc.Trailer.TryGetValue("ID", out var id) && id is PArr && id.E > id.S) trailerExtra.Append(" /ID ").Append(Encoding.Latin1.GetString(doc.Data.Span.Slice(id.S, id.E - id.S)));
        trailerExtra.Append($" /Prev {doc.LastStartXref}");

        entries.Sort((a, b) => a.Num.CompareTo(b.Num));
        if (doc.LastIsXrefStream)
        {
            int xnum = Math.Max(newSize, 1);
            long xoff = output.Position - header;
            entries.Add((xnum, xoff, 0));
            newSize = xnum + 1;
            var rows = new MemoryStream();
            var index = new StringBuilder();
            int i = 0;
            while (i < entries.Count)
            {
                int j = i;
                while (j + 1 < entries.Count && entries[j + 1].Num == entries[j].Num + 1) j++;
                index.Append($"{entries[i].Num} {j - i + 1} ");
                for (int k = i; k <= j; k++)
                {
                    rows.WriteByte(1);
                    uint off = (uint)entries[k].Off;
                    rows.WriteByte((byte)(off >> 24)); rows.WriteByte((byte)(off >> 16)); rows.WriteByte((byte)(off >> 8)); rows.WriteByte((byte)off);
                    rows.WriteByte((byte)(entries[k].Gen >> 8)); rows.WriteByte((byte)entries[k].Gen);
                }
                i = j + 1;
            }
            var rowBytes = rows.ToArray();
            output.Write(Encoding.Latin1.GetBytes($"{xnum} 0 obj\n<< /Type /XRef /Size {newSize} /W [1 4 2] /Index [{index.ToString().TrimEnd()}]{trailerExtra} /Length {rowBytes.Length} >>\nstream\n"));
            output.Write(rowBytes);
            output.Write("\nendstream\nendobj\n"u8);
            output.Write(Encoding.ASCII.GetBytes($"startxref\n{xoff}\n%%EOF\n"));
        }
        else
        {
            long xoff = output.Position - header;
            var sb = new StringBuilder("xref\n");
            int i = 0;
            while (i < entries.Count)
            {
                int j = i;
                while (j + 1 < entries.Count && entries[j + 1].Num == entries[j].Num + 1) j++;
                sb.Append($"{entries[i].Num} {j - i + 1}\n");
                for (int k = i; k <= j; k++) sb.Append($"{entries[k].Off:D10} {entries[k].Gen:D5} n\r\n");
                i = j + 1;
            }
            sb.Append($"trailer\n<< /Size {newSize}{trailerExtra} >>\nstartxref\n{xoff}\n%%EOF\n");
            output.Write(Encoding.Latin1.GetBytes(sb.ToString()));
        }
        foreach (var k in keyOf.Values) result.Applied.Add(k);
        return true;
    }
}

/// <summary>Keeps the XMP packet in step with the Info dictionary (dc:title, dc:creator, dc:description, pdf:Keywords ...).</summary>
internal static class PdfXmpPatcher
{
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace Pdf = "http://ns.adobe.com/pdf/1.3/";
    private static readonly XNamespace XmlNs = "http://www.w3.org/XML/1998/namespace";

    public static byte[]? Patch(byte[] xmp, Dictionary<string, string?> values)
    {
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CheckCharacters = false };
            using var ms = new MemoryStream(xmp);
            using var xr = XmlReader.Create(ms, settings);
            doc = XDocument.Load(xr, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException) { return null; }
        var descs = doc.Descendants(Rdf + "Description").ToList();
        if (descs.Count == 0) return null;

        foreach (var (key, value) in values)
        {
            switch (key)
            {
                case "Title": SetContainer(descs, Dc + "title", "dc", "Alt", value); break;
                case "Subject": SetContainer(descs, Dc + "description", "dc", "Alt", value); break;
                case "Author": SetContainer(descs, Dc + "creator", "dc", "Seq", value); break;
                case "Keywords": SetText(descs, Pdf + "Keywords", "pdf", value); break;
                case "Producer": SetText(descs, Pdf + "Producer", "pdf", value); break;
                case "Creator": SetText(descs, Xmp + "CreatorTool", "xmp", value); break;
                case "CreationDate": SetText(descs, Xmp + "CreateDate", "xmp", value); break;
                case "ModDate": SetText(descs, Xmp + "ModifyDate", "xmp", value); break;
            }
        }
        var ws = new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = doc.Declaration == null, Indent = false };
        using var outMs = new MemoryStream();
        using (var xw = XmlWriter.Create(outMs, ws)) doc.Save(xw);
        return outMs.ToArray();
    }

    private static void EnsurePrefix(XElement d, XNamespace ns, string prefix)
    {
        if (d.GetPrefixOfNamespace(ns) == null) d.SetAttributeValue(XNamespace.Xmlns + prefix, ns.NamespaceName);
    }

    private static void SetText(List<XElement> descs, XName name, string prefix, string? value)
    {
        bool empty = string.IsNullOrEmpty(value);
        foreach (var d in descs)
        {
            var a = d.Attribute(name);
            if (a != null) { if (empty) a.Remove(); else a.Value = value!; return; }
            var e = d.Element(name);
            if (e != null) { if (empty) e.Remove(); else { e.RemoveNodes(); e.Value = value!; } return; }
        }
        if (empty) return;
        EnsurePrefix(descs[0], name.Namespace, prefix);
        descs[0].Add(new XElement(name, value));
    }

    private static void SetContainer(List<XElement> descs, XName name, string prefix, string kind, string? value)
    {
        bool empty = string.IsNullOrEmpty(value);
        XElement Build()
        {
            var li = new XElement(Rdf + "li", value);
            if (kind == "Alt") li.SetAttributeValue(XmlNs + "lang", "x-default");
            return new XElement(name, new XElement(Rdf + kind, li));
        }
        foreach (var d in descs)
        {
            var e = d.Element(name);
            if (e != null) { if (empty) e.Remove(); else e.ReplaceWith(Build()); return; }
            var a = d.Attribute(name);
            if (a != null) { a.Remove(); if (!empty) { EnsurePrefix(d, name.Namespace, prefix); d.Add(Build()); } return; }
        }
        if (empty) return;
        EnsurePrefix(descs[0], name.Namespace, prefix);
        descs[0].Add(Build());
    }
}
