using System.Windows.Documents;

namespace BetterNotepad.Markdown;

/// <summary>The five GitHub alert kinds (<c>&gt; [!NOTE]</c> ...).</summary>
public enum AlertKind { Note, Tip, Important, Warning, Caution }

/// <summary>Content the native renderer only approximates; the app may switch to a WebView2 renderer for it.</summary>
[Flags]
public enum RichContent
{
    None = 0,

    /// <summary>A <c>mermaid</c> fenced diagram.</summary>
    Mermaid = 1,

    /// <summary><c>$$</c> blocks, <c>math</c>/<c>latex</c> fences or inline <c>$x$</c> formulas.</summary>
    Math = 2,

    /// <summary>Raw HTML the native renderer cannot show faithfully (tables, scripts, iframes, video...).</summary>
    Html = 4,
}

/// <summary>One heading of the document, for a table of contents. <see cref="Line"/> is 1-based.</summary>
public sealed record MarkdownHeading(int Level, string Text, int Line, string Slug);

public enum MarkdownBlockKind { Heading, Paragraph, List, Quote, Callout, Code, Table, Rule, Html, Footnotes, Other }

/// <summary>A top-level source block with the WPF element it was rendered to. Lines are 1-based, inclusive.</summary>
public sealed class MarkdownBlockInfo
{
    internal MarkdownBlockInfo(MarkdownRenderer owner) => Owner = owner;

    internal MarkdownRenderer Owner { get; }

    public int Line => StartLine0 + 1;

    public int EndLine => EndLine0 + 1;

    public MarkdownBlockKind Kind { get; internal set; }

    /// <summary>The rendered block; null for blocks that render to nothing (HTML comments).</summary>
    public Block? Element { get; internal set; }

    internal int StartLine0;
    internal int EndLine0;          // last non-blank line of the block
    internal int RegionEnd0;        // first line of the next block (exclusive)
    internal int TextStart;
    internal int TextLength;
    internal Type? SourceType;
    internal RichContent Rich;
    internal double DefaultTopMargin;
    internal bool Alive = true;
    internal double CachedTop = double.NaN;
    internal int CachedVersion = -1;
}

/// <summary>Summary of one <see cref="MarkdownRenderer.Render"/> call.</summary>
public readonly record struct MarkdownRenderResult(bool Incremental, int Reused, int Rebuilt, bool HeadingsChanged, bool RichChanged);

/// <summary>Texts shown by the renderer itself. The app fills them from its localization (PL + EN).</summary>
public sealed class MarkdownLabels
{
    public string Copy { get; init; } = "Copy";
    public string Copied { get; init; } = "Copied";
    public string SelectAll { get; init; } = "Select all";
    public string RemoteImage { get; init; } = "Remote image (not loaded)";
    public string ImageNotFound { get; init; } = "Image not found";
    public string ImageUnsupported { get; init; } = "Image format not supported";
    public string BackToReference { get; init; } = "Back to text";
}

public sealed class MarkdownOptions
{
    public MarkdownLabels Labels { get; set; } = new();

    /// <summary>Localized title of a GitHub alert callout.</summary>
    public Func<AlertKind, string> AlertTitle { get; set; } = DefaultAlertTitle;

    /// <summary>
    /// Syntax highlighting hook: (language, code) -> inlines, or null for plain text. Called for fenced code with a language.
    /// Must return fresh inlines on every call. Use SetResourceReference with Brush.Syntax.* to follow theme changes live.
    /// </summary>
    public Func<string, string, IEnumerable<Inline>?>? Highlighter { get; set; }

    public string BodyFont { get; set; } = "Segoe UI Variable Text, Segoe UI";
    public string HeadingFont { get; set; } = "Segoe UI Variable Display, Segoe UI";
    public string CodeFont { get; set; } = "Cascadia Mono, Consolas";
    public double FontSize { get; set; } = 14.5;
    public double LineHeightFactor { get; set; } = 1.55;

    /// <summary>Readable column width in DIPs; the column is centered when the view is wider.</summary>
    public double MaxContentWidth { get; set; } = 860;

    /// <summary>Soft line breaks render as line breaks (GitHub comment style) instead of spaces (CommonMark).</summary>
    public bool HardLineBreaks { get; set; }

    /// <summary>Images wider than this are decoded at this width to save memory.</summary>
    public int MaxImagePixelWidth { get; set; } = 1800;

    private static string DefaultAlertTitle(AlertKind k) => k switch
    {
        AlertKind.Note => "Note",
        AlertKind.Tip => "Tip",
        AlertKind.Important => "Important",
        AlertKind.Warning => "Warning",
        _ => "Caution",
    };
}
