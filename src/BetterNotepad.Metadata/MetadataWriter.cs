namespace BetterNotepad.Metadata;

/// <summary>
/// Editing and cleaning. Every content rewrite goes to a temp file next to the original, is verified, and then atomically
/// swapped in (attributes, times and the Zone.Identifier stream are restored unless the caller asked otherwise).
/// </summary>
public static class MetadataWriter
{
    // ======================================================================== Set

    public static Task<SetResult> SetAsync(string path, IReadOnlyDictionary<string, string?> changes, bool keepBackup = false, CancellationToken ct = default) =>
        Task.Run(() => Set(path, changes, keepBackup, ct), ct);

    /// <summary>
    /// Applies edits by entry key (see <see cref="MetadataEntry.Key"/>). A null / empty value clears a text field.
    /// Keys that cannot be applied are reported in <see cref="SetResult.Rejected"/>; a failure of the file as a whole throws.
    /// </summary>
    public static SetResult Set(string path, IReadOnlyDictionary<string, string?> changes, bool keepBackup = false, CancellationToken ct = default)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new MetadataException(MetadataErrorCode.NotFound, "File not found: " + full);
        var result = new SetResult();
        var format = FormatDetector.Detect(full);

        var fileChanges = changes.Where(kv => kv.Key.StartsWith("file.", StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value);
        string? prefix = format switch { MetadataFormat.Jpeg => "exif.", MetadataFormat.Pdf => "pdf.", MetadataFormat.Office => "office.", _ => null };
        var content = new Dictionary<string, string?>();
        foreach (var (key, value) in changes)
        {
            if (fileChanges.ContainsKey(key)) continue;
            if (prefix != null && key.StartsWith(prefix, StringComparison.Ordinal)) content[key] = value;
            else result.Rejected[key] = IsKnownPrefix(key) ? "notEditable" : "unknown";
        }

        try
        {
            if (content.Count > 0) WriteContent(full, format, content, fileChanges, keepBackup, result, ct);
            if (fileChanges.Count > 0) FileMetadata.Apply(full, fileChanges, result);
        }
        catch (Exception ex) when (IsFormatError(ex)) { throw new MetadataException(MetadataErrorCode.Corrupt, ex.Message, ex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new MetadataException(MetadataErrorCode.Io, ex.Message, ex); }
        return result;
    }

    /// <summary>Exceptions a damaged / hostile file can provoke inside the parsers; they all mean "this file's structure is not usable".</summary>
    internal static bool IsFormatError(Exception ex) =>
        ex is PdfFormatException or InvalidDataException or System.Xml.XmlException or IndexOutOfRangeException or ArgumentException or InvalidOperationException
           or InvalidCastException or NullReferenceException or OverflowException or FormatException or KeyNotFoundException or NotSupportedException;

    private static bool IsKnownPrefix(string key) =>
        key.StartsWith("exif.", StringComparison.Ordinal) || key.StartsWith("gps.", StringComparison.Ordinal) || key.StartsWith("xmp.", StringComparison.Ordinal)
        || key.StartsWith("iptc.", StringComparison.Ordinal) || key.StartsWith("text.", StringComparison.Ordinal) || key.StartsWith("pdf.", StringComparison.Ordinal)
        || key.StartsWith("office.", StringComparison.Ordinal);

    private static void WriteContent(string full, MetadataFormat format, Dictionary<string, string?> content, Dictionary<string, string?> fileChanges,
                                     bool keepBackup, SetResult result, CancellationToken ct)
    {
        bool ro = (File.GetAttributes(full) & FileAttributes.ReadOnly) != 0;
        if (ro)
        {
            if (fileChanges.TryGetValue("file.ReadOnly", out var v) && Txt.TryBool(v, out bool on) && !on)
                File.SetAttributes(full, File.GetAttributes(full) & ~FileAttributes.ReadOnly);
            else throw new MetadataException(MetadataErrorCode.ReadOnly, "The file is read-only.");
        }

        var snap = AtomicFile.Capture(full);
        string temp = AtomicFile.NewTemp(full);
        try
        {
            ct.ThrowIfCancellationRequested();
            int before = result.Applied.Count;
            bool wrote = BuildToTemp(full, temp,
                (data, output) => ContentOps.BuildSet(format, data, content, result, output),
                (orig, res) => ContentOps.VerifySet(format, orig, res));
            if (wrote && result.Applied.Count > before)
            {
                ct.ThrowIfCancellationRequested();
                AtomicFile.Commit(temp, full, snap, keepBackup, keepZone: true, resetTimes: false);
            }
            else AtomicFile.Cleanup(temp);
        }
        catch { AtomicFile.Cleanup(temp); throw; }
    }

    /// <summary>Maps the original, lets <paramref name="build"/> write the new content to the temp file, maps that and verifies. Everything is released before returning.</summary>
    private static bool BuildToTemp(string full, string temp, Func<ReadOnlyMemory<byte>, Stream, bool> build, Action<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> verify)
    {
        using var mf = MappedFile.Open(full);
        bool built;
        using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16))
        {
            built = build(mf.Memory, fs);
            fs.Flush();
        }
        if (!built) { AtomicFile.Cleanup(temp); return false; }
        using var outMf = MappedFile.Open(temp);
        verify(mf.Memory, outMf.Memory);
        return true;
    }

    // ======================================================================== Strip

    public static Task<StripResult> StripAsync(string path, StripOptions? options = null, CancellationToken ct = default) =>
        Task.Run(() => Strip(path, options, ct), ct);

    /// <summary>Removes metadata according to <paramref name="options"/> (default: everything). Nothing is written when there is nothing to remove.</summary>
    public static StripResult Strip(string path, StripOptions? options = null, CancellationToken ct = default)
    {
        var o = options ?? StripOptions.Everything;
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new MetadataException(MetadataErrorCode.NotFound, "File not found: " + full);
        var format = FormatDetector.Detect(full);
        long before = new FileInfo(full).Length;
        var removed = new List<string>();
        var snap = AtomicFile.Capture(full);
        string temp = AtomicFile.NewTemp(full);
        bool changed = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (format != MetadataFormat.Unknown)
                changed = BuildToTemp(full, temp,
                    (data, output) => ContentOps.BuildStrip(format, data, o, removed, output),
                    (orig, res) => ContentOps.VerifyStrip(format, orig, res));
            if (changed)
            {
                if ((snap.Attributes & FileAttributes.ReadOnly) != 0) throw new MetadataException(MetadataErrorCode.ReadOnly, "The file is read-only.");
                ct.ThrowIfCancellationRequested();
                AtomicFile.Commit(temp, full, snap, o.KeepBackup, keepZone: !o.ZoneIdentifier, resetTimes: o.ResetFileTimes);
            }
            else AtomicFile.Cleanup(temp);
        }
        catch (Exception ex) when (IsFormatError(ex)) { AtomicFile.Cleanup(temp); throw new MetadataException(MetadataErrorCode.Corrupt, ex.Message, ex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AtomicFile.Cleanup(temp); throw new MetadataException(MetadataErrorCode.Io, ex.Message, ex); }
        catch { AtomicFile.Cleanup(temp); throw; }

        bool zoneRemoved = false;
        if (o.ZoneIdentifier && snap.Zone != null)
        {
            zoneRemoved = changed || FileMetadata.RemoveZone(full);
            if (!changed) AtomicFile.ApplyTimes(full, snap, o.ResetFileTimes);
        }
        if (!changed && o.ResetFileTimes && !zoneRemoved) AtomicFile.ApplyTimes(full, snap, true);

        return new StripResult
        {
            ContentChanged = changed,
            ZoneRemoved = zoneRemoved,
            BytesBefore = before,
            BytesAfter = new FileInfo(full).Length,
            Removed = removed,
        };
    }
}
