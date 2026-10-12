using System.Windows;
using System.Windows.Media;
using BetterNotepad.Editor.Text;
using BetterNotepad.UI.Themes;
using ICSharpCode.AvalonEdit.Rendering;

namespace BetterNotepad.Editor.Decorations;

/// <summary>1 px vertical indentation guides (VS Code style); blank lines continue the guides of the block around them.</summary>
internal sealed class IndentGuideRenderer(CodeEditor editor) : IBackgroundRenderer
{
    private const int MaxLookaround = 200;

    public KnownLayer Layer => KnownLayer.Background;

    public bool Enabled { get; set; } = true;

    public void Draw(TextView view, DrawingContext dc)
    {
        if (!Enabled || !view.VisualLinesValid || view.VisualLines.Count == 0 || view.Document is not { } doc) return;
        var size = Math.Max(1, editor.Options.IndentationSize);
        var unit = view.WideSpaceWidth * size;
        if (unit < 3) return;
        var dpi = VisualTreeHelper.GetDpi(view);
        var px = 1 / dpi.DpiScaleX;
        var geo = new StreamGeometry();
        var any = false;
        using (var ctx = geo.Open())
        {
            foreach (var visual in view.VisualLines)
            {
                var line = visual.FirstDocumentLine;
                var cols = TextHelpers.IndentColumns(doc, line, size);
                if (cols < 0)
                {
                    // blank line: the shallower of the nearest non-blank lines above and below
                    int Find(int dir)
                    {
                        var l = dir < 0 ? line.PreviousLine : line.NextLine;
                        for (var n = 0; l is not null && n < MaxLookaround; n++, l = dir < 0 ? l.PreviousLine : l.NextLine)
                        {
                            var c = TextHelpers.IndentColumns(doc, l, size);
                            if (c >= 0) return c;
                        }
                        return 0;
                    }
                    cols = Math.Min(Find(-1), Find(+1));
                }
                if (cols <= 0) continue;
                var top = visual.VisualTop - view.VerticalOffset;
                var bottom = top + visual.Height;
                for (var k = 0; k * size < cols; k++)
                {
                    var x = k * unit - view.HorizontalOffset;
                    if (x < -1) continue;
                    // centre of a device pixel so the line stays 1 px sharp
                    x = Math.Floor(x / px) * px + px / 2;
                    ctx.BeginFigure(new Point(x, top), false, false);
                    ctx.LineTo(new Point(x, bottom), true, false);
                    any = true;
                }
            }
        }
        if (!any) return;
        geo.Freeze();
        var brush = new SolidColorBrush(ThemeManager.I.Current.IndentGuide);
        brush.Freeze();
        var pen = new Pen(brush, px);
        pen.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
