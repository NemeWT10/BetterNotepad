namespace BetterNotepad.Pdf;

public enum PdfToWordMethod
{
    None,
    /// <summary>Microsoft Word (late-bound COM) opened the PDF and saved the DOCX.</summary>
    MsWord,
    /// <summary>Built-in converter (text, paragraphs, images, one section per page).</summary>
    BuiltIn,
}

/// <summary>Outcome code; the UI maps it to a localized message (the library has no UI strings).</summary>
public enum PdfToWordStatus
{
    Ok,
    WordNotInstalled,
    /// <summary>Word started but could not open / convert the file.</summary>
    WordFailed,
    WordTimedOut,
    Canceled,
    /// <summary>The built-in converter failed.</summary>
    Failed,
}

public sealed class PdfToWordResult
{
    public bool Success => Status == PdfToWordStatus.Ok;
    public PdfToWordStatus Status { get; init; }
    /// <summary>Which engine produced the file (None when nothing was written).</summary>
    public PdfToWordMethod Method { get; init; }
    public string OutputPath { get; init; } = "";
    /// <summary>When Word was tried first and failed, why (null when Word was not attempted or succeeded).</summary>
    public PdfToWordStatus? WordAttempt { get; init; }
    /// <summary>Technical detail in English (exception text etc.) - for logs, not for end-user UI.</summary>
    public string? Detail { get; init; }
    public Exception? Error { get; init; }
    public int Pages { get; init; }
    public int Paragraphs { get; init; }
    public int Images { get; init; }
}
