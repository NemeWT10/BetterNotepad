using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Session;
using BetterNotepad.Editor;
using BetterNotepad.Editor.Highlighting;
using BetterNotepad.UI.Controls;
using BetterNotepad.UI.Windows;
using AvalonSelection = ICSharpCode.AvalonEdit.Editing.Selection;
using IOPath = System.IO.Path;
using TextDocument = BetterNotepad.Documents.TextDocument;

namespace BetterNotepad.UI.Palette;

/// <summary>
/// The palette overlay of one window: an <c>Ov.Panel</c> card under the title bar with a mode chip, the
/// query field and a virtualized result list. A prefix typed first switches the mode
/// (&gt; commands, : line, @ symbol); Backspace on an empty query goes back to files. Click outside or Esc
/// closes and gives the focus back. Go to line / symbol preview live and restore the editor on cancel.
/// </summary>
internal sealed class PaletteView : Grid
{
    private const double CardWidth = 640;
    private const int LargeScan = 400_000;

    private readonly MainWindow _w;
    private readonly CodeEditor? _editor;
    private readonly Border _card = new();
    private readonly FrostedHost _host;
    private readonly Border _chip = new();
    private readonly Icon _chipIcon = new() { FontSize = 12, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock _chipText = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _input = new();
    private readonly TextBlock _placeholder = new();
    private readonly ListBox _list = new();
    private readonly StackPanel _empty = new();
    private readonly Icon _emptyIcon = new() { FontSize = 22, Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock _emptyTitle = new() { FontSize = 13.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _emptyText = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24, 4, 24, 0), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Grid _footer = new() { Margin = new Thickness(10, 1, 8, 2), Height = 28 };
    private readonly Rectangle _listSeparator = new();

    private PaletteMode _mode;
    private List<PaletteCandidate> _source = new();
    private List<PaletteRow> _rows = new();
    private EditorState? _saved;
    private IInputElement? _previousFocus;
    private bool _syncingText;
    private bool _interacted;
    private bool _accepted;
    private bool _scanning;
    private int _scanVersion;
    private PaletteRow? _pressed;

    public PaletteView(MainWindow window)
    {
        _w = window;
        _editor = window.ActiveView?.Editor;
        Background = Brushes.Transparent; // the click catcher around the card
        Focusable = false;

        _card.SetResourceReference(StyleProperty, "Ov.Panel");
        _host = new FrostedHost(_card, window.GroupsHost)
        {
            MaxWidth = CardWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16, 6, 16, 0),
            Opacity = 0,
        };

        var layout = new Grid();
        for (var i = 0; i < 5; i++) layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── input row: [mode chip] query ──
        var inputRow = new Grid { Height = 42, Margin = new Thickness(6, 3, 8, 2) };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _chip.CornerRadius = new CornerRadius(7);
        _chip.Padding = new Thickness(8, 3, 10, 4);
        _chip.VerticalAlignment = VerticalAlignment.Center;
        _chip.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
        _chipIcon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
        _chipText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal };
        chipRow.Children.Add(_chipIcon);
        chipRow.Children.Add(_chipText);
        _chip.Child = chipRow;
        inputRow.Children.Add(_chip);

        _input.SetResourceReference(StyleProperty, "BareField");
        _input.FontSize = 14.5;
        _input.Margin = new Thickness(10, 0, 0, 0);
        _input.VerticalAlignment = VerticalAlignment.Stretch;
        _input.TextChanged += (_, _) => OnTextChanged();
        Grid.SetColumn(_input, 1);
        inputRow.Children.Add(_input);

        _placeholder.FontSize = 14.5;
        _placeholder.Margin = new Thickness(12, 0, 0, 0);
        _placeholder.VerticalAlignment = VerticalAlignment.Center;
        _placeholder.IsHitTestVisible = false;
        _placeholder.TextTrimming = TextTrimming.CharacterEllipsis;
        _placeholder.Opacity = 0.75;
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
        Grid.SetColumn(_placeholder, 1);
        inputRow.Children.Add(_placeholder);
        layout.Children.Add(inputRow);

        _listSeparator.SetResourceReference(StyleProperty, "Ov.Separator");
        _listSeparator.Margin = new Thickness(2, 2, 2, 4);
        Grid.SetRow(_listSeparator, 1);
        layout.Children.Add(_listSeparator);

        // ── results ──
        _list.SetResourceReference(StyleProperty, "BareList");
        _list.Focusable = false;
        _list.MaxHeight = 404;
        _list.Padding = new Thickness(0, 0, 0, 2);
        _list.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(PaletteRowView)) };
        var itemStyle = new Style(typeof(ListBoxItem), (Style)Application.Current.FindResource("PaletteItem"));
        itemStyle.Setters.Add(new Setter(MarginProperty, new Binding(nameof(PaletteRow.ContainerMargin))));
        itemStyle.Setters.Add(new Setter(FocusableProperty, false));
        _list.ItemContainerStyle = itemStyle;
        VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Item);
        _list.SelectionChanged += (_, _) =>
        {
            if (_interacted) PreviewSelected();
        };
        _list.PreviewMouseLeftButtonDown += OnListMouseDown;
        _list.PreviewMouseLeftButtonUp += OnListMouseUp;
        Grid.SetRow(_list, 2);
        layout.Children.Add(_list);

        _empty.Margin = new Thickness(0, 16, 0, 20);
        _empty.Visibility = Visibility.Collapsed;
        _emptyIcon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
        _emptyIcon.Opacity = 0.7;
        _emptyTitle.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelText");
        _emptyText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
        _empty.Children.Add(_emptyIcon);
        _empty.Children.Add(_emptyTitle);
        _empty.Children.Add(_emptyText);
        Grid.SetRow(_empty, 2);
        layout.Children.Add(_empty);

        var footerSep = new Rectangle();
        footerSep.SetResourceReference(StyleProperty, "Ov.Separator");
        footerSep.Margin = new Thickness(2, 2, 2, 0);
        Grid.SetRow(footerSep, 3);
        layout.Children.Add(footerSep);
        Grid.SetRow(_footer, 4);
        layout.Children.Add(_footer);

        _card.Child = layout;
        Children.Add(_host);

        PreviewKeyDown += OnKey;
        SizeChanged += (_, _) =>
        {
            _list.MaxHeight = Math.Max(160, Math.Min(404, ActualHeight * 0.62));
            AlignToPill();
            _host.Sync();
        };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>The card hangs centered under the title bar's pill (which is centered in the free space, not in the window).</summary>
    private void AlignToPill()
    {
        var width = Math.Min(CardWidth, Math.Max(0, ActualWidth - 32));
        var center = ActualWidth / 2;
        try
        {
            if (_w.PalettePill.IsVisible && _w.PalettePill.ActualWidth > 0)
                center = _w.PalettePill.TranslatePoint(new Point(_w.PalettePill.ActualWidth / 2, 0), this).X;
        }
        catch (InvalidOperationException)
        {
            // not in the same visual tree yet: window center
        }
        var left = Math.Clamp(center - width / 2, 16, Math.Max(16, ActualWidth - 16 - width));
        _host.HorizontalAlignment = HorizontalAlignment.Left;
        _host.Width = width;
        _host.Margin = new Thickness(left, 6, 0, 0);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, _host.Sync); // frosted backdrop follows the new position
    }

    public PaletteMode Mode => _mode;

    public bool IsClosing { get; private set; }

    public string Query => _input.Text;

    internal IReadOnlyList<PaletteRow> Rows => _rows;

    internal PaletteRow? Selected => _list.SelectedItem as PaletteRow;

    internal FrameworkElement Card => _host;

    // ───────────── open / close ─────────────

    public void Open(PaletteMode mode, string? initialText)
    {
        var focused = Keyboard.FocusedElement;
        _previousFocus = focused is DependencyObject d && Window.GetWindow(d) == _w && !IsInside(d, this) ? focused : null;
        _w.PreviewMouseDown += OnWindowMouseDown;

        var (m, text) = Parse(mode, initialText);
        SetMode(m, text, bounce: false);

        Motion.Transforms(_host);
        _host.RenderTransformOrigin = new Point(0.5, 0);
        Motion.Appear(_host, rise: -10, fromScale: 0.985, ms: 240);
        Dispatcher.BeginInvoke(FocusInput, DispatcherPriority.Input);
    }

    /// <summary>Closes the palette; <paramref name="then"/> runs afterwards (the focus is back by then).</summary>
    public void Close(bool restoreFocus = true, Action? then = null)
    {
        if (IsClosing) return;
        IsClosing = true;
        _scanVersion++;
        _w.PreviewMouseDown -= OnWindowMouseDown;
        PreviewKeyDown -= OnKey;
        IsHitTestVisible = false;
        if (!_accepted) RestoreEditor();
        _saved = null;

        Motion.Disappear(_host, () => (Parent as Panel)?.Children.Remove(this), sink: -6, ms: 130);
        if (restoreFocus) FocusBack();
        if (then is not null)
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    then();
                }
                catch (Exception ex)
                {
                    Log.Error("palette: action failed", ex);
                    _w.Toasts.Show(Loc.T("error.unexpected", ex.Message), ToastKind.Error);
                }
            }, DispatcherPriority.Input);
        }
    }

    private void FocusInput()
    {
        if (IsClosing) return;
        // test instances never take the keyboard: logical focus only
        if (AppEnv.TestMode && !_w.IsActive) FocusManager.SetFocusedElement(_w, _input);
        else _input.Focus();
        _input.CaretIndex = _input.Text.Length;
    }

    private void FocusBack()
    {
        if (AppEnv.TestMode && !_w.IsActive) return;
        if (_previousFocus is UIElement { IsVisible: true } el && el.Focus()) return;
        _w.ActiveView?.FocusContent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Loc.LanguageChanged += OnLanguageChanged;

    private void OnUnloaded(object sender, RoutedEventArgs e) => Loc.LanguageChanged -= OnLanguageChanged;

    private void OnLanguageChanged()
    {
        if (!IsClosing) SetMode(_mode, _input.Text, bounce: false);
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsClosing || e.OriginalSource is not DependencyObject d || IsInside(d, _card)) return;
        if (IsInside(d, _w.PalettePill))
        {
            // the pill toggles: a click while open closes
            e.Handled = true;
            Close();
            return;
        }
        var onCatcher = IsInside(d, this);
        if (onCatcher) e.Handled = true;
        Close(restoreFocus: onCatcher);
    }

    private static bool IsInside(DependencyObject? d, DependencyObject ancestor)
    {
        while (d is not null)
        {
            if (d == ancestor) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    // ───────────── modes ─────────────

    private static PaletteMode? ModeForPrefix(char c) => c switch
    {
        '>' => PaletteMode.Commands,
        ':' => PaletteMode.GoToLine,
        '@' => PaletteMode.Symbols,
        _ => null,
    };

    private static (PaletteMode, string) Parse(PaletteMode mode, string? text)
    {
        text ??= "";
        if (text.Length > 0 && ModeForPrefix(text[0]) is { } m) return (m, text[1..]);
        return (mode, text);
    }

    private static bool IsPreviewMode(PaletteMode m) => m is PaletteMode.GoToLine or PaletteMode.Symbols;

    /// <summary>Switches the mode (rebuilding the candidates) and sets the query.</summary>
    public void SetMode(PaletteMode mode, string text, bool bounce = true)
    {
        var changed = mode != _mode;
        _mode = mode;
        if (IsPreviewMode(mode)) _saved ??= EditorState.Capture(_editor);
        else
        {
            RestoreEditor();
            _saved = null;
        }

        _scanning = false;
        _scanVersion++;
        _source = mode switch
        {
            PaletteMode.Commands => BuildCommands(),
            PaletteMode.Files => BuildFiles(),
            PaletteMode.Symbols => BuildSymbols(),
            PaletteMode.Language => BuildLanguages(),
            _ => new List<PaletteCandidate>(),
        };

        var (glyph, name, placeholder) = mode switch
        {
            PaletteMode.Commands => (PaletteGlyphs.Command, "pal.mode.commands", "pal.ph.commands"),
            PaletteMode.GoToLine => (PaletteGlyphs.Line, "pal.mode.line", "pal.ph.line"),
            PaletteMode.Symbols => (PaletteGlyphs.Symbol, "pal.mode.symbols", "pal.ph.symbols"),
            PaletteMode.Language => (PaletteGlyphs.Language, "pal.mode.language", "pal.ph.language"),
            _ => (Glyphs.Search, "pal.mode.files", "pal.ph.files"),
        };
        _chipIcon.Glyph = glyph;
        _chipText.Text = Loc.T(name);
        System.Windows.Automation.AutomationProperties.SetName(_input, Loc.T(name) + ": " + Loc.T(placeholder));
        _placeholder.Text = Loc.T(placeholder);
        BuildFooter();

        _syncingText = true;
        _input.Text = text;
        _input.CaretIndex = text.Length;
        _syncingText = false;
        _interacted = text.Length > 0;
        if (changed && bounce) Motion.Bounce(_chip, 1.1);
        Refresh();
    }

    private void OnTextChanged()
    {
        if (_syncingText || IsClosing) return;
        var text = _input.Text;
        if (text.Length > 0 && ModeForPrefix(text[0]) is { } m && (_mode == PaletteMode.Files || text.Length == 1 || m == _mode))
        {
            SetMode(m, text[1..]);
            return;
        }
        _interacted = true;
        Refresh();
    }

    // ───────────── list ─────────────

    private void Refresh()
    {
        var query = _input.Text;
        _placeholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_mode == PaletteMode.GoToLine)
        {
            _source = BuildLine(query);
            _rows = _source.Select(c => new PaletteRow(c, null, null, false, false)).ToList();
        }
        else
        {
            _rows = PaletteFilter.Apply(_source, query, PaletteFilter.MinScorePerChar);
        }
        _list.ItemsSource = _rows;

        var index = DefaultIndex(query);
        _list.SelectedIndex = index;
        if (index >= 0)
        {
            _list.UpdateLayout();
            _list.ScrollIntoView(_rows[index]);
        }
        UpdateEmptyState(query);
        if (_interacted) PreviewSelected();
    }

    private int DefaultIndex(string query)
    {
        if (_rows.Count == 0) return -1;
        if (query.Length > 0) return 0;
        switch (_mode)
        {
            case PaletteMode.Files when _rows.Count > 1 && _rows[0].Item.Id == DocKey(_w.ActiveDocument):
                return 1; // the tab you are in is listed first, the one you want is next
            case PaletteMode.Language:
            {
                var current = (_w.ActiveDocument as TextDocument)?.LanguageId;
                var i = _rows.FindIndex(r => r.Item.Id == "lang:" + current);
                return Math.Max(0, i);
            }
            case PaletteMode.Symbols when _saved is not null:
            {
                // the symbol the caret is in
                var line = _saved.Line;
                var best = 0;
                for (var i = 0; i < _rows.Count; i++)
                    if (_rows[i].Item.Order <= line) best = i;
                return best;
            }
        }
        return 0;
    }

    private void UpdateEmptyState(string query)
    {
        var empty = _rows.Count == 0;
        _list.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;

        var needsEditor = IsPreviewMode(_mode) && _editor is null || _mode == PaletteMode.Language && _w.ActiveDocument is not TextDocument;
        _emptyText.Visibility = Visibility.Visible;
        if (_scanning)
        {
            _emptyIcon.Glyph = PaletteGlyphs.Symbol;
            _emptyTitle.Text = Loc.T("pal.empty.scanning");
            _emptyText.Visibility = Visibility.Collapsed;
        }
        else if (needsEditor)
        {
            _emptyIcon.Glyph = Glyphs.Info;
            _emptyTitle.Text = Loc.T("pal.empty.title");
            _emptyText.Text = Loc.T("pal.empty.noEditor");
        }
        else if (query.Length == 0)
        {
            _emptyIcon.Glyph = _mode == PaletteMode.Symbols ? PaletteGlyphs.Symbol : Glyphs.Page;
            _emptyTitle.Text = Loc.T("pal.empty.title");
            _emptyText.Text = Loc.T(_mode == PaletteMode.Symbols ? "pal.empty.symbols" : "pal.empty.files");
        }
        else
        {
            _emptyIcon.Glyph = Glyphs.Search;
            _emptyTitle.Text = Loc.T("pal.empty.title");
            _emptyText.Text = Loc.T("pal.empty.query", query.Trim());
        }
    }

    /// <summary>Moves the selection (wraps around), e.g. Up / Down or the palette shortcut pressed again.</summary>
    public void MoveSelection(int delta)
    {
        if (_rows.Count == 0) return;
        _interacted = true;
        var i = _list.SelectedIndex < 0 ? 0 : _list.SelectedIndex + delta;
        i = ((i % _rows.Count) + _rows.Count) % _rows.Count;
        Select(i);
    }

    private void Page(int direction)
    {
        if (_rows.Count == 0) return;
        _interacted = true;
        var perPage = Math.Max(1, (int)(_list.ActualHeight / 36) - 1);
        Select(Math.Clamp(Math.Max(0, _list.SelectedIndex) + direction * perPage, 0, _rows.Count - 1));
    }

    private void Select(int i)
    {
        _list.SelectedIndex = i;
        _list.ScrollIntoView(_rows[i]);
    }

    /// <summary>Runs the selected row (Enter / click).</summary>
    public void AcceptSelected()
    {
        var row = Selected;
        if (row?.Item.Accept is not { } accept)
        {
            Motion.Shake(_host);
            return;
        }
        _accepted = true;
        var restoreFocus = _mode is PaletteMode.Commands or PaletteMode.Language;
        Close(restoreFocus, accept);
    }

    /// <summary>Test hook: types a query as if the user did.</summary>
    internal void SetQuery(string text)
    {
        _input.Text = text;
        _input.CaretIndex = text.Length;
    }

    /// <summary>Test hook: Backspace in an empty field.</summary>
    internal void BackspaceOnEmpty()
    {
        if (_input.Text.Length == 0 && _mode != PaletteMode.Files) SetMode(PaletteMode.Files, "");
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(1);
                break;
            case Key.Up:
                MoveSelection(-1);
                break;
            case Key.Tab:
                MoveSelection(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                break;
            case Key.PageDown:
                Page(1);
                break;
            case Key.PageUp:
                Page(-1);
                break;
            case Key.Home when ctrl && _rows.Count > 0:
                _interacted = true;
                Select(0);
                break;
            case Key.End when ctrl && _rows.Count > 0:
                _interacted = true;
                Select(_rows.Count - 1);
                break;
            case Key.Enter:
                AcceptSelected();
                break;
            case Key.Escape:
                Close();
                break;
            case Key.Back when _input.Text.Length == 0 && _mode != PaletteMode.Files:
                SetMode(PaletteMode.Files, "");
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = ContainerAt(e.OriginalSource as DependencyObject)?.DataContext as PaletteRow;
        if (_pressed is null) return; // the scrollbar
        e.Handled = true; // keep the keyboard in the query field
        _interacted = true;
        _list.SelectedItem = _pressed;
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        var row = ContainerAt(e.OriginalSource as DependencyObject)?.DataContext as PaletteRow;
        if (row is null || row != _pressed) return;
        e.Handled = true;
        _pressed = null;
        AcceptSelected();
    }

    private static ListBoxItem? ContainerAt(DependencyObject? d)
    {
        while (d is not null and not ListBox)
        {
            if (d is ListBoxItem item) return item;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    // ───────────── preview (go to line / symbol) ─────────────

    private void PreviewSelected()
    {
        if (!IsPreviewMode(_mode) || IsClosing) return;
        if (Selected?.Item.Preview is { } preview) preview();
        else RestoreEditor();
    }

    private void RestoreEditor() => _saved?.Restore();

    private static void PreviewRange(CodeEditor ed, int line, int column, int length)
    {
        var doc = ed.Document;
        if (doc is null || doc.LineCount == 0) return;
        line = Math.Clamp(line, 1, doc.LineCount);
        var dl = doc.GetLineByNumber(line);
        column = Math.Clamp(column, 1, dl.Length + 1);
        var start = dl.Offset + column - 1;
        length = Math.Clamp(length, 0, dl.EndOffset - start);
        if (length > 0)
        {
            ed.TextArea.Selection = AvalonSelection.Create(ed.TextArea, start, start + length);
            ed.TextArea.Caret.Offset = start;
        }
        else
        {
            // the whole line, from its first non-blank character: where you will land
            var lineStart = dl.Offset;
            while (lineStart < dl.EndOffset && char.IsWhiteSpace(doc.GetCharAt(lineStart))) lineStart++;
            ed.TextArea.Selection = lineStart < dl.EndOffset ? AvalonSelection.Create(ed.TextArea, lineStart, dl.EndOffset) : AvalonSelection.Create(ed.TextArea, lineStart, lineStart);
            ed.TextArea.Caret.Offset = lineStart;
        }
        ed.ScrollTo(line, column, ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineMiddle, ed.ViewportHeight / 2, 0.3);
    }

    /// <summary>Caret, selection and scroll of the editor before the palette moved it.</summary>
    private sealed class EditorState
    {
        private readonly CodeEditor _ed;
        private readonly int _caret;
        private readonly int _selStart;
        private readonly int _selLength;
        private readonly double _v;
        private readonly double _h;

        private EditorState(CodeEditor ed)
        {
            _ed = ed;
            _caret = ed.CaretOffset;
            _selStart = ed.SelectionStart;
            _selLength = ed.SelectionLength;
            _v = ed.VerticalOffset;
            _h = ed.HorizontalOffset;
            Line = ed.TextArea.Caret.Line;
            Column = ed.TextArea.Caret.Column;
        }

        public int Line { get; }

        public int Column { get; }

        public static EditorState? Capture(CodeEditor? ed) => ed?.Document is null ? null : new EditorState(ed);

        public void Restore()
        {
            var len = _ed.Document.TextLength;
            var caret = Math.Min(_caret, len);
            _ed.TextArea.ClearSelection();
            _ed.TextArea.Caret.Offset = caret;
            if (_selLength > 0 && _selStart + _selLength <= len)
            {
                var anchor = caret == _selStart ? _selStart + _selLength : _selStart;
                _ed.TextArea.Selection = AvalonSelection.Create(_ed.TextArea, anchor, caret);
            }
            _ed.ScrollToVerticalOffset(_v);
            _ed.ScrollToHorizontalOffset(_h);
        }
    }

    // ───────────── candidates ─────────────

    private List<PaletteCandidate> BuildCommands()
    {
        var ctx = _w.PaletteContext();
        var visible = new Dictionary<string, AppCommand>(StringComparer.Ordinal);
        foreach (var cmd in CommandRegistry.All)
        {
            if (!cmd.ShowInPalette || cmd.Id == "palette.commands") continue;
            if (cmd.Kinds is not null && (ctx.Document is null || Array.IndexOf(cmd.Kinds, ctx.Document.Kind) < 0)) continue;
            bool can;
            try { can = cmd.CanExecute?.Invoke(ctx) ?? true; }
            catch { can = false; }
            if (can) visible[cmd.Id] = cmd;
        }

        var list = new List<PaletteCandidate>();
        var order = 0;
        var recentLabel = Loc.T("pal.group.recent");
        var recent = PaletteHistory.I.Commands.Where(visible.ContainsKey).ToList();
        foreach (var id in recent)
            list.Add(CommandCandidate(visible[id], recentLabel, order++, 0.35 - order * 0.01));

        var otherLabel = recent.Count > 0 ? Loc.T("pal.group.other") : null;
        var culture = Loc.I.Culture;
        foreach (var cmd in visible.Values.Where(c => !recent.Contains(c.Id))
                     .OrderBy(c => CategoryRank(c.CategoryKey))
                     .ThenBy(c => c.Category, StringComparer.Create(culture, true))
                     .ThenBy(c => c.Title, StringComparer.Create(culture, true)))
            list.Add(CommandCandidate(cmd, otherLabel, order++, 0));
        return list;
    }

    private static readonly string[] CategoryOrder =
        ["cmd.cat.file", "cmd.cat.edit", "cmd.cat.go", "cmd.cat.view", "cmd.cat.tabs", "cmd.cat.tools", "cmd.cat.general", "cmd.cat.app"];

    /// <summary>Browsing order of the unfiltered list: everyday work first, quitting the app last.</summary>
    private static int CategoryRank(string key)
    {
        var i = Array.IndexOf(CategoryOrder, key);
        return i < 0 ? CategoryOrder.Length - 2 : i; // module categories sit before "app"
    }

    private PaletteCandidate CommandCandidate(AppCommand cmd, string? group, int order, double boost)
    {
        var extra = new List<string>(4);
        if (Loc.AllEntries.TryGetValue(cmd.TitleKey, out var both)) extra.Add(Loc.I.IsPolish ? both.En : both.Pl);
        extra.Add(cmd.Category + " " + cmd.Title);
        if (!string.IsNullOrEmpty(cmd.SearchTerms)) extra.Add(cmd.SearchTerms);
        var id = cmd.Id;
        return new PaletteCandidate
        {
            Title = cmd.Title,
            Detail = cmd.Category,
            Gesture = Keymap.GestureFor(id),
            Group = group,
            Extra = extra.ToArray(),
            Id = id,
            Order = order,
            Boost = boost,
            Accept = () =>
            {
                PaletteHistory.I.Used(id);
                _w.RunCommand(id);
            },
        };
    }

    private static string? DocKey(Document? d) => d is null ? null : "doc:" + d.Id;

    private List<PaletteCandidate> BuildFiles()
    {
        var list = new List<PaletteCandidate>();
        var seen = new HashSet<Document>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tabsLabel = Loc.T("pal.group.tabs");
        var order = 0;

        // open tabs: this window's active group by recent use, then its other group, then other windows
        var groups = new List<TabGroup> { _w.Model.ActiveGroup };
        groups.AddRange(_w.Model.Groups.Where(g => g != _w.Model.ActiveGroup));
        groups.AddRange(Workspace.I.Windows.Where(m => m != _w.Model).SelectMany(m => m.Groups));
        foreach (var g in groups)
        {
            foreach (var doc in g.Mru.Concat(g.Tabs))
            {
                if (!seen.Add(doc)) continue;
                if (doc.FilePath is { } p) paths.Add(p);
                var (glyph, brush) = StateGlyph(doc);
                var target = doc;
                list.Add(new PaletteCandidate
                {
                    Title = doc.Title,
                    Detail = doc.FilePath is { } fp ? DisplayFolder(IOPath.GetDirectoryName(fp)) : Loc.T("pal.file.unsaved"),
                    MatchPath = doc.FilePath is not null,
                    Extra = doc.FilePath is { } full ? [full] : null,
                    Glyph = glyph,
                    GlyphBrush = brush,
                    Hint = doc.IsDirty ? Loc.T("pal.file.modified") : null,
                    Group = tabsLabel,
                    Id = DocKey(doc),
                    Order = order++,
                    Boost = 0.3,
                    Accept = () => ActivateDocument(target),
                });
            }
        }

        var recentLabel = Loc.T("pal.group.recentFiles");
        foreach (var path in RecentFiles.I.Items)
        {
            if (!paths.Add(path) || !LikelyExists(path)) continue;
            var p = path;
            list.Add(new PaletteCandidate
            {
                Title = IOPath.GetFileName(path),
                Detail = DisplayFolder(IOPath.GetDirectoryName(path)),
                MatchPath = true,
                Extra = [path],
                Glyph = Glyphs.History,
                Group = recentLabel,
                Id = "file:" + path,
                Order = order++,
                Accept = () => _w.OpenFile(p),
            });
        }
        return list;
    }

    internal static (string Glyph, string Brush) StateGlyph(Document doc)
    {
        if (doc.External != ExternalChange.None) return (Glyphs.Warning, "Brush.Warning");
        if (doc.IsDirty) return (Glyphs.Save, "Brush.AccentText"); // same as the tab: accent = unsaved, amber = changed on disk
        if (doc.IsUntitled) return (Glyphs.Page, "Brush.PanelTextDim");
        return (Glyphs.Save, "Brush.PanelTextDim");
    }

    /// <summary>
    /// A folder as shown in a row: the user profile becomes ~, and a long path keeps its root and the
    /// folders nearest to the file — C:\Users\me\src\app → ~\src\app, E:\a\b\c\d\e → E:\…\d\e.
    /// </summary>
    internal static string DisplayFolder(string? dir, int max = 58)
    {
        if (string.IsNullOrEmpty(dir)) return "";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 3 && dir.StartsWith(home, StringComparison.OrdinalIgnoreCase) && (dir.Length == home.Length || dir[home.Length] == '\\'))
            dir = "~" + dir[home.Length..];
        if (dir.Length <= max) return dir;

        var unc = dir.StartsWith(@"\\", StringComparison.Ordinal);
        var parts = dir.TrimStart('\\').Split('\\');
        var rootCount = unc ? Math.Min(2, parts.Length) : 1;
        var root = (unc ? @"\\" : "") + string.Join('\\', parts.Take(rootCount));
        var tail = new List<string>();
        var length = root.Length + 3;
        for (var i = parts.Length - 1; i >= rootCount; i--)
        {
            if (tail.Count > 0 && length + parts[i].Length + 1 > max) break;
            tail.Insert(0, parts[i]);
            length += parts[i].Length + 1;
        }
        return tail.Count >= parts.Length - rootCount ? dir : root + "\\…\\" + string.Join('\\', tail);
    }

    /// <summary>Recent files deleted meanwhile are left out (network paths are not probed — they can hang).</summary>
    private static bool LikelyExists(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try { return System.IO.File.Exists(path); }
        catch { return true; }
    }

    private void ActivateDocument(Document doc)
    {
        var group = _w.Model.Groups.FirstOrDefault(g => g.Tabs.Contains(doc));
        if (group is not null)
        {
            group.Active = doc;
            _w.Model.ActiveGroup = group;
            if (!(AppEnv.TestMode && !_w.IsActive)) Dispatcher.BeginInvoke(() => _w.ActiveView?.FocusContent(), DispatcherPriority.Input);
            return;
        }
        if (Workspace.I.Locate(doc) is not { } where) return;
        where.Group.Active = doc;
        where.Window.ActiveGroup = where.Group;
        var other = App.Current.WindowFor(where.Window);
        other?.BringToFront();
    }

    private static readonly Regex LineQuery = new(@"^\s*(\d{1,9})\s*(?:[:,.\s]\s*(\d{1,9})?)?\s*$", RegexOptions.CultureInvariant);

    private List<PaletteCandidate> BuildLine(string query)
    {
        var list = new List<PaletteCandidate>();
        var ed = _editor;
        if (ed?.Document is not { } doc) return list;
        var lines = doc.LineCount;
        if (string.IsNullOrWhiteSpace(query))
        {
            list.Add(new PaletteCandidate
            {
                Title = Loc.T("pal.line.prompt", _saved?.Line ?? 1, _saved?.Column ?? 1, lines),
                Glyph = Glyphs.Info,
                IsInfo = true,
            });
            return list;
        }
        var m = LineQuery.Match(query);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var line))
        {
            list.Add(new PaletteCandidate { Title = Loc.T("pal.line.invalid", lines), Glyph = Glyphs.Warning, GlyphBrush = "Brush.Warning", IsInfo = true });
            return list;
        }
        int? column = m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var c) ? Math.Max(1, c) : null;
        line = Math.Clamp(line, 1, lines);
        var text = doc.GetText(doc.GetLineByNumber(line)).Trim();
        if (text.Length > 90) text = text[..90] + "…";
        var col = column ?? 1;
        list.Add(new PaletteCandidate
        {
            Title = column is null ? Loc.T("pal.line.go", line) : Loc.T("pal.line.goCol", line, col),
            Detail = text,
            DetailMono = true,
            Hint = line == lines && lines > 1 ? Loc.T("pal.line.last") : null,
            Glyph = PaletteGlyphs.Line,
            GlyphBrush = "Brush.AccentText",
            Id = $"line:{line}:{col}",
            Accept = () => ed.GoTo(line, col),
            Preview = () => PreviewRange(ed, line, col, column is null ? 0 : 1),
        });
        return list;
    }

    private List<PaletteCandidate> BuildSymbols()
    {
        if (_editor?.Document is not { } doc || _editor.Model is not { } model) return new();
        var lang = model.LanguageId;
        if (!SymbolScanner.Supports(lang)) return new();
        var text = doc.Text;
        if (text.Length < LargeScan) return SymbolCandidates(SymbolScanner.Scan(text, lang));

        // a big file: scan off the UI thread, show "finding symbols…" meanwhile
        _scanning = true;
        var version = ++_scanVersion;
        Task.Run(() => SymbolScanner.Scan(text, lang)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (version != _scanVersion || IsClosing || _mode != PaletteMode.Symbols) return;
            _scanning = false;
            _source = t.IsCompletedSuccessfully ? SymbolCandidates(t.Result) : new();
            Refresh();
        }));
        return new();
    }

    private List<PaletteCandidate> SymbolCandidates(List<Symbol> symbols)
    {
        var ed = _editor!;
        var list = new List<PaletteCandidate>(symbols.Count);
        foreach (var s in symbols)
        {
            var (letter, brush, kindKey) = KindLook(s);
            var kind = Loc.T(kindKey);
            var sym = s;
            list.Add(new PaletteCandidate
            {
                Title = s.Name,
                Detail = s.Container is null ? kind : kind + " · " + s.Container,
                Letter = letter,
                LetterBrush = brush,
                Hint = s.Line.ToString(System.Globalization.CultureInfo.InvariantCulture),
                HintMono = true,
                Extra = s.Container is null ? null : [s.Container + " " + s.Name],
                Id = $"sym:{s.Name}:{s.Line}",
                Order = s.Line,
                Accept = () => ed.GoTo(sym.Line, sym.Column),
                Preview = () => PreviewRange(ed, sym.Line, sym.Column, sym.Length),
            });
        }
        return list;
    }

    private static (string Letter, string Brush, string KindKey) KindLook(Symbol s) => s.Kind switch
    {
        SymbolKind.Function => ("F", "Brush.Syntax.Function", "pal.sym.function"),
        SymbolKind.Method => ("M", "Brush.Syntax.Function", "pal.sym.method"),
        SymbolKind.Class => ("C", "Brush.Syntax.Type", "pal.sym.class"),
        SymbolKind.Interface => ("I", "Brush.Syntax.Type", "pal.sym.interface"),
        SymbolKind.Struct => ("S", "Brush.Syntax.Type", "pal.sym.struct"),
        SymbolKind.Enum => ("E", "Brush.Syntax.Constant", "pal.sym.enum"),
        SymbolKind.Namespace => ("N", "Brush.Syntax.Keyword", "pal.sym.namespace"),
        SymbolKind.Type => ("T", "Brush.Syntax.Type", "pal.sym.type"),
        SymbolKind.Heading => ("H" + Math.Clamp(s.Level, 1, 6), "Brush.Syntax.Heading", "pal.sym.heading"),
        SymbolKind.Section => ("§", "Brush.Syntax.Section", "pal.sym.section"),
        SymbolKind.Label => ("L", "Brush.Syntax.Label", "pal.sym.label"),
        SymbolKind.Table => ("T", "Brush.Syntax.Type", "pal.sym.table"),
        _ => ("K", "Brush.Syntax.Key", "pal.sym.key"),
    };

    private List<PaletteCandidate> BuildLanguages()
    {
        var list = new List<PaletteCandidate>();
        if (_w.ActiveDocument is not TextDocument doc) return list;
        list.Add(new PaletteCandidate
        {
            Title = Loc.T("pal.lang.auto"),
            Detail = Loc.T("pal.lang.autoDetail"),
            Glyph = Glyphs.Refresh,
            GlyphBrush = "Brush.AccentText",
            Id = "lang:auto",
            Order = 0,
            Accept = () => AutoDetectLanguage(doc),
        });
        var order = 1;
        var culture = Loc.I.Culture;
        foreach (var lang in Languages.List.OrderBy(l => l.Name, StringComparer.Create(culture, true)))
        {
            var current = lang.Id == doc.LanguageId;
            var id = lang.Id;
            var exts = string.Join("  ", lang.Extensions.Take(4));
            list.Add(new PaletteCandidate
            {
                Title = lang.Name,
                Detail = exts,
                Glyph = current ? Glyphs.Checkmark : "",
                GlyphBrush = "Brush.AccentText",
                Hint = current ? Loc.T("pal.lang.current") : null,
                Extra = [lang.Id, .. lang.Extensions],
                Id = "lang:" + id,
                Order = order++,
                Accept = () => SetLanguage(doc, id),
            });
        }
        return list;
    }

    private void SetLanguage(TextDocument doc, string id)
    {
        doc.LanguageExplicit = true;
        doc.LanguageId = id;
    }

    private void AutoDetectLanguage(TextDocument doc)
    {
        doc.LanguageExplicit = false;
        var byName = Languages.ForFileName(doc.NameForType);
        doc.LanguageId = byName == Languages.PlainText && doc.HasBuffer ? Languages.Detect(doc.Buffer.Text) : byName;
        _w.Toasts.Show(Loc.T("pal.lang.changed", Languages.Get(doc.LanguageId).Name), ToastKind.Info);
    }

    // ───────────── footer ─────────────

    private void BuildFooter()
    {
        _footer.Children.Clear();
        _footer.ColumnDefinitions.Clear();
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Hint(">", "pal.prefix.commands", _mode == PaletteMode.Commands));
        left.Children.Add(Hint(":", "pal.prefix.line", _mode == PaletteMode.GoToLine));
        left.Children.Add(Hint("@", "pal.prefix.symbols", _mode == PaletteMode.Symbols));
        _footer.Children.Add(left);

        var enter = _mode switch
        {
            PaletteMode.Commands => "pal.hint.run",
            PaletteMode.GoToLine or PaletteMode.Symbols => "pal.hint.go",
            PaletteMode.Language => "pal.hint.pick",
            _ => "pal.hint.open",
        };
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(Hint("↑↓", "pal.hint.select", false));
        right.Children.Add(Hint("Enter", enter, false));
        right.Children.Add(Hint("Esc", "pal.hint.close", false, last: true));
        Grid.SetColumn(right, 1);
        _footer.Children.Add(right);
    }

    private static FrameworkElement Hint(string key, string labelKey, bool active, bool last = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, last ? 0 : 14, 0), VerticalAlignment = VerticalAlignment.Center };
        var chip = PaletteRowView.KeyChip(key);
        chip.Margin = new Thickness(0, 0, 5, 0);
        chip.MinWidth = 20;
        if (chip.Child is TextBlock t) t.HorizontalAlignment = HorizontalAlignment.Center;
        if (active)
        {
            chip.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
            chip.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
            chip.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.AccentText");
        }
        row.Children.Add(chip);
        var label = new TextBlock { Text = Loc.T(labelKey), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, active ? "Brush.AccentText" : "Brush.PanelTextDim");
        row.Children.Add(label);
        return row;
    }
}

/// <summary>Segoe Fluent glyphs used only by the palette.</summary>
internal static class PaletteGlyphs
{
    public const string Command = "\uE756";   // CommandPrompt
    public const string Line = "\uE8FD";      // BulletedList
    public const string Symbol = "\uE943";    // Code
    public const string Language = "\uE8C1";  // Characters
}
