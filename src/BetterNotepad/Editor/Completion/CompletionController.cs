using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using BetterNotepad.Core.Localization;
using BetterNotepad.Editor.Highlighting;
using BetterNotepad.Editor.MultiCaret;
using BetterNotepad.Editor.Text;
using BetterNotepad.UI;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;

namespace BetterNotepad.Editor.Completion;

/// <summary>
/// Word completion while typing: the language's keywords and the words of the document (no LSP). A small
/// Ov.Panel popover under the word; ↑/↓ choose, Enter / Tab accept, Esc closes. Never takes keyboard focus.
/// </summary>
internal sealed partial class CompletionController
{
    private const int MaxItems = 40;
    private const int Visible = 8;
    private const int MaxScan = 1_500_000;
    private const double Shadow = 16;

    private readonly CodeEditor _editor;
    private readonly MultiCaretController _carets;
    private readonly Popup _popup;
    private readonly Border _panel;
    private readonly StackPanel _rows;
    private readonly KeyHandler _keys;
    private readonly List<(string Text, bool Keyword)> _items = [];
    private int _index;
    private int _first;
    private int _start = -1;
    private string _prefix = "";
    private string[]? _keywords;
    private string? _keywordsFor;
    private HashSet<string>? _words;
    private ITextSourceVersion? _wordsVersion;

    public CompletionController(CodeEditor editor, MultiCaretController carets)
    {
        _editor = editor;
        _carets = carets;
        _rows = new StackPanel();
        _panel = new Border { Child = _rows, Padding = new Thickness(4), CornerRadius = new CornerRadius(10), MinWidth = 180, MaxWidth = 420 };
        _panel.SetResourceReference(FrameworkElement.StyleProperty, "Ov.Panel");
        _popup = new Popup
        {
            AllowsTransparency = true,
            Focusable = false,
            StaysOpen = true,
            Placement = PlacementMode.Relative,
            PlacementTarget = editor.TextArea.TextView,
            Child = new Grid { Margin = new Thickness(Shadow), Children = { _panel } },
        };
        _keys = new KeyHandler(this, editor.TextArea);

        var area = editor.TextArea;
        area.TextEntered += OnTextEntered;
        area.Caret.PositionChanged += (_, _) =>
        {
            if (IsOpen) Refresh(openIfClosed: false);
        };
        area.LostKeyboardFocus += (_, _) =>
        {
            if (!ForceOpen) Close();
        };
        area.TextView.ScrollOffsetChanged += (_, _) => Close();
        area.DocumentChanged += (_, _) =>
        {
            _words = null;
            Close();
        };
    }

    public bool Enabled { get; set; } = true;

    /// <summary>Letters of a word that open the popover on their own (Notepad++ style); 0 = only Ctrl+Space.</summary>
    public int AutoChars { get; set; } = 2;

    public bool IsOpen => _popup.IsOpen;

    /// <summary>Snapshots only: keep the popover open although the editor has no focus.</summary>
    public static bool ForceOpen { get; set; }

    internal FrameworkElement Panel => _panel;

    public IReadOnlyList<(string Text, bool Keyword)> Items => _items;

    /// <summary>The language changed: keywords are re-read on the next popover.</summary>
    public void LanguageChanged()
    {
        _keywordsFor = null;
        Close();
    }

    private void OnTextEntered(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length == 1 && TextHelpers.IsWordChar(e.Text[0])) Refresh(openIfClosed: AutoChars > 0);
        else Close();
    }

    /// <summary>Opens (or refilters) the popover for the word before the caret.</summary>
    public void Refresh(bool openIfClosed, bool manual = false)
    {
        var doc = _editor.Document;
        if (!Enabled || doc is null || _carets.HasExtra || _editor.IsReadOnly || _editor.Model?.IsLarge == true || !_editor.TextArea.Selection.IsEmpty)
        {
            Close();
            return;
        }
        var caret = _editor.TextArea.Caret.Offset;
        var line = doc.GetLineByOffset(caret);
        var start = caret;
        while (start > line.Offset && TextHelpers.IsWordChar(doc.GetCharAt(start - 1))) start--;
        var after = caret < doc.TextLength && TextHelpers.IsWordChar(doc.GetCharAt(caret));
        if (caret - start < (IsOpen || manual ? 1 : Math.Max(1, AutoChars)) || after || char.IsDigit(doc.GetCharAt(start)))
        {
            Close();
            return;
        }
        if (!IsOpen && !openIfClosed) return;
        _start = start;
        _prefix = doc.GetText(start, caret - start);
        Collect(doc);
        if (_items.Count == 0)
        {
            Close();
            return;
        }
        _index = 0;
        _first = 0;
        Render();
        Place(doc);
        if (!IsOpen)
        {
            _popup.IsOpen = true;
            _editor.TextArea.PushStackedInputHandler(_keys);
            _panel.Opacity = 0;
            Motion.Appear(_panel, rise: 4, fromScale: 0.98, ms: 180);
        }
    }

    public void Close()
    {
        if (!_popup.IsOpen) return;
        _popup.IsOpen = false;
        _editor.TextArea.PopStackedInputHandler(_keys);
    }

    private void Collect(TextDocument doc)
    {
        _items.Clear();
        var prefix = _prefix;
        var languageId = _editor.Model?.LanguageId ?? Languages.PlainText;
        if (_keywordsFor != languageId)
        {
            _keywords = KeywordsOf(languageId);
            _keywordsFor = languageId;
        }
        var version = doc.Version;
        if (_words is null || _wordsVersion is null || !version.BelongsToSameDocumentAs(_wordsVersion) || version.CompareAge(_wordsVersion) != 0 && !IsOpen)
        {
            _words = WordsOf(doc);
            _wordsVersion = version;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal) { prefix };
        foreach (var k in _keywords!)
            if (k.Length > prefix.Length && k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && seen.Add(k)) _items.Add((k, true));
        foreach (var w in _words)
            if (w.Length > prefix.Length && w.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && seen.Add(w)) _items.Add((w, false));
        // exact-case prefix first, then shorter, then keywords, then alphabetical
        _items.Sort((a, b) =>
        {
            var ca = a.Text.StartsWith(prefix, StringComparison.Ordinal) ? 0 : 1;
            var cb = b.Text.StartsWith(prefix, StringComparison.Ordinal) ? 0 : 1;
            if (ca != cb) return ca.CompareTo(cb);
            if (a.Text.Length != b.Text.Length) return a.Text.Length.CompareTo(b.Text.Length);
            if (a.Keyword != b.Keyword) return a.Keyword ? -1 : 1;
            return string.CompareOrdinal(a.Text, b.Text);
        });
        if (_items.Count > MaxItems) _items.RemoveRange(MaxItems, _items.Count - MaxItems);
    }

    [GeneratedRegex(@"[\p{L}_][\p{L}\p{Nd}_]{2,63}")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\(\?[>:]([^()]*)\)")]
    private static partial Regex AlternationRegex();

    private static HashSet<string> WordsOf(TextDocument doc)
    {
        var text = doc.GetText(0, Math.Min(doc.TextLength, MaxScan));
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in WordRegex().Matches(text))
        {
            set.Add(m.Value);
            if (set.Count > 50_000) break;
        }
        return set;
    }

    /// <summary>Keywords from the highlighting definition: keyword lists ("\b(?&gt;a|b)\b") and word alternations of rules ("(?:a|b)").</summary>
    private static string[] KeywordsOf(string languageId)
    {
        if (HighlightingProvider.ForLanguage(languageId) is not { } def) return [];
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var rule in def.MainRuleSet.Rules)
        {
            foreach (Match m in AlternationRegex().Matches(rule.Regex.ToString()))
            {
                foreach (var part in m.Groups[1].Value.Split('|'))
                {
                    string w;
                    try
                    {
                        w = Regex.Unescape(part.Trim());
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }
                    if (w.Length >= 2 && w.All(TextHelpers.IsWordChar)) set.Add(w);
                }
            }
        }
        return [.. set];
    }

    // ───────────── popover ─────────────

    private void Render()
    {
        _rows.Children.Clear();
        var font = _editor.FontFamily;
        var size = Math.Max(11, _editor.FontSize * 0.95);
        var count = Math.Min(Visible, _items.Count - _first);
        for (var i = _first; i < _first + count; i++)
        {
            var (text, keyword) = _items[i];
            var word = new TextBlock { FontFamily = font, FontSize = size, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var head = new Run(text[.._prefix.Length]) { FontWeight = FontWeights.SemiBold };
            head.SetResourceReference(TextElement.ForegroundProperty, "Brush.AccentText");
            word.Inlines.Add(head);
            word.Inlines.Add(new Run(text[_prefix.Length..]));
            var kind = new TextBlock
            {
                Text = Loc.T(keyword ? "completion.keyword" : "completion.word"),
                FontSize = 11,
                Margin = new Thickness(18, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            kind.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
            var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            Grid.SetColumn(kind, 1);
            grid.Children.Add(word);
            grid.Children.Add(kind);
            var row = new Border { Child = grid, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3), Cursor = Cursors.Hand };
            if (i == _index) row.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
            else row.Background = Brushes.Transparent;
            var at = i;
            row.MouseLeftButtonUp += (_, e) =>
            {
                _index = at;
                Accept();
                e.Handled = true;
            };
            _rows.Children.Add(row);
        }
    }

    /// <summary>Under the word, with the item text aligned to the typed letters.</summary>
    private void Place(TextDocument doc)
    {
        var view = _editor.TextArea.TextView;
        var pos = view.GetVisualPosition(new TextViewPosition(doc.GetLocation(_start)), ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineBottom) - view.ScrollOffset;
        _popup.HorizontalOffset = pos.X - Shadow - 4 - 8 - 1;
        _popup.VerticalOffset = pos.Y - Shadow + 2;
    }

    private void Step(int delta)
    {
        if (_items.Count == 0) return;
        _index = Math.Clamp(_index + delta, 0, _items.Count - 1);
        if (_index < _first) _first = _index;
        if (_index >= _first + Visible) _first = _index - Visible + 1;
        Render();
    }

    /// <summary>Replaces the typed prefix with the chosen word (one undo step).</summary>
    public void Accept()
    {
        var doc = _editor.Document;
        if (!IsOpen || doc is null || _index >= _items.Count) return;
        var text = _items[_index].Text;
        var caret = _editor.TextArea.Caret.Offset;
        Close();
        if (_start < 0 || caret < _start || caret > doc.TextLength) return;
        doc.Replace(_start, caret - _start, text);
        _editor.TextArea.Caret.Offset = _start + text.Length;
    }

    /// <summary>Runs before AvalonEdit's own key handling (and before the multi-caret Enter / Tab).</summary>
    private sealed class KeyHandler(CompletionController owner, TextArea area) : TextAreaStackedInputHandler(area)
    {
        public override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (!owner.IsOpen || Keyboard.Modifiers != ModifierKeys.None) return;
            switch (e.Key)
            {
                case Key.Down: owner.Step(1); break;
                case Key.Up: owner.Step(-1); break;
                case Key.PageDown: owner.Step(Visible); break;
                case Key.PageUp: owner.Step(-Visible); break;
                case Key.Enter or Key.Tab: owner.Accept(); break;
                case Key.Escape: owner.Close(); break;
                default: return;
            }
            e.Handled = true;
        }
    }
}
