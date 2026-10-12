using System.Globalization;
using System.Text;

namespace BetterNotepad.Metadata;

/// <summary>Metadata every file has: dates, attributes and the "downloaded from the internet" mark (Zone.Identifier stream).</summary>
internal static class FileMetadata
{
    public static readonly string[] Keys =
        { "file.Created", "file.Modified", "file.Accessed", "file.ReadOnly", "file.Hidden", "file.Archive", "file.System", "file.Zone" };

    public static void Read(MetadataReport report, string path)
    {
        var g = report.Group(MetadataGroupId.File);
        void Add(string name, string value, MetadataValueKind kind, bool editable, bool sensitive = false) =>
            g.Entries.Add(new MetadataEntry("file." + name, "meta.file." + name, value, kind, editable, sensitive, name));

        Add("Created", Txt.Iso(File.GetCreationTime(path)), MetadataValueKind.DateTime, true);
        Add("Modified", Txt.Iso(File.GetLastWriteTime(path)), MetadataValueKind.DateTime, true);
        Add("Accessed", Txt.Iso(File.GetLastAccessTime(path)), MetadataValueKind.DateTime, true);

        var a = File.GetAttributes(path);
        Add("ReadOnly", B(a, FileAttributes.ReadOnly), MetadataValueKind.Boolean, true);
        Add("Hidden", B(a, FileAttributes.Hidden), MetadataValueKind.Boolean, true);
        Add("Archive", B(a, FileAttributes.Archive), MetadataValueKind.Boolean, true);
        Add("System", B(a, FileAttributes.System), MetadataValueKind.Boolean, true);

        var zone = AtomicFile.ReadZone(path);
        Add("Zone", zone != null ? "true" : "false", MetadataValueKind.Boolean, zone != null);
        if (zone != null)
        {
            var text = Encoding.UTF8.GetString(zone);
            foreach (var line in text.Split('\n'))
            {
                var l = line.Trim();
                int eq = l.IndexOf('=');
                if (eq <= 0) continue;
                string k = l.Substring(0, eq).Trim(), v = l.Substring(eq + 1).Trim();
                switch (k)
                {
                    case "ZoneId": Add("ZoneId", v, MetadataValueKind.Number, false); break;
                    case "ReferrerUrl": Add("ReferrerUrl", Txt.Clip(v), MetadataValueKind.Text, false, true); break;
                    case "HostUrl": Add("HostUrl", Txt.Clip(v), MetadataValueKind.Text, false, true); break;
                    case "HostIpAddress": Add("HostIpAddress", v, MetadataValueKind.Text, false, true); break;
                    case "LastWriterPackageFamilyName": Add("LastWriterPackageFamilyName", v, MetadataValueKind.Text, false, true); break;
                }
            }
        }
    }

    private static string B(FileAttributes a, FileAttributes f) => (a & f) != 0 ? "true" : "false";

    public static bool RemoveZone(string path)
    {
        var attrs = File.GetAttributes(path);
        bool ro = (attrs & FileAttributes.ReadOnly) != 0;
        try
        {
            if (ro) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            return AtomicFile.RemoveZone(path);
        }
        finally { if (ro) try { File.SetAttributes(path, attrs); } catch { } }
    }

    /// <summary>Applies the "file.*" changes. Order matters: times first, attributes last (read-only would block the rest).</summary>
    public static void Apply(string path, IReadOnlyDictionary<string, string?> changes, SetResult result)
    {
        foreach (var key in changes.Keys)
            if (!Keys.Contains(key)) { result.Rejected[key] = key.StartsWith("file.", StringComparison.Ordinal) ? "notEditable" : "unknown"; }

        // times
        foreach (var (key, value) in changes)
        {
            if (key is not ("file.Created" or "file.Modified" or "file.Accessed")) continue;
            if (!Txt.TryParseIso(value ?? "", out var dto)) { result.Rejected[key] = "invalidValue"; continue; }
            var local = dto.LocalDateTime;
            try
            {
                if (key == "file.Created") File.SetCreationTime(path, local);
                else if (key == "file.Modified") File.SetLastWriteTime(path, local);
                else File.SetLastAccessTime(path, local);
                result.Applied.Add(key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new MetadataException(MetadataErrorCode.Io, ex.Message, ex); }
        }

        // zone
        if (changes.TryGetValue("file.Zone", out var zv))
        {
            if (!Txt.TryBool(zv, out bool wantZone) || wantZone) result.Rejected["file.Zone"] = wantZone ? "notEditable" : "invalidValue";
            else { RemoveZone(path); result.Applied.Add("file.Zone"); }
        }

        // attributes
        var attrs = File.GetAttributes(path);
        var next = attrs;
        var map = new (string Key, FileAttributes Flag)[]
        { ("file.ReadOnly", FileAttributes.ReadOnly), ("file.Hidden", FileAttributes.Hidden), ("file.Archive", FileAttributes.Archive), ("file.System", FileAttributes.System) };
        var attrKeys = new List<string>();
        foreach (var (key, flag) in map)
        {
            if (!changes.TryGetValue(key, out var v)) continue;
            if (!Txt.TryBool(v, out bool on)) { result.Rejected[key] = "invalidValue"; continue; }
            next = on ? next | flag : next & ~flag;
            attrKeys.Add(key);
        }
        if (attrKeys.Count > 0)
        {
            try
            {
                if (next == 0) next = FileAttributes.Normal;
                File.SetAttributes(path, next);
                result.Applied.AddRange(attrKeys);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new MetadataException(MetadataErrorCode.Io, ex.Message, ex); }
        }
    }
}
