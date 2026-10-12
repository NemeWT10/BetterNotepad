using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BetterNotepad.UI.Themes;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace BetterNotepad.Editor.Bookmarks;

/// <summary>Narrow gutter left of the line numbers: a small accent pill per bookmark; a click toggles the bookmark.</summary>
internal sealed class BookmarkMargin : AbstractMargin
{
    private readonly Func<BookmarkSet?> _set;
    private BookmarkSet? _subscribed;

    public BookmarkMargin(Func<BookmarkSet?> set)
    {
        _set = set;
        Cursor = Cursors.Hand;
        Loaded += (_, _) => Resubscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    public double MarginWidth { get; set; } = 14;

    /// <summary>The document changed (another model attached): follow its bookmark set.</summary>
    public void Resubscribe()
    {
        Unsubscribe();
        if (!IsLoaded) return;
        _subscribed = _set();
        if (_subscribed is not null) _subscribed.Changed += InvalidateVisual;
        InvalidateVisual();
    }

    private void Unsubscribe()
    {
        if (_subscribed is not null) _subscribed.Changed -= InvalidateVisual;
        _subscribed = null;
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

    protected override void OnRender(DrawingContext dc)
    {
        var view = TextView;
        var set = _set();
        if (view is null || !view.VisualLinesValid || set is null || set.Count == 0) return;
        var brush = new SolidColorBrush(ThemeManager.I.Current.Accent);
        brush.Freeze();
        var dpi = VisualTreeHelper.GetDpi(this);
        var px = 1 / dpi.DpiScaleX;
        var w = Math.Round(4 / px) * px;
        var x = Math.Round((RenderSize.Width - w) / 2 / px) * px;
        foreach (var visual in view.VisualLines)
        {
            if (!set.Contains(visual.FirstDocumentLine.LineNumber)) continue;
            var first = visual.TextLines[0];
            var top = visual.GetTextLineVisualYPosition(first, VisualYPosition.LineTop) - view.VerticalOffset;
            var h = first.Height;
            var rect = new Rect(x, Math.Round((top + h * 0.16) / px) * px, w, Math.Round(h * 0.68 / px) * px);
            dc.DrawRoundedRectangle(brush, null, rect, w / 2, w / 2);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var view = TextView;
        if (view is null || _set() is not { } set) return;
        var visual = view.GetVisualLineFromVisualTop(e.GetPosition(view).Y + view.VerticalOffset);
        if (visual is null) return;
        set.Toggle(visual.FirstDocumentLine.LineNumber);
        e.Handled = true;
    }
}
