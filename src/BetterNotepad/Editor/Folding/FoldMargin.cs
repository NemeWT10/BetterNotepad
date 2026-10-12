using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BetterNotepad.UI.Themes;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;

namespace BetterNotepad.Editor.Folding;

/// <summary>
/// Folding gutter with thin chevrons (VS Code style): open regions show ⌄ while the mouse is over the gutter,
/// folded regions always show › . Click toggles. Colors are the line-number tokens of the theme.
/// </summary>
internal sealed class FoldMargin : AbstractMargin
{
    private FoldingManager? _manager;
    private int _hoverLine = -1;

    public FoldMargin() => Cursor = Cursors.Hand;

    public double MarginWidth { get; set; } = 14;

    /// <summary>Snapshots only: show the open-region chevrons as if hovered.</summary>
    public static bool ForceShowAll { get; set; }

    public FoldingManager? Manager
    {
        get => _manager;
        set
        {
            _manager = value;
            InvalidateVisual();
        }
    }

    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (oldTextView is not null) oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView is not null) newTextView.VisualLinesChanged += OnVisualLinesChanged;
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) => new(MarginWidth, 0);

    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters) => new PointHitTestResult(this, hitTestParameters.HitPoint);

    /// <summary>The fold regions that start on <paramref name="line"/>.</summary>
    public static IEnumerable<FoldingSection> StartingOn(FoldingManager manager, DocumentLine line)
    {
        var f = manager.GetNextFolding(line.Offset);
        while (f is not null && f.StartOffset <= line.EndOffset)
        {
            foreach (var s in manager.GetFoldingsAt(f.StartOffset)) yield return s;
            f = manager.GetNextFolding(f.StartOffset + 1);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var view = TextView;
        if (_manager is null || view is null || !view.VisualLinesValid) return;
        var p = ThemeManager.I.Current;
        var dim = Pen(p.LineNumber);
        var strong = Pen(p.LineNumberActive);
        var showOpen = IsMouseOver || ForceShowAll;
        var cx = RenderSize.Width / 2;
        foreach (var visual in view.VisualLines)
        {
            var line = visual.FirstDocumentLine;
            var sections = StartingOn(_manager, line).ToList();
            if (sections.Count == 0) continue;
            var folded = sections.Any(s => s.IsFolded);
            if (!folded && !showOpen) continue;
            var first = visual.TextLines[0];
            var cy = visual.GetTextLineVisualYPosition(first, VisualYPosition.LineMiddle) - view.VerticalOffset;
            var size = Math.Clamp(first.Height * 0.2, 2.5, 4.5);
            var pen = folded || line.LineNumber == _hoverLine ? strong : dim;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                if (folded)
                {
                    ctx.BeginFigure(new Point(cx - size / 2, cy - size), false, false);
                    ctx.LineTo(new Point(cx + size / 2, cy), true, true);
                    ctx.LineTo(new Point(cx - size / 2, cy + size), true, true);
                }
                else
                {
                    ctx.BeginFigure(new Point(cx - size, cy - size / 2), false, false);
                    ctx.LineTo(new Point(cx, cy + size / 2), true, true);
                    ctx.LineTo(new Point(cx + size, cy - size / 2), true, true);
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
    }

    private static Pen Pen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        var pen = new Pen(b, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    private DocumentLine? LineAt(MouseEventArgs e)
    {
        var view = TextView;
        return view?.GetVisualLineFromVisualTop(e.GetPosition(view).Y + view.VerticalOffset)?.FirstDocumentLine;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var n = LineAt(e)?.LineNumber ?? -1;
        if (n == _hoverLine) return;
        _hoverLine = n;
        InvalidateVisual();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverLine = -1;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_manager is null || LineAt(e) is not { } line) return;
        var sections = StartingOn(_manager, line).ToList();
        if (sections.Count == 0) return;
        var unfold = sections.Any(s => s.IsFolded);
        if (unfold) foreach (var s in sections) s.IsFolded = false;
        else sections.OrderByDescending(s => s.Length).First().IsFolded = true;
        e.Handled = true;
    }
}
