namespace BetterNotepad.Pdf;

/// <summary>Process-wide control of the PDF engine (PDFium + its worker thread).</summary>
public static class PdfRuntime
{
    /// <summary>Optional explicit path of pdfium.dll; set before the first document is opened.</summary>
    public static string? NativeLibraryPath { get => Pdfium.NativeLibraryPath; set => Pdfium.NativeLibraryPath = value; }

    /// <summary>True while the engine thread and PDFium are alive (started by the first <see cref="PdfDocumentHandle.OpenAsync"/>).</summary>
    public static bool IsEngineRunning => PdfEngine.IsRunning;

    public static int OpenDocumentCount => PdfEngine.OpenDocuments;

    /// <summary>
    /// Unloads PDFium state and ends the engine thread when no document is open (call when the last PDF tab is closed to get the memory back).
    /// Returns false (and does nothing) when documents are still open. The engine restarts transparently on the next open.
    /// </summary>
    public static bool TryShutdown() => PdfEngine.Shutdown();
}
