using System.Windows;
using System.Windows.Media;
using BetterNotepad.UI.Themes;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace BetterNotepad.Editor.Find;

/// <summary>Paints all search hits (and the current one stronger) behind the text.</summary>
public sealed class SearchRenderer : IBackgroundRenderer
{
    private readonly TextSegmentCollection<TextSegment> _hits = new();
    private TextSegment? _current;

    public KnownLayer Layer => KnownLayer.Selection;

    public int Count => _hits.Count;

    public void Set(IEnumerable<(int Offset, int Length)> hits, (int Offset, int Length)? current)
    {
        _hits.Clear();
        foreach (var (o, l) in hits) _hits.Add(new TextSegment { StartOffset = o, Length = Math.Max(l, 0) });
        _current = current is { } c ? new TextSegment { StartOffset = c.Offset, Length = c.Length } : null;
    }

    public void Clear()
    {
        _hits.Clear();
        _current = null;
    }

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (_hits.Count == 0 || !textView.VisualLinesValid) return;
        var p = ThemeManager.I.Current;
        var normal = new SolidColorBrush(p.FindMatch);
        var active = new SolidColorBrush(p.FindMatchActive);
        var activePen = new Pen(new SolidColorBrush(p.Warning), 1);
        normal.Freeze();
        active.Freeze();
        activePen.Freeze();

        var lines = textView.VisualLines;
        if (lines.Count == 0) return;
        var from = lines[0].FirstDocumentLine.Offset;
        var to = lines[^1].LastDocumentLine.EndOffset;

        foreach (var hit in _hits.FindOverlappingSegments(from, to - from))
        {
            var isCurrent = _current is not null && hit.StartOffset == _current.StartOffset && hit.Length == _current.Length;
            var geo = new BackgroundGeometryBuilder { CornerRadius = 3, AlignToWholePixels = true, BorderThickness = isCurrent ? 1 : 0 };
            geo.AddSegment(textView, hit.Length == 0 ? new TextSegment { StartOffset = hit.StartOffset, Length = 1 } : hit);
            var g = geo.CreateGeometry();
            if (g is null) continue;
            dc.DrawGeometry(isCurrent ? active : normal, isCurrent ? activePen : null, g);
        }
    }
}
