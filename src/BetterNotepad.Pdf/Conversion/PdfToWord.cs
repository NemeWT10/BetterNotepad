namespace BetterNotepad.Pdf;

/// <summary>PDF -> DOCX entry point: Microsoft Word when installed (best fidelity), otherwise the built-in converter.</summary>
public static class PdfToWord
{
    /// <summary>
    /// Converts an open document. With <paramref name="preferWord"/> Word is tried first; when it is missing or fails the built-in writer runs
    /// and <see cref="PdfToWordResult.WordAttempt"/> says why Word was not used. The output file is replaced only on success.
    /// </summary>
    public static async Task<PdfToWordResult> ConvertAsync(PdfDocumentHandle pdf, string docxPath, bool preferWord = true,
        DocxWriterOptions? options = null, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        PdfToWordResult? word = null;
        if (preferWord && WordConverter.IsWordInstalled)
        {
            word = await WordConverter.TryWithMsWordAsync(pdf.FilePath, docxPath, null, ct).ConfigureAwait(false);
            if (word.Success || word.Status == PdfToWordStatus.Canceled) return word;
        }
        var builtIn = await FallbackDocxWriter.WriteAsync(pdf, docxPath, options, progress, ct).ConfigureAwait(false);
        PdfToWordStatus? attempt = word?.Status ?? (preferWord ? PdfToWordStatus.WordNotInstalled : null);
        return new PdfToWordResult
        {
            Status = builtIn.Status, Method = builtIn.Method, OutputPath = builtIn.OutputPath, Detail = builtIn.Detail ?? word?.Detail, Error = builtIn.Error,
            Pages = builtIn.Pages, Paragraphs = builtIn.Paragraphs, Images = builtIn.Images, WordAttempt = attempt,
        };
    }
}
