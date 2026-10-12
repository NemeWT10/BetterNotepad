using System.Globalization;

namespace BetterNotepad.Metadata;

internal static class PdfFormat
{
    private static readonly string[] InfoOrder = { "Title", "Author", "Subject", "Keywords", "Creator", "Producer", "CreationDate", "ModDate", "Trapped" };
    private static readonly HashSet<string> Sensitive = new() { "Author", "Creator", "Producer" };

    public static void Read(MetadataReport report, ReadOnlyMemory<byte> data)
    {
        PdfDocument doc;
        try { doc = PdfDocument.Open(data); }
        catch (MetadataException) { report.Warnings.Add("read.partial"); return; }
        if (doc.Rebuilt) report.Warnings.Add("pdf.xrefRebuilt");

        var g = report.Group(MetadataGroupId.Pdf);
        g.Entries.Add(new MetadataEntry("pdf.Version", "meta.pdf.Version", doc.Version, MetadataValueKind.Text, false, false, "PDF version"));
        g.Entries.Add(new MetadataEntry("pdf.Pages", "meta.pdf.Pages", doc.PageCount().ToString(CultureInfo.InvariantCulture), MetadataValueKind.Number, false, false, "Pages"));
        g.Entries.Add(new MetadataEntry("pdf.Encrypted", "meta.pdf.Encrypted", doc.Encrypted ? "true" : "false", MetadataValueKind.Boolean, false, false, "Encrypted"));
        if (doc.Encrypted) { report.Warnings.Add("pdf.encrypted"); return; }

        PDict? info = null;
        PObj? infoObj = null;
        if (doc.InfoRef != null) { infoObj = doc.Load(doc.InfoRef.Num); info = infoObj?.Value as PDict; }

        string? Text(PV? v)
        {
            v = doc.Resolve(v);
            return v switch
            {
                PStr s => PdfText.Decode(s.B),
                PName n => n.N,
                PNum n => n.IsInt ? n.L.ToString(CultureInfo.InvariantCulture) : Txt.Num(n.D),
                PBool b => b.V ? "true" : "false",
                _ => null,
            };
        }

        foreach (var key in InfoOrder)
        {
            string? raw = info != null ? Text(info.Get(key)) : null;
            bool editable = key != "Trapped";
            if (raw == null && !editable) continue;
            string value = raw ?? "";
            var kind = MetadataValueKind.Text;
            if (key is "CreationDate" or "ModDate")
            {
                kind = MetadataValueKind.DateTime;
                if (raw != null)
                {
                    var dt = PdfText.ParseDate(raw);
                    if (dt != null) value = Txt.Iso(dt.Value); else kind = MetadataValueKind.Text;
                }
            }
            g.Entries.Add(new MetadataEntry("pdf." + key, "meta.pdf." + key, Txt.Clip(value), kind, editable, Sensitive.Contains(key) && value.Length > 0, key));
        }
        if (info != null)
        {
            var used = new HashSet<string>();
            foreach (var e in info.Entries)
            {
                if (InfoOrder.Contains(e.Key)) continue;
                string? t = Text(e.Value);
                if (t == null) continue;
                string k = "pdf.Custom." + e.Key; int n = 2;
                while (!used.Add(k)) k = "pdf.Custom." + e.Key + "#" + n++;
                g.Entries.Add(new MetadataEntry(k, "meta.pdf.Custom", Txt.Clip(t), MetadataValueKind.Text, false, true, e.Key));
            }
        }

        try
        {
            var xmp = doc.ReadCatalogXmp();
            if (xmp != null)
            {
                var props = XmpReader.Read(xmp.Value.Bytes);
                if (props == null) report.Warnings.Add("xmp.unreadable");
                else if (props.Count > 0) XmpReader.AddEntries(report.Group(MetadataGroupId.Xmp), props);
            }
            else if (doc.Catalog?.Get("Metadata") != null) report.Warnings.Add("xmp.unreadable");
        }
        catch (PdfFormatException) { report.Warnings.Add("xmp.unreadable"); }
    }
}
