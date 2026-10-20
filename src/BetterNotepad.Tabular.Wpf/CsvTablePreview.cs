using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace BetterNotepad.Tabular.Wpf;

/// <summary>Why a cell could not be edited.</summary>
public enum CsvEditBlock
{
    /// <summary>The file is locked (read only).</summary>
    Locked,

    /// <summary>The record swallows the rest of the file through a quote that is never closed.</summary>
    UnclosedQuote,

    /// <summary>The text changed meanwhile; the table is being refreshed.</summary>
    Stale,
}

/// <summary>
/// CSV / TSV as a table next to (or instead of) the text: virtualized grid with sticky header and source line numbers, sampled
/// column widths, zebra rows, a slim bar with the filter (all columns or one), the detected dialect (separator, quote, header
/// row — each can be overridden), warnings for ragged rows or an unclosed quote and the lock state. Sorting and filtering change
/// only the view. Spreadsheet keys: arrows, Tab / Shift+Tab, Enter / F2 / double-click / typing to edit, Esc, Delete, Ctrl+C
/// (TSV), Ctrl+Z / Ctrl+Y (routed to the host's undo). The file text stays the source of truth: an edit is handed to the host
/// as <see cref="EditRequested"/> text edits, which it applies to its buffer as one undo step. Parsing, sorting and filtering
/// run off the UI thread. Source lines are 1-based.
/// </summary>
public sealed class CsvTablePreview : UserControl, IDisposable
{
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private const string GlyphWarning = "";
    private const string GlyphLock = "";
    private const string GlyphUnlock = "";
    private const string GlyphChevron = "";
    private const string GlyphClear = "";

    private readonly CsvPreviewOptions _options;
    private CsvPreviewLabels _l;
    private readonly Grid _root = new();
    private readonly CsvGrid _grid = new();
    private readonly ScrollBar _vbar = new() { Orientation = Orientation.Vertical };
    private readonly ScrollBar _hbar = new() { Orientation = Orientation.Horizontal };
    private readonly Canvas _overlay = new() { ClipToBounds = true };
    private readonly TextBox _editor = new();
    private readonly Border _editorFrame = new();
    private readonly TextBox _filter = new();
    private readonly TextBlock _filterHint = new();
    private readonly Grid _filterField = new();
    private readonly StackPanel _filterRight = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 4, 0) };
    private readonly Button _filterClear = new();
    private readonly Button _scopeChip = new();
    private readonly TextBlock _scopeText = new();
    private readonly TextBlock _status = new();
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _warningChip = new();
    private readonly TextBlock _warningText = new();
    private readonly Button _lockChip = new();
    private readonly TextBlock _lockIcon = new();
    private readonly TextBlock _lockText = new();
    private readonly Button _dialectChip = new();
    private readonly TextBlock _dialectSymbol = new();
    private readonly TextBlock _dialectName = new();
    private readonly Ellipse _dialectDot = new() { Width = 5, Height = 5, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _headerToggle = new();
    private readonly Grid _bar = new() { Height = 40 };

    private string _text = "";
    private string? _fileName;
    private bool _textDropped;
    private CsvTable? _table;
    private CsvView? _view;
    private CsvDialect? _detected;
    private CsvDialect? _dialect;
    private CsvDialectOverride _override = CsvDialectOverride.None;
    private int[] _rowLines = [];
    private CsvShape _shape = CsvShape.Empty;
    private CsvViewSpec _spec = new();
    private int _filterColumn = -1;
    private CancellationTokenSource? _parseCts;
    private CancellationTokenSource? _viewCts;
    private CancellationTokenSource? _shapeCts;
    private DispatcherTimer? _parseTimer;
    private DispatcherTimer? _filterTimer;
    private bool _dirty = true;
    private bool _disposed;
    private bool _syncing;
    private bool _readOnly;
    private bool _compact;
    private bool _settingFilter;
    private (int Row, int Col)? _editing;
    private int _resizeCol = -1;
    private double _resizeStartX;
    private double _resizeStartW;
    private bool _selecting;
    private bool _selectingRows;
    private (int Row, int Col) _dragStart;
    private int? _pendingReveal;
    private object? _tipKey;

    public CsvTablePreview(CsvPreviewOptions? options = null)
    {
        _options = options ?? new CsvPreviewOptions();
        _l = _options.Labels;
        _readOnly = _options.ReadOnly;
        _grid.MonospaceFont = _options.MonospaceFont;
        Focusable = true;
        IsTabStop = true;
        FocusVisualStyle = null;
        SetResourceReference(BackgroundProperty, "Brush.Editor.Bg");

        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var bar = BuildTopBar();
        Grid.SetColumnSpan(bar, 2);
        _root.Children.Add(bar);

        Grid.SetRow(_grid, 1);
        _root.Children.Add(_grid);
        Grid.SetRow(_overlay, 1);
        _root.Children.Add(_overlay);
        Grid.SetRow(_vbar, 1);
        Grid.SetColumn(_vbar, 1);
        _root.Children.Add(_vbar);
        Grid.SetRow(_hbar, 2);
        _root.Children.Add(_hbar);
        _vbar.Scroll += (_, _) => OnUserScroll(_hbar.Value, _vbar.Value);
        _hbar.Scroll += (_, _) => OnUserScroll(_hbar.Value, _vbar.Value);

        BuildCellEditor();

        Content = _root;
        _grid.MouseLeftButtonDown += OnGridDown;
        _grid.MouseMove += OnGridMove;
        _grid.MouseLeftButtonUp += OnGridUp;
        _grid.MouseLeave += OnGridLeave;
        _grid.MouseWheel += OnWheel;
        _grid.SizeChanged += (_, _) => UpdateScrollBars();
        _grid.ContextMenu = new ContextMenu();
        _grid.ContextMenuOpening += OnContextMenu;
        IsVisibleChanged += OnVisibleChanged;
        IsKeyboardFocusWithinChanged += OnFocusWithinChanged;
        TextInput += OnTypeToEdit;
        ApplyLabels();
    }

    // ───────────── public surface ─────────────

    /// <summary>"Show in text" (row number double-click, context menu): go to this source line in the editor.</summary>
    public event Action<int>? LineRequested;

    /// <summary>The user scrolled the table (file order, no filter): top source line and progress through it.</summary>
    public event Action<int, double>? Scrolled;

    /// <summary>
    /// Cell edits as text edits against <see cref="LastEditBase"/> (non-overlapping, any order). The host applies them to its
    /// buffer as one undo step (descending offsets); the table already shows the result, so the echo through <see cref="Update"/>
    /// costs nothing. After the event <see cref="Text"/> is the edited text.
    /// </summary>
    public event Action<IReadOnlyList<TextEdit>>? EditRequested;

    /// <summary>The user moved to a cell (click, keys): its range in the text (start, length), e.g. to move the editor caret.</summary>
    public event Action<int, int>? ActiveCellChanged;

    /// <summary>Ctrl+Z (false) / Ctrl+Y, Ctrl+Shift+Z (true) in the table: the host undoes / redoes in its buffer.</summary>
    public event Action<bool>? UndoRequested;

    /// <summary>An edit was refused (locked file, unclosed quote): the host may explain why.</summary>
    public event Action<CsvEditBlock>? EditBlocked;

    /// <summary>The lock chip was clicked.</summary>
    public event Action? UnlockRequested;

    /// <summary>The user changed the separator, quote or header choice (or went back to automatic).</summary>
    public event Action<CsvDialectOverride>? OverrideChanged;

    /// <summary>Cells were copied (number of cells).</summary>
    public event Action<int>? Copied;

    public CsvTable? Table => _table;

    public CsvView? View => _view;

    public CsvDialect? Dialect => _dialect;

    public CsvShape Shape => _shape;

    /// <summary>The text the table describes (or is about to, while a parse runs).</summary>
    public string Text => _text;

    /// <summary>The text the edits of the current <see cref="EditRequested"/> refer to.</summary>
    public string? LastEditBase { get; private set; }

    public CsvPreviewLabels Labels
    {
        get => _l;
        set
        {
            _l = value ?? new CsvPreviewLabels();
            ApplyLabels();
            _grid.ClearCache();
            _grid.InvalidateVisual();
        }
    }

    public bool IsReadOnly
    {
        get => _readOnly;
        set
        {
            if (_readOnly == value) return;
            _readOnly = value;
            if (value) CancelEdit();
            UpdateChips();
        }
    }

    /// <summary>Whether the first row holds column names (detected, switchable in the bar).</summary>
    public bool HasHeader
    {
        get => _dialect?.HasHeader ?? true;
        set
        {
            if (_dialect is not null && _dialect.HasHeader == value && _override.HasHeader == value) return;
            SetOverride(_override with { HasHeader = value });
        }
    }

    /// <summary>The user's choice over the detected dialect (per tab); setting it parses again.</summary>
    public CsvDialectOverride Override
    {
        get => _override;
        set
        {
            value ??= CsvDialectOverride.None;
            if (_override == value) return;
            _override = value;
            if (value.IsEmpty) _detected = null; // back to automatic: look at the text again
            Reparse();
        }
    }

    /// <summary>Cells in a monospace font (the editor's), e.g. for aligned codes and numbers.</summary>
    public bool Monospace
    {
        get => _grid.Monospace;
        set
        {
            if (_grid.Monospace == value) return;
            _grid.Monospace = value;
            _editor.FontFamily = value ? new FontFamily(_options.MonospaceFont) : CsvGrid.UiFace.FontFamily;
            if (_table is not null) Show(_table, _view!, remeasure: true);
        }
    }

    /// <summary>The parse / view work in flight (tests, render harness).</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>True while the table does not yet describe the latest text (a parse is scheduled or running).</summary>
    public bool IsRefreshing => _dirty || _table is null || !ReferenceEquals(_table.Text, _text);

    /// <summary>
    /// Sets the text. Parsed in the background after a short pause (at once with <paramref name="immediate"/>, or when nothing
    /// is shown yet); deferred while hidden. Another <paramref name="fileName"/> = another file: the dialect is detected again.
    /// </summary>
    public void Update(string text, string? fileName, bool immediate = false)
    {
        if (_disposed) return;
        text ??= "";
        var fileChanged = !string.Equals(fileName, _fileName, StringComparison.OrdinalIgnoreCase);
        if (!fileChanged && !_textDropped && (ReferenceEquals(text, _text) || text == _text))
        {
            if (immediate && _dirty && IsVisible) Pending = ParseAsync(keepWidths: true);
            return;
        }
        if (fileChanged) _detected = null;
        _text = text;
        _fileName = fileName;
        _textDropped = false;
        _dirty = true;
        if (!IsVisible) return;
        if (_table is null || immediate) Pending = ParseAsync(keepWidths: true);
        else Schedule(ref _parseTimer, _options.RefreshDelay + TimeSpan.FromMilliseconds(Math.Min(500, text.Length / 40000)), () => Pending = ParseAsync(keepWidths: true));
    }

    /// <summary>Editor → table sync (see IPreviewView.ScrollToLine). Only in file order (no sort / filter).</summary>
    public void ScrollToLine(int line, double lineFraction, double scrollFraction)
    {
        if (_table is null || _view is null || !_view.IsIdentity || _rowLines.Length == 0) return;
        double y;
        if (scrollFraction >= 0.999) y = double.MaxValue;
        else
        {
            var row = Array.BinarySearch(_rowLines, line);
            if (row < 0) row = ~row - 1;
            row = Math.Clamp(row, 0, _rowLines.Length - 1);
            var span = (row + 1 < _rowLines.Length ? _rowLines[row + 1] : _rowLines[row] + 1) - _rowLines[row];
            var f = Math.Clamp((line - _rowLines[row] + lineFraction) / Math.Max(1, span), 0, 1);
            var display = row - _table.HeaderRowCount;
            y = display < 0 ? 0 : (display + f) * CsvGrid.RowHeight;
        }
        _syncing = true;
        SetScroll(_grid.ScrollX, y);
        _syncing = false;
    }

    /// <summary>Selects the cell that holds text offset <paramref name="offset"/> (after an undo, a search…), once the table is current.</summary>
    public void SelectAtOffset(int offset)
    {
        if (IsRefreshing)
        {
            _pendingReveal = offset;
            return;
        }
        Reveal(offset);
    }

    /// <summary>Moves the keyboard focus into the filter box.</summary>
    public void FocusFilter()
    {
        CommitEdit(0, 0);
        _filter.Focus();
        _filter.SelectAll();
    }

    /// <summary>
    /// Keys a host window claims before the table sees them (F2 = rename, Ctrl+F = find there): call from the window's
    /// preview key handler while the focus is inside the table. Returns true when the table used the key.
    /// </summary>
    public bool HandleWindowKey(KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.F2 && mods == ModifierKeys.None)
        {
            if (_editing is not null) return true;
            if (_filter.IsKeyboardFocusWithin) return false;
            if (_grid.HasSelection) BeginEdit(_grid.Active.Row, _grid.Active.Col, null);
            else Select(0, 0, false);
            return true;
        }
        if (e.Key == Key.F && mods == ModifierKeys.Control)
        {
            FocusFilter();
            return true;
        }
        return false;
    }

    /// <summary>Frees the parsed table of a hidden tab (and with <paramref name="dropText"/> the text too); the next show parses again.</summary>
    public void ReleaseContent(bool dropText = false)
    {
        CancelEdit();
        _parseTimer?.Stop();
        _parseCts?.Cancel();
        _viewCts?.Cancel();
        _shapeCts?.Cancel();
        _table = null;
        _view = null;
        _rowLines = [];
        _shape = CsvShape.Empty;
        _grid.Table = null;
        _grid.View = null;
        _grid.Lines = [];
        _grid.InvalidateVisual();
        if (dropText)
        {
            _text = "";
            _textDropped = true;
        }
        _dirty = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        ReleaseContent(dropText: true);
        _parseTimer?.Stop();
        _filterTimer?.Stop();
        _disposed = true;
        IsVisibleChanged -= OnVisibleChanged;
        IsKeyboardFocusWithinChanged -= OnFocusWithinChanged;
    }

    // ───────────── parsing / view ─────────────

    private static void Schedule(ref DispatcherTimer? timer, TimeSpan delay, Action action)
    {
        timer ??= new DispatcherTimer(DispatcherPriority.Background);
        timer.Stop();
        var t = timer;
        t.Interval = delay;
        t.Tick -= Fire;
        t.Tag = action;
        t.Tick += Fire;
        t.Start();

        static void Fire(object? s, EventArgs e)
        {
            var t = (DispatcherTimer)s!;
            t.Stop();
            (t.Tag as Action)?.Invoke();
        }
    }

    private sealed record ParseResult(CsvTable Table, int[] Lines, CsvView View, CsvDialect Detected, CsvDialect Dialect, CsvShape Shape);

    private void Reparse(bool remeasure = true)
    {
        _dirty = true;
        if (IsVisible && !_textDropped) Pending = ParseAsync(keepWidths: !remeasure);
    }

    private async Task ParseAsync(bool keepWidths)
    {
        _parseTimer?.Stop();
        _parseCts?.Cancel();
        var cts = _parseCts = new CancellationTokenSource();
        var text = _text;
        var fileName = _fileName;
        var detected = _detected is { LooksTabular: true } ? _detected : null; // keep a confident detection: typing must not flip it
        var ov = _override;
        var spec = _spec;
        _dirty = false;
        ParseResult r;
        try
        {
            r = await Task.Run(() =>
            {
                var det = detected ?? CsvDialect.Detect(text, fileName);
                var d = ov.ApplyTo(det);
                var t = CsvTable.Parse(text, d, fileName, cts.Token);
                var shape = t.AnalyzeShape(cts.Token); // settles ColumnCount off the UI thread too
                var lines = LineIndex(t, cts.Token);
                var v = CsvView.Create(t, ClampSpec(spec, t), cts.Token);
                return new ParseResult(t, lines, v, det, d, shape);
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // a broken dialect choice or an engine fault: keep what is shown, never take the app down
            return;
        }
        if (cts != _parseCts || _disposed || _textDropped) return;
        if (!ReferenceEquals(r.Table.Text, _text)) return; // an edit or a newer text arrived meanwhile
        _detected = r.Detected;
        _dialect = r.Dialect;
        _rowLines = r.Lines;
        _shape = r.Shape;
        Show(r.Table, r.View, remeasure: !keepWidths || _grid.Widths.Length != r.Table.ColumnCount);
        if (_pendingReveal is { } offset)
        {
            _pendingReveal = null;
            Reveal(offset);
        }
    }

    private static CsvViewSpec ClampSpec(CsvViewSpec spec, CsvTable t)
    {
        if (spec.SortColumn >= t.ColumnCount) spec = spec with { SortColumn = -1 };
        if (spec.Filters.Any(f => f.Column >= t.ColumnCount)) spec = spec with { Filters = spec.Filters.Select(f => f.Column >= t.ColumnCount ? f with { Column = -1 } : f).ToList() };
        return spec;
    }

    /// <summary>1-based source line of every record start (records may span lines inside quotes).</summary>
    private static int[] LineIndex(CsvTable t, CancellationToken ct)
    {
        var lines = new int[t.RowCount];
        var text = t.Text.AsSpan();
        var line = 1;
        var pos = 0;
        for (var r = 0; r < t.RowCount; r++)
        {
            if ((r & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            var start = t.RowStart(r);
            if (start > pos) line += CountBreaks(text[pos..start]);
            pos = Math.Max(pos, start);
            lines[r] = line;
        }
        return lines;
    }

    /// <summary>Line breaks the way the editor counts them: CRLF, LF and a lone CR.</summary>
    private static int CountBreaks(ReadOnlySpan<char> s)
    {
        var n = s.Count('\n');
        if (s.IndexOf('\r') < 0) return n;
        for (var i = 0; i < s.Length; i++)
            if (s[i] == '\r' && (i + 1 >= s.Length || s[i + 1] != '\n')) n++;
        return n;
    }

    private void Show(CsvTable table, CsvView view, bool remeasure)
    {
        _table = table;
        _view = view;
        _grid.Table = table;
        _grid.View = view;
        _grid.Lines = _rowLines;
        _grid.ExpectedColumns = _shape.ExpectedColumns;
        _grid.UpdateGutter();
        if (remeasure || _grid.Widths.Length != table.ColumnCount)
        {
            var (w, n) = CsvGrid.Measure(table, _grid.CellFace);
            _grid.Widths = w;
            _grid.Numeric = n;
        }
        _grid.SortColumn = view.Spec.SortColumn;
        _grid.SortDescending = view.Spec.SortDescending;
        var filter = view.Spec.Filters.FirstOrDefault(f => !string.IsNullOrEmpty(f.Text));
        _grid.Highlight = filter.Text;
        _grid.HighlightColumn = filter.Column;
        ClampSelection();
        UpdateStatus();
        UpdateChips();
        UpdateScrollBars();
        PlaceEditor();
        _grid.InvalidateVisual();
    }

    private void ApplySpec(CsvViewSpec spec, bool resetScroll)
    {
        _spec = spec;
        if (_table is null) return;
        _viewCts?.Cancel();
        var cts = _viewCts = new CancellationTokenSource();
        var table = _table;
        var activeSource = _view is not null && _grid.HasSelection && _grid.Active.Row < _view.RowCount ? _view.SourceRow(_grid.Active.Row) : -1;
        if (spec.SortColumn >= 0 && table.DataRowCount > 20000) _status.Text = _l.Sorting;
        Pending = Run();

        async Task Run()
        {
            CsvView v;
            try
            {
                v = await Task.Run(() => CsvView.Create(table, ClampSpec(spec, table), cts.Token), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (cts != _viewCts || table != _table || _disposed) return;
            Show(table, v, remeasure: false);
            // the selection stays on the same record wherever it went
            var at = activeSource >= 0 ? v.DisplayIndexOf(activeSource) : -1;
            if (at >= 0)
            {
                _grid.Active = (at, _grid.Active.Col);
                _grid.Anchor = _grid.Active;
            }
            else _grid.HasSelection = false;
            if (resetScroll)
            {
                SetScroll(_grid.ScrollX, 0);
                if (at >= 0) EnsureVisible(at, _grid.Active.Col);
            }
            _grid.InvalidateVisual();
        }
    }

    private void UpdateStatus()
    {
        if (_table is null || _view is null)
        {
            _status.Text = "";
            return;
        }
        var rows = _view.RowCount == _table.DataRowCount && _spec.Filters.Count == 0 ? _l.RowsText(_table.DataRowCount) : _l.FilteredText(_view.RowCount, _table.DataRowCount);
        _status.Text = rows + "  ·  " + _l.ColumnsText(_table.ColumnCount);
    }

    // ───────────── scrolling ─────────────

    private void UpdateScrollBars()
    {
        var maxY = Math.Max(0, _grid.ContentHeight - _grid.ViewportHeight + 8);
        var maxX = Math.Max(0, _grid.ContentWidth - _grid.ViewportWidth + 8);
        _vbar.Maximum = maxY;
        _vbar.ViewportSize = Math.Max(1, _grid.ViewportHeight);
        _vbar.LargeChange = Math.Max(1, _grid.ViewportHeight - CsvGrid.RowHeight);
        _vbar.SmallChange = CsvGrid.RowHeight;
        _vbar.Visibility = maxY > 0 ? Visibility.Visible : Visibility.Collapsed;
        _hbar.Maximum = maxX;
        _hbar.ViewportSize = Math.Max(1, _grid.ViewportWidth);
        _hbar.LargeChange = Math.Max(1, _grid.ViewportWidth * 0.8);
        _hbar.SmallChange = 40;
        _hbar.Visibility = maxX > 0 ? Visibility.Visible : Visibility.Collapsed;
        SetScroll(_grid.ScrollX, _grid.ScrollY);
    }

    private void SetScroll(double x, double y)
    {
        x = Math.Clamp(x, 0, _hbar.Maximum);
        y = Math.Clamp(y, 0, _vbar.Maximum);
        var changed = x != _grid.ScrollX || y != _grid.ScrollY;
        _grid.ScrollX = x;
        _grid.ScrollY = y;
        _hbar.Value = x;
        _vbar.Value = y;
        if (!changed) return;
        PlaceEditor();
        _grid.InvalidateVisual();
    }

    private void OnUserScroll(double x, double y)
    {
        var before = _grid.ScrollY;
        SetScroll(x, y);
        if (!_syncing && Math.Abs(before - _grid.ScrollY) > 0.1) ReportScroll();
    }

    private void ReportScroll()
    {
        if (_table is null || _view is null || !_view.IsIdentity || _rowLines.Length == 0 || Scrolled is null) return;
        var pos = _grid.ScrollY / CsvGrid.RowHeight;
        var display = (int)Math.Floor(pos);
        var frac = pos - display;
        var row = Math.Clamp(display + _table.HeaderRowCount, 0, _rowLines.Length - 1);
        if (display == 0 && _grid.ScrollY < 0.5 && _table.HeaderRowCount > 0) row = 0;
        var span = (row + 1 < _rowLines.Length ? _rowLines[row + 1] : _rowLines[row] + 1) - _rowLines[row];
        var x = _rowLines[row] + frac * span;
        Scrolled.Invoke((int)Math.Floor(x), x - Math.Floor(x));
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var dy = -e.Delta / 120.0 * CsvGrid.RowHeight * 3;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) OnUserScroll(_grid.ScrollX + dy * 2, _grid.ScrollY);
        else OnUserScroll(_grid.ScrollX, _grid.ScrollY + dy);
        e.Handled = true;
    }

    private void EnsureVisible(int row, int col, bool vertical = true)
    {
        var r = _grid.CellRect(row, col);
        double x = _grid.ScrollX, y = _grid.ScrollY;
        if (vertical)
        {
            if (r.Top < CsvGrid.HeaderHeight) y -= CsvGrid.HeaderHeight - r.Top;
            else if (r.Bottom > _grid.ActualHeight) y += r.Bottom - _grid.ActualHeight;
        }
        if (r.Left < _grid.GutterWidth) x -= _grid.GutterWidth - r.Left;
        else if (r.Right > _grid.ActualWidth) x += Math.Min(r.Right - _grid.ActualWidth, r.Left - _grid.GutterWidth);
        OnUserScroll(x, y);
    }

    // ───────────── mouse ─────────────

    private void OnGridDown(object sender, MouseButtonEventArgs e)
    {
        if (_editing is not null) CommitEdit(0, 0);
        Focus();
        var p = e.GetPosition(_grid);
        var (zone, row, col) = _grid.Hit(p);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (zone)
        {
            case CsvGrid.Zone.HeaderEdge when e.ClickCount == 2 && _table is not null:
                var fitted = (double[])_grid.Widths.Clone();
                fitted[col] = CsvGrid.FitColumn(_table, col, _grid.CellFace);
                _grid.Widths = fitted;
                UpdateScrollBars();
                PlaceEditor();
                _grid.InvalidateVisual();
                break;
            case CsvGrid.Zone.HeaderEdge:
                _resizeCol = col;
                _resizeStartX = p.X;
                _resizeStartW = _grid.Widths[col];
                _grid.CaptureMouse();
                break;
            case CsvGrid.Zone.Header when shift || ctrl:
                SelectColumn(col, shift);
                break;
            case CsvGrid.Zone.Header:
                // asc → desc → file order
                var spec = _spec.SortColumn != col ? _spec with { SortColumn = col, SortDescending = false }
                    : !_spec.SortDescending ? _spec with { SortDescending = true }
                    : _spec with { SortColumn = -1, SortDescending = false };
                ApplySpec(spec, resetScroll: true);
                break;
            case CsvGrid.Zone.Gutter:
                if (e.ClickCount == 2)
                {
                    RequestLine(row);
                    break;
                }
                Select(row, 0, shift, wholeRow: true);
                _dragStart = _grid.Active;
                _selectingRows = true;
                _grid.CaptureMouse();
                break;
            case CsvGrid.Zone.Cell:
                if (e.ClickCount == 2)
                {
                    _selecting = false;
                    _grid.ReleaseMouseCapture();
                    BeginEdit(row, col, null);
                    break;
                }
                Select(row, col, shift);
                _dragStart = _grid.Active;
                _selecting = true;
                _grid.CaptureMouse();
                break;
            case CsvGrid.Zone.Corner:
                SelectAll();
                break;
        }
        e.Handled = true;
    }

    private void OnGridMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_grid);
        if (_resizeCol >= 0)
        {
            var widths = (double[])_grid.Widths.Clone();
            widths[_resizeCol] = Math.Max(40, _resizeStartW + p.X - _resizeStartX);
            _grid.Widths = widths;
            UpdateScrollBars();
            PlaceEditor();
            _grid.InvalidateVisual();
            return;
        }
        if ((_selecting || _selectingRows) && e.LeftButton == MouseButtonState.Pressed && _view is { RowCount: > 0 })
        {
            // drag selection, scrolling one row at a time past the edges
            var row = Math.Clamp((int)Math.Floor((p.Y - CsvGrid.HeaderHeight + _grid.ScrollY) / CsvGrid.RowHeight), 0, _view.RowCount - 1);
            var col = _selectingRows ? Math.Max(0, _grid.ColumnCount - 1) : ColumnAt(p.X);
            if ((row, col) != _grid.Active)
            {
                _grid.Active = (row, col);
                if (p.Y < CsvGrid.HeaderHeight || p.Y > _grid.ActualHeight || p.X < _grid.GutterWidth || p.X > _grid.ActualWidth) EnsureVisible(row, col, vertical: true);
                _grid.InvalidateVisual();
            }
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed) return;
        var (zone, hitRow, hitCol) = _grid.Hit(p);
        var hoverRow = zone is CsvGrid.Zone.Cell or CsvGrid.Zone.Gutter ? hitRow : -1;
        var hoverHeader = zone == CsvGrid.Zone.Header ? hitCol : -1;
        if (hoverRow != _grid.HoverRow || hoverHeader != _grid.HoverHeader)
        {
            _grid.HoverRow = hoverRow;
            _grid.HoverHeader = hoverHeader;
            _grid.InvalidateVisual();
        }
        _grid.Cursor = zone == CsvGrid.Zone.HeaderEdge ? Cursors.SizeWE : zone == CsvGrid.Zone.Header ? Cursors.Hand : null;
        UpdateTip(zone, hitRow, hitCol);
    }

    private int ColumnAt(double x)
    {
        var left = _grid.GutterWidth - _grid.ScrollX;
        for (var i = 0; i < _grid.Widths.Length; i++)
        {
            left += _grid.Widths[i];
            if (x < left) return i;
        }
        return Math.Max(0, _grid.Widths.Length - 1);
    }

    private void OnGridUp(object sender, MouseButtonEventArgs e)
    {
        var moved = (_selecting || _selectingRows) && _grid.Active != _dragStart;
        _resizeCol = -1;
        _selecting = false;
        _selectingRows = false;
        _grid.ReleaseMouseCapture();
        if (moved) RaiseActiveCell();
    }

    private void OnGridLeave(object sender, MouseEventArgs e)
    {
        if (_grid.HoverRow < 0 && _grid.HoverHeader < 0) return;
        _grid.HoverRow = -1;
        _grid.HoverHeader = -1;
        _grid.InvalidateVisual();
    }

    /// <summary>Header: full name + how sorting works; row number: the line (and a ragged row's cell count); a cut value: all of it.</summary>
    private void UpdateTip(CsvGrid.Zone zone, int row, int col)
    {
        object? key = zone switch
        {
            CsvGrid.Zone.Header => ("h", col),
            CsvGrid.Zone.Gutter => ("g", row),
            CsvGrid.Zone.Cell => ("c", row, col),
            _ => null,
        };
        if (Equals(key, _tipKey)) return;
        _tipKey = key;
        string? tip = null;
        if (_table is not null && _view is not null)
        {
            switch (zone)
            {
                case CsvGrid.Zone.Header:
                    var headers = _table.GetHeaders();
                    var title = _table.HeaderRowCount > 0 && col < headers.Length ? headers[col] : CsvTable.ColumnLetters(col);
                    tip = (title.Length > 0 ? Shorten(title, 200) + "\n" : "") + _l.SortTip;
                    break;
                case CsvGrid.Zone.Gutter when row < _view.RowCount:
                    var src = _view.SourceRow(row);
                    var line = src < _rowLines.Length ? _rowLines[src] : src + 1;
                    tip = string.Format(_l.Culture, _l.LineTip, line);
                    var cells = _table.RowEnd(src) == _table.RowStart(src) ? 0 : _table.GetFieldCount(src);
                    if (_shape.ExpectedColumns > 0 && cells > 0 && cells != _shape.ExpectedColumns)
                        tip += "\n" + string.Format(_l.Culture, _l.RowCellsTip, cells, _shape.ExpectedColumns);
                    break;
                case CsvGrid.Zone.Cell when row < _view.RowCount:
                    var value = _table.GetCell(_view.SourceRow(row), col);
                    if (_grid.IsClipped(value, col)) tip = Shorten(value, 2000);
                    break;
            }
        }
        _grid.ToolTip = null; // close the old one: a tooltip does not follow its content
        if (tip is not null) _grid.ToolTip = tip;
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private void RequestLine(int display)
    {
        if (_view is null || display < 0 || display >= _view.RowCount || _rowLines.Length == 0) return;
        var src = _view.SourceRow(display);
        if (src < _rowLines.Length) LineRequested?.Invoke(_rowLines[src]);
    }

    // ───────────── selection / keyboard ─────────────

    private void Select(int row, int col, bool extend, bool wholeRow = false, bool raise = true)
    {
        if (_view is null || _view.RowCount == 0) return;
        row = Math.Clamp(row, 0, _view.RowCount - 1);
        col = Math.Clamp(col, 0, Math.Max(0, _grid.ColumnCount - 1));
        if (!extend || !_grid.HasSelection) _grid.Anchor = wholeRow ? (row, 0) : (row, col);
        _grid.Active = wholeRow ? (row, Math.Max(0, _grid.ColumnCount - 1)) : (row, col);
        _grid.HasSelection = true;
        _grid.InvalidateVisual();
        if (raise) RaiseActiveCell();
    }

    private void SelectColumn(int col, bool extend)
    {
        if (_view is null || _view.RowCount == 0) return;
        var anchorCol = extend && _grid.HasSelection ? _grid.Anchor.Col : col;
        _grid.Anchor = (0, anchorCol);
        _grid.Active = (_view.RowCount - 1, col);
        _grid.HasSelection = true;
        _grid.InvalidateVisual();
    }

    private void SelectAll()
    {
        if (_view is null || _view.RowCount == 0) return;
        _grid.Anchor = (0, 0);
        _grid.Active = (_view.RowCount - 1, Math.Max(0, _grid.ColumnCount - 1));
        _grid.HasSelection = true;
        _grid.InvalidateVisual();
    }

    private void ClampSelection()
    {
        if (_view is null || _view.RowCount == 0)
        {
            _grid.HasSelection = false;
            return;
        }
        var maxR = _view.RowCount - 1;
        var maxC = Math.Max(0, _grid.ColumnCount - 1);
        _grid.Anchor = (Math.Min(_grid.Anchor.Row, maxR), Math.Min(_grid.Anchor.Col, maxC));
        _grid.Active = (Math.Min(_grid.Active.Row, maxR), Math.Min(_grid.Active.Col, maxC));
    }

    private void RaiseActiveCell()
    {
        if (_table is null || _view is null || !_grid.HasSelection || ActiveCellChanged is null) return;
        var (r, c) = _grid.Active;
        if (r >= _view.RowCount) return;
        var src = _view.SourceRow(r);
        if (_table.TryGetField(src, c, out var f)) ActiveCellChanged(f.Start, f.Length);
        else ActiveCellChanged(_table.RowEnd(src), 0);
    }

    private void Reveal(int offset)
    {
        if (_table is null || _view is null || !_table.TryFindCell(Math.Clamp(offset, 0, _table.Text.Length), out var row, out var col)) return;
        var d = _view.DisplayIndexOf(row);
        if (d < 0) return; // the header, or filtered out
        Select(d, col, false);
        EnsureVisible(d, col);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _editing is not null || e.OriginalSource is TextBox || _view is null) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (alt) return;
        var (r, c) = _grid.HasSelection ? _grid.Active : (0, 0);
        var page = Math.Max(1, (int)(_grid.ViewportHeight / CsvGrid.RowHeight) - 1);
        var lastRow = _view.RowCount - 1;
        var lastCol = Math.Max(0, _grid.ColumnCount - 1);
        (int, int)? move = e.Key switch
        {
            Key.Up => ctrl ? (0, c) : (r - 1, c),
            Key.Down => ctrl ? (lastRow, c) : (r + 1, c),
            Key.Left => ctrl ? (r, 0) : (r, c - 1),
            Key.Right => ctrl ? (r, lastCol) : (r, c + 1),
            Key.PageUp => (r - page, c),
            Key.PageDown => (r + page, c),
            Key.Home => ctrl ? (0, 0) : (r, 0),
            Key.End => ctrl ? (lastRow, lastCol) : (r, lastCol),
            Key.Tab when !ctrl => shift ? (r, c - 1) : (r, c + 1),
            Key.Enter when shift && !ctrl => (r - 1, c),
            _ => null,
        };
        if (move is { } m)
        {
            Select(m.Item1, m.Item2, shift && e.Key is not (Key.Tab or Key.Enter));
            EnsureVisible(_grid.Active.Row, _grid.Active.Col);
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Enter when !ctrl:
            case Key.F2 when !ctrl && !shift:
                if (_grid.HasSelection) BeginEdit(r, c, null);
                else Select(0, 0, false);
                e.Handled = true;
                break;
            case Key.C when ctrl && !shift:
                CopySelection();
                e.Handled = true;
                break;
            case Key.A when ctrl && !shift:
                SelectAll();
                e.Handled = true;
                break;
            case Key.Z when ctrl:
                UndoRequested?.Invoke(shift);
                e.Handled = true;
                break;
            case Key.Y when ctrl && !shift:
                UndoRequested?.Invoke(true);
                e.Handled = true;
                break;
            case Key.F when ctrl && !shift:
                FocusFilter();
                e.Handled = true;
                break;
            case Key.Space when shift && !ctrl:
                Select(r, 0, false, wholeRow: true);
                e.Handled = true;
                break;
            case Key.Space when ctrl && !shift:
                SelectColumn(c, false);
                e.Handled = true;
                break;
            case Key.Delete when _grid.HasSelection && !ctrl:
                ClearSelection();
                e.Handled = true;
                break;
            case Key.Back when _grid.HasSelection && !ctrl:
                BeginEdit(r, c, "");
                e.Handled = true;
                break;
            case Key.Escape when !string.IsNullOrEmpty(_filter.Text):
                _filter.Text = "";
                e.Handled = true;
                break;
            case Key.Escape when _grid.HasSelection && (_grid.Anchor != _grid.Active):
                _grid.Anchor = _grid.Active;
                _grid.InvalidateVisual();
                e.Handled = true;
                break;
        }
    }

    private void OnTypeToEdit(object sender, TextCompositionEventArgs e)
    {
        if (_editing is not null || e.OriginalSource is TextBox || !_grid.HasSelection || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        BeginEdit(_grid.Active.Row, _grid.Active.Col, e.Text);
        e.Handled = true;
    }

    private void OnFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var focused = IsKeyboardFocusWithin;
        if (_grid.IsActiveFocus == focused) return;
        _grid.IsActiveFocus = focused;
        _grid.InvalidateVisual();
    }

    /// <summary>The selected block as TSV (values with tabs or line breaks are quoted), handed to the clipboard.</summary>
    internal string? CopySelection()
    {
        if (_table is null || _view is null || !_grid.HasSelection) return null;
        var (r0, r1) = (Math.Min(_grid.Anchor.Row, _grid.Active.Row), Math.Max(_grid.Anchor.Row, _grid.Active.Row));
        var (c0, c1) = (Math.Min(_grid.Anchor.Col, _grid.Active.Col), Math.Max(_grid.Anchor.Col, _grid.Active.Col));
        var sb = new StringBuilder();
        for (var r = r0; r <= r1; r++)
        {
            var src = _view.SourceRow(r);
            for (var c = c0; c <= c1; c++)
            {
                if (c > c0) sb.Append('\t');
                var v = _table.GetCell(src, c);
                if (v.AsSpan().IndexOfAny('\t', '\n', '\r') >= 0) v = "\"" + v.Replace("\"", "\"\"") + "\"";
                sb.Append(v);
            }
            if (r < r1 || r1 > r0) sb.Append("\r\n");
        }
        var text = sb.ToString();
        var ok = _options.SetClipboard?.Invoke(text) ?? TrySetClipboard(text);
        if (ok) Copied?.Invoke((r1 - r0 + 1) * (c1 - c0 + 1));
        return text;
    }

    private static bool TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false; // clipboard busy
        }
    }

    private void ClearSelection()
    {
        if (_table is null || _view is null) return;
        if (_readOnly)
        {
            EditBlocked?.Invoke(CsvEditBlock.Locked);
            return;
        }
        var (r0, r1) = (Math.Min(_grid.Anchor.Row, _grid.Active.Row), Math.Max(_grid.Anchor.Row, _grid.Active.Row));
        var (c0, c1) = (Math.Min(_grid.Anchor.Col, _grid.Active.Col), Math.Max(_grid.Anchor.Col, _grid.Active.Col));
        if ((long)(r1 - r0 + 1) * (c1 - c0 + 1) > 200_000) return;
        var edits = new List<TextEdit>();
        var unclosed = _table.HasUnterminatedQuote ? _table.RowCount - 1 : -1;
        for (var r = r0; r <= r1; r++)
        {
            var src = _view.SourceRow(r);
            if (src == unclosed) continue;
            var fields = _table.GetFieldCount(src);
            for (var c = c0; c <= c1 && c < fields; c++)
                if (_table.GetCellRaw(src, c).Length > 0 && _table.GetCell(src, c).Length > 0) edits.Add(_table.SetCell(src, c, ""));
        }
        Emit(edits);
    }

    // ───────────── editing ─────────────

    private void BuildCellEditor()
    {
        _editor.Template = (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TextBox">
  <ScrollViewer x:Name="PART_ContentHost" VerticalAlignment="Center" Focusable="False"
                HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden"/>
</ControlTemplate>
""");
        _editor.BorderThickness = new Thickness(0);
        _editor.Padding = new Thickness(7, 0, 7, 0); // a text box applies its padding inside the content host: no margin in the template
        _editor.Background = System.Windows.Media.Brushes.Transparent;
        _editor.VerticalContentAlignment = VerticalAlignment.Center;
        _editor.FontFamily = CsvGrid.UiFace.FontFamily;
        _editor.FontSize = 12.5;
        _editor.SetResourceReference(TextBox.ForegroundProperty, "Brush.Editor.Text");
        _editor.SetResourceReference(TextBox.CaretBrushProperty, "Brush.AccentText");
        _editor.SetResourceReference(TextBox.SelectionBrushProperty, "Brush.Accent");
        _editor.SelectionOpacity = 0.4;
        _editor.PreviewKeyDown += OnEditorKey;
        _editor.TextChanged += (_, _) => PlaceEditor();
        _editor.LostKeyboardFocus += (_, _) => CommitEdit(0, 0);
        _editorFrame.Child = _editor;
        _editorFrame.BorderThickness = new Thickness(1.5);
        _editorFrame.CornerRadius = new CornerRadius(4);
        _editorFrame.Visibility = Visibility.Collapsed;
        _editorFrame.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
        _editorFrame.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        _editorFrame.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.28 };
        _overlay.Children.Add(_editorFrame);
    }

    private void BeginEdit(int row, int col, string? typed)
    {
        if (_table is null || _view is null || row < 0 || row >= _view.RowCount || col < 0 || col >= _grid.ColumnCount) return;
        if (_readOnly)
        {
            Select(row, col, false);
            EditBlocked?.Invoke(CsvEditBlock.Locked);
            return;
        }
        var src = _view.SourceRow(row);
        if (_table.HasUnterminatedQuote && src == _table.RowCount - 1)
        {
            Select(row, col, false);
            EditBlocked?.Invoke(CsvEditBlock.UnclosedQuote);
            return;
        }
        if (IsRefreshing && _table is not null && !ReferenceEquals(_table.Text, _text))
        {
            // the text changed (typed in the editor) and the table is a moment behind: do not edit an old copy
            Pending = ParseAsync(keepWidths: true);
            EditBlocked?.Invoke(CsvEditBlock.Stale);
            return;
        }
        Select(row, col, false);
        EnsureVisible(row, col);
        _editing = (row, col);
        _editor.Text = typed ?? (col < _table!.GetFieldCount(src) ? _table.GetCell(src, col) : "");
        _editor.TextAlignment = _grid.Numeric.ElementAtOrDefault(col) && typed is null ? TextAlignment.Right : TextAlignment.Left;
        _editorFrame.Visibility = Visibility.Visible;
        PlaceEditor();
        if (Animate) Fade(_editorFrame, 90);
        _editor.Focus();
        if (typed is null) _editor.SelectAll();
        else _editor.CaretIndex = _editor.Text.Length;
    }

    private bool Animate => _options.AnimationsEnabled?.Invoke() ?? true;

    private void PlaceEditor()
    {
        if (_editing is not { } at) return;
        var r = _grid.CellRect(at.Row, at.Col);
        var lines = 1;
        foreach (var ch in _editor.Text) if (ch == '\n') lines++;
        lines = Math.Min(lines, 8);
        var textWidth = 0.0;
        if (_editor.Text.Length is > 0 and < 2000)
            foreach (var line in _editor.Text.Split('\n')) textWidth = Math.Max(textWidth, TextMeasure.For(_grid.CellFace, 12.5).Width(line));
        var right = _editor.TextAlignment == TextAlignment.Right;
        var room = right ? r.Right - _grid.GutterWidth + 1 : _grid.ActualWidth - r.X - 6;
        var width = Math.Min(Math.Max(r.Width + 2, room), Math.Max(Math.Max(r.Width + 2, 96), textWidth + 30));
        _editorFrame.Width = width;
        _editorFrame.Height = Math.Max(r.Height + 2, lines * 17.5 + 10);
        Canvas.SetLeft(_editorFrame, right ? r.Right + 1 - width : r.X - 1);
        Canvas.SetTop(_editorFrame, r.Y - 1);
    }

    private void OnEditorKey(object sender, KeyEventArgs e)
    {
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Escape:
                CancelEdit();
                e.Handled = true;
                break;
            case Key.System when e.SystemKey == Key.Enter:
            case Key.Enter when Keyboard.Modifiers.HasFlag(ModifierKeys.Alt):
                // Alt+Enter: a line break inside the cell (written with the file's own line ending)
                var i = _editor.CaretIndex;
                var selected = _editor.SelectionLength;
                _editor.Text = _editor.Text.Remove(_editor.SelectionStart, selected).Insert(_editor.SelectionStart, "\n");
                _editor.CaretIndex = i - (selected > 0 && _editor.SelectionStart < i ? selected : 0) + 1;
                e.Handled = true;
                break;
            case Key.Enter:
                CommitEdit(shift ? -1 : 1, 0);
                e.Handled = true;
                break;
            case Key.Tab:
                CommitEdit(0, shift ? -1 : 1);
                e.Handled = true;
                break;
            case Key.Up when _editor.Text.IndexOf('\n') < 0:
                CommitEdit(-1, 0);
                e.Handled = true;
                break;
            case Key.Down when _editor.Text.IndexOf('\n') < 0:
                CommitEdit(1, 0);
                e.Handled = true;
                break;
        }
    }

    private void CommitEdit(int dRow, int dCol)
    {
        if (_editing is not { } at) return;
        _editing = null;
        var value = _editor.Text;
        HideEditor();
        if (_table is not null && _view is not null && at.Row < _view.RowCount)
        {
            var src = _view.SourceRow(at.Row);
            var old = at.Col < _table.GetFieldCount(src) ? _table.GetCell(src, at.Col) : "";
            if (value != old)
            {
                value = CsvWriter.NormalizeLineBreaks(value, _table.Dialect.LineEnding); // typed breaks the way the file writes them
                if (value != old) Emit([_table.SetCell(src, at.Col, value)]);
            }
        }
        if (dRow != 0 || dCol != 0)
        {
            Select(at.Row + dRow, at.Col + dCol, false);
            EnsureVisible(_grid.Active.Row, _grid.Active.Col);
        }
        if (IsVisible) Focus();
    }

    private void CancelEdit()
    {
        if (_editing is null) return;
        _editing = null;
        HideEditor();
        if (IsVisible) Focus();
    }

    private void HideEditor() => _editorFrame.Visibility = Visibility.Collapsed;

    /// <summary>Hands edits to the host and shows them right away (the table takes them in; the echo through Update is free).</summary>
    private void Emit(IReadOnlyList<TextEdit> edits)
    {
        var real = edits.Where(e => !e.IsNoOp).ToList();
        if (real.Count == 0 || _table is null || _view is null) return;
        if (!ReferenceEquals(_table.Text, _text) && _table.Text != _text)
        {
            // edits computed on an older copy of the text: never send them; show the current text instead
            Pending = ParseAsync(keepWidths: true);
            EditBlocked?.Invoke(CsvEditBlock.Stale);
            return;
        }
        _parseTimer?.Stop();
        _parseCts?.Cancel(); // nothing older may land after this
        var before = _table;
        CsvTable table;
        if (real.Count <= 8) table = before.WithEdits(real);
        else table = CsvTable.Parse(TextEdit.ApplyAll(before.Text, real), before.Dialect); // many cells: one parse beats many shifts
        _text = table.Text;
        _dirty = false;
        _rowLines = LineIndex(table, CancellationToken.None);
        // the edited rows stay where they are (no re-sort, no re-filter) until the user sorts or filters again
        var view = _view.Retain(table);
        Show(table, view, remeasure: false);
        RefreshShapeLater(table);
        LastEditBase = before.Text;
        try
        {
            EditRequested?.Invoke(real);
        }
        finally
        {
            LastEditBase = null;
        }
    }

    private void RefreshShapeLater(CsvTable table)
    {
        _shapeCts?.Cancel();
        var cts = _shapeCts = new CancellationTokenSource();
        _ = Task.Run(() => table.AnalyzeShape(cts.Token), cts.Token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && cts == _shapeCts && ReferenceEquals(table, _table) && !_disposed)
            {
                _shape = t.Result;
                _grid.ExpectedColumns = _shape.ExpectedColumns;
                UpdateChips();
                _grid.InvalidateVisual();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ───────────── chrome: filter, status, dialect, header, warnings, lock ─────────────

    private const string ChipTemplate = """
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ButtonBase">
  <Grid x:Name="Root" SnapsToDevicePixels="True">
    <Border CornerRadius="8" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"/>
    <Border x:Name="Hv" CornerRadius="8" Background="Transparent"/>
    <ContentPresenter Margin="{TemplateBinding Padding}" VerticalAlignment="Center" HorizontalAlignment="Center"/>
  </Grid>
  <ControlTemplate.Triggers>
    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Hv" Property="Background" Value="{DynamicResource Brush.TabHover}"/></Trigger>
    <Trigger Property="IsPressed" Value="True"><Setter TargetName="Root" Property="Opacity" Value="0.82"/></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""";

    private static ControlTemplate? _chipTemplate;

    private static ControlTemplate Chip => _chipTemplate ??= (ControlTemplate)XamlReader.Parse(ChipTemplate);

    private static TextBlock Glyph(string glyph, double size, string brush)
    {
        var t = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily(IconFont),
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        TextOptions.SetTextRenderingMode(t, TextRenderingMode.Grayscale);
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    private static void StyleChip(ButtonBase b, bool bordered = true)
    {
        b.Template = Chip;
        b.Focusable = false;
        b.Cursor = Cursors.Hand;
        b.Height = 28;
        b.Padding = new Thickness(10, 0, 10, 0);
        b.Margin = new Thickness(6, 0, 0, 0);
        b.FontSize = 12;
        b.Background = System.Windows.Media.Brushes.Transparent;
        b.BorderThickness = new Thickness(bordered ? 1 : 0);
        if (bordered) b.SetResourceReference(Control.BorderBrushProperty, "Brush.Border");
        ToolTipService.SetInitialShowDelay(b, 450);
    }

    private FrameworkElement BuildTopBar()
    {
        _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        BuildFilterField();
        _bar.Children.Add(_filterField);

        _status.FontSize = 12;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(6, 0, 8, 0);
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        Grid.SetColumn(_status, 1);
        _bar.Children.Add(_status);

        // warning: ragged rows / an unclosed quote — calm, clickable (goes to the place)
        StyleChip(_warningChip, bordered: false);
        _warningChip.SetResourceReference(Control.BackgroundProperty, "Brush.WarningSoft");
        var warn = new StackPanel { Orientation = Orientation.Horizontal };
        warn.Children.Add(Glyph(GlyphWarning, 12.5, "Brush.Warning"));
        _warningText.Margin = new Thickness(7, 0, 0, 1);
        _warningText.VerticalAlignment = VerticalAlignment.Center;
        _warningText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        warn.Children.Add(_warningText);
        _warningChip.Content = warn;
        _warningChip.Visibility = Visibility.Collapsed;
        _warningChip.Click += (_, _) => GoToWarning();
        _chips.Children.Add(_warningChip);

        // lock: only while the file is locked; click = unlock (like the padlock elsewhere in the app)
        StyleChip(_lockChip);
        var lk = new StackPanel { Orientation = Orientation.Horizontal };
        _lockIcon.Text = GlyphLock;
        _lockIcon.FontFamily = new FontFamily(IconFont);
        _lockIcon.FontSize = 12;
        _lockIcon.VerticalAlignment = VerticalAlignment.Center;
        _lockIcon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Warning");
        lk.Children.Add(_lockIcon);
        _lockText.Margin = new Thickness(7, 0, 0, 1);
        _lockText.VerticalAlignment = VerticalAlignment.Center;
        _lockText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        lk.Children.Add(_lockText);
        _lockChip.Content = lk;
        _lockChip.MouseEnter += (_, _) => _lockIcon.Text = GlyphUnlock;
        _lockChip.MouseLeave += (_, _) => _lockIcon.Text = GlyphLock;
        _lockChip.Click += (_, _) => UnlockRequested?.Invoke();
        _chips.Children.Add(_lockChip);

        // dialect: separator symbol in a key cap + its name; the menu also holds the quote and "detect again"
        StyleChip(_dialectChip);
        _dialectChip.Padding = new Thickness(5, 0, 9, 0);
        var dl = new StackPanel { Orientation = Orientation.Horizontal };
        var cap = new Border { CornerRadius = new CornerRadius(5), MinWidth = 20, Height = 20, Padding = new Thickness(5, 0, 5, 0), BorderThickness = new Thickness(1) };
        cap.SetResourceReference(Border.BackgroundProperty, "Brush.BgAlt");
        cap.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        _dialectSymbol.FontFamily = new FontFamily(_options.MonospaceFont);
        _dialectSymbol.FontSize = 12.5;
        _dialectSymbol.FontWeight = FontWeights.SemiBold;
        _dialectSymbol.HorizontalAlignment = HorizontalAlignment.Center;
        _dialectSymbol.VerticalAlignment = VerticalAlignment.Center;
        _dialectSymbol.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
        cap.Child = _dialectSymbol;
        dl.Children.Add(cap);
        _dialectName.Margin = new Thickness(8, 0, 0, 1);
        _dialectName.VerticalAlignment = VerticalAlignment.Center;
        _dialectName.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        dl.Children.Add(_dialectName);
        _dialectDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Brush.Accent");
        dl.Children.Add(_dialectDot);
        var chevron = Glyph(GlyphChevron, 8.5, "Brush.TextFaint");
        chevron.Margin = new Thickness(8, 1, 0, 0);
        dl.Children.Add(chevron);
        _dialectChip.Content = dl;
        _dialectChip.Click += (_, _) => OpenDialectMenu();
        _chips.Children.Add(_dialectChip);

        // header-row chip: the detected setting, one click to flip
        _headerToggle.Focusable = false;
        _headerToggle.Cursor = Cursors.Hand;
        _headerToggle.FontSize = 12;
        _headerToggle.Height = 28;
        _headerToggle.Margin = new Thickness(6, 0, 0, 0);
        _headerToggle.VerticalAlignment = VerticalAlignment.Center;
        _headerToggle.Template = (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ToggleButton">
  <Grid SnapsToDevicePixels="True">
    <Border x:Name="Bd" CornerRadius="8" Background="Transparent" BorderThickness="1" BorderBrush="{DynamicResource Brush.Border}"/>
    <Border x:Name="Hv" CornerRadius="8" Background="Transparent"/>
    <StackPanel Orientation="Horizontal" Margin="10,0,11,0" VerticalAlignment="Center">
      <Path x:Name="Tick" Data="M1 5 L4 8 L10 1.5" Width="11" Height="10" Margin="0,0,7,0" StrokeThickness="1.6" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
            StrokeLineJoin="Round" Stroke="{DynamicResource Brush.AccentText}" VerticalAlignment="Center" Visibility="Collapsed"/>
      <ContentPresenter x:Name="Label" VerticalAlignment="Center" TextElement.Foreground="{DynamicResource Brush.TextDim}"/>
    </StackPanel>
  </Grid>
  <ControlTemplate.Triggers>
    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Hv" Property="Background" Value="{DynamicResource Brush.TabHover}"/></Trigger>
    <Trigger Property="IsChecked" Value="True">
      <Setter TargetName="Bd" Property="Background" Value="{DynamicResource Brush.AccentSoft}"/>
      <Setter TargetName="Bd" Property="BorderBrush" Value="Transparent"/>
      <Setter TargetName="Tick" Property="Visibility" Value="Visible"/>
      <Setter TargetName="Label" Property="TextElement.Foreground" Value="{DynamicResource Brush.Text}"/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""");
        _headerToggle.Click += (_, _) => HasHeader = _headerToggle.IsChecked == true;
        ToolTipService.SetInitialShowDelay(_headerToggle, 450);
        _chips.Children.Add(_headerToggle);

        _chips.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(_chips, 2);
        _bar.Children.Add(_chips);

        var host = new Border { Child = _bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        host.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        host.SetResourceReference(Border.BackgroundProperty, "Brush.Editor.Bg");
        return host;
    }

    private void BuildFilterField()
    {
        var glass = new Path
        {
            Data = Geometry.Parse("M6.5 2 A4.5 4.5 0 1 0 6.5 11 A4.5 4.5 0 1 0 6.5 2 Z M9.8 9.8 L13.5 13.5"),
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 15,
            Height = 15,
            Margin = new Thickness(11, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        glass.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Brush.TextFaint");
        _filter.Template = (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TextBox">
  <Border x:Name="Bd" CornerRadius="9" Background="{DynamicResource Brush.BgAlt}" BorderBrush="{DynamicResource Brush.Border}" BorderThickness="1">
    <ScrollViewer x:Name="PART_ContentHost" VerticalAlignment="Center" Focusable="False"
                  HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden"/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource Brush.BorderStrong}"/></Trigger>
    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource Brush.Accent}"/></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""");
        _filter.Padding = new Thickness(31, 0, 120, 0);
        _filter.Height = 28;
        _filter.FontFamily = CsvGrid.UiFace.FontFamily;
        _filter.FontSize = 12.5;
        _filter.VerticalContentAlignment = VerticalAlignment.Center;
        _filter.SetResourceReference(TextBox.ForegroundProperty, "Brush.Text");
        _filter.SetResourceReference(TextBox.CaretBrushProperty, "Brush.Text");
        _filter.SetResourceReference(TextBox.SelectionBrushProperty, "Brush.Accent");
        _filter.SelectionOpacity = 0.45;
        _filter.TextChanged += (_, _) =>
        {
            _filterHint.Visibility = _filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _filterClear.Visibility = _filter.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (_settingFilter) return;
            Schedule(ref _filterTimer, TimeSpan.FromMilliseconds(_table is { DataRowCount: > 50000 } ? 260 : 160), ApplyFilter);
        };
        _filter.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _filter.Text.Length > 0)
            {
                _filter.Text = "";
                e.Handled = true;
            }
            else if (e.Key == Key.Escape || e.Key is Key.Down or Key.Enter)
            {
                Focus();
                if (!_grid.HasSelection) Select(0, 0, false);
                e.Handled = true;
            }
        };
        _filterHint.FontSize = 12.5;
        _filterHint.Margin = new Thickness(33, 0, 0, 1);
        _filterHint.VerticalAlignment = VerticalAlignment.Center;
        _filterHint.IsHitTestVisible = false;
        _filterHint.TextTrimming = TextTrimming.CharacterEllipsis;
        _filterHint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");

        // right end of the field: clear (while there is text) and where to look (all columns / one)
        StyleChip(_filterClear, bordered: false);
        _filterClear.Height = 22;
        _filterClear.Width = 22;
        _filterClear.Padding = new Thickness(0);
        _filterClear.Margin = new Thickness(0, 0, 2, 0);
        _filterClear.Content = Glyph(GlyphClear, 9, "Brush.TextDim");
        _filterClear.Visibility = Visibility.Collapsed;
        _filterClear.Click += (_, _) =>
        {
            _filter.Text = "";
            ApplyFilter();
        };
        _filterRight.Children.Add(_filterClear);

        StyleChip(_scopeChip, bordered: false);
        _scopeChip.Height = 22;
        _scopeChip.Padding = new Thickness(7, 0, 6, 0);
        _scopeChip.Margin = new Thickness(0);
        var scope = new StackPanel { Orientation = Orientation.Horizontal };
        _scopeText.FontSize = 11.5;
        _scopeText.MaxWidth = 120;
        _scopeText.TextTrimming = TextTrimming.CharacterEllipsis;
        _scopeText.VerticalAlignment = VerticalAlignment.Center;
        _scopeText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        scope.Children.Add(_scopeText);
        var chev = Glyph(GlyphChevron, 7.5, "Brush.TextFaint");
        chev.Margin = new Thickness(5, 1, 0, 0);
        scope.Children.Add(chev);
        _scopeChip.Content = scope;
        _scopeChip.Click += (_, _) => OpenScopeMenu();
        _filterRight.Children.Add(_scopeChip);
        _filterRight.VerticalAlignment = VerticalAlignment.Center;
        _filterRight.SizeChanged += (_, _) => _filter.Padding = new Thickness(31, 0, _filterRight.ActualWidth + 8, 0);

        _filterField.Children.Add(_filter);
        _filterField.Children.Add(glass);
        _filterField.Children.Add(_filterHint);
        _filterField.Children.Add(_filterRight);
        _filterField.Margin = new Thickness(10, 6, 6, 6);
        _filterField.Width = 340;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (!double.IsInfinity(constraint.Width)) LayoutBar(constraint.Width);
        return base.MeasureOverride(constraint);
    }

    /// <summary>
    /// The filter takes up to 340 px and the counts what is left; below ~620 px the chips show only their symbols, below ~460 px
    /// the filter drops its column chooser. Done from the measure pass: the width offered there is the real one, while an
    /// overflowing bar would report its own wider size.
    /// </summary>
    private void LayoutBar(double width)
    {
        if (width <= 0) return;
        var compact = width < 620;
        if (compact != _compact)
        {
            _compact = compact;
            var v = compact ? Visibility.Collapsed : Visibility.Visible;
            _dialectName.Visibility = v;
            _warningText.Visibility = v;
            _lockText.Visibility = v;
            _warningChip.Padding = compact ? new Thickness(8, 0, 8, 0) : new Thickness(10, 0, 10, 0);
            _lockChip.Padding = _warningChip.Padding;
            UpdateChips();
        }
        var scope = width < 460 ? Visibility.Collapsed : Visibility.Visible;
        if (_scopeChip.Visibility != scope) _scopeChip.Visibility = scope;
        _chips.Measure(new Size(double.PositiveInfinity, 40));
        var chips = _chips.DesiredSize.Width + _chips.Margin.Right;
        var room = width - chips - 18 - (compact ? 0 : 150);
        var field = Math.Clamp(room, 120, 340);
        if (Math.Abs(_filterField.Width - field) > 0.5) _filterField.Width = field;
        var status = width - field - chips > 170 ? Visibility.Visible : Visibility.Collapsed;
        if (_status.Visibility != status) _status.Visibility = status;
    }

    private void ApplyFilter()
    {
        var text = _filter.Text;
        _grid.Highlight = text;
        _grid.HighlightColumn = _filterColumn;
        ApplySpec(_spec with { Filters = text.Length == 0 ? [] : [new CsvColumnFilter(_filterColumn, text)] }, resetScroll: true);
    }

    private void SetFilterColumn(int col)
    {
        _filterColumn = col;
        UpdateScope();
        if (_filter.Text.Length > 0) ApplyFilter();
    }

    private void UpdateScope()
    {
        string name;
        if (_filterColumn < 0 || _table is null) name = _l.FilterAllColumns;
        else
        {
            var headers = _table.GetHeaders();
            name = _table.HeaderRowCount > 0 && _filterColumn < headers.Length && headers[_filterColumn].Trim().Length > 0 ? headers[_filterColumn] : CsvTable.ColumnLetters(_filterColumn);
        }
        _scopeText.Text = CsvGrid.Display(name);
        _scopeChip.ToolTip = _l.FilterScopeTip;
        if (_filterColumn >= 0) _scopeText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
        else _scopeText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
    }

    private void OpenScopeMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _scopeChip, Placement = PlacementMode.Bottom };
        var all = new MenuItem { Header = _l.FilterAllColumns, IsCheckable = true, IsChecked = _filterColumn < 0 };
        all.Click += (_, _) => SetFilterColumn(-1);
        menu.Items.Add(all);
        if (_table is not null && _table.ColumnCount > 0)
        {
            menu.Items.Add(new Separator());
            var headers = _table.GetHeaders();
            var count = Math.Min(_table.ColumnCount, 40);
            for (var c = 0; c < count; c++)
            {
                var col = c;
                var title = _table.HeaderRowCount > 0 && headers[c].Trim().Length > 0 ? Shorten(CsvGrid.Display(headers[c]), 60) : CsvTable.ColumnLetters(c);
                var item = new MenuItem { Header = title, IsCheckable = true, IsChecked = _filterColumn == c, InputGestureText = _table.HeaderRowCount > 0 ? CsvTable.ColumnLetters(c) : "" };
                item.Click += (_, _) => SetFilterColumn(col);
                menu.Items.Add(item);
            }
        }
        menu.IsOpen = true;
    }

    /// <summary>The separator / quote menu (for tests and snapshots too).</summary>
    internal ContextMenu BuildDialectMenu()
    {
        var d = _dialect ?? CsvDialect.Default;
        var menu = new ContextMenu { PlacementTarget = _dialectChip, Placement = PlacementMode.Bottom };
        foreach (var (sep, symbol) in new[] { (',', ","), (';', ";"), ('\t', "⇥"), ('|', "|") })
        {
            var s = sep;
            var item = new MenuItem { Header = KeyCapHeader(symbol, _l.DelimiterName(sep)), IsCheckable = true, IsChecked = d.Delimiter == sep };
            item.Click += (_, _) => SetOverride(_override with { Delimiter = s });
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        foreach (var (quote, name) in new[] { ('"', _l.DoubleQuote), ('\'', _l.SingleQuote) })
        {
            var q = quote;
            var item = new MenuItem { Header = KeyCapHeader(quote.ToString(), name), IsCheckable = true, IsChecked = d.Quote == quote };
            item.Click += (_, _) => SetOverride(_override with { Quote = q });
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var auto = new MenuItem { Header = _l.DetectAgain, IsEnabled = !_override.IsEmpty };
        auto.Click += (_, _) => SetOverride(CsvDialectOverride.None);
        menu.Items.Add(auto);
        return menu;
    }

    private void OpenDialectMenu() => BuildDialectMenu().IsOpen = true;

    /// <summary>A menu header: the character in a small key cap (like the chip in the bar), then its name.</summary>
    private FrameworkElement KeyCapHeader(string symbol, string name)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var cap = new Border { CornerRadius = new CornerRadius(4), Width = 20, Height = 19, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 0) };
        cap.SetResourceReference(Border.BorderBrushProperty, "Brush.PanelBorder");
        cap.SetResourceReference(Border.BackgroundProperty, "Brush.PanelHover");
        var t = new TextBlock
        {
            Text = symbol,
            FontFamily = new FontFamily(_options.MonospaceFont),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
        cap.Child = t;
        row.Children.Add(cap);
        row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private void SetOverride(CsvDialectOverride o)
    {
        Override = o;
        UpdateChips();
        OverrideChanged?.Invoke(o);
    }

    private void UpdateChips()
    {
        var d = _dialect ?? _detected;
        _dialectChip.Visibility = d is null ? Visibility.Hidden : Visibility.Visible;
        if (d is not null)
        {
            _dialectSymbol.Text = d.Delimiter == '\t' ? "⇥" : d.Delimiter.ToString();
            var name = _l.DelimiterName(d.Delimiter);
            _dialectName.Text = name;
            var chosen = _override.Delimiter is not null || _override.Quote is not null;
            _dialectDot.Visibility = chosen ? Visibility.Visible : Visibility.Collapsed;
            var quote = d.Quote == '\'' ? _l.SingleQuote : _l.DoubleQuote;
            _dialectChip.ToolTip = string.Format(_l.Culture, _l.DialectTip, name.ToLower(_l.Culture), quote.ToLower(_l.Culture), chosen ? _l.ChosenByYou : _l.Detected);
            _headerToggle.IsChecked = d.HasHeader;
        }
        _headerToggle.Visibility = d is null ? Visibility.Hidden : Visibility.Visible;
        _lockChip.Visibility = _readOnly ? Visibility.Visible : Visibility.Collapsed;

        string? warning = null, tip = null;
        if (_table?.HasUnterminatedQuote == true)
        {
            warning = string.Format(_l.Culture, _l.UnclosedQuote, _rowLines.Length > 0 ? _rowLines[^1] : 1);
            tip = _l.UnclosedQuoteTip;
        }
        else if (_shape.IsRagged)
        {
            warning = _l.RaggedText(_shape.RaggedCount);
            tip = string.Format(_l.Culture, _l.RaggedTip, _shape.ExpectedColumns);
        }
        var was = _warningChip.Visibility == Visibility.Visible;
        _warningText.Text = warning ?? "";
        _warningChip.ToolTip = warning is null ? null : (_compact ? warning + "\n" : "") + tip;
        _warningChip.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;
        if (warning is not null && !was && Animate) Fade(_warningChip, 180);
        UpdateScope();
    }

    /// <summary>Warning chip: to the unclosed quote, or to the next ragged row after the selection (wrapping around).</summary>
    private void GoToWarning()
    {
        if (_table is null || _view is null) return;
        Focus();
        if (_table.HasUnterminatedQuote)
        {
            var d = _view.DisplayIndexOf(_table.RowCount - 1);
            if (d >= 0)
            {
                Select(d, 0, false);
                EnsureVisible(d, 0);
            }
            return;
        }
        var rows = _shape.RaggedRows;
        if (rows.Count == 0) return;
        var current = _grid.HasSelection && _grid.Active.Row < _view.RowCount ? _view.SourceRow(_grid.Active.Row) : -1;
        var start = 0;
        while (start < rows.Count && rows[start] <= current) start++;
        for (var i = 0; i < rows.Count; i++)
        {
            var src = rows[(start + i) % rows.Count];
            var d = _view.DisplayIndexOf(src);
            if (d < 0) continue;
            var fields = _table.GetFieldCount(src);
            var col = Math.Min(Math.Max(0, fields - 1), Math.Max(0, _grid.ColumnCount - 1));
            Select(d, col, false);
            EnsureVisible(d, col);
            return;
        }
    }

    private void ApplyLabels()
    {
        _filterHint.Text = _l.FilterPlaceholder;
        _filterClear.ToolTip = _l.FilterClear;
        _headerToggle.Content = _l.HeaderRow;
        _headerToggle.ToolTip = _l.HeaderRowTip;
        _lockText.Text = _l.ReadOnly;
        _lockChip.ToolTip = _l.ReadOnlyTip;
        _grid.EmptyText = _l.Empty;
        _grid.EmptyHint = _l.EmptyHint;
        _grid.NoMatchesText = _l.NoMatches;
        UpdateStatus();
        UpdateChips();
    }

    private static void Fade(UIElement element, int ms)
    {
        var a = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        a.Completed += (_, _) => element.BeginAnimation(OpacityProperty, null);
        element.BeginAnimation(OpacityProperty, a);
    }

    private void OnContextMenu(object sender, ContextMenuEventArgs e)
    {
        if (_view is null || _table is null)
        {
            e.Handled = true;
            return;
        }
        var (zone, row, col) = _grid.Hit(Mouse.GetPosition(_grid));
        if (zone == CsvGrid.Zone.Cell && !_grid.IsSelected(row, col)) Select(row, col, false);
        if (zone == CsvGrid.Zone.Gutter && !(_grid.HasSelection && row >= Math.Min(_grid.Anchor.Row, _grid.Active.Row) && row <= Math.Max(_grid.Anchor.Row, _grid.Active.Row)))
            Select(row, 0, false, wholeRow: true);
        var m = _grid.ContextMenu!;
        m.Items.Clear();
        MenuItem Item(string header, string gesture, Action click, bool enabled = true)
        {
            var i = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
            i.Click += (_, _) => click();
            return i;
        }
        var active = _grid.Active;
        var has = _grid.HasSelection;
        m.Items.Add(Item(_l.Copy, "Ctrl+C", () => CopySelection(), has));
        m.Items.Add(Item(_l.EditCell, "Enter", () => BeginEdit(active.Row, active.Col, null), !_readOnly && has));
        m.Items.Add(Item(_l.ClearCells, "Delete", ClearSelection, !_readOnly && has));
        var line = has && active.Row < _view.RowCount && _view.SourceRow(active.Row) < _rowLines.Length ? _rowLines[_view.SourceRow(active.Row)] : 0;
        m.Items.Add(Item(_l.ShowInText, line > 0 ? line.ToString(_l.Culture) : "", () => RequestLine(active.Row), has));
        m.Items.Add(new Separator());
        var c = zone is CsvGrid.Zone.Cell or CsvGrid.Zone.Header ? col : active.Col;
        m.Items.Add(Item(_l.SortAscending, "", () => ApplySpec(_spec with { SortColumn = c, SortDescending = false }, resetScroll: true)));
        m.Items.Add(Item(_l.SortDescending, "", () => ApplySpec(_spec with { SortColumn = c, SortDescending = true }, resetScroll: true)));
        m.Items.Add(Item(_l.ClearSort, "", () => ApplySpec(_spec with { SortColumn = -1 }, resetScroll: false), _spec.SortColumn >= 0));
        m.Items.Add(Item(_l.FilterColumn, "", () =>
        {
            SetFilterColumn(c);
            FocusFilter();
        }));
        m.Items.Add(new Separator());
        var mono = new MenuItem { Header = _l.Monospace, IsCheckable = true, IsChecked = Monospace };
        mono.Click += (_, _) => Monospace = mono.IsChecked;
        m.Items.Add(mono);
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (IsVisible)
        {
            RefreshFieldText();
            if (_dirty && !_textDropped) Pending = ParseAsync(keepWidths: true);
            return;
        }
        CommitEdit(0, 0);
        if (_parseTimer?.IsEnabled == true)
        {
            _parseTimer.Stop();
            _dirty = true;
        }
        _filterTimer?.Stop();
        _tipKey = null;
        _grid.ToolTip = null;
    }

    /// <summary>A text box hidden while the theme changed paints its text with the old brush until the text is set again.</summary>
    private void RefreshFieldText()
    {
        if (_filter.Text.Length == 0) return;
        var (text, start, length) = (_filter.Text, _filter.SelectionStart, _filter.SelectionLength);
        _settingFilter = true;
        _filter.Text = "";
        _filter.Text = text;
        _settingFilter = false;
        _filter.Select(start, length);
    }

    // ───────────── test hooks ─────────────

    internal void SelectForTest(int row, int col, int toRow, int toCol)
    {
        Select(row, col, false);
        Select(toRow, toCol, true);
    }

    internal void EditForTest(int row, int col, string value)
    {
        BeginEdit(row, col, null);
        if (_editing is null) return;
        _editor.Text = value;
        CommitEdit(1, 0);
    }

    internal void BeginEditForTest(int row, int col) => BeginEdit(row, col, null);

    internal void SetEditorTextForTest(string text) => _editor.Text = text;

    internal void CancelEditForTest() => CancelEdit();

    internal bool IsEditing => _editing is not null;

    internal void SortForTest(int col, bool descending) => ApplySpec(_spec with { SortColumn = col, SortDescending = descending }, resetScroll: true);

    internal void FilterForTest(string text) => _filter.Text = text;

    internal void FilterColumnForTest(int col) => SetFilterColumn(col);

    internal void FlushFilterForTest()
    {
        _filterTimer?.Stop();
        ApplyFilter();
    }

    internal void ClearForTest() => ClearSelection();

    internal void KeyForTest(Key key, ModifierKeys mods = ModifierKeys.None)
    {
        // the paths the keys take, without a keyboard device
        switch (key)
        {
            case Key.Z when mods == ModifierKeys.Control: UndoRequested?.Invoke(false); break;
            case Key.Y when mods == ModifierKeys.Control: UndoRequested?.Invoke(true); break;
        }
    }

    internal void GoToWarningForTest() => GoToWarning();

    internal (int Row, int Col) ActiveForTest => _grid.Active;

    internal bool HasSelectionForTest => _grid.HasSelection;

    internal double ScrollYForTest => _grid.ScrollY;

    internal void ScrollByUser(double y) => OnUserScroll(_grid.ScrollX, y);

    internal void ScrollToCellForTest(int row, int col) => EnsureVisible(row, col);

    internal string StatusForTest => _status.Text;

    internal string WarningForTest => _warningChip.Visibility == Visibility.Visible ? _warningText.Text : "";

    internal bool LockChipVisibleForTest => _lockChip.Visibility == Visibility.Visible;

    internal FrameworkElement DialectChipForTest => _dialectChip;

    internal void HoverForTest(int row, int header)
    {
        _grid.HoverRow = row;
        _grid.HoverHeader = header;
        _grid.InvalidateVisual();
    }
}
