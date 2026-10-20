using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BetterNotepad.Tabular.Wpf;

/// <summary>
/// The table, drawn directly (no element per cell): only the visible rows and columns are laid out, so 200k rows or 300
/// columns scroll like 100. Sticky header and row-number gutter (source line numbers), zebra rows, 1 px lines snapped to
/// device pixels, hover, selection, the active cell, filter matches and a quiet marker on rows with a different cell count.
/// Positions: display rows (after sort / filter) and columns; scroll offsets in DIPs. The formatted text of visible cells is
/// kept from one frame to the next, so scrolling only formats the rows that come into view.
/// </summary>
internal sealed class CsvGrid : FrameworkElement
{
    public const double RowHeight = 26;
    public const double HeaderHeight = 32;
    public const double CellPadX = 10;
    private const double CellSize = 12.5;
    private const double HeaderSize = 12;
    private const double GutterSize = 11;
    private const double SortMarkRoom = 16;

    public static readonly DependencyProperty ThemeProbeProperty = DependencyProperty.Register(
        nameof(ThemeProbe), typeof(object), typeof(CsvGrid), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((CsvGrid)d).OnThemeChanged()));

    private static readonly FontFamily UiFamily = new("Segoe UI Variable Text, Segoe UI");
    internal static readonly Typeface UiFace = new(UiFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    internal static readonly Typeface UiBold = new(UiFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private Typeface _monoFace = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private Brushes? _brushes;
    private Dictionary<long, FormattedText> _cells = new();
    private Dictionary<long, FormattedText> _nextCells = new();
    private CsvTable? _table;
    private double[] _widths = [];
    private bool _monospace;

    public CsvGrid()
    {
        SetResourceReference(ThemeProbeProperty, "Brush.Text");
        ClipToBounds = true;
        Focusable = false;
        SnapsToDevicePixels = true;
    }

    /// <summary>Bound to a theme brush: any theme switch re-reads the brushes.</summary>
    public object? ThemeProbe
    {
        get => GetValue(ThemeProbeProperty);
        set => SetValue(ThemeProbeProperty, value);
    }

    public CsvTable? Table
    {
        get => _table;
        set
        {
            _table = value;
            ClearCache();
        }
    }

    public CsvView? View { get; set; }

    /// <summary>1-based line in the file of every record (row numbers in the gutter).</summary>
    public int[] Lines { get; set; } = [];

    public double[] Widths
    {
        get => _widths;
        set
        {
            _widths = value;
            ClearCache();
        }
    }

    public bool[] Numeric { get; set; } = [];

    public bool Monospace
    {
        get => _monospace;
        set
        {
            if (_monospace == value) return;
            _monospace = value;
            ClearCache();
        }
    }

    public string MonospaceFont
    {
        set
        {
            _monoFace = new Typeface(new FontFamily(value), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            ClearCache();
        }
    }

    internal Typeface CellFace => _monospace ? _monoFace : UiFace;

    /// <summary>Cells per record the header (or most records) have; rows with another count get a marker. 0 = none.</summary>
    public int ExpectedColumns { get; set; }

    /// <summary>Filter text to mark inside the cells (case-insensitive); <see cref="HighlightColumn"/> -1 = every column.</summary>
    public string? Highlight { get; set; }

    public int HighlightColumn { get; set; } = -1;

    public int HoverRow { get; set; } = -1;

    public int HoverHeader { get; set; } = -1;

    /// <summary>The preview has keyboard focus: the active cell wears the accent, otherwise a quiet outline.</summary>
    public bool IsActiveFocus { get; set; }

    public string EmptyText { get; set; } = "";

    public string EmptyHint { get; set; } = "";

    public string NoMatchesText { get; set; } = "";

    public double ScrollX { get; set; }

    public double ScrollY { get; set; }

    public int SortColumn { get; set; } = -1;

    public bool SortDescending { get; set; }

    public (int Row, int Col) Anchor { get; set; } = (0, 0);

    public (int Row, int Col) Active { get; set; } = (0, 0);

    public bool HasSelection { get; set; }

    public int RowCount => View?.RowCount ?? 0;

    public int ColumnCount => _widths.Length;

    public double GutterWidth { get; private set; } = 44;

    public double ContentWidth
    {
        get
        {
            double sum = 0;
            foreach (var w in _widths) sum += w;
            return sum;
        }
    }

    public double ContentHeight => RowCount * RowHeight;

    public double ViewportWidth => Math.Max(0, ActualWidth - GutterWidth);

    public double ViewportHeight => Math.Max(0, ActualHeight - HeaderHeight);

    private double Dpi => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    public void ClearCache()
    {
        _cells.Clear();
        _nextCells.Clear();
    }

    private void OnThemeChanged()
    {
        _brushes = null;
        ClearCache();
    }

    public void UpdateGutter()
    {
        var last = Lines.Length > 0 ? Lines[^1] : (Table?.RowCount ?? 0);
        var digits = Math.Max(2, last.ToString(CultureInfo.InvariantCulture).Length);
        GutterWidth = Math.Ceiling(digits * 6.6 + 26);
    }

    // ───────────── geometry ─────────────

    public double ColumnLeft(int col)
    {
        double x = 0;
        for (var i = 0; i < col && i < _widths.Length; i++) x += _widths[i];
        return GutterWidth + x - ScrollX;
    }

    public Rect CellRect(int row, int col) =>
        new(ColumnLeft(col), HeaderHeight + row * RowHeight - ScrollY, col < _widths.Length ? _widths[col] : 0, RowHeight);

    public enum Zone { None, Corner, Header, HeaderEdge, Gutter, Cell }

    /// <summary>What lies under a point: zone, display row, column (HeaderEdge: the column whose right edge it is).</summary>
    public (Zone Zone, int Row, int Col) Hit(Point p)
    {
        var col = -1;
        var edge = -1;
        if (p.X >= GutterWidth)
        {
            var x = GutterWidth - ScrollX;
            for (var i = 0; i < _widths.Length; i++)
            {
                var right = x + _widths[i];
                if (Math.Abs(p.X - right) <= 4) edge = i;
                if (p.X >= x && p.X < right) col = i;
                x = right;
                if (x > p.X + 5) break;
            }
        }
        if (p.Y < HeaderHeight)
        {
            if (p.X < GutterWidth) return (Zone.Corner, -1, -1);
            if (edge >= 0) return (Zone.HeaderEdge, -1, edge);
            return col >= 0 ? (Zone.Header, -1, col) : (Zone.None, -1, -1);
        }
        var row = (int)Math.Floor((p.Y - HeaderHeight + ScrollY) / RowHeight);
        if (row < 0 || row >= RowCount) return (Zone.None, -1, col);
        if (p.X < GutterWidth) return (Zone.Gutter, row, -1);
        return col >= 0 ? (Zone.Cell, row, col) : (Zone.None, row, -1);
    }

    public bool IsSelected(int row, int col)
    {
        if (!HasSelection) return false;
        var (r0, r1) = (Math.Min(Anchor.Row, Active.Row), Math.Max(Anchor.Row, Active.Row));
        var (c0, c1) = (Math.Min(Anchor.Col, Active.Col), Math.Max(Anchor.Col, Active.Col));
        return row >= r0 && row <= r1 && col >= c0 && col <= c1;
    }

    // ───────────── column widths (sampled) ─────────────

    public static (double[] Widths, bool[] Numeric) Measure(CsvTable table, Typeface cellFace)
    {
        var cols = table.ColumnCount;
        var widths = new double[cols];
        var numeric = new bool[cols];
        var headers = table.GetHeaders();
        var numbers = new int[cols];
        var filled = new int[cols];
        var head = TextMeasure.For(UiBold, HeaderSize);
        var body = TextMeasure.For(cellFace, CellSize);
        for (var c = 0; c < cols; c++)
            widths[c] = head.Width(Display(headers[c])) + CellPadX * 2 + SortMarkRoom;
        // wide tables sample fewer rows: the cost stays about the same for 7 or 700 columns
        var budget = Math.Clamp(160_000 / Math.Max(1, cols), 24, 200);
        foreach (var r in SampleRows(table, budget))
        {
            var fields = table.GetFields(r);
            var count = Math.Min(cols, fields.Length);
            for (var c = 0; c < count; c++)
            {
                if (fields[c].Length == 0) continue;
                var v = CsvScanner.Decode(table.Text, fields[c], table.Dialect.Quote);
                if (v.Length == 0) continue;
                filled[c]++;
                if (CsvValue.Classify(v) == CsvValueKind.Number) numbers[c]++;
                var w = v.Length > 80 ? 9999 : body.Width(Display(v)) + CellPadX * 2;
                if (w > widths[c]) widths[c] = w;
            }
        }
        for (var c = 0; c < cols; c++)
        {
            widths[c] = Math.Ceiling(Math.Clamp(widths[c] * 1.04 + 2, 56, 340));
            numeric[c] = filled[c] > 0 && numbers[c] >= filled[c] * 0.8;
        }
        return (widths, numeric);
    }

    /// <summary>Width that fits every sampled value of one column (double-click on its edge).</summary>
    public static double FitColumn(CsvTable table, int col, Typeface cellFace)
    {
        var head = TextMeasure.For(UiBold, HeaderSize);
        var body = TextMeasure.For(cellFace, CellSize);
        var headers = table.GetHeaders();
        var w = (col < headers.Length ? head.Width(Display(headers[col])) : 0) + CellPadX * 2 + SortMarkRoom;
        foreach (var r in SampleRows(table, 2000))
        {
            var v = table.GetCell(r, col);
            if (v.Length > 0) w = Math.Max(w, body.Width(Display(v)) + CellPadX * 2);
        }
        return Math.Ceiling(Math.Clamp(w * 1.04 + 2, 48, 900));
    }

    private static IEnumerable<int> SampleRows(CsvTable table, int budget)
    {
        var first = table.HeaderRowCount;
        var n = table.RowCount;
        var head = Math.Min(n, first + budget * 3 / 5);
        for (var r = first; r < head; r++) yield return r;
        if (n - head <= 0) yield break;
        // plus rows spread over the rest (long values further down still count)
        var spread = Math.Max(1, budget - (head - first));
        var step = Math.Max(1, (n - head) / spread);
        for (var r = head; r < n; r += step) yield return r;
    }

    /// <summary>One display line for a cell: line breaks and tabs shown as marks.</summary>
    public static string Display(string v)
    {
        if (v.Length > 400) v = v[..400];
        return v.AsSpan().IndexOfAny('\r', '\n', '\t') < 0 ? v : v.Replace("\r\n", " ↵ ").Replace('\n', '↵').Replace('\r', '↵').Replace('\t', '→');
    }

    /// <summary>True when the value does not fit the column (shown with an ellipsis) or spans lines: worth a tooltip.</summary>
    public bool IsClipped(string value, int col)
    {
        if (value.Length == 0 || col >= _widths.Length) return false;
        if (value.AsSpan().IndexOfAny('\r', '\n') >= 0 || value.Length > 400) return true;
        return TextMeasure.For(CellFace, CellSize).Width(value) > _widths[col] - CellPadX * 2;
    }

    // ───────────── rendering ─────────────

    private sealed class Brushes
    {
        public Brush Bg = null!, Stripe = null!, Header = null!, Gutter = null!, Line = null!, LineStrong = null!, Text = null!, Dim = null!, Faint = null!,
            SelFill = null!, Accent = null!, SelHeader = null!, Hover = null!, Match = null!, Warning = null!;
    }

    private Brushes B()
    {
        if (_brushes is not null) return _brushes;
        Brush R(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
        var gray = System.Windows.Media.Brushes.Gray;
        _brushes = new Brushes
        {
            Bg = R("Brush.Editor.Bg", System.Windows.Media.Brushes.White),
            Stripe = R("Brush.Md.TableStripe", System.Windows.Media.Brushes.Transparent),
            Header = R("Brush.Md.TableHeader", gray),
            Gutter = R("Brush.Md.TableStripe", System.Windows.Media.Brushes.Transparent),
            Line = R("Brush.Border", gray),
            LineStrong = R("Brush.BorderStrong", gray),
            Text = R("Brush.Editor.Text", System.Windows.Media.Brushes.Black),
            Dim = R("Brush.TextDim", gray),
            Faint = R("Brush.TextFaint", gray),
            SelFill = R("Brush.AccentSoft", System.Windows.Media.Brushes.LightBlue),
            Accent = R("Brush.Accent", System.Windows.Media.Brushes.Blue),
            SelHeader = R("Brush.AccentSoft", System.Windows.Media.Brushes.LightBlue),
            Hover = R("Brush.TabHover", System.Windows.Media.Brushes.Transparent),
            Match = R("Brush.Editor.FindMatch", System.Windows.Media.Brushes.Khaki),
            Warning = R("Brush.Warning", System.Windows.Media.Brushes.Orange),
        };
        return _brushes;
    }

    private static long Key(int src, int slot) => (long)src * 1_048_576 + slot;

    protected override void OnRender(DrawingContext dc)
    {
        var b = B();
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(b.Bg, null, new Rect(0, 0, w, h));
        if (Table is null || View is null) return;
        var ppd = Dpi;
        var px = 1 / ppd; // one device pixel
        double Snap(double v) => Math.Round(v * ppd) / ppd;

        var rows = RowCount;
        var first = Math.Max(0, (int)Math.Floor(ScrollY / RowHeight));
        var last = Math.Min(rows - 1, (int)Math.Ceiling((ScrollY + h) / RowHeight));
        var gutter = GutterWidth;
        var lastColRight = GutterWidth + ContentWidth - ScrollX;
        var bodyRight = Math.Max(gutter, Math.Min(w, lastColRight));
        var face = CellFace;
        var culture = CultureInfo.CurrentUICulture;

        // visible columns
        var colX = new double[_widths.Length + 1];
        colX[0] = gutter - ScrollX;
        for (var c = 0; c < _widths.Length; c++) colX[c + 1] = colX[c] + _widths[c];
        var c0 = 0;
        while (c0 < _widths.Length && colX[c0 + 1] <= gutter) c0++;
        var c1 = c0;
        while (c1 < _widths.Length && colX[c1] < w) c1++;

        var visibleRows = Math.Max(0, last - first + 1);
        var fieldCounts = new int[visibleRows];
        var needle = string.IsNullOrEmpty(Highlight) ? null : Highlight;
        var unfocusedSelection = HasSelection && !IsActiveFocus;

        // body
        dc.PushClip(new RectangleGeometry(new Rect(gutter, HeaderHeight, Math.Max(0, w - gutter), Math.Max(0, h - HeaderHeight))));
        for (var r = first; r <= last; r++)
        {
            var y = HeaderHeight + r * RowHeight - ScrollY;
            var rowRect = new Rect(gutter, y, bodyRight - gutter, RowHeight);
            if (r % 2 == 1) dc.DrawRectangle(b.Stripe, null, rowRect);
            if (r == HoverRow) dc.DrawRectangle(b.Hover, null, rowRect);
            var src = View.SourceRow(r);
            var fields = Table.GetFieldCount(src);
            fieldCounts[r - first] = Table.RowEnd(src) == Table.RowStart(src) ? 0 : fields;
            for (var c = c0; c < c1; c++)
            {
                var cell = new Rect(colX[c], y, _widths[c], RowHeight);
                if (IsSelected(r, c))
                {
                    if (unfocusedSelection) dc.PushOpacity(0.55);
                    dc.DrawRectangle(b.SelFill, null, cell);
                    if (unfocusedSelection) dc.Pop();
                }
                if (c >= fields) continue;
                var key = Key(src, c + 2);
                if (!_cells.TryGetValue(key, out var ft))
                {
                    var v = Table.GetCell(src, c);
                    ft = v.Length == 0 ? null! : Text(Display(v), face, CellSize, b.Text, _widths[c] - CellPadX * 2, ppd, culture);
                }
                if (ft is null) continue;
                _nextCells[key] = ft;
                var tx = Numeric.Length > c && Numeric[c] ? cell.Right - CellPadX - ft.Width : cell.X + CellPadX;
                var origin = new Point(Snap(tx), Snap(y + (RowHeight - ft.Height) / 2));
                if (needle is not null && (HighlightColumn < 0 || HighlightColumn == c))
                {
                    var at = ft.Text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                    if (at >= 0 && ft.BuildHighlightGeometry(origin, at, Math.Min(needle.Length, ft.Text.Length - at)) is { } geo)
                    {
                        var box = geo.Bounds;
                        box.Inflate(1, -1);
                        dc.DrawRoundedRectangle(b.Match, null, box, 2.5, 2.5);
                    }
                }
                dc.DrawText(ft, origin);
            }
            dc.DrawRectangle(b.Line, null, new Rect(gutter, Snap(y + RowHeight) - px, bodyRight - gutter, px));
        }
        for (var c = c0; c < c1; c++)
            dc.DrawRectangle(b.Line, null, new Rect(Snap(colX[c + 1]) - px, HeaderHeight, px, Math.Max(0, Math.Min(h, HeaderHeight + ContentHeight - ScrollY) - HeaderHeight)));
        // active cell
        if (HasSelection && Active.Row >= first && Active.Row <= last && Active.Col < _widths.Length)
        {
            var a = CellRect(Active.Row, Active.Col);
            a.Inflate(-0.75, -0.75);
            dc.DrawRoundedRectangle(null, Pen(IsActiveFocus ? b.Accent : b.LineStrong, 1.5), a, 2, 2);
        }
        dc.Pop();

        // gutter: the line of the record in the file, so a sorted or filtered view still says where a row lives
        dc.PushClip(new RectangleGeometry(new Rect(0, HeaderHeight, gutter, Math.Max(0, h - HeaderHeight))));
        dc.DrawRectangle(b.Gutter, null, new Rect(0, HeaderHeight, gutter, Math.Max(0, h - HeaderHeight)));
        var (sr0, sr1) = HasSelection ? (Math.Min(Anchor.Row, Active.Row), Math.Max(Anchor.Row, Active.Row)) : (-1, -1);
        for (var r = first; r <= last; r++)
        {
            var y = HeaderHeight + r * RowHeight - ScrollY;
            var selected = r >= sr0 && r <= sr1;
            if (selected) dc.DrawRectangle(b.SelHeader, null, new Rect(0, y, gutter, RowHeight));
            else if (r == HoverRow) dc.DrawRectangle(b.Hover, null, new Rect(0, y, gutter, RowHeight));
            var src = View.SourceRow(r);
            var key = Key(src, selected ? 0 : 1);
            if (!_cells.TryGetValue(key, out var ft))
            {
                var line = src < Lines.Length ? Lines[src] : src + 1;
                ft = Text(line.ToString(CultureInfo.InvariantCulture), selected ? UiBold : UiFace, GutterSize, selected ? b.Dim : b.Faint, gutter - 12, ppd, culture);
            }
            _nextCells[key] = ft;
            dc.DrawText(ft, new Point(Snap(gutter - 10 - ft.Width), Snap(y + (RowHeight - ft.Height) / 2)));
            var cells = fieldCounts[r - first];
            if (ExpectedColumns > 0 && cells > 0 && cells != ExpectedColumns)
                dc.DrawRoundedRectangle(b.Warning, null, new Rect(3, y + 6, 2.5, RowHeight - 12), 1.25, 1.25);
            dc.DrawRectangle(b.Line, null, new Rect(0, Snap(y + RowHeight) - px, gutter, px));
        }
        dc.Pop();
        dc.DrawRectangle(b.LineStrong, null, new Rect(Snap(gutter) - px, 0, px, h));

        // sticky header
        dc.DrawRectangle(b.Header, null, new Rect(0, 0, w, HeaderHeight));
        Corner(dc, b, gutter);
        dc.PushClip(new RectangleGeometry(new Rect(gutter, 0, Math.Max(0, w - gutter), HeaderHeight)));
        var headers = Table.GetHeaders();
        var (sc0, sc1) = HasSelection ? (Math.Min(Anchor.Col, Active.Col), Math.Max(Anchor.Col, Active.Col)) : (-1, -1);
        for (var c = c0; c < c1; c++)
        {
            var cell = new Rect(colX[c], 0, _widths[c], HeaderHeight);
            if (c >= sc0 && c <= sc1) dc.DrawRectangle(b.SelHeader, null, cell);
            if (c == HoverHeader) dc.DrawRectangle(b.Hover, null, cell);
            var sorted = c == SortColumn;
            var room = _widths[c] - CellPadX * 2 - (sorted ? SortMarkRoom : 0);
            var title = Table.HeaderRowCount > 0 && c < headers.Length ? headers[c] : CsvTable.ColumnLetters(c);
            var ft = Text(Display(title), UiBold, HeaderSize, sorted || c == HoverHeader ? b.Text : b.Dim, room, ppd, culture);
            var numeric = Numeric.Length > c && Numeric[c];
            var tx = numeric ? cell.Right - CellPadX - ft.Width - (sorted ? SortMarkRoom : 0) : cell.X + CellPadX;
            dc.DrawText(ft, new Point(Snap(tx), Snap((HeaderHeight - ft.Height) / 2)));
            if (sorted)
            {
                var mx = numeric ? cell.Right - CellPadX - 5 : Math.Min(tx + ft.Width + 10, cell.Right - CellPadX - 5);
                SortMark(dc, b.Accent, new Point(mx, HeaderHeight / 2), SortDescending);
            }
            dc.DrawRectangle(b.Line, null, new Rect(Snap(colX[c + 1]) - px, 7, px, HeaderHeight - 14));
        }
        dc.Pop();
        dc.DrawRectangle(b.LineStrong, null, new Rect(0, Snap(HeaderHeight) - px, w, px));

        // nothing to show: a calm note in the middle
        if (rows == 0)
        {
            var empty = Table.DataRowCount == 0;
            var main = Text(empty ? EmptyText : NoMatchesText, UiBold, 13, b.Dim, Math.Max(10, w - 40), ppd, culture);
            var cy = HeaderHeight + Math.Max(40, (h - HeaderHeight) * 0.38);
            dc.DrawText(main, new Point(Snap((w - main.Width) / 2), Snap(cy)));
            if (empty && EmptyHint.Length > 0)
            {
                var hint = Text(EmptyHint, UiFace, 12, b.Faint, Math.Max(10, w - 40), ppd, culture);
                dc.DrawText(hint, new Point(Snap((w - hint.Width) / 2), Snap(cy + main.Height + 4)));
            }
        }

        (_cells, _nextCells) = (_nextCells, _cells);
        _nextCells.Clear();
    }

    /// <summary>The select-all corner: a small faint triangle like in a spreadsheet.</summary>
    private static void Corner(DrawingContext dc, Brushes b, double gutter)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            var right = gutter - 7;
            var bottom = HeaderHeight - 7;
            ctx.BeginFigure(new Point(right, bottom - 7), true, true);
            ctx.LineTo(new Point(right, bottom), true, true);
            ctx.LineTo(new Point(right - 7, bottom), true, true);
        }
        g.Freeze();
        dc.PushOpacity(0.55);
        dc.DrawGeometry(b.Faint, null, g);
        dc.Pop();
    }

    private static Pen Pen(Brush b, double w)
    {
        var p = new Pen(b, w);
        p.Freeze();
        return p;
    }

    private static void SortMark(DrawingContext dc, Brush brush, Point c, bool descending)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            var d = descending ? 1 : -1;
            ctx.BeginFigure(new Point(c.X - 4, c.Y - 2 * d), false, false);
            ctx.LineTo(new Point(c.X, c.Y + 2 * d), true, true);
            ctx.LineTo(new Point(c.X + 4, c.Y - 2 * d), true, true);
        }
        g.Freeze();
        var pen = new Pen(brush, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    private static FormattedText Text(string s, Typeface face, double size, Brush brush, double maxWidth, double ppd, CultureInfo culture)
    {
        var ft = new FormattedText(s, culture, FlowDirection.LeftToRight, face, size, brush, ppd)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        if (maxWidth > 4) ft.MaxTextWidth = maxWidth;
        return ft;
    }
}

/// <summary>
/// Fast text widths for column sizing: advance widths per character, cached per typeface and size (kerning ignored, the
/// caller adds a small margin). Far cheaper than a FormattedText per cell when a wide table is measured.
/// </summary>
internal sealed class TextMeasure
{
    private static readonly Dictionary<(Typeface, double), TextMeasure> Cache = new();
    private readonly Dictionary<char, double> _widths = new();
    private readonly GlyphTypeface? _glyphs;
    private readonly Typeface _face;
    private readonly double _size;

    private TextMeasure(Typeface face, double size)
    {
        _face = face;
        _size = size;
        if (face.TryGetGlyphTypeface(out var g)) _glyphs = g;
    }

    public static TextMeasure For(Typeface face, double size)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue((face, size), out var m)) Cache[(face, size)] = m = new TextMeasure(face, size);
            return m;
        }
    }

    public double Width(string s)
    {
        double sum = 0;
        foreach (var c in s) sum += Char(c);
        return sum;
    }

    private double Char(char c)
    {
        if (_widths.TryGetValue(c, out var w)) return w;
        if (_glyphs is not null && _glyphs.CharacterToGlyphMap.TryGetValue(c, out var index)) w = _glyphs.AdvanceWidths[index] * _size;
        else
        {
            var ft = new FormattedText(c.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, _size, System.Windows.Media.Brushes.Black, 1.0);
            w = ft.WidthIncludingTrailingWhitespace;
        }
        _widths[c] = w;
        return w;
    }
}
