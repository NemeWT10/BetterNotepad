using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BetterNotepad.UI.Themes;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace BetterNotepad.Editor.MultiCaret;

/// <summary>Paints the selections of the extra carets in the selection layer, rounded like the main selection.</summary>
internal sealed class ExtraSelectionRenderer(TextArea area, MultiCaretController carets) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView view, DrawingContext dc)
    {
        if (carets.Extra.Count == 0 || !view.VisualLinesValid || view.VisualLines.Count == 0 || view.Document is not { } doc) return;
        var from = view.VisualLines[0].FirstDocumentLine.Offset;
        var to = view.VisualLines[^1].LastDocumentLine.EndOffset;
        var geo = new BackgroundGeometryBuilder { CornerRadius = area.SelectionCornerRadius, AlignToWholePixels = true };
        var any = false;
        foreach (var r in carets.Extra)
        {
            if (r.IsEmpty || r.End < from || r.Start > to) continue;
            geo.AddSegment(view, new TextSegment { StartOffset = r.Start, EndOffset = Math.Min(r.End, doc.TextLength) });
            geo.CloseFigure();
            any = true;
        }
        if (!any || geo.CreateGeometry() is not { } g) return;
        var brush = new SolidColorBrush(ThemeManager.I.Current.Selection);
        brush.Freeze();
        dc.DrawGeometry(brush, null, g);
    }
}

/// <summary>
/// A layer above AvalonEdit's caret layer that paints the extra carets. It repaints on the main caret's own blink
/// timer and reads its blink phase, so all carets blink together; hidden whenever the main caret is hidden.
/// </summary>
internal sealed class ExtraCaretLayer : UIElement
{
    private static readonly FieldInfo? AdornerField = typeof(Caret).GetField("caretAdorner", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? BlinkField = AdornerField?.FieldType.GetField("blink", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? VisibleField = AdornerField?.FieldType.GetField("isVisible", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? TimerField = AdornerField?.FieldType.GetField("caretBlinkTimer", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly TextArea _area;
    private readonly MultiCaretController _carets;

    public ExtraCaretLayer(TextArea area, MultiCaretController carets)
    {
        _area = area;
        _carets = carets;
        IsHitTestVisible = false;
        if (AdornerField?.GetValue(area.Caret) is { } adorner && TimerField?.GetValue(adorner) is DispatcherTimer timer)
            timer.Tick += (_, _) =>
            {
                if (_carets.HasExtra) InvalidateVisual();
            };
        area.GotKeyboardFocus += (_, _) => InvalidateVisual();
        area.LostKeyboardFocus += (_, _) => InvalidateVisual();
        area.Caret.PositionChanged += (_, _) =>
        {
            if (_carets.HasExtra) InvalidateVisual();
        };
        area.TextView.ScrollOffsetChanged += (_, _) =>
        {
            if (_carets.HasExtra) InvalidateVisual();
        };
        area.TextView.VisualLinesChanged += (_, _) =>
        {
            if (_carets.HasExtra) InvalidateVisual();
        };
    }

    /// <summary>Snapshots only: draw every caret (also the main one) as if the editor had focus.</summary>
    public static bool ForceVisible { get; set; }

    /// <summary>Same on / off phase as AvalonEdit's caret (its private blink state), hidden without focus.</summary>
    private bool MainCaretShown()
    {
        if (ForceVisible) return true;
        var layer = AdornerField?.GetValue(_area.Caret);
        if (layer is null || BlinkField is null || VisibleField is null) return _area.IsKeyboardFocusWithin;
        return (bool)VisibleField.GetValue(layer)! && (bool)BlinkField.GetValue(layer)!;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var view = _area.TextView;
        if (!view.VisualLinesValid || view.VisualLines.Count == 0 || view.Document is not { } doc) return;
        if (_carets.Extra.Count == 0 && !ForceVisible || !MainCaretShown()) return;
        var from = view.VisualLines[0].FirstDocumentLine.Offset;
        var to = view.VisualLines[^1].LastDocumentLine.EndOffset;
        var brush = new SolidColorBrush(ThemeManager.I.Current.Caret);
        brush.Freeze();
        var width = Math.Max(1, SystemParameters.CaretWidth);
        foreach (var r in _carets.Extra)
        {
            if (r.Caret < from || r.Caret > to) continue;
            if (CaretRect(view, doc, r.Caret, width) is { } rect) dc.DrawRectangle(brush, null, rect);
        }
        if (ForceVisible && CaretRect(view, doc, _area.Caret.Offset, width) is { } main) dc.DrawRectangle(brush, null, main);
    }

    private static Rect? CaretRect(TextView view, TextDocument doc, int offset, double width)
    {
        offset = Math.Clamp(offset, 0, doc.TextLength);
        var line = doc.GetLineByOffset(offset);
        if (view.GetVisualLine(line.LineNumber) is not { } visual) return null;
        var column = visual.GetVisualColumn(offset - visual.FirstDocumentLine.Offset);
        var textLine = visual.GetTextLine(column);
        var x = visual.GetTextLineVisualXPosition(textLine, column) - view.HorizontalOffset;
        var y = visual.GetTextLineVisualYPosition(textLine, VisualYPosition.LineTop) - view.VerticalOffset;
        // pixel-snapped like AvalonEdit's own caret
        var dpi = VisualTreeHelper.GetDpi(view);
        var px = 1 / dpi.DpiScaleX;
        var py = 1 / dpi.DpiScaleY;
        x = Math.Round(x / px) * px;
        y = Math.Round(y / py) * py;
        return new Rect(x, y, Math.Max(px, Math.Round(width / px) * px), Math.Round(textLine.Height / py) * py);
    }
}
