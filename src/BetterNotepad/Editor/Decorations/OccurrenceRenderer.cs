using System.Windows.Media;
using BetterNotepad.Core;
using BetterNotepad.Editor.Text;
using BetterNotepad.UI.Themes;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace BetterNotepad.Editor.Decorations;

/// <summary>
/// Notepad++-style highlight of every occurrence of the selected text (in code also of the word at the caret).
/// The term is picked after a short pause; matches are searched only in the visible lines, so huge files cost nothing.
/// </summary>
internal sealed class OccurrenceRenderer : IBackgroundRenderer
{
    private const int MaxTerm = 256;
    private const int MaxScan = 300_000;
    private const int MaxHits = 2_000;

    private readonly CodeEditor _editor;
    private readonly Debouncer _debounce;
    private string? _term;
    private bool _wholeWord;

    public OccurrenceRenderer(CodeEditor editor)
    {
        _editor = editor;
        _debounce = new Debouncer(TimeSpan.FromMilliseconds(140), Update);
        editor.TextArea.SelectionChanged += (_, _) => Trigger();
        editor.TextArea.Caret.PositionChanged += (_, _) => Trigger();
    }

    public KnownLayer Layer => KnownLayer.Selection;

    public bool Enabled { get; set; } = true;

    /// <summary>Also highlight the word under an empty caret (code only).</summary>
    public bool CaretWord { get; set; }

    public string? Term => _term;

    private void Trigger()
    {
        if (Enabled) _debounce.Trigger();
        else if (_term is not null) Set(null, false);
    }

    /// <summary>Picks the term now (tests, snapshots).</summary>
    public void Update()
    {
        var doc = _editor.Document;
        var area = _editor.TextArea;
        if (doc is null || !Enabled || _editor.Model?.IsLarge == true)
        {
            Set(null, false);
            return;
        }
        string? term = null;
        var whole = false;
        var sel = area.Selection;
        if (!sel.IsEmpty)
        {
            if (sel is not RectangleSelection && !sel.IsMultiline && sel.SurroundingSegment is { Length: > 0 and <= MaxTerm } seg)
            {
                var text = doc.GetText(seg);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    term = text;
                    whole = TextHelpers.IsWholeWord(doc, seg.Offset, seg.Length);
                }
            }
        }
        else if (CaretWord && TextHelpers.WordAt(doc, area.Caret.Offset) is { } w && w.End - w.Start is >= 2 and <= MaxTerm)
        {
            term = doc.GetText(w.Start, w.End - w.Start);
            whole = true;
        }
        Set(term, whole);
    }

    private void Set(string? term, bool whole)
    {
        if (term == _term && whole == _wholeWord) return;
        _term = term;
        _wholeWord = whole;
        _editor.TextArea.TextView.InvalidateLayer(Layer);
    }

    public void Draw(TextView view, DrawingContext dc)
    {
        if (_term is not { } term || !Enabled || !view.VisualLinesValid || view.VisualLines.Count == 0 || view.Document is not { } doc) return;
        var from = view.VisualLines[0].FirstDocumentLine.Offset;
        var to = Math.Min(view.VisualLines[^1].LastDocumentLine.EndOffset, from + MaxScan);
        if (to - from < term.Length) return;
        var text = doc.GetText(from, to - from);
        var geo = new BackgroundGeometryBuilder { CornerRadius = 3, AlignToWholePixels = true };
        var hits = 0;
        for (var i = text.IndexOf(term, StringComparison.Ordinal); i >= 0 && hits < MaxHits; i = text.IndexOf(term, i + term.Length, StringComparison.Ordinal))
        {
            if (_wholeWord && !TextHelpers.IsWholeWord(text, i, term.Length)) continue;
            geo.AddSegment(view, new TextSegment { StartOffset = from + i, Length = term.Length });
            geo.CloseFigure();
            hits++;
        }
        if (hits == 0 || geo.CreateGeometry() is not { } g) return;
        var brush = new SolidColorBrush(ThemeManager.I.Current.Occurrence);
        brush.Freeze();
        dc.DrawGeometry(brush, null, g);
    }
}
