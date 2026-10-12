using System.IO.Compression;

namespace BetterNotepad.Metadata;

/// <summary>
/// Format dispatch working purely on memory / streams (no file system). The public readers and writers wrap these with
/// memory-mapped input, a temp file and an atomic swap; the test harness drives the very same code with byte arrays, so malformed
/// input can be exercised without ever touching the disk.
/// </summary>
internal static class ContentOps
{
    public static void Read(MetadataReport report, MetadataFormat format, ReadOnlyMemory<byte> data)
    {
        switch (format)
        {
            case MetadataFormat.Jpeg: JpegFormat.Read(report, data.Span); break;
            case MetadataFormat.Png: PngFormat.Read(report, data.Span); break;
            case MetadataFormat.WebP: WebpFormat.Read(report, data.Span); break;
            case MetadataFormat.Pdf: PdfFormat.Read(report, data); break;
            case MetadataFormat.Office: OfficeFormat.Read(report, data); break;
        }
    }

    // ---------------------------------------------------------------- strip

    /// <summary>Writes the cleaned file to <paramref name="output"/> (must be readable, writable and seekable). False = nothing to remove.</summary>
    public static bool BuildStrip(MetadataFormat format, ReadOnlyMemory<byte> data, StripOptions o, List<string> removed, Stream output)
    {
        switch (format)
        {
            case MetadataFormat.Jpeg:
            case MetadataFormat.Png:
            case MetadataFormat.WebP:
            {
                var span = data.Span;
                var plan = format == MetadataFormat.Jpeg ? JpegFormat.PlanStrip(span, o, removed)
                         : format == MetadataFormat.Png ? PngFormat.PlanStrip(span, o, removed)
                         : WebpFormat.PlanStrip(span, o, removed);
                if (plan == null) return false;
                Chunk.Write(output, span, plan);
                return true;
            }
            case MetadataFormat.Office:
            {
                output.Write(data.Span);
                output.Position = 0;
                bool changed;
                using (var z = new ZipArchive(output, ZipArchiveMode.Update, leaveOpen: true)) changed = OfficeFormat.Strip(z, o, removed);
                return changed;
            }
            case MetadataFormat.Pdf:
            {
                var doc = PdfDocument.Open(data);
                return PdfStripper.Strip(doc, o, output, removed);
            }
            default:
                return false;
        }
    }

    /// <summary>Throws <see cref="MetadataErrorCode.VerificationFailed"/> unless the result is a faithful, still valid copy of the original minus metadata.</summary>
    public static void VerifyStrip(MetadataFormat format, ReadOnlyMemory<byte> original, ReadOnlyMemory<byte> result)
    {
        switch (format)
        {
            case MetadataFormat.Jpeg:
                if (JpegFormat.CoreHash(original.Span) != JpegFormat.CoreHash(result.Span)) throw new MetadataException(MetadataErrorCode.VerificationFailed, "Image data would change; nothing was written.");
                break;
            case MetadataFormat.Png:
                if (PngFormat.CoreHash(original.Span) != PngFormat.CoreHash(result.Span)) throw new MetadataException(MetadataErrorCode.VerificationFailed, "Image data would change; nothing was written.");
                // Chunks we keep are copied verbatim, so only damage that was already there may remain; the rewritten eXIf stub must be valid.
                if (PngFormat.CrcFailures(result.Span, false) > PngFormat.CrcFailures(original.Span, true)) throw new MetadataException(MetadataErrorCode.VerificationFailed, "PNG chunk CRC mismatch.");
                break;
            case MetadataFormat.WebP:
                if (WebpFormat.CoreHash(original.Span) != WebpFormat.CoreHash(result.Span)) throw new MetadataException(MetadataErrorCode.VerificationFailed, "Image data would change; nothing was written.");
                break;
            case MetadataFormat.Office:
                OfficeFormat.Verify(result, original);
                OfficeFormat.VerifyUntouched(original, result);
                break;
            case MetadataFormat.Pdf:
                PdfVerifier.Verify(result, PdfDocument.Open(original).PageCount());
                break;
        }
    }

    // ---------------------------------------------------------------- set

    /// <summary>False when nothing was written (no applicable change).</summary>
    public static bool BuildSet(MetadataFormat format, ReadOnlyMemory<byte> data, IReadOnlyDictionary<string, string?> content, SetResult result, Stream output)
    {
        switch (format)
        {
            case MetadataFormat.Jpeg:
            {
                var plan = JpegFormat.PlanSetExif(data.Span, content, result);
                if (plan == null) return false;
                Chunk.Write(output, data.Span, plan);
                return true;
            }
            case MetadataFormat.Office:
            {
                output.Write(data.Span);
                output.Position = 0;
                using (var z = new ZipArchive(output, ZipArchiveMode.Update, leaveOpen: true)) OfficeFormat.Set(z, content, result);
                return true;
            }
            case MetadataFormat.Pdf:
            {
                var doc = PdfDocument.Open(data);
                return PdfEditor.Apply(doc, data.Span, content, output, result);
            }
            default:
                return false;
        }
    }

    public static void VerifySet(MetadataFormat format, ReadOnlyMemory<byte> original, ReadOnlyMemory<byte> result)
    {
        switch (format)
        {
            case MetadataFormat.Jpeg:
                if (JpegFormat.CoreHash(original.Span) != JpegFormat.CoreHash(result.Span)) throw new MetadataException(MetadataErrorCode.VerificationFailed, "Image data changed.");
                break;
            case MetadataFormat.Office:
                OfficeFormat.Verify(result, original);
                break;
            case MetadataFormat.Pdf:
                PdfVerifier.Verify(result, -1, tolerate: original);   // append-only edit: pre-existing damage is not our doing
                break;
        }
    }

    // ---------------------------------------------------------------- in-memory pipelines (tests)

    /// <summary>Full strip pipeline on a byte array. Null = nothing to remove. Throws <see cref="MetadataException"/> only.</summary>
    internal static byte[]? StripBytes(byte[] data, StripOptions o, List<string> removed)
    {
        try
        {
            var format = FormatDetector.Detect(data);
            using var ms = new MemoryStream();
            if (!BuildStrip(format, data, o, removed, ms)) return null;
            var result = ms.ToArray();
            VerifyStrip(format, data, result);
            return result;
        }
        catch (Exception ex) when (MetadataWriter.IsFormatError(ex)) { throw new MetadataException(MetadataErrorCode.Corrupt, ex.Message, ex); }
    }

    internal static byte[]? SetBytes(byte[] data, IReadOnlyDictionary<string, string?> changes, SetResult result)
    {
        try
        {
            var format = FormatDetector.Detect(data);
            using var ms = new MemoryStream();
            int before = result.Applied.Count;
            if (!BuildSet(format, data, changes, result, ms) || result.Applied.Count == before) return null;
            var output = ms.ToArray();
            VerifySet(format, data, output);
            return output;
        }
        catch (Exception ex) when (MetadataWriter.IsFormatError(ex)) { throw new MetadataException(MetadataErrorCode.Corrupt, ex.Message, ex); }
    }

    internal static MetadataReport ReadBytes(byte[] data)
    {
        var format = FormatDetector.Detect(data);
        var report = new MetadataReport { Path = "", Format = format, CanStripContent = format != MetadataFormat.Unknown };
        try { Read(report, format, data); }
        catch (Exception ex) when (ex is not OperationCanceledException) { report.Warnings.Add("read.partial"); }
        report.Groups.Sort((a, b) => ((int)a.Id).CompareTo((int)b.Id));
        return report;
    }
}
