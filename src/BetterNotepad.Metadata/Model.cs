namespace BetterNotepad.Metadata;

/// <summary>Container format recognised by content sniffing (the extension is only a hint for Office packages).</summary>
public enum MetadataFormat { Unknown, Jpeg, Png, WebP, Pdf, Office }

/// <summary>Logical groups shown by the metadata window. Group label key: <c>meta.group.{Id}</c>.</summary>
public enum MetadataGroupId { File, Exif, Gps, Xmp, Iptc, ImageText, Pdf, Office }

public enum MetadataValueKind
{
    /// <summary>Free text.</summary>
    Text,
    /// <summary>Number (invariant culture).</summary>
    Number,
    /// <summary>ISO 8601 (<c>yyyy-MM-ddTHH:mm:ss[zzz]</c>); local time for file times. The UI formats it.</summary>
    DateTime,
    /// <summary>"true" / "false".</summary>
    Boolean,
}

/// <summary>
/// One metadata field. <see cref="Key"/> is the stable id used in <c>SetAsync</c> (e.g. <c>exif.Model</c>);
/// <see cref="LabelKey"/> is the localisation key (<c>meta.exif.Model</c>); <see cref="RawName"/> is a readable
/// English/raw fallback for keys the app has no translation for (unknown tags, custom properties, PNG keywords).
/// </summary>
public sealed record MetadataEntry(
    string Key,
    string LabelKey,
    string Value,
    MetadataValueKind Kind = MetadataValueKind.Text,
    bool Editable = false,
    bool Sensitive = false,
    string? RawName = null);

public sealed class MetadataGroup
{
    public MetadataGroup(MetadataGroupId id) { Id = id; }
    public MetadataGroupId Id { get; }
    public string LabelKey => "meta.group." + Id;
    public List<MetadataEntry> Entries { get; } = new();
}

public sealed class MetadataReport
{
    public string Path { get; init; } = "";
    public MetadataFormat Format { get; init; }
    public List<MetadataGroup> Groups { get; } = new();

    /// <summary>Stable warning codes, e.g. <c>pdf.encrypted</c>, <c>pdf.xrefRebuilt</c>, <c>pdf.xmpUnreadable</c>, <c>read.partial</c>.</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>True when <c>StripAsync</c> can clear content metadata (file-level clearing is always possible).</summary>
    public bool CanStripContent { get; init; }

    public MetadataEntry? Find(string key) => Groups.SelectMany(g => g.Entries).FirstOrDefault(e => e.Key == key);
    public bool HasSensitive => Groups.Any(g => g.Entries.Any(e => e.Sensitive));
    public int EntryCount => Groups.Sum(g => g.Entries.Count);

    internal MetadataGroup Group(MetadataGroupId id)
    {
        var g = Groups.FirstOrDefault(x => x.Id == id);
        if (g == null) { g = new MetadataGroup(id); Groups.Add(g); }
        return g;
    }
}

public enum MetadataErrorCode
{
    NotFound, Unsupported, Corrupt, Encrypted, ReadOnly, TooLarge, VerificationFailed, Io,
}

public sealed class MetadataException : Exception
{
    public MetadataErrorCode Code { get; }
    public MetadataException(MetadataErrorCode code, string message, Exception? inner = null) : base(message, inner) { Code = code; }
}

/// <summary>What to remove. Defaults = remove everything the format carries.</summary>
public sealed class StripOptions
{
    /// <summary>All EXIF (camera, dates, GPS, thumbnail). JPEG APP1 Exif, PNG eXIf, WebP EXIF.</summary>
    public bool Exif { get; init; } = true;
    /// <summary>Only the GPS block inside EXIF (used when <see cref="Exif"/> is false; JPEG only).</summary>
    public bool GpsOnly { get; init; }
    /// <summary>XMP packets (images, PDF Metadata streams).</summary>
    public bool Xmp { get; init; } = true;
    /// <summary>IPTC / Photoshop resources (JPEG APP13).</summary>
    public bool Iptc { get; init; } = true;
    /// <summary>JPEG COM, PNG tEXt/zTXt/iTXt/tIME.</summary>
    public bool TextAndComments { get; init; } = true;
    /// <summary>Other embedded blobs: vendor APPn, C2PA/JUMBF, FlashPix, Office thumbnail, PDF /PieceInfo.</summary>
    public bool OtherEmbedded { get; init; } = true;
    /// <summary>PDF Info dictionary, Office core / extended / custom properties.</summary>
    public bool DocumentProperties { get; init; } = true;
    /// <summary>Remove the "downloaded from the internet" Zone.Identifier stream.</summary>
    public bool ZoneIdentifier { get; init; } = true;
    /// <summary>Set created / modified / accessed to "now" (otherwise the original times are kept).</summary>
    public bool ResetFileTimes { get; init; }
    /// <summary>Keep a minimal EXIF carrying only the Orientation tag, so the picture is not shown rotated after cleaning.</summary>
    public bool KeepOrientation { get; init; } = true;
    /// <summary>Keep the original as <c>name.ext.bak</c> next to the file.</summary>
    public bool KeepBackup { get; init; }

    public static StripOptions Everything => new();
}

public sealed class StripResult
{
    /// <summary>False when the file had nothing to remove (it was not rewritten).</summary>
    public bool ContentChanged { get; init; }
    public bool ZoneRemoved { get; init; }
    public long BytesBefore { get; init; }
    public long BytesAfter { get; init; }
    /// <summary>Stable tokens of what was removed, e.g. <c>jpeg.exif</c>, <c>png.tEXt</c>, <c>pdf.info</c>, <c>office.core</c>.</summary>
    public IReadOnlyList<string> Removed { get; init; } = Array.Empty<string>();
}

public sealed class SetResult
{
    public List<string> Applied { get; } = new();
    /// <summary>Non-fatal notes, e.g. <c>xmp.notSynced</c>.</summary>
    public List<string> Warnings { get; } = new();
    /// <summary>Key to reason (<c>unknown</c>, <c>notEditable</c>, <c>invalidValue</c>, <c>notPresent</c>, <c>tooLong</c>).</summary>
    public Dictionary<string, string> Rejected { get; } = new();
}

public enum HashAlgorithmKind { Sha256, Md5, Sha1 }
