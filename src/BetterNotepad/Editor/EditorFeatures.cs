using System.Windows;
using System.Windows.Media;
using BetterNotepad.Core.Settings;
using BetterNotepad.Editor.Bookmarks;
using BetterNotepad.Editor.Completion;
using BetterNotepad.Editor.Decorations;
using BetterNotepad.Editor.Folding;
using BetterNotepad.Editor.Highlighting;
using BetterNotepad.Editor.MultiCaret;
using BetterNotepad.Editor.Text;
using BetterNotepad.UI.Themes;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;

namespace BetterNotepad.Editor;

/// <summary>
/// The "simple IDE" layer of one <see cref="CodeEditor"/>: multiple carets, occurrences, bracket match,
/// indent guides, folding, bookmarks and the gutter (bookmarks | line numbers | fold chevrons).
/// CodeEditor forwards theme / settings / language changes here.
/// </summary>
public sealed class EditorFeatures
{
    private readonly CodeEditor _editor;
    private readonly ActiveLineNumberMargin _numbers;
    private readonly BookmarkMargin _bookmarkMargin;
    private readonly FoldMargin _foldMargin;
    private readonly OccurrenceRenderer _occurrences;
    private readonly BracketHighlighter _brackets;
    private readonly IndentGuideRenderer _guides;
    private readonly FoldingController _folding;
    private bool _foldingEnabled = true;

    public EditorFeatures(CodeEditor editor)
    {
        _editor = editor;
        var area = editor.TextArea;
        var view = area.TextView;

        Carets = new MultiCaretController(editor);

        _guides = new IndentGuideRenderer(editor);
        _occurrences = new OccurrenceRenderer(editor);
        _brackets = new BracketHighlighter(editor);
        view.BackgroundRenderers.Add(_guides);
        view.BackgroundRenderers.Add(_occurrences);
        view.BackgroundRenderers.Add(_brackets);
        view.BackgroundRenderers.Add(new ExtraSelectionRenderer(area, Carets));
        CaretLayer = new ExtraCaretLayer(area, Carets);
        view.InsertLayer(CaretLayer, KnownLayer.Caret, LayerInsertionPosition.Above);

        _bookmarkMargin = new BookmarkMargin(() => Bookmarks);
        _numbers = new ActiveLineNumberMargin(area, Carets.ExtraLines) { Margin = new Thickness(2, 0, 4, 0) };
        _foldMargin = new FoldMargin { Visibility = Visibility.Collapsed };
        area.LeftMargins.Add(_bookmarkMargin);
        area.LeftMargins.Add(_numbers);
        area.LeftMargins.Add(_foldMargin);
        _folding = new FoldingController(editor, _foldMargin);
        Completion = new CompletionController(editor, Carets);

        Carets.Changed += () =>
        {
            view.InvalidateLayer(KnownLayer.Selection);
            CaretLayer.InvalidateVisual();
            _numbers.InvalidateVisual();
        };
    }

    public MultiCaretController Carets { get; }

    /// <summary>The bookmarks of the shown document (null before a document is attached / loaded).</summary>
    public BookmarkSet? Bookmarks => _editor.Model is { HasBuffer: true } m ? BookmarkSet.For(m) : null;

    internal FoldingController Folding => _folding;

    internal ExtraCaretLayer CaretLayer { get; }

    internal CompletionController Completion { get; }

    internal OccurrenceRenderer Occurrences => _occurrences;

    internal BracketHighlighter Brackets => _brackets;

    public void Attach() => _bookmarkMargin.Resubscribe();

    public void ApplyTheme()
    {
        var p = ThemeManager.I.Current;
        _numbers.SetValue(System.Windows.Controls.Control.ForegroundProperty, Brush(p.LineNumber));
        _numbers.ActiveForeground = Brush(p.LineNumberActive);
        // the "{…}" box of a folded region (static in AvalonEdit; the theme is global anyway)
        FoldingElementGenerator.TextBrush = Brush(p.TextFaint);
        _numbers.InvalidateVisual();
        _bookmarkMargin.InvalidateVisual();
        _foldMargin.InvalidateVisual();
    }

    public void ApplySettings(EditorSettings s)
    {
        _guides.Enabled = s.IndentGuides;
        _occurrences.Enabled = s.HighlightOccurrences;
        _foldingEnabled = s.Folding;
        Completion.Enabled = s.Completion;
        Completion.AutoChars = s.CompletionChars;
        ApplyLanguage();
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    public void ApplyLanguage()
    {
        var s = SettingsService.I.Get<EditorSettings>();
        var languageId = _editor.Model?.LanguageId ?? Languages.PlainText;
        var lang = Languages.Get(languageId);
        var syntax = CodeSyntax.For(languageId);
        // line numbers are a per-type setting (code / text), so they follow language changes too
        var lineNumbers = lang.IsCode ? s.LineNumbersCode : s.LineNumbersText;
        _numbers.Visibility = lineNumbers ? Visibility.Visible : Visibility.Collapsed;
        _bookmarkMargin.MarginWidth = lineNumbers ? 16 : 12;
        _bookmarkMargin.InvalidateMeasure();
        _brackets.Syntax = syntax;
        _occurrences.CaretWord = lang.IsCode;
        var o = _editor.Options;
        Carets.Typing = new TypingContext(s.AutoCloseBrackets, lang.IsCode, lang.Folding, o.IndentationString, o.IndentationSize, o.ConvertTabsToSpaces, syntax);
        _folding.Configure(_foldingEnabled, lang.Folding, syntax);
        Completion.LanguageChanged();
        _bookmarkMargin.Resubscribe();
    }

    // ───────────── commands ─────────────

    public void Fold() => _folding.Fold();

    public void Unfold() => _folding.Unfold();

    public void FoldAll(bool folded) => _folding.SetAll(folded);

    public void ToggleBookmark()
    {
        if (Bookmarks is { } set) set.Toggle(_editor.TextArea.Caret.Line);
    }

    /// <summary>Moves to the next / previous bookmark; false when there are none.</summary>
    public bool GoToBookmark(bool backward)
    {
        if (Bookmarks?.Next(_editor.TextArea.Caret.Line, backward) is not { } line) return false;
        Carets.Clear();
        var doc = _editor.Document;
        _editor.TextArea.ClearSelection();
        _editor.TextArea.Caret.Offset = doc.GetLineByNumber(line).Offset;
        _editor.TextArea.Caret.BringCaretToView();
        _editor.ScrollToLine(line);
        return true;
    }

    private static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
