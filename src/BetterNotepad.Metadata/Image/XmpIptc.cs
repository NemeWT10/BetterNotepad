using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace BetterNotepad.Metadata;

internal readonly record struct XmpProp(string Name, string Value);

internal static class XmpReader
{
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace XmlNs = "http://www.w3.org/XML/1998/namespace";

    private static readonly Dictionary<string, string> KnownPrefixes = new()
    {
        ["http://purl.org/dc/elements/1.1/"] = "dc",
        ["http://ns.adobe.com/xap/1.0/"] = "xmp",
        ["http://ns.adobe.com/xap/1.0/rights/"] = "xmpRights",
        ["http://ns.adobe.com/xap/1.0/mm/"] = "xmpMM",
        ["http://ns.adobe.com/xap/1.0/bj/"] = "xmpBJ",
        ["http://ns.adobe.com/xap/1.0/sType/ResourceEvent#"] = "stEvt",
        ["http://ns.adobe.com/xap/1.0/sType/ResourceRef#"] = "stRef",
        ["http://ns.adobe.com/photoshop/1.0/"] = "photoshop",
        ["http://ns.adobe.com/tiff/1.0/"] = "tiff",
        ["http://ns.adobe.com/exif/1.0/"] = "exif",
        ["http://ns.adobe.com/exif/1.0/aux/"] = "aux",
        ["http://ns.adobe.com/camera-raw-settings/1.0/"] = "crs",
        ["http://ns.adobe.com/lightroom/1.0/"] = "lr",
        ["http://ns.adobe.com/pdf/1.3/"] = "pdf",
        ["http://ns.adobe.com/pdfx/1.3/"] = "pdfx",
        ["http://www.aiim.org/pdfa/ns/id/"] = "pdfaid",
        ["http://iptc.org/std/Iptc4xmpCore/1.0/xmlns/"] = "Iptc4xmpCore",
        ["http://iptc.org/std/Iptc4xmpExt/2008-02-29/"] = "Iptc4xmpExt",
        ["http://ns.microsoft.com/photo/1.0/"] = "MicrosoftPhoto",
        ["http://www.metadataworkinggroup.com/schemas/regions/"] = "mwg-rs",
        ["http://ns.adobe.com/xap/1.0/t/pg/"] = "xmpTPg",
        ["http://ns.adobe.com/xmp/1.0/DynamicMedia/"] = "xmpDM",
    };

    private static readonly Regex SensitiveRx = new(
        "creator|author|gps|history|credit|source|location|city|country|state|address|contact|owner|photographer|serial|email|lastmodifiedby|derived|ingredients|documentid|instanceid",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsSensitive(string name) => SensitiveRx.IsMatch(name);

    /// <summary>Returns null when the packet cannot be parsed.</summary>
    public static List<XmpProp>? Read(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return new();
        try
        {
            int s = data.IndexOf("<x:xmpmeta"u8);
            int e = data.LastIndexOf("</x:xmpmeta>"u8);
            ReadOnlySpan<byte> slice = s >= 0 && e > s ? data.Slice(s, e + 12 - s) : data;
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CheckCharacters = false, MaxCharactersInDocument = 64 * 1024 * 1024 };
            using var ms = new MemoryStream(slice.ToArray());
            using var xr = XmlReader.Create(ms, settings);
            var doc = XDocument.Load(xr);
            var list = new List<XmpProp>();
            foreach (var rdf in doc.Descendants(Rdf + "RDF"))
                foreach (var desc in rdf.Elements(Rdf + "Description"))
                    ReadStruct(desc, "", list, 0);
            return list;
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException) { return null; }
    }

    private static bool Skippable(XAttribute a) =>
        a.IsNamespaceDeclaration || a.Name.Namespace == Rdf || a.Name.Namespace == XmlNs;

    private static string PName(XElement ctx, XName n)
    {
        if (n.Namespace == XNamespace.None) return n.LocalName;
        if (!KnownPrefixes.TryGetValue(n.NamespaceName, out var p)) p = ctx.GetPrefixOfNamespace(n.Namespace) ?? "ns";
        return p + ":" + n.LocalName;
    }

    private static void Add(List<XmpProp> list, string name, string value)
    {
        if (list.Count >= 400) return;
        value = value.Trim();
        if (name.Contains("Thumbnails", StringComparison.Ordinal)) return;
        if (value.Length > 500 && value.IndexOf(' ') < 0) value = $"({value.Length} chars)";
        list.Add(new XmpProp(name, Txt.Clip(value)));
    }

    private static bool IsSimple(XElement li) => !li.HasElements && !li.Attributes().Any(a => !Skippable(a));

    private static void ReadStruct(XElement el, string path, List<XmpProp> list, int depth)
    {
        if (depth > 6) return;
        foreach (var a in el.Attributes())
            if (!Skippable(a)) Add(list, path + PName(el, a.Name), a.Value);
        foreach (var child in el.Elements()) ReadProp(child, path, list, depth);
    }

    private static void ReadProp(XElement p, string path, List<XmpProp> list, int depth)
    {
        string name = path + PName(p, p.Name);
        var first = p.Elements().FirstOrDefault();
        if (first == null)
        {
            var res = p.Attribute(Rdf + "resource");
            if (res != null) { Add(list, name, res.Value); return; }
            if (p.Attributes().Any(a => !Skippable(a)) && string.IsNullOrWhiteSpace(p.Value)) { ReadStruct(p, name + "/", list, depth + 1); return; }
            Add(list, name, p.Value);
            return;
        }
        if (first.Name == Rdf + "Alt" || first.Name == Rdf + "Bag" || first.Name == Rdf + "Seq")
        {
            var items = first.Elements(Rdf + "li").ToList();
            if (items.All(IsSimple))
            {
                if (first.Name == Rdf + "Alt")
                {
                    var pick = items.FirstOrDefault(i => (string?)i.Attribute(XmlNs + "lang") == "x-default") ?? items.FirstOrDefault();
                    if (pick != null) Add(list, name, pick.Value);
                }
                else Add(list, name, string.Join("; ", items.Select(i => i.Value.Trim()).Where(v => v.Length > 0)));
            }
            else
            {
                for (int i = 0; i < items.Count && i < 50; i++)
                {
                    var li = items[i];
                    var inner = li.Elements(Rdf + "Description").FirstOrDefault() ?? li;
                    ReadStruct(inner, $"{name}[{i + 1}]/", list, depth + 1);
                }
            }
            return;
        }
        if (first.Name == Rdf + "Description") { ReadStruct(first, name + "/", list, depth + 1); return; }
        if (p.Attribute(Rdf + "parseType")?.Value == "Resource") { ReadStruct(p, name + "/", list, depth + 1); return; }
        Add(list, name, p.Value);
    }

    public static void AddEntries(MetadataGroup group, IReadOnlyList<XmpProp> props)
    {
        var used = new HashSet<string>();
        foreach (var pr in props)
        {
            string kn = pr.Name.Replace(':', '.');
            string key = "xmp." + kn;
            int n = 2;
            while (!used.Add(key)) key = "xmp." + kn + "#" + n++;
            group.Entries.Add(new MetadataEntry(key, "meta." + key, pr.Value, MetadataValueKind.Text, false, IsSensitive(pr.Name), pr.Name));
        }
    }
}

internal static class IptcReader
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [5] = "ObjectName", [7] = "EditStatus", [10] = "Urgency", [15] = "Category", [20] = "SupplementalCategory", [25] = "Keywords",
        [26] = "ContentLocationCode", [27] = "ContentLocationName", [30] = "ReleaseDate", [35] = "ReleaseTime", [40] = "SpecialInstructions",
        [55] = "DateCreated", [60] = "TimeCreated", [62] = "DigitalCreationDate", [63] = "DigitalCreationTime", [65] = "OriginatingProgram",
        [70] = "ProgramVersion", [80] = "ByLine", [85] = "ByLineTitle", [90] = "City", [92] = "Sublocation", [95] = "ProvinceState",
        [100] = "CountryCode", [101] = "CountryName", [103] = "OriginalTransmissionReference", [105] = "Headline", [110] = "Credit",
        [115] = "Source", [116] = "CopyrightNotice", [118] = "Contact", [120] = "CaptionAbstract", [122] = "WriterEditor",
        [135] = "LanguageIdentifier",
    };

    private static readonly HashSet<string> Sensitive = new()
    {
        "ByLine", "ByLineTitle", "City", "Sublocation", "ProvinceState", "CountryName", "CountryCode", "Credit", "Source", "Contact",
        "WriterEditor", "CaptionAbstract", "ContentLocationName", "OriginatingProgram", "CopyrightNotice",
    };

    /// <summary>payload = APP13 data after the segment length ("Photoshop 3.0\0" + 8BIM resources).</summary>
    public static void AddEntries(MetadataGroup group, ReadOnlySpan<byte> payload)
    {
        const string sig = "Photoshop 3.0\0";
        if (!Bin.StartsWith(payload, 0, sig)) return;
        int p = sig.Length;
        while (p + 12 <= payload.Length && Bin.StartsWith(payload, p, "8BIM"))
        {
            int id = Bin.U16(payload, p + 4, false);
            int nameLen = payload[p + 6];
            int q = p + 7 + nameLen; if ((1 + nameLen) % 2 == 1) q++;
            if (q + 4 > payload.Length) break;
            uint size = Bin.U32(payload, q, false);
            q += 4;
            if (size > payload.Length - q) break;
            if (id == 0x0404) ParseIim(group, payload.Slice(q, (int)size));
            p = q + (int)size; if ((size & 1) == 1) p++;
        }
    }

    private static void ParseIim(MetadataGroup group, ReadOnlySpan<byte> d)
    {
        bool utf8 = false;
        var values = new List<(int Id, string Text)>();
        int p = 0;
        // first pass: charset (1:90)
        for (int q = 0; q + 5 <= d.Length && d[q] == 0x1C;)
        {
            int rec = d[q + 1], ds = d[q + 2]; int len = Bin.U16(d, q + 3, false);
            int dataAt = q + 5;
            if ((len & 0x8000) != 0) { int n = len & 0x7FFF; if (n > 4 || dataAt + n > d.Length) break; long l = 0; for (int i = 0; i < n; i++) l = l << 8 | d[dataAt + i]; dataAt += n; len = (int)l; }
            if (dataAt + len > d.Length) break;
            if (rec == 1 && ds == 90 && len >= 3 && d[dataAt] == 0x1B && d[dataAt + 1] == 0x25 && d[dataAt + 2] == 0x47) utf8 = true;
            q = dataAt + len;
        }
        while (p + 5 <= d.Length && d[p] == 0x1C)
        {
            int rec = d[p + 1], ds = d[p + 2]; int len = Bin.U16(d, p + 3, false);
            int dataAt = p + 5;
            if ((len & 0x8000) != 0) { int n = len & 0x7FFF; if (n > 4 || dataAt + n > d.Length) break; long l = 0; for (int i = 0; i < n; i++) l = l << 8 | d[dataAt + i]; dataAt += n; len = (int)l; }
            if (dataAt + len > d.Length) break;
            if (rec == 2 && Names.ContainsKey(ds))
            {
                var bytes = d.Slice(dataAt, len);
                string s;
                if (utf8) { try { s = new UTF8Encoding(false, true).GetString(bytes); } catch { s = Encoding.Latin1.GetString(bytes); } }
                else s = Encoding.Latin1.GetString(bytes);
                values.Add((ds, s.TrimEnd('\0')));
            }
            p = dataAt + len;
        }
        foreach (var grp in values.GroupBy(v => v.Id))
        {
            string name = Names[grp.Key];
            string text = string.Join("; ", grp.Select(g => g.Text));
            group.Entries.Add(new MetadataEntry("iptc." + name, "meta.iptc." + name, Txt.Clip(text), MetadataValueKind.Text, false, Sensitive.Contains(name), name));
        }
    }
}
