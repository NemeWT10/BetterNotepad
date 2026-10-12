namespace BetterNotepad.Metadata;

public static class MetadataReader
{
    public static Task<MetadataReport> ReadAsync(string path, CancellationToken ct = default) =>
        Task.Run(() => Read(path, ct), ct);

    /// <summary>
    /// Reads everything the file carries. Never throws for damaged content: the File group is always returned and problems
    /// are reported through <see cref="MetadataReport.Warnings"/>. Throws <see cref="MetadataException"/> only when the file
    /// cannot be opened at all.
    /// </summary>
    public static MetadataReport Read(string path, CancellationToken ct = default)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new MetadataException(MetadataErrorCode.NotFound, "File not found: " + full);
        ct.ThrowIfCancellationRequested();

        var format = FormatDetector.Detect(full);
        var report = new MetadataReport { Path = full, Format = format, CanStripContent = format != MetadataFormat.Unknown };
        try { FileMetadata.Read(report, full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new MetadataException(MetadataErrorCode.Io, ex.Message, ex); }

        try
        {
            ct.ThrowIfCancellationRequested();
            if (format != MetadataFormat.Unknown)
            {
                using var mf = MappedFile.Open(full);
                ContentOps.Read(report, format, mf.Memory);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not MetadataException me || me.Code != MetadataErrorCode.NotFound)
        {
            report.Warnings.Add("read.partial");
        }
        report.Groups.Sort((a, b) => ((int)a.Id).CompareTo((int)b.Id));
        return report;
    }
}
