using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace BetterNotepad.Tabular.Wpf;

/// <summary>
/// CSV / TSV as a table next to (or instead of) the text: virtualized grid with sticky header, sampled column widths, zebra rows,
/// sort by a header click and a filter box (both change only the view), selection + copy as TSV, inline cell editing
/// (double-click / F2 / typing; Enter, Tab move on). The file text stays the source of truth: an edit is handed to the app as
/// <see cref="EditRequested"/> text edits, which it applies to the editor buffer (one undo step) and feeds back via
/// <see cref="Update"/>. Parsing, sorting and filtering run off the UI thread. Source lines are 1-based.
/// </summary>
public sealed class CsvTablePreview : UserControl, IDisposable
{
    private readonly CsvPreviewOptions _options;
    private readonly CsvPreviewLabels _l;
    private readonly Grid _root = new();
    private readonly CsvGrid _grid = new();
    private readonly ScrollBar _vbar = new() { Orientation = Orientation.Vertical };
    private readonly ScrollBar _hbar = new() { Orientation = Orientation.Horizontal };
    private readonly Canvas _overlay = new() { ClipToBounds = true };
    private readonly TextBox _editor = new();
    private readonly TextBox _filter = new();
    private readonly TextBlock _filterHint = new();
    private readonly TextBlock _status = new();
    private readonly ToggleButton _headerToggle = new();

    private string _text = "";
    private string? _fileName;
    private CsvTable? _table;
    private CsvView? _view;
    private CsvDialect? _dialect;
    private int[] _rowLines = [];
    private CsvViewSpec _spec = new();
    private CancellationTokenSource? _parseCts;
    private CancellationTokenSource? _viewCts;
    private DispatcherTimer? _parseTimer;
    private DispatcherTimer? _filterTimer;
    private bool _dirty = true;
    private bool _disposed;
    private bool _syncing;
    private (int Row, int Col)? _editing;
    private string? _pendingText;
    private int _resizeCol = -1;
    private double _resizeStartX;
    private double _resizeStartW;
    private bool _selecting;
    private bool _readOnly;

    public CsvTablePreview(CsvPreviewOptions? options = null)
    {
        _options = options ?? new CsvPreviewOptions();
        _l = _options.Labels;
        _readOnly = _options.ReadOnly;
        Focusable = true;
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

        // inline cell editor
        _editor.BorderThickness = new Thickness(0);
        _editor.Padding = new Thickness(7, 0, 7, 0);
        _editor.VerticalContentAlignment = VerticalAlignment.Center;
        _editor.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        _editor.FontSize = 12.5;
        _editor.Visibility = Visibility.Collapsed;
        _editor.SetResourceReference(TextBox.BackgroundProperty, "Brush.Surface");
        _editor.SetResourceReference(TextBox.ForegroundProperty, "Brush.Editor.Text");
        _editor.SetResourceReference(TextBox.CaretBrushProperty, "Brush.AccentText");
        _editor.SetResourceReference(TextBox.SelectionBrushProperty, "Brush.Accent");
        _editor.SelectionOpacity = 0.4;
        _editor.PreviewKeyDown += OnEditorKey;
        _editor.LostKeyboardFocus += (_, _) => CommitEdit(0, 0);
        var frame = new Border { Child = _editor, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(3), Visibility = Visibility.Collapsed };
        frame.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
        frame.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = 0.25 };
        _overlay.Children.Add(frame);

        Content = _root;
        _grid.MouseLeftButtonDown += OnGridDown;
        _grid.MouseMove += OnGridMove;
        _grid.MouseLeftButtonUp += OnGridUp;
        _grid.MouseWheel += OnWheel;
        _grid.SizeChanged += (_, _) => UpdateScrollBars();
        _grid.ContextMenu = new ContextMenu();
        _grid.ContextMenuOpening += OnContextMenu;
        IsVisibleChanged += OnVisibleChanged;
        TextInput += OnTypeToEdit;
    }

    private Border EditorFrame => (Border)_overlay.Children[0];

    // ───────────── public surface ─────────────

    /// <summary>Double-click on a row number (or "Show in text"): go to this source line in the editor.</summary>
    public event Action<int>? LineRequested;

    /// <summary>The user scrolled the table (unsorted, unfiltered view only): top source line and progress through it.</summary>
    public event Action<int, double>? Scrolled;

    /// <summary>
    /// Cell edits as text edits against the text last given to <see cref="Update"/> (non-overlapping, any order). The app applies
    /// them to the editor buffer as one undo step; the buffer change comes back through <see cref="Update"/>.
    /// </summary>
    public event Action<IReadOnlyList<TextEdit>>? EditRequested;

    public CsvTable? Table => _table;

    public CsvView? View => _view;

    public CsvDialect? Dialect => _dialect;

    public bool IsReadOnly
    {
        get => _readOnly;
        set
        {
            _readOnly = value;
            if (value) CancelEdit();
        }
    }

    /// <summary>Whether the first row holds column names (detected, switchable in the bar).</summary>
    public bool HasHeader
    {
        get => _dialect?.HasHeader ?? true;
        set
        {
            if (_dialect is null || _dialect.HasHeader == value) return;
            _dialect = _dialect with { HasHeader = value };
            _headerToggle.IsChecked = value;
            _ = ParseAsync(keepWidths: false);
        }
    }

    /// <summary>The parse / view work in flight (render harness).</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>Sets the text. Parsed in the background after a short pause; deferred while hidden.</summary>
    public void Update(string text, string? fileName)
    {
        if (_disposed) return;
        text ??= "";
        if (!_dirty && ReferenceEquals(text, _text) || !_dirty && text == _text && fileName == _fileName) return;
        if (fileName != _fileName) _dialect = null; // another file: detect again
        _text = text;
        _fileName = fileName;
        _dirty = true;
        // our own edit coming back: the table already has it
        if (_table is not null && _pendingText is not null && text == _pendingText)
        {
            _pendingText = null;
            _dirty = false;
            return;
        }
        if (!IsVisible) return;
        if (_table is null) Pending = ParseAsync(keepWidths: false);
        else Schedule(ref _parseTimer, _options.RefreshDelay + TimeSpan.FromMilliseconds(Math.Min(600, text.Length / 20000)), () => Pending = ParseAsync(keepWidths: true));
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

    /// <summary>Frees the parsed table of a hidden tab; the next show parses again.</summary>
    public void ReleaseContent()
    {
        CancelEdit();
        _parseCts?.Cancel();
        _viewCts?.Cancel();
        _table = null;
        _view = null;
        _rowLines = [];
        _grid.Table = null;
        _grid.View = null;
        _grid.InvalidateVisual();
        _dirty = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        ReleaseContent();
        _parseTimer?.Stop();
        _filterTimer?.Stop();
        _disposed = true;
        IsVisibleChanged -= OnVisibleChanged;
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

    private async Task ParseAsync(bool keepWidths)
    {
        _parseTimer?.Stop();
        _parseCts?.Cancel();
        var cts = _parseCts = new CancellationTokenSource();
        var text = _text;
        var fileName = _fileName;
        var dialect = _dialect;
        var spec = _spec;
        _dirty = false;
        (CsvTable Table, int[] Lines, CsvView View, CsvDialect Dialect) r;
        try
        {
            r = await Task.Run(() =>
            {
                var d = dialect ?? CsvDialect.Detect(text, fileName);
                var t = CsvTable.Parse(text, d, fileName, cts.Token);
                var lines = LineIndex(t, cts.Token);
                var v = CsvView.Create(t, ClampSpec(spec, t), cts.Token);
                return (t, lines, v, d);
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts != _parseCts || _disposed) return;
        var columnsChanged = _table is null || _table.ColumnCount != r.Table.ColumnCount;
        _dialect = r.Dialect;
        _headerToggle.IsChecked = r.Dialect.HasHeader;
        _rowLines = r.Lines;
        Show(r.Table, r.View, remeasure: !keepWidths || columnsChanged);
    }

    private static CsvViewSpec ClampSpec(CsvViewSpec spec, CsvTable t) =>
        spec.SortColumn >= t.ColumnCount ? spec with { SortColumn = -1 } : spec;

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
            if (start > pos) line += text[pos..start].Count('\n');
            pos = Math.Max(pos, start);
            lines[r] = line;
        }
        return lines;
    }

    private void Show(CsvTable table, CsvView view, bool remeasure)
    {
        _table = table;
        _view = view;
        _grid.Table = table;
        _grid.View = view;
        _grid.UpdateGutter();
        if (remeasure || _grid.Widths.Length != table.ColumnCount)
        {
            var (w, n) = CsvGrid.Measure(table, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            _grid.Widths = w;
            _grid.Numeric = n;
        }
        _grid.SortColumn = view.Spec.SortColumn;
        _grid.SortDescending = view.Spec.SortDescending;
        ClampSelection();
        UpdateStatus();
        UpdateScrollBars();
        _grid.InvalidateVisual();
    }

    private void ApplySpec(CsvViewSpec spec)
    {
        _spec = spec;
        if (_table is null) return;
        _viewCts?.Cancel();
        var cts = _viewCts = new CancellationTokenSource();
        var table = _table;
        var activeSource = _view is not null && _grid.HasSelection && _grid.Active.Row < _view.RowCount ? _view.SourceRow(_grid.Active.Row) : -1;
        _status.Text = _l.Sorting;
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
            // a new order starts at the top; the selection stays on the same record wherever it went
            var at = activeSource >= 0 ? v.DisplayIndexOf(activeSource) : -1;
            if (at >= 0)
            {
                _grid.Active = (at, _grid.Active.Col);
                _grid.Anchor = _grid.Active;
            }
            else
            {
                _grid.HasSelection = false;
            }
            SetScroll(_grid.ScrollX, 0);
        }
    }

    private void UpdateStatus()
    {
        if (_table is null || _view is null) return;
        var c = CultureInfo.CurrentCulture;
        var total = _table.DataRowCount.ToString("N0", c);
        var shown = _view.RowCount.ToString("N0", c);
        var rows = _view.RowCount == _table.DataRowCount ? string.Format(c, _l.RowsFormat, total) : string.Format(c, _l.FilteredFormat, shown, total);
        var cols = string.Format(c, _l.ColumnsFormat, _table.ColumnCount.ToString("N0", c));
        var sep = _table.Dialect.Delimiter switch { ',' => _l.Comma, ';' => _l.Semicolon, '\t' => _l.Tab, '|' => _l.Pipe, var d => "‘" + d + "’" };
        _status.Text = $"{rows}  ·  {cols}  ·  {sep}";
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

    private void EnsureVisible(int row, int col)
    {
        var r = _grid.CellRect(row, col);
        double x = _grid.ScrollX, y = _grid.ScrollY;
        if (r.Top < CsvGrid.HeaderHeight) y -= CsvGrid.HeaderHeight - r.Top;
        else if (r.Bottom > _grid.ActualHeight) y += r.Bottom - _grid.ActualHeight;
        if (r.Left < _grid.GutterWidth) x -= _grid.GutterWidth - r.Left;
        else if (r.Right > _grid.ActualWidth) x += Math.Min(r.Right - _grid.ActualWidth, r.Left - _grid.GutterWidth);
        OnUserScroll(x, y);
    }

    // ───────────── mouse ─────────────

    private void OnGridDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(_grid);
        var (zone, row, col) = _grid.Hit(p);
        switch (zone)
        {
            case CsvGrid.Zone.HeaderEdge:
                _resizeCol = col;
                _resizeStartX = p.X;
                _resizeStartW = _grid.Widths[col];
                _grid.CaptureMouse();
                break;
            case CsvGrid.Zone.Header:
                // asc → desc → file order
                var spec = _spec.SortColumn != col ? _spec with { SortColumn = col, SortDescending = false }
                    : !_spec.SortDescending ? _spec with { SortDescending = true }
                    : _spec with { SortColumn = -1, SortDescending = false };
                ApplySpec(spec);
                break;
            case CsvGrid.Zone.Gutter:
                if (e.ClickCount == 2) RequestLine(row);
                else Select(row, 0, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), wholeRow: true);
                break;
            case CsvGrid.Zone.Cell:
                if (e.ClickCount == 2)
                {
                    BeginEdit(row, col, null);
                    break;
                }
                CommitEdit(0, 0);
                Select(row, col, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
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
        if (_selecting && e.LeftButton == MouseButtonState.Pressed)
        {
            var (zone, row, col) = _grid.Hit(p);
            if (zone == CsvGrid.Zone.Cell && (row, col) != _grid.Active)
            {
                _grid.Active = (row, col);
                _grid.InvalidateVisual();
            }
            return;
        }
        _grid.Cursor = _grid.Hit(p).Zone == CsvGrid.Zone.HeaderEdge ? Cursors.SizeWE : _grid.Hit(p).Zone == CsvGrid.Zone.Header ? Cursors.Hand : null;
        if (_grid.Hit(p).Zone == CsvGrid.Zone.Header) _grid.ToolTip = _l.SortTip;
        else _grid.ToolTip = null;
    }

    private void OnGridUp(object sender, MouseButtonEventArgs e)
    {
        _resizeCol = -1;
        _selecting = false;
        _grid.ReleaseMouseCapture();
    }

    private void RequestLine(int display)
    {
        if (_view is null || display < 0 || display >= _view.RowCount || _rowLines.Length == 0) return;
        LineRequested?.Invoke(_rowLines[_view.SourceRow(display)]);
    }

    // ───────────── selection / keyboard ─────────────

    private void Select(int row, int col, bool extend, bool wholeRow = false)
    {
        if (_view is null || _view.RowCount == 0) return;
        row = Math.Clamp(row, 0, _view.RowCount - 1);
        col = Math.Clamp(col, 0, Math.Max(0, _grid.ColumnCount - 1));
        if (!extend || !_grid.HasSelection) _grid.Anchor = wholeRow ? (row, 0) : (row, col);
        _grid.Active = wholeRow ? (row, Math.Max(0, _grid.ColumnCount - 1)) : (row, col);
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _editing is not null || e.OriginalSource is TextBox || _view is null) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var (r, c) = _grid.HasSelection ? _grid.Active : (0, 0);
        var page = Math.Max(1, (int)(_grid.ViewportHeight / CsvGrid.RowHeight) - 1);
        var lastRow = _view.RowCount - 1;
        var lastCol = Math.Max(0, _grid.ColumnCount - 1);
        (int, int)? move = e.Key switch
        {
            Key.Up => (r - 1, c),
            Key.Down => (r + 1, c),
            Key.Left => (r, c - 1),
            Key.Right => (r, c + 1),
            Key.PageUp => (r - page, c),
            Key.PageDown => (r + page, c),
            Key.Home => ctrl ? (0, 0) : (r, 0),
            Key.End => ctrl ? (lastRow, lastCol) : (r, lastCol),
            Key.Tab => shift ? (r, c - 1) : (r, c + 1),
            Key.Enter => shift ? (r - 1, c) : (r + 1, c),
            _ => null,
        };
        if (move is { } m)
        {
            Select(m.Item1, m.Item2, shift && e.Key is not (Key.Tab or Key.Enter));
            EnsureVisible(_grid.Active.Row, _grid.Active.Col);
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.C)
        {
            CopySelection();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.A)
        {
            SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && _grid.HasSelection)
        {
            BeginEdit(r, c, null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _grid.HasSelection)
        {
            ClearSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _spec.Filters.Count > 0 && !string.IsNullOrEmpty(_filter.Text))
        {
            _filter.Text = "";
            e.Handled = true;
        }
    }

    private void OnTypeToEdit(object sender, TextCompositionEventArgs e)
    {
        if (_editing is not null || e.OriginalSource is TextBox || !_grid.HasSelection || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        BeginEdit(_grid.Active.Row, _grid.Active.Col, e.Text);
        e.Handled = true;
    }

    /// <summary>The selected block as TSV (values with tabs, line breaks or quotes are quoted), to the clipboard.</summary>
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
                if (v.IndexOfAny(['\t', '\n', '\r', '"']) >= 0) v = "\"" + v.Replace("\"", "\"\"") + "\"";
                sb.Append(v);
            }
            sb.Append("\r\n");
        }
        var text = sb.ToString();
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // clipboard busy
        }
        return text;
    }

    private void ClearSelection()
    {
        if (_readOnly || _table is null || _view is null) return;
        var (r0, r1) = (Math.Min(_grid.Anchor.Row, _grid.Active.Row), Math.Max(_grid.Anchor.Row, _grid.Active.Row));
        var (c0, c1) = (Math.Min(_grid.Anchor.Col, _grid.Active.Col), Math.Max(_grid.Anchor.Col, _grid.Active.Col));
        if ((long)(r1 - r0 + 1) * (c1 - c0 + 1) > 200_000) return;
        var edits = new List<TextEdit>();
        for (var r = r0; r <= r1; r++)
        {
            var src = _view.SourceRow(r);
            for (var c = c0; c <= c1; c++)
                if (c < _table.GetFieldCount(src) && _table.GetCell(src, c).Length > 0) edits.Add(_table.SetCell(src, c, ""));
        }
        Emit(edits);
    }

    // ───────────── editing ─────────────

    private void BeginEdit(int row, int col, string? typed)
    {
        if (_readOnly || _table is null || _view is null || row < 0 || row >= _view.RowCount || col < 0 || col >= _grid.ColumnCount) return;
        Select(row, col, false);
        EnsureVisible(row, col);
        _editing = (row, col);
        var src = _view.SourceRow(row);
        _editor.Text = typed ?? (col < _table.GetFieldCount(src) ? _table.GetCell(src, col) : "");
        _editor.TextAlignment = _grid.Numeric.ElementAtOrDefault(col) && typed is null ? TextAlignment.Right : TextAlignment.Left;
        EditorFrame.Visibility = Visibility.Visible;
        _editor.Visibility = Visibility.Visible;
        PlaceEditor();
        _editor.Focus();
        if (typed is null) _editor.SelectAll();
        else _editor.CaretIndex = _editor.Text.Length;
    }

    private void PlaceEditor()
    {
        if (_editing is not { } at) return;
        var r = _grid.CellRect(at.Row, at.Col);
        var f = EditorFrame;
        f.Width = Math.Max(r.Width + 2, 80);
        f.Height = r.Height + 2;
        Canvas.SetLeft(f, r.X - 1);
        Canvas.SetTop(f, r.Y - 1);
    }

    private void OnEditorKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CancelEdit();
                e.Handled = true;
                break;
            case Key.Enter when !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt):
                CommitEdit(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1, 0);
                e.Handled = true;
                break;
            case Key.Enter:
                // Alt+Enter: a line break inside the cell
                var i = _editor.CaretIndex;
                _editor.Text = _editor.Text.Insert(i, "\n");
                _editor.CaretIndex = i + 1;
                e.Handled = true;
                break;
            case Key.Tab:
                CommitEdit(0, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
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
            if (value != old) Emit([_table.SetCell(src, at.Col, value)]);
        }
        if (dRow != 0 || dCol != 0)
        {
            Select(at.Row + dRow, at.Col + dCol, false);
            EnsureVisible(_grid.Active.Row, _grid.Active.Col);
        }
        Focus();
    }

    private void CancelEdit()
    {
        if (_editing is null) return;
        _editing = null;
        HideEditor();
        Focus();
    }

    private void HideEditor()
    {
        EditorFrame.Visibility = Visibility.Collapsed;
        _editor.Visibility = Visibility.Collapsed;
    }

    /// <summary>Hands edits to the app and shows them right away (the table takes them in; the echo through Update is skipped).</summary>
    private void Emit(IReadOnlyList<TextEdit> edits)
    {
        var real = edits.Where(e => !e.IsNoOp).ToList();
        if (real.Count == 0 || _table is null || _view is null) return;
        var table = _table.WithEdits(real);
        _pendingText = table.Text;
        _text = table.Text;
        if (real.Any(e => e.Replacement.Contains('\n') || e.Length > 0 && _table.Text.AsSpan(e.Offset, e.Length).Contains('\n')))
            _rowLines = LineIndex(table, CancellationToken.None);
        var view = _view.IsIdentity && table.RowCount == _table.RowCount ? CsvView.Create(table, _view.Spec) : _view;
        Show(table, view, remeasure: false);
        if (!view.IsIdentity || view.Table != table) ApplySpec(_spec); // re-sort / re-filter with the new values
        EditRequested?.Invoke(real);
    }

    // ───────────── chrome ─────────────

    private FrameworkElement BuildTopBar()
    {
        var bar = new Grid { Height = 44 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 360 });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // filter field: a soft pill with a magnifier
        var field = new Grid();
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
        glass.SetResourceReference(Shape.StrokeProperty, "Brush.TextFaint");
        _filter.Template = (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TextBox">
  <Border x:Name="Bd" CornerRadius="9" Background="{DynamicResource Brush.BgAlt}" BorderBrush="{DynamicResource Brush.Border}" BorderThickness="1">
    <ScrollViewer x:Name="PART_ContentHost" Margin="30,0,8,0" VerticalAlignment="Center"/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource Brush.BorderStrong}"/></Trigger>
    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource Brush.Accent}"/></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""");
        // the text box adds its Padding inside the content host itself: the inset lives in the template only
        _filter.Padding = new Thickness(0);
        _filter.Height = 30;
        _filter.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        _filter.FontSize = 12.5;
        _filter.VerticalContentAlignment = VerticalAlignment.Center;
        _filter.SetResourceReference(TextBox.ForegroundProperty, "Brush.Text");
        _filter.SetResourceReference(TextBox.CaretBrushProperty, "Brush.Text");
        _filter.SetResourceReference(TextBox.SelectionBrushProperty, "Brush.Accent");
        _filter.SelectionOpacity = 0.45;
        _filter.TextChanged += (_, _) =>
        {
            _filterHint.Visibility = _filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Schedule(ref _filterTimer, TimeSpan.FromMilliseconds(200), () =>
                ApplySpec(_spec with { Filters = _filter.Text.Length == 0 ? [] : [new CsvColumnFilter(-1, _filter.Text)] }));
        };
        _filter.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _filter.Text.Length > 0)
            {
                _filter.Text = "";
                e.Handled = true;
            }
            else if (e.Key is Key.Down or Key.Enter)
            {
                Focus();
                if (!_grid.HasSelection) Select(0, 0, false);
                e.Handled = true;
            }
        };
        _filterHint.Text = _l.FilterPlaceholder;
        _filterHint.FontSize = 12.5;
        _filterHint.Margin = new Thickness(33, 0, 0, 1);
        _filterHint.VerticalAlignment = VerticalAlignment.Center;
        _filterHint.IsHitTestVisible = false;
        _filterHint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        field.Children.Add(_filter);
        field.Children.Add(glass);
        field.Children.Add(_filterHint);
        field.Margin = new Thickness(12, 7, 8, 7);
        bar.Children.Add(field);

        _status.FontSize = 12;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(8, 0, 12, 0);
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        Grid.SetColumn(_status, 2);
        bar.Children.Add(_status);

        // header-row chip: the detected setting, one click to flip
        _headerToggle.Content = _l.HeaderRow;
        _headerToggle.ToolTip = _l.HeaderRowTip;
        _headerToggle.Focusable = false;
        _headerToggle.Cursor = Cursors.Hand;
        _headerToggle.FontSize = 12;
        _headerToggle.Margin = new Thickness(0, 0, 12, 0);
        _headerToggle.VerticalAlignment = VerticalAlignment.Center;
        _headerToggle.Template = (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ToggleButton">
  <Border x:Name="Bd" CornerRadius="8" Padding="10,5" Background="Transparent" BorderThickness="1" BorderBrush="{DynamicResource Brush.Border}">
    <StackPanel Orientation="Horizontal">
      <Path x:Name="Tick" Data="M1 5 L4 8 L10 1.5" Width="11" Height="10" Margin="0,0,7,0" StrokeThickness="1.6" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
            StrokeLineJoin="Round" Stroke="{DynamicResource Brush.AccentText}" VerticalAlignment="Center" Visibility="Collapsed"/>
      <ContentPresenter x:Name="Label" VerticalAlignment="Center" TextElement.Foreground="{DynamicResource Brush.TextDim}"/>
    </StackPanel>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="Background" Value="{DynamicResource Brush.SurfaceHover}"/></Trigger>
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
        Grid.SetColumn(_headerToggle, 3);
        bar.Children.Add(_headerToggle);

        var host = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        host.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        host.SetResourceReference(Border.BackgroundProperty, "Brush.Editor.Bg");
        return host;
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
        var m = _grid.ContextMenu!;
        m.Items.Clear();
        MenuItem Item(string header, string gesture, Action click, bool enabled = true)
        {
            var i = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
            i.Click += (_, _) => click();
            return i;
        }
        var active = _grid.Active;
        m.Items.Add(Item(_l.Copy, "Ctrl+C", () => CopySelection(), _grid.HasSelection));
        m.Items.Add(Item(_l.EditCell, "F2", () => BeginEdit(active.Row, active.Col, null), !_readOnly && _grid.HasSelection));
        m.Items.Add(Item(_l.ShowInText, "", () => RequestLine(active.Row), _grid.HasSelection));
        m.Items.Add(new Separator());
        var c = zone is CsvGrid.Zone.Cell or CsvGrid.Zone.Header ? col : active.Col;
        m.Items.Add(Item(_l.SortAscending, "", () => ApplySpec(_spec with { SortColumn = c, SortDescending = false })));
        m.Items.Add(Item(_l.SortDescending, "", () => ApplySpec(_spec with { SortColumn = c, SortDescending = true })));
        m.Items.Add(Item(_l.ClearSort, "", () => ApplySpec(_spec with { SortColumn = -1 }), _spec.SortColumn >= 0));
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (IsVisible && _dirty) Pending = ParseAsync(keepWidths: _table is not null);
        if (!IsVisible)
        {
            CommitEdit(0, 0);
            if (_parseTimer?.IsEnabled == true)
            {
                _parseTimer.Stop();
                _dirty = true;
            }
        }
    }

    // ───────────── harness hooks ─────────────

    internal void SelectForTest(int row, int col, int toRow, int toCol)
    {
        Select(row, col, false);
        Select(toRow, toCol, true);
    }

    internal void EditForTest(int row, int col, string value)
    {
        BeginEdit(row, col, null);
        _editor.Text = value;
        CommitEdit(1, 0);
    }

    internal void BeginEditForTest(int row, int col) => BeginEdit(row, col, null);

    internal void CancelEditForTest() => CancelEdit();

    internal void SortForTest(int col, bool descending) => ApplySpec(_spec with { SortColumn = col, SortDescending = descending });

    internal void FilterForTest(string text) => _filter.Text = text;

    internal double ScrollYForTest => _grid.ScrollY;

    internal void ScrollByUser(double y) => OnUserScroll(_grid.ScrollX, y);
}
