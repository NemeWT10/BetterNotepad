using System.Windows.Media;
using System.Windows.Threading;
using BetterNotepad.Editor.Text;
using BetterNotepad.UI.Themes;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace BetterNotepad.Editor.Decorations;

/// <summary>Highlights the bracket next to the caret and its partner (brackets in strings and comments do not count).</summary>
internal sealed class BracketHighlighter : IBackgroundRenderer
{
    private readonly CodeEditor _editor;
    private bool _pending;
    private (int Open, int Close)? _pair;

    public BracketHighlighter(CodeEditor editor)
    {
        _editor = editor;
        editor.TextArea.Caret.PositionChanged += (_, _) => Schedule();
        editor.TextArea.DocumentChanged += (_, _) => Schedule();
    }

    public KnownLayer Layer => KnownLayer.Selection;

    public CodeSyntax Syntax { get; set; } = CodeSyntax.Plain;

    public (int Open, int Close)? Pair => _pair;

    /// <summary>Coalesces caret moves (holding an arrow key) into one search per idle moment.</summary>
    private void Schedule()
    {
        if (_pending) return;
        _pending = true;
        _editor.Dispatcher.BeginInvoke(DispatcherPriority.Background, Update);
    }

    public void Update()
    {
        _pending = false;
        var doc = _editor.Document;
        (int, int)? pair = null;
        if (doc is not null && _editor.Model?.IsLarge != true && doc.TextLength > 0)
        {
            var caret = _editor.TextArea.Caret.Offset;
            char At(int o) => o >= 0 && o < doc.TextLength ? doc.GetCharAt(o) : '\0';
            var before = At(caret - 1);
            var after = At(caret);
            // the closer just typed / passed wins, then the opener ahead, then the remaining sides
            int? at = CodeScanner.IsClose(before) ? caret - 1
                : CodeScanner.IsOpen(after) ? caret
                : CodeScanner.IsOpen(before) ? caret - 1
                : CodeScanner.IsClose(after) ? caret : null;
            if (at is { } o) pair = CodeScanner.MatchBracket(doc, o, Syntax);
        }
        if (pair == _pair) return;
        _pair = pair;
        _editor.TextArea.TextView.InvalidateLayer(Layer);
    }

    public void Draw(TextView view, DrawingContext dc)
    {
        if (_pair is not { } pair || !view.VisualLinesValid || view.Document is not { } doc || pair.Close >= doc.TextLength) return;
        var p = ThemeManager.I.Current;
        var fill = new SolidColorBrush(p.BracketMatch);
        fill.Freeze();
        var geo = new BackgroundGeometryBuilder { CornerRadius = 2.5, AlignToWholePixels = true, BorderThickness = 0 };
        geo.AddSegment(view, new TextSegment { StartOffset = pair.Open, Length = 1 });
        geo.CloseFigure();
        geo.AddSegment(view, new TextSegment { StartOffset = pair.Close, Length = 1 });
        if (geo.CreateGeometry() is { } g) dc.DrawGeometry(fill, null, g);
    }
}
