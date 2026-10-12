using BetterNotepad.Core;
using BetterNotepad.Editor.Highlighting;
using BetterNotepad.Editor.Text;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;

namespace BetterNotepad.Editor.Folding;

/// <summary>
/// Code folding of one editor: its own FoldingManager (folded state is per view), regions recomputed on a
/// background thread from a snapshot after a quiet period, applied only if the text has not changed since.
/// </summary>
internal sealed class FoldingController
{
    private const int MaxLength = 8_000_000;

    private readonly CodeEditor _editor;
    private readonly FoldMargin _margin;
    private readonly Debouncer _debounce;
    private FoldingManager? _manager;
    private FoldingElementGenerator? _generator;
    private TextDocument? _doc;
    private FoldingKind _kind;
    private CodeSyntax _syntax = CodeSyntax.Plain;
    private int _run;

    public FoldingController(CodeEditor editor, FoldMargin margin)
    {
        _editor = editor;
        _margin = margin;
        _debounce = new Debouncer(TimeSpan.FromMilliseconds(450), Schedule);
        editor.TextArea.DocumentChanged += (_, _) => Uninstall();
    }

    public FoldingManager? Manager => _manager;

    /// <summary>Turns folding on / off for the current document, language and settings.</summary>
    public void Configure(bool enabled, FoldingKind kind, CodeSyntax syntax)
    {
        var doc = _editor.Document;
        if (!enabled || kind == FoldingKind.None || doc is null || _editor.Model?.IsLarge == true || doc.TextLength > MaxLength)
        {
            Uninstall();
            return;
        }
        _syntax = syntax;
        if (_manager is not null && _doc == doc && _kind == kind) return;
        Uninstall();
        _doc = doc;
        _kind = kind;
        _manager = new FoldingManager(doc);
        _generator = new FoldingElementGenerator { FoldingManager = _manager };
        _editor.TextArea.TextView.ElementGenerators.Add(_generator);
        _margin.Manager = _manager;
        _margin.Visibility = System.Windows.Visibility.Visible;
        doc.Changed += OnChanged;
        Schedule();
    }

    private void Uninstall()
    {
        _debounce.Cancel();
        _run++;
        if (_doc is not null) _doc.Changed -= OnChanged;
        if (_generator is not null) _editor.TextArea.TextView.ElementGenerators.Remove(_generator);
        _manager?.Clear();
        _manager = null;
        _generator = null;
        _doc = null;
        _margin.Manager = null;
        _margin.Visibility = System.Windows.Visibility.Collapsed;
    }

    private void OnChanged(object? sender, DocumentChangeEventArgs e) => _debounce.Trigger();

    /// <summary>Recomputes in the background (the result is dropped if the text changed meanwhile).</summary>
    private void Schedule()
    {
        if (_doc is not { } doc || _manager is null) return;
        var snapshot = doc.CreateSnapshot();
        var kind = _kind;
        var syntax = _syntax;
        var tab = _editor.Options.IndentationSize;
        var run = ++_run;
        Task.Run(() =>
        {
            try
            {
                var list = FoldingStrategies.Compute(snapshot.Text, kind, syntax, tab, out var error);
                _editor.Dispatcher.BeginInvoke(() => Apply(run, doc, snapshot.Version, list, error));
            }
            catch (Exception ex)
            {
                Log.Warn("folding failed: " + ex.Message);
            }
        });
    }

    private void Apply(int run, TextDocument doc, ITextSourceVersion? version, List<NewFolding> list, int error)
    {
        if (run != _run || _manager is null || doc != _doc) return;
        if (version is null || !version.BelongsToSameDocumentAs(doc.Version) || version.CompareAge(doc.Version) != 0) return;
        _manager.UpdateFoldings(list, error);
        _margin.InvalidateVisual();
    }

    /// <summary>Synchronous update (tests).</summary>
    public void UpdateNow()
    {
        if (_doc is not { } doc || _manager is null) return;
        _debounce.Cancel();
        _run++;
        var list = FoldingStrategies.Compute(doc.Text, _kind, _syntax, _editor.Options.IndentationSize, out var error);
        _manager.UpdateFoldings(list, error);
        _margin.InvalidateVisual();
    }

    private FoldingSection? Innermost(Func<FoldingSection, bool> filter)
    {
        if (_manager is null || _editor.Document is not { } doc) return null;
        var caret = _editor.TextArea.Caret.Offset;
        var line = doc.GetLineByOffset(caret);
        // a region starting on the caret line wins (caret on "void Foo() {"), then the innermost one around the caret
        var onLine = FoldMargin.StartingOn(_manager, line).Where(filter).OrderBy(s => s.Length).FirstOrDefault();
        return onLine ?? _manager.GetFoldingsContaining(caret).Where(filter).OrderBy(s => s.Length).FirstOrDefault();
    }

    public void Fold()
    {
        if (Innermost(s => !s.IsFolded) is not { } s) return;
        s.IsFolded = true;
        var caret = _editor.TextArea.Caret.Offset;
        if (caret > s.StartOffset && caret < s.EndOffset) _editor.TextArea.Caret.Offset = s.StartOffset;
    }

    public void Unfold()
    {
        if (_manager is null || _editor.Document is not { } doc) return;
        var line = doc.GetLineByOffset(_editor.TextArea.Caret.Offset);
        var any = false;
        foreach (var s in FoldMargin.StartingOn(_manager, line).Where(s => s.IsFolded))
        {
            s.IsFolded = false;
            any = true;
        }
        if (!any && Innermost(s => s.IsFolded) is { } inner) inner.IsFolded = false;
    }

    public void SetAll(bool folded)
    {
        if (_manager is null) return;
        foreach (var s in _manager.AllFoldings) s.IsFolded = folded;
        var caret = _editor.TextArea.Caret.Offset;
        if (folded && _manager.GetFoldingsContaining(caret).Where(s => s.IsFolded && caret > s.StartOffset).OrderByDescending(s => s.Length).FirstOrDefault() is { } outer)
            _editor.TextArea.Caret.Offset = outer.StartOffset;
    }
}
