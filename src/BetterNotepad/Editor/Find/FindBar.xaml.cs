using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.UI;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Search;

namespace BetterNotepad.Editor.Find;

/// <summary>Find / replace bar (Ctrl+F / Ctrl+H) floating at the top right of an editor.</summary>
public partial class FindBar : UserControl
{
    private const int MaxHits = 20000;

    private CodeEditor? _editor;
    private readonly SearchRenderer _renderer = new();
    private readonly List<ISearchResult> _hits = new();
    private ISearchStrategy? _strategy;
    private (int Start, int End)? _scope;
    private int _current = -1;
    private Debouncer? _research;

    public FindBar()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
        Loaded += (_, _) => Loc.LanguageChanged += UpdateCount;
        Unloaded += (_, _) => Loc.LanguageChanged -= UpdateCount;
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    public void Attach(CodeEditor editor)
    {
        _editor = editor;
        editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);
        editor.DocumentChanged += (_, _) => Research();
        editor.TextChanged += (_, _) => { if (IsOpen) (_research ??= new Debouncer(TimeSpan.FromMilliseconds(160), Research)).Trigger(); };
    }

    public void Open(bool replace)
    {
        if (_editor is null) return;
        var wasOpen = IsOpen;
        Visibility = Visibility.Visible;
        if (!wasOpen) RefreshFieldText();
        if (!wasOpen) Motion.Appear(this, rise: -6, fromScale: 0.98, ms: 220);
        SetReplace(replace || ReplaceRow.Visibility == Visibility.Visible && wasOpen);

        var sel = _editor.SelectedText;
        if (!string.IsNullOrEmpty(sel) && !sel.Contains('\n') && sel.Length < 200)
            Query.Text = sel;
        else if (string.IsNullOrEmpty(Query.Text) && WordAtCaret() is { Length: > 0 } word)
            Query.Text = word;

        Query.Focus();
        Query.SelectAll();
        Research();
    }

    /// <summary>
    /// A theme switch while the bar was hidden can leave a TextBox drawing its text in the old color (seen as white text
    /// on Daylight after Aurora): re-flowing the text makes it render with the current Foreground.
    /// </summary>
    private void RefreshFieldText()
    {
        foreach (var box in new[] { Query, ReplaceBox })
        {
            if (box.Text.Length == 0) continue;
            var (text, start, length) = (box.Text, box.SelectionStart, box.SelectionLength);
            box.Text = "";
            box.Text = text;
            box.Select(start, length);
        }
    }

    public void Close()
    {
        if (!IsOpen) return;
        Visibility = Visibility.Collapsed;
        _renderer.Clear();
        _scope = null;
        SelectionToggle.IsChecked = false;
        _editor?.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Selection);
        _editor?.Focus();
    }

    private string WordAtCaret()
    {
        if (_editor?.Document is not { } doc) return "";
        var offset = _editor.CaretOffset;
        int s = offset, e = offset;
        while (s > 0 && IsWordChar(doc.GetCharAt(s - 1))) s--;
        while (e < doc.TextLength && IsWordChar(doc.GetCharAt(e))) e++;
        return e > s ? doc.GetText(s, e - s) : "";
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private void SetReplace(bool on)
    {
        ReplaceRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ReplaceToggle.IsChecked = on;
        ReplaceChevron.Glyph = on ? "" : "";
    }

    private void OnToggleReplace(object sender, RoutedEventArgs e) => SetReplace(ReplaceRow.Visibility != Visibility.Visible);

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Research();
        if (_hits.Count > 0) SelectNearest();
    }

    private void OnReplaceTextChanged(object sender, TextChangedEventArgs e)
        => ReplacePlaceholder.Visibility = ReplaceBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        Research();
        if (_hits.Count > 0) SelectNearest();
    }

    private void OnSelectionToggle(object sender, RoutedEventArgs e)
    {
        if (SelectionToggle.IsChecked == true && _editor is { SelectionLength: > 0 })
            _scope = (_editor.SelectionStart, _editor.SelectionStart + _editor.SelectionLength);
        else
        {
            _scope = null;
            SelectionToggle.IsChecked = false;
        }
        Research();
    }

    /// <summary>Recomputes all hits for the current query and options.</summary>
    public void Research()
    {
        _hits.Clear();
        _current = -1;
        _strategy = null;
        Count.Foreground = (System.Windows.Media.Brush)FindResource("Brush.PanelTextDim");
        if (_editor?.Document is not { } doc || !IsOpen || Query.Text.Length == 0)
        {
            _renderer.Clear();
            Redraw();
            UpdateCount();
            return;
        }
        try
        {
            var mode = RegexToggle.IsChecked == true ? SearchMode.RegEx : SearchMode.Normal;
            _strategy = SearchStrategyFactory.Create(Query.Text, CaseToggle.IsChecked != true, WordToggle.IsChecked == true, mode);
            var (start, end) = _scope ?? (0, doc.TextLength);
            end = Math.Min(end, doc.TextLength);
            foreach (var r in _strategy.FindAll(doc, start, Math.Max(0, end - start)))
            {
                if (r.Length == 0 && mode == SearchMode.RegEx) continue;
                _hits.Add(r);
                if (_hits.Count >= MaxHits) break;
            }
        }
        catch (SearchPatternException)
        {
            Count.Text = Loc.T("find.badRegex");
            Count.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Danger");
            _renderer.Clear();
            Redraw();
            return;
        }
        _current = IndexAtOrAfter(_editor.SelectionStart);
        if (_current >= 0 && (_hits[_current].Offset != _editor.SelectionStart || _hits[_current].Length != _editor.SelectionLength)) _current = -1;
        Paint();
    }

    private void Paint()
    {
        _renderer.Set(_hits.Select(h => (h.Offset, h.Length)), _current >= 0 ? (_hits[_current].Offset, _hits[_current].Length) : null);
        Redraw();
        UpdateCount();
    }

    private void Redraw() => _editor?.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Selection);

    private void UpdateCount()
    {
        if (_strategy is null && Query.Text.Length > 0 && Count.Text == Loc.T("find.badRegex")) return;
        Count.Text = Query.Text.Length == 0 ? "" :
            _hits.Count == 0 ? Loc.T("find.noResults") :
            _current >= 0 ? Loc.T("find.countOf", _current + 1, _hits.Count >= MaxHits ? $"{MaxHits}+" : _hits.Count) :
            Loc.T("find.count", _hits.Count >= MaxHits ? $"{MaxHits}+" : _hits.Count);
    }

    private int IndexAtOrAfter(int offset)
    {
        for (var i = 0; i < _hits.Count; i++)
            if (_hits[i].Offset >= offset) return i;
        return _hits.Count > 0 ? 0 : -1;
    }

    private void SelectNearest()
    {
        if (_editor is null || _hits.Count == 0) return;
        Select(IndexAtOrAfter(_editor.SelectionStart));
    }

    public void FindNext() => Step(+1);

    public void FindPrevious() => Step(-1);

    private void Step(int dir)
    {
        if (_editor is null) return;
        if (!IsOpen) { Open(false); return; }
        if (_hits.Count == 0) { Motion.Shake(this); return; }
        int index;
        if (dir > 0)
        {
            var after = _editor.SelectionStart + Math.Max(_editor.SelectionLength, 0);
            index = _hits.FindIndex(h => h.Offset >= after && !(h.Offset == _editor.SelectionStart && h.Length == _editor.SelectionLength));
            if (index < 0) index = 0;
        }
        else
        {
            index = _hits.FindLastIndex(h => h.Offset < _editor.SelectionStart);
            if (index < 0) index = _hits.Count - 1;
        }
        Select(index);
    }

    private void Select(int index)
    {
        if (_editor is null || index < 0 || index >= _hits.Count) return;
        _current = index;
        var hit = _hits[index];
        _editor.Select(hit.Offset, hit.Length);
        var loc = _editor.Document.GetLocation(hit.Offset);
        _editor.ScrollTo(loc.Line, loc.Column);
        Paint();
    }

    private void OnNext(object sender, RoutedEventArgs e) => FindNext();

    private void OnPrevious(object sender, RoutedEventArgs e) => FindPrevious();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleCommonKeys(e)) return;
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift) FindPrevious();
            else FindNext();
            e.Handled = true;
        }
    }

    private void OnReplaceKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleCommonKeys(e)) return;
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control) ReplaceAll();
            else ReplaceOne();
            e.Handled = true;
        }
    }

    private bool HandleCommonKeys(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { Close(); e.Handled = true; return true; }
        if (Keyboard.Modifiers == ModifierKeys.Alt)
        {
            System.Windows.Controls.Primitives.ToggleButton? t = key switch { Key.C => CaseToggle, Key.W => WordToggle, Key.R => RegexToggle, Key.L => SelectionToggle, _ => null };
            if (t is not null)
            {
                t.IsChecked = t.IsChecked != true;
                if (t == SelectionToggle) OnSelectionToggle(t, e); else OnOptionChanged(t, e);
                e.Handled = true;
                return true;
            }
        }
        return false;
    }

    private void OnReplaceOne(object sender, RoutedEventArgs e) => ReplaceOne();

    private void OnReplaceAll(object sender, RoutedEventArgs e) => ReplaceAll();

    public void ReplaceOne()
    {
        if (_editor is null || _editor.IsReadOnly || _hits.Count == 0) return;
        var hit = _hits.FirstOrDefault(h => h.Offset == _editor.SelectionStart && h.Length == _editor.SelectionLength);
        if (hit is null)
        {
            FindNext();
            return;
        }
        var replacement = RegexToggle.IsChecked == true ? hit.ReplaceWith(ReplaceBox.Text) : ReplaceBox.Text;
        _editor.Document.Replace(hit.Offset, hit.Length, replacement);
        _editor.CaretOffset = hit.Offset + replacement.Length;
        Research();
        FindNext();
    }

    public void ReplaceAll()
    {
        if (_editor is null || _editor.IsReadOnly || _hits.Count == 0) return;
        var regex = RegexToggle.IsChecked == true;
        var doc = _editor.Document;
        var count = _hits.Count;
        doc.BeginUpdate();
        try
        {
            for (var i = _hits.Count - 1; i >= 0; i--)
            {
                var h = _hits[i];
                doc.Replace(h.Offset, h.Length, regex ? h.ReplaceWith(ReplaceBox.Text) : ReplaceBox.Text);
            }
        }
        finally
        {
            doc.EndUpdate();
        }
        Research();
        UI.Windows.MainWindow.Of(this)?.Toasts.Show(Loc.Plural("find.replaced", count), UI.ToastKind.Success);
    }
}
