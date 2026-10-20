using System.IO;

namespace BetterNotepad.Documents;

public enum DocumentKind
{
    /// <summary>Plain text and source code (and logs).</summary>
    Text,
    Markdown,
    Html,
    Svg,
    Csv,
    Pdf,
    Image,
    /// <summary>Binary file shown read-only as hex.</summary>
    Hex,
    /// <summary>File that cannot be displayed: metadata / hash card.</summary>
    Info,
}

/// <summary>How a text document with a preview is shown (per tab).</summary>
public enum ViewMode
{
    Edit,
    Split,
    Preview,
}

public enum LineEnding
{
    CRLF,
    LF,
    CR,
}

public static class LineEndings
{
    public static string Text(this LineEnding e) => e switch
    {
        LineEnding.LF => "\n",
        LineEnding.CR => "\r",
        _ => "\r\n",
    };

    public static string Label(this LineEnding e) => e switch
    {
        LineEnding.LF => "LF",
        LineEnding.CR => "CR",
        _ => "CRLF",
    };

    public static LineEnding Parse(string? s) => s?.ToUpperInvariant() switch
    {
        "LF" => LineEnding.LF,
        "CR" => LineEnding.CR,
        _ => LineEnding.CRLF,
    };
}

/// <summary>Maps file names to document kinds.</summary>
public static class FileTypes
{
    private static readonly HashSet<string> MarkdownExt = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdown", ".mkd", ".mkdn" };
    private static readonly HashSet<string> HtmlExt = new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm", ".xhtml" };
    private static readonly HashSet<string> SvgExt = new(StringComparer.OrdinalIgnoreCase) { ".svg" };
    private static readonly HashSet<string> CsvExt = new(StringComparer.OrdinalIgnoreCase) { ".csv", ".tsv", ".tab", ".psv" };
    private static readonly HashSet<string> PdfExt = new(StringComparer.OrdinalIgnoreCase) { ".pdf" };

    public static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".bmp", ".dib", ".ico", ".tif", ".tiff", ".webp" };

    /// <summary>Containers / office / executables: never decoded as text.</summary>
    public static readonly HashSet<string> BinaryInfoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".docm", ".dotx", ".xlsx", ".xlsm", ".xltx", ".pptx", ".pptm", ".potx", ".odt", ".ods", ".odp",
        ".doc", ".xls", ".ppt", ".zip", ".7z", ".rar", ".gz", ".tar", ".xz", ".bz2", ".exe", ".dll", ".msi", ".iso",
        ".mp3", ".mp4", ".mkv", ".avi", ".mov", ".wav", ".flac", ".ogg", ".ttf", ".otf", ".woff", ".woff2",
    };

    public static DocumentKind KindFor(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return DocumentKind.Text;
        var ext = Path.GetExtension(fileName);
        if (MarkdownExt.Contains(ext)) return DocumentKind.Markdown;
        if (HtmlExt.Contains(ext)) return DocumentKind.Html;
        if (SvgExt.Contains(ext)) return DocumentKind.Svg;
        if (CsvExt.Contains(ext)) return DocumentKind.Csv;
        if (PdfExt.Contains(ext)) return DocumentKind.Pdf;
        if (ImageExt.Contains(ext)) return DocumentKind.Image;
        if (BinaryInfoExt.Contains(ext)) return DocumentKind.Info;
        return DocumentKind.Text;
    }

    /// <summary>Kinds backed by an editable text buffer.</summary>
    public static bool IsTextual(DocumentKind kind) => kind is DocumentKind.Text or DocumentKind.Markdown or DocumentKind.Html or DocumentKind.Svg or DocumentKind.Csv;

    /// <summary>Text kinds that offer a preview (Preview | Split | Edit, CSV: Table | Text).</summary>
    public static bool HasPreview(DocumentKind kind) => kind is DocumentKind.Markdown or DocumentKind.Html or DocumentKind.Svg or DocumentKind.Csv;
}
