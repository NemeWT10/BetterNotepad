using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;

namespace BetterNotepad.Editor.Decorations;

/// <summary>Line numbers with the caret lines (all carets) in Brush.Editor.LineNumberActive. Keeps AvalonEdit's click / drag line selection.</summary>
internal sealed class ActiveLineNumberMargin : LineNumberMargin
{
    private readonly TextArea _area;
    private readonly Func<IEnumerable<int>> _extraLines;
    private int _caretLine;

    public ActiveLineNumberMargin(TextArea area, Func<IEnumerable<int>> extraLines)
    {
        _area = area;
        _extraLines = extraLines;
        area.Caret.PositionChanged += (_, _) =>
        {
            var line = _area.Caret.Line;
            if (line == _caretLine) return;
            _caretLine = line;
            InvalidateVisual();
        };
    }

    public Brush? ActiveForeground { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        var view = TextView;
        if (view is null || !view.VisualLinesValid) return;
        var normal = (Brush)GetValue(Control.ForegroundProperty);
        var active = ActiveForeground ?? normal;
        var caretLine = _area.Caret.Line;
        HashSet<int>? extra = null;
        foreach (var n in _extraLines()) (extra ??= []).Add(n);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var mode = TextOptions.GetTextFormattingMode(this);
        var width = RenderSize.Width;
        foreach (var line in view.VisualLines)
        {
            var number = line.FirstDocumentLine.LineNumber;
            var isActive = number == caretLine || extra?.Contains(number) == true;
            var text = new FormattedText(number.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, emSize, isActive ? active : normal, null, mode, dpi);
            var y = line.GetTextLineVisualYPosition(line.TextLines[0], ICSharpCode.AvalonEdit.Rendering.VisualYPosition.TextTop);
            dc.DrawText(text, new Point(width - text.Width, y - view.VerticalOffset));
        }
    }
}
