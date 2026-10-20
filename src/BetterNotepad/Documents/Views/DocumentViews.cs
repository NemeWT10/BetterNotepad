using System.Windows;
using BetterNotepad.Editor;

namespace BetterNotepad.Documents.Views;

/// <summary>A document shown in one tab group. Created lazily, cached a while, then disposed.</summary>
public interface IDocumentView
{
    Document Document { get; }

    FrameworkElement Element { get; }

    /// <summary>The text editor of this view, if it has one (for editor commands).</summary>
    CodeEditor? Editor { get; }

    void OnActivated();

    void OnDeactivated();

    /// <summary>Writes caret/scroll/zoom into the document model (session).</summary>
    void CaptureState();

    void FocusContent();

    void Dispose();
}

/// <summary>A view with its own zoom (PDF…): the zoom commands and shortcuts drive it instead of the text editor zoom.</summary>
public interface IZoomableView
{
    /// <summary>One zoom step in (+1) or out (-1).</summary>
    void ZoomStep(int direction);

    /// <summary>Back to the view's natural zoom (PDF: fit width).</summary>
    void ZoomReset();
}

/// <summary>A view that adapts to a small compact window (a tab pinned on top), e.g. the PDF hides its thumbnails.</summary>
public interface ICompactView
{
    void EnterCompact();
}

/// <summary>A view that is not a text editor with something for the status bar (an image: pixel size, file size, zoom).</summary>
public interface IStatusInfoView
{
    /// <summary>Left part ("1920 × 1080 px · 2,4 MB · PNG"), null = nothing.</summary>
    string? StatusText { get; }

    /// <summary>Zoom on the right ("100%"), null = hidden; clicking it calls <see cref="StatusZoomClicked"/>.</summary>
    string? StatusZoom { get; }

    string? StatusZoomTip { get; }

    void StatusZoomClicked();

    /// <summary>The texts changed (UI thread).</summary>
    event Action? StatusChanged;
}

/// <summary>
/// A view (or preview) whose rendered content can be dropped while its window waits hidden in the tray: rendered PDF pages,
/// a Markdown preview document. It rebuilds that content by itself when it is shown again; nothing the user set is lost.
/// </summary>
public interface IReleasableView
{
    void ReleaseRendering();
}

/// <summary>Live preview next to a text editor (Markdown, HTML, SVG, CSV table).</summary>
public interface IPreviewView
{
    FrameworkElement Element { get; }

    /// <summary>
    /// Editor → preview sync: source <paramref name="line"/> (1-based) is at the top of the editor, <paramref name="lineFraction"/>
    /// of it scrolled away; <paramref name="scrollFraction"/> is the editor's overall position (0..1) so both reach the end together.
    /// </summary>
    void ScrollToLine(int line, double lineFraction, double scrollFraction);

    /// <summary>Double-click in the preview: go to this source line in the editor.</summary>
    event Action<int>? LineRequested;

    /// <summary>Preview → editor sync: the user scrolled the preview; top source line (1-based) and progress through it.</summary>
    event Action<int, double>? Scrolled;

    void Dispose();
}

public static class DocumentViewRegistry
{
    private static readonly Dictionary<DocumentKind, Func<Document, IDocumentView>> Views = new();
    private static readonly Dictionary<DocumentKind, Func<TextDocument, IPreviewView>> Previews = new();

    public static void RegisterView(DocumentKind kind, Func<Document, IDocumentView> factory) => Views[kind] = factory;

    public static void RegisterPreview(DocumentKind kind, Func<TextDocument, IPreviewView> factory) => Previews[kind] = factory;

    public static bool HasPreview(DocumentKind kind) => Previews.ContainsKey(kind);

    public static IPreviewView? CreatePreview(TextDocument doc) => Previews.TryGetValue(doc.Kind, out var f) ? f(doc) : null;

    public static IDocumentView Create(Document doc)
    {
        if (doc is TextDocument t) return new TextDocumentView(t);
        if (Views.TryGetValue(doc.Kind, out var f)) return f(doc);
        return new UnsupportedView(doc);
    }
}
