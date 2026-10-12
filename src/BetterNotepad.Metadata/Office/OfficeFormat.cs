using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace BetterNotepad.Metadata;

/// <summary>
/// OOXML packages (docx/xlsx/pptx and macro / template variants). Implemented on <see cref="ZipArchive"/> + LINQ to XML
/// instead of System.IO.Packaging: no WPF dependency, entries that are not touched keep their bytes and order, and the
/// property parts are located through the package relationships (works for the psmdcp location Packaging writes too).
/// </summary>
internal static class OfficeFormat
{
    private static readonly string[] Extensions =
    {
        ".docx", ".docm", ".dotx", ".dotm", ".xlsx", ".xlsm", ".xltx", ".xltm", ".xlsb", ".xlam",
        ".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm", ".ppam", ".vsdx", ".vsdm",
    };

    private static readonly XNamespace Cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace DcTerms = "http://purl.org/dc/terms/";
    private static readonly XNamespace DcMiType = "http://purl.org/dc/dcmitype/";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace Ep = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";
    private static readonly XNamespace Vt = "http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes";
    private static readonly XNamespace CustomNs = "http://schemas.openxmlformats.org/officeDocument/2006/custom-properties";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace CtNs = "http://schemas.openxmlformats.org/package/2006/content-types";

    private const string CoreRelType = "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties";
    private const string AppRelType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties";
    private const string CoreContentType = "application/vnd.openxmlformats-package.core-properties+xml";
    private const string AppContentType = "application/vnd.openxmlformats-officedocument.extended-properties+xml";

    private enum Part { Core, App }
    private sealed record Field(string Key, Part Part, XNamespace Ns, string Local, MetadataValueKind Kind, bool Editable, bool Sensitive, bool AlwaysShow);

    private static readonly Field[] Fields =
    {
        new("Title", Part.Core, Dc, "title", MetadataValueKind.Text, true, false, true),
        new("Subject", Part.Core, Dc, "subject", MetadataValueKind.Text, true, false, true),
        new("Author", Part.Core, Dc, "creator", MetadataValueKind.Text, true, true, true),
        new("Keywords", Part.Core, Cp, "keywords", MetadataValueKind.Text, true, false, true),
        new("Comments", Part.Core, Dc, "description", MetadataValueKind.Text, true, true, true),
        new("LastModifiedBy", Part.Core, Cp, "lastModifiedBy", MetadataValueKind.Text, true, true, true),
        new("Revision", Part.Core, Cp, "revision", MetadataValueKind.Number, true, false, true),
        new("Created", Part.Core, DcTerms, "created", MetadataValueKind.DateTime, true, false, true),
        new("Modified", Part.Core, DcTerms, "modified", MetadataValueKind.DateTime, true, false, true),
        new("LastPrinted", Part.Core, Cp, "lastPrinted", MetadataValueKind.DateTime, true, false, false),
        new("Category", Part.Core, Cp, "category", MetadataValueKind.Text, true, false, true),
        new("ContentStatus", Part.Core, Cp, "contentStatus", MetadataValueKind.Text, true, false, false),
        new("Language", Part.Core, Dc, "language", MetadataValueKind.Text, true, false, false),
        new("Identifier", Part.Core, Dc, "identifier", MetadataValueKind.Text, false, false, false),
        new("Version", Part.Core, Cp, "version", MetadataValueKind.Text, true, false, false),
        new("Application", Part.App, Ep, "Application", MetadataValueKind.Text, true, true, true),
        new("AppVersion", Part.App, Ep, "AppVersion", MetadataValueKind.Text, true, true, false),
        new("Company", Part.App, Ep, "Company", MetadataValueKind.Text, true, true, true),
        new("Manager", Part.App, Ep, "Manager", MetadataValueKind.Text, true, true, true),
        new("Template", Part.App, Ep, "Template", MetadataValueKind.Text, true, true, false),
        new("HyperlinkBase", Part.App, Ep, "HyperlinkBase", MetadataValueKind.Text, true, true, false),
        new("TotalTime", Part.App, Ep, "TotalTime", MetadataValueKind.Number, false, false, false),
        new("Pages", Part.App, Ep, "Pages", MetadataValueKind.Number, false, false, false),
        new("Words", Part.App, Ep, "Words", MetadataValueKind.Number, false, false, false),
        new("Characters", Part.App, Ep, "Characters", MetadataValueKind.Number, false, false, false),
        new("CharactersWithSpaces", Part.App, Ep, "CharactersWithSpaces", MetadataValueKind.Number, false, false, false),
        new("Lines", Part.App, Ep, "Lines", MetadataValueKind.Number, false, false, false),
        new("Paragraphs", Part.App, Ep, "Paragraphs", MetadataValueKind.Number, false, false, false),
        new("Slides", Part.App, Ep, "Slides", MetadataValueKind.Number, false, false, false),
        new("Notes", Part.App, Ep, "Notes", MetadataValueKind.Number, false, false, false),
        new("HiddenSlides", Part.App, Ep, "HiddenSlides", MetadataValueKind.Number, false, false, false),
        new("PresentationFormat", Part.App, Ep, "PresentationFormat", MetadataValueKind.Text, false, false, false),
        new("DocSecurity", Part.App, Ep, "DocSecurity", MetadataValueKind.Number, false, false, false),
    };

    // ---------------- detection ----------------

    public static bool IsOfficePackage(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            using var z = new ZipArchive(fs, ZipArchiveMode.Read);
            return IsOffice(z, Path.GetExtension(path));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { return false; }
    }

    public static bool IsOfficePackage(ReadOnlyMemory<byte> data, string? extension)
    {
        try
        {
            using var z = new ZipArchive(new ReadOnlyMemoryStream(data), ZipArchiveMode.Read);
            return IsOffice(z, extension);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { return false; }
    }

    private static bool IsOffice(ZipArchive z, string? extension)
    {
        var ct = z.GetEntry("[Content_Types].xml");
        if (ct == null || z.GetEntry("_rels/.rels") == null) return false;
        if (extension != null && Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return true;
        if (ct.Length > 4 * 1024 * 1024) return false;
        using var sr = new StreamReader(ct.Open());
        var s = sr.ReadToEnd();
        return s.Contains("officedocument", StringComparison.OrdinalIgnoreCase) || s.Contains("ms-word", StringComparison.OrdinalIgnoreCase)
            || s.Contains("ms-excel", StringComparison.OrdinalIgnoreCase) || s.Contains("ms-powerpoint", StringComparison.OrdinalIgnoreCase)
            || s.Contains("ms-visio", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- package helpers ----------------

    private sealed class Located
    {
        public string? Core, App, Custom, Thumbnail;
        public XDocument? Rels;
    }

    private static XDocument LoadXml(ZipArchiveEntry e)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CheckCharacters = false };
        using var s = e.Open();
        using var xr = XmlReader.Create(s, settings);
        return XDocument.Load(xr, LoadOptions.PreserveWhitespace);
    }

    private static void SaveXml(ZipArchiveEntry e, XDocument doc)
    {
        using var s = e.Open();
        s.SetLength(0);
        var ws = new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false, Indent = false };
        using var xw = XmlWriter.Create(s, ws);
        if (doc.Declaration == null) doc.Declaration = new XDeclaration("1.0", "UTF-8", "yes");
        doc.Save(xw);
    }

    private static ZipArchiveEntry? Entry(ZipArchive z, string name)
    {
        var e = z.GetEntry(name);
        if (e != null) return e;
        foreach (var x in z.Entries) if (string.Equals(x.FullName, name, StringComparison.OrdinalIgnoreCase)) return x;
        return null;
    }

    private static string Resolve(string target)
    {
        target = Uri.UnescapeDataString(target.Replace('\\', '/'));
        return target.TrimStart('/');
    }

    private static Located Locate(ZipArchive z)
    {
        var loc = new Located();
        var relsEntry = Entry(z, "_rels/.rels");
        if (relsEntry == null) return loc;
        loc.Rels = LoadXml(relsEntry);
        foreach (var r in loc.Rels.Descendants(RelNs + "Relationship"))
        {
            if ((string?)r.Attribute("TargetMode") == "External") continue;
            var type = (string?)r.Attribute("Type") ?? "";
            var target = (string?)r.Attribute("Target");
            if (target == null) continue;
            var name = Resolve(target);
            if (type.EndsWith("/metadata/core-properties", StringComparison.Ordinal)) loc.Core ??= name;
            else if (type.EndsWith("/extended-properties", StringComparison.Ordinal)) loc.App ??= name;
            else if (type.EndsWith("/custom-properties", StringComparison.Ordinal)) loc.Custom ??= name;
            else if (type.EndsWith("/metadata/thumbnail", StringComparison.Ordinal)) loc.Thumbnail ??= name;
        }
        return loc;
    }

    private static string Fmt(DateTimeOffset d) => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // ---------------- read ----------------

    public static void Read(MetadataReport report, ReadOnlyMemory<byte> data)
    {
        using var z = new ZipArchive(new ReadOnlyMemoryStream(data), ZipArchiveMode.Read);
        var loc = Locate(z);
        var group = new MetadataGroup(MetadataGroupId.Office);

        XElement? core = null, app = null;
        try { if (loc.Core != null && Entry(z, loc.Core) is { } ce) core = LoadXml(ce).Root; } catch (XmlException) { report.Warnings.Add("office.coreUnreadable"); }
        try { if (loc.App != null && Entry(z, loc.App) is { } ae) app = LoadXml(ae).Root; } catch (XmlException) { report.Warnings.Add("office.appUnreadable"); }

        foreach (var f in Fields)
        {
            var root = f.Part == Part.Core ? core : app;
            var el = root?.Element(f.Ns + f.Local);
            if (el == null && !f.AlwaysShow) continue;
            string text = el?.Value.Trim() ?? "";
            string value = text;
            if (f.Kind == MetadataValueKind.DateTime && text.Length > 0)
            {
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)) value = Txt.Iso(dto.ToLocalTime());
            }
            group.Entries.Add(new MetadataEntry("office." + f.Key, "meta.office." + f.Key, Txt.Clip(value), f.Kind, f.Editable, f.Sensitive && text.Length > 0, f.Key));
        }

        if (loc.Custom != null && Entry(z, loc.Custom) is { } cue)
        {
            try
            {
                var root = LoadXml(cue).Root;
                var used = new HashSet<string>();
                foreach (var p in root?.Elements(CustomNs + "property") ?? Enumerable.Empty<XElement>())
                {
                    string name = (string?)p.Attribute("name") ?? "";
                    var v = p.Elements().FirstOrDefault();
                    if (name.Length == 0 || v == null) continue;
                    string key = "office.custom." + name; int n = 2;
                    while (!used.Add(key)) key = "office.custom." + name + "#" + n++;
                    string ln = v.Name.LocalName;
                    var kind = ln is "i1" or "i2" or "i4" or "i8" or "int" or "ui1" or "ui2" or "ui4" or "ui8" or "uint" or "r4" or "r8" or "decimal" ? MetadataValueKind.Number
                             : ln == "bool" ? MetadataValueKind.Boolean
                             : ln is "filetime" or "date" ? MetadataValueKind.DateTime : MetadataValueKind.Text;
                    group.Entries.Add(new MetadataEntry(key, "meta.office.Custom", Txt.Clip(v.Value), kind, ln == "lpwstr" || ln == "lpstr", true, name));
                }
            }
            catch (XmlException) { report.Warnings.Add("office.customUnreadable"); }
        }
        if (loc.Thumbnail != null && Entry(z, loc.Thumbnail) is { } te)
            group.Entries.Add(new MetadataEntry("office.Thumbnail", "meta.office.Thumbnail", te.Length.ToString(CultureInfo.InvariantCulture), MetadataValueKind.Number, false, true, "Thumbnail"));

        if (group.Entries.Count > 0) report.Groups.Add(group);
    }

    // ---------------- set ----------------

    public static void Set(ZipArchive z, IReadOnlyDictionary<string, string?> changes, SetResult result)
    {
        var loc = Locate(z);
        var byKey = Fields.ToDictionary(f => "office." + f.Key);

        XDocument? coreDoc = null, appDoc = null, customDoc = null;
        ZipArchiveEntry? coreEntry = null, appEntry = null, customEntry = null;
        bool coreNew = false, appNew = false;

        // load / create what we need
        bool needCore = changes.Keys.Any(k => byKey.TryGetValue(k, out var f) && f.Part == Part.Core && f.Editable);
        bool needApp = changes.Keys.Any(k => byKey.TryGetValue(k, out var f) && f.Part == Part.App && f.Editable);
        bool needCustom = changes.Keys.Any(k => k.StartsWith("office.custom.", StringComparison.Ordinal));

        if (needCore)
        {
            if (loc.Core != null && Entry(z, loc.Core) is { } e) { coreEntry = e; coreDoc = LoadXml(e); }
            else
            {
                coreDoc = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"),
                    new XElement(Cp + "coreProperties",
                        new XAttribute(XNamespace.Xmlns + "cp", Cp.NamespaceName), new XAttribute(XNamespace.Xmlns + "dc", Dc.NamespaceName),
                        new XAttribute(XNamespace.Xmlns + "dcterms", DcTerms.NamespaceName), new XAttribute(XNamespace.Xmlns + "dcmitype", DcMiType.NamespaceName),
                        new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName)));
                coreNew = true;
            }
        }
        if (needApp)
        {
            if (loc.App != null && Entry(z, loc.App) is { } e) { appEntry = e; appDoc = LoadXml(e); }
            else
            {
                appDoc = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"),
                    new XElement(Ep + "Properties", new XAttribute("xmlns", Ep.NamespaceName), new XAttribute(XNamespace.Xmlns + "vt", Vt.NamespaceName)));
                appNew = true;
            }
        }
        if (needCustom && loc.Custom != null && Entry(z, loc.Custom) is { } cu) { customEntry = cu; customDoc = LoadXml(cu); }

        foreach (var (key, value) in changes)
        {
            if (key.StartsWith("office.custom.", StringComparison.Ordinal))
            {
                if (customDoc?.Root == null) { result.Rejected[key] = "notPresent"; continue; }
                string full = key.Substring("office.custom.".Length);
                string name = full; int hash = full.LastIndexOf('#');
                int skip = 0;
                if (hash > 0 && int.TryParse(full.AsSpan(hash + 1), out var idx)) { name = full.Substring(0, hash); skip = idx - 1; }
                var prop = customDoc.Root.Elements(CustomNs + "property").Where(p => (string?)p.Attribute("name") == name).Skip(skip).FirstOrDefault();
                var v = prop?.Elements().FirstOrDefault();
                if (v == null) { result.Rejected[key] = "notPresent"; continue; }
                if (v.Name.LocalName is not ("lpwstr" or "lpstr")) { result.Rejected[key] = "notEditable"; continue; }
                v.Value = value ?? "";
                result.Applied.Add(key);
                continue;
            }
            if (!byKey.TryGetValue(key, out var f)) { result.Rejected[key] = "unknown"; continue; }
            if (!f.Editable) { result.Rejected[key] = "notEditable"; continue; }
            var root = (f.Part == Part.Core ? coreDoc : appDoc)!.Root!;
            string v2 = value?.Trim() ?? "";
            if (v2.Length == 0) { root.Elements(f.Ns + f.Local).Remove(); result.Applied.Add(key); continue; }
            if (f.Kind == MetadataValueKind.DateTime)
            {
                if (!Txt.TryParseIso(v2, out var dto)) { result.Rejected[key] = "invalidValue"; continue; }
                v2 = Fmt(dto);
            }
            else if (f.Kind == MetadataValueKind.Number && !long.TryParse(v2, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) { result.Rejected[key] = "invalidValue"; continue; }

            EnsureNamespace(root, f.Ns);
            var el = root.Element(f.Ns + f.Local);
            if (el == null) { el = new XElement(f.Ns + f.Local); root.Add(el); }
            el.Value = v2;
            if (f.Ns == DcTerms)
            {
                EnsureNamespace(root, Xsi);
                el.SetAttributeValue(Xsi + "type", root.GetPrefixOfNamespace(DcTerms) + ":W3CDTF");
            }
            result.Applied.Add(key);
        }

        if (coreDoc != null)
        {
            if (coreNew)
            {
                coreEntry = z.CreateEntry("docProps/core.xml", CompressionLevel.Optimal);
                RegisterPart(z, loc, "/docProps/core.xml", CoreContentType, CoreRelType, "docProps/core.xml");
            }
            SaveXml(coreEntry!, coreDoc);
        }
        if (appDoc != null)
        {
            if (appNew)
            {
                appEntry = z.CreateEntry("docProps/app.xml", CompressionLevel.Optimal);
                RegisterPart(z, loc, "/docProps/app.xml", AppContentType, AppRelType, "docProps/app.xml");
            }
            SaveXml(appEntry!, appDoc);
        }
        if (customDoc != null && customEntry != null) SaveXml(customEntry, customDoc);
    }

    private static void EnsureNamespace(XElement root, XNamespace ns)
    {
        if (ns == XNamespace.None) return;
        if (root.GetPrefixOfNamespace(ns) != null) return;
        string prefix = ns == DcTerms ? "dcterms" : ns == Dc ? "dc" : ns == Cp ? "cp" : ns == Xsi ? "xsi" : "p" + Math.Abs(ns.NamespaceName.GetHashCode() % 1000);
        root.SetAttributeValue(XNamespace.Xmlns + prefix, ns.NamespaceName);
    }

    /// <summary>Adds the content-type override and the package relationship for a newly created property part.</summary>
    private static void RegisterPart(ZipArchive z, Located loc, string partName, string contentType, string relType, string target)
    {
        var ctEntry = Entry(z, "[Content_Types].xml") ?? throw new MetadataException(MetadataErrorCode.Corrupt, "Missing [Content_Types].xml");
        var ct = LoadXml(ctEntry);
        if (!ct.Descendants(CtNs + "Override").Any(o => string.Equals((string?)o.Attribute("PartName"), partName, StringComparison.OrdinalIgnoreCase)))
            ct.Root!.Add(new XElement(CtNs + "Override", new XAttribute("PartName", partName), new XAttribute("ContentType", contentType)));
        SaveXml(ctEntry, ct);

        var relsEntry = Entry(z, "_rels/.rels") ?? throw new MetadataException(MetadataErrorCode.Corrupt, "Missing _rels/.rels");
        var rels = loc.Rels ?? LoadXml(relsEntry);
        string id = "rIdBn" + Guid.NewGuid().ToString("N").Substring(0, 8);
        rels.Root!.Add(new XElement(RelNs + "Relationship", new XAttribute("Id", id), new XAttribute("Type", relType), new XAttribute("Target", target)));
        SaveXml(relsEntry, rels);
        loc.Rels = rels;
    }

    // ---------------- strip ----------------

    public static bool Strip(ZipArchive z, StripOptions o, List<string> removed)
    {
        bool changed = false;
        var loc = Locate(z);

        if (o.DocumentProperties)
        {
            changed |= ClearPart(z, loc.Core, removed, "office.core");
            changed |= ClearPart(z, loc.App, removed, "office.app");
            changed |= ClearPart(z, loc.Custom, removed, "office.custom");
        }
        if (o.OtherEmbedded)
        {
            if (loc.Thumbnail != null && Entry(z, loc.Thumbnail) is { } te && loc.Rels != null)
            {
                te.Delete();
                foreach (var r in loc.Rels.Descendants(RelNs + "Relationship").Where(r => ((string?)r.Attribute("Type") ?? "").EndsWith("/metadata/thumbnail", StringComparison.Ordinal)).ToList()) r.Remove();
                SaveXml(Entry(z, "_rels/.rels")!, loc.Rels);
                removed.Add("office.thumbnail");
                changed = true;
            }
            // Excel records the absolute path of the file that was saved (leaks the user's folder names).
            var wb = Entry(z, "xl/workbook.xml");
            if (wb != null && wb.Length < 64 * 1024 * 1024)
            {
                var doc = LoadXml(wb);
                var abs = doc.Descendants().Where(e => e.Name.LocalName == "absPath").ToList();
                if (abs.Count > 0)
                {
                    foreach (var a in abs)
                    {
                        if (a.Document == null) continue;   // already removed together with its wrapper
                        var ac = a.Ancestors().FirstOrDefault(x => x.Name.LocalName == "AlternateContent");
                        if (ac != null && ac.Descendants().All(x => x.Name.LocalName is "Choice" or "Fallback" or "absPath")) ac.Remove();
                        else a.Remove();
                    }
                    SaveXml(wb, doc);
                    removed.Add("office.absPath");
                    changed = true;
                }
            }
        }
        return changed;
    }

    private static bool ClearPart(ZipArchive z, string? name, List<string> removed, string token)
    {
        if (name == null || Entry(z, name) is not { } e) return false;
        var doc = LoadXml(e);
        var root = doc.Root;
        if (root == null) return false;
        bool has = root.Elements().Any();
        if (!has) return false;
        root.RemoveNodes();
        SaveXml(e, doc);
        removed.Add(token);
        return true;
    }

    // ---------------- verification ----------------

    /// <summary>Every part that is not a property / relationship / content-type part must be byte-identical (CRC + length) after a strip.</summary>
    public static void VerifyUntouched(ReadOnlyMemory<byte> original, ReadOnlyMemory<byte> output)
    {
        using var a = new ZipArchive(new ReadOnlyMemoryStream(original), ZipArchiveMode.Read);
        using var b = new ZipArchive(new ReadOnlyMemoryStream(output), ZipArchiveMode.Read);
        var loc = Locate(a);
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml" };
        foreach (var n in new[] { loc.Core, loc.App, loc.Custom, loc.Thumbnail }) if (n != null) touched.Add(n);
        foreach (var e in a.Entries)
        {
            if (touched.Contains(e.FullName)) continue;
            var f = Entry(b, e.FullName) ?? throw new MetadataException(MetadataErrorCode.VerificationFailed, "Part lost: " + e.FullName);
            if (f.Length != e.Length || f.Crc32 != e.Crc32) throw new MetadataException(MetadataErrorCode.VerificationFailed, "Part changed: " + e.FullName);
        }
    }

    /// <summary>Opens the package, checks the main parts are well-formed XML and that the package relationships point at existing parts.</summary>
    /// <summary>
    /// The package must still open: well-formed XML in the main bookkeeping parts, relationships that point at existing parts.
    /// Problems the original already had are tolerated (a slightly damaged file can still be cleaned); new ones are not.
    /// </summary>
    public static void Verify(ReadOnlyMemory<byte> result, ReadOnlyMemory<byte>? original = null)
    {
        var problems = Problems(result);
        if (original != null) problems.ExceptWith(Problems(original.Value));
        if (problems.Count > 0) throw new MetadataException(MetadataErrorCode.VerificationFailed, "Office package check failed: " + problems.First());
    }

    private static HashSet<string> Problems(ReadOnlyMemory<byte> data)
    {
        var problems = new HashSet<string>();
        using var z = new ZipArchive(new ReadOnlyMemoryStream(data), ZipArchiveMode.Read);
        var ct = Entry(z, "[Content_Types].xml");
        if (ct == null) problems.Add("missing [Content_Types].xml");
        else { try { LoadXml(ct); } catch (XmlException) { problems.Add("malformed [Content_Types].xml"); } }
        Located loc;
        try { loc = Locate(z); }
        catch (XmlException) { problems.Add("malformed _rels/.rels"); return problems; }
        foreach (var n in new[] { loc.Core, loc.App, loc.Custom })
            if (n != null)
            {
                var e = Entry(z, n);
                if (e == null) { problems.Add("missing part " + n); continue; }
                try { LoadXml(e); } catch (XmlException) { problems.Add("malformed part " + n); }
            }
        if (loc.Rels != null)
            foreach (var r in loc.Rels.Descendants(RelNs + "Relationship"))
            {
                if ((string?)r.Attribute("TargetMode") == "External") continue;
                var t = Resolve((string?)r.Attribute("Target") ?? "");
                if (t.Length > 0 && Entry(z, t) == null) problems.Add("dangling relationship to " + t);
            }
        return problems;
    }
}
