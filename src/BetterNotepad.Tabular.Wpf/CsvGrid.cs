using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BetterNotepad.Tabular.Wpf;

/// <summary>
/// The table, drawn directly (no element per cell): only the visible rows are laid out, so 100k+ rows scroll like 100.
/// Sticky header and row-number gutter, zebra rows, 1 px lines snapped to device pixels, selection and the active cell.
/// Positions: display rows (after sort / filter) and columns; scroll offsets in DIPs.
/// </summary>
internal sealed class CsvGrid : FrameworkElement
{
    public const double RowHeight = 26;
    public const double HeaderHeight = 32;
    private const double CellPadX = 10;

    public static readonly DependencyProperty ThemeProbeProperty = DependencyProperty.Register(
        nameof(ThemeProbe), typeof(object), typeof(CsvGrid), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((CsvGrid)d)._brushes = null));

    private static readonly Typeface CellFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface HeaderFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface NumberFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private Brushes? _brushes;

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

    public CsvTable? Table { get; set; }

    public CsvView? View { get; set; }

    public double[] Widths { get; set; } = [];

    public bool[] Numeric { get; set; } = [];

    public double ScrollX { get; set; }

    public double ScrollY { get; set; }

    public int SortColumn { get; set; } = -1;

    public bool SortDescending { get; set; }

    public (int Row, int Col) Anchor { get; set; } = (0, 0);

    public (int Row, int Col) Active { get; set; } = (0, 0);

    public bool HasSelection { get; set; }

    public int RowCount => View?.RowCount ?? 0;

    public int ColumnCount => Widths.Length;

    public double GutterWidth { get; private set; } = 44;

    public double ContentWidth => Widths.Sum();

    public double ContentHeight => RowCount * RowHeight;

    public double ViewportWidth => Math.Max(0, ActualWidth - GutterWidth);

    public double ViewportHeight => Math.Max(0, ActualHeight - HeaderHeight);

    private double Dpi => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    public void UpdateGutter()
    {
        var digits = Math.Max(2, (Table?.DataRowCount ?? 0).ToString(CultureInfo.InvariantCulture).Length);
        GutterWidth = Math.Ceiling(digits * 7.4 + 22);
    }

    // ───────────── geometry ─────────────

    public double ColumnLeft(int col)
    {
        double x = 0;
        for (var i = 0; i < col && i < Widths.Length; i++) x += Widths[i];
        return GutterWidth + x - ScrollX;
    }

    public Rect CellRect(int row, int col) =>
        new(ColumnLeft(col), HeaderHeight + row * RowHeight - ScrollY, col < Widths.Length ? Widths[col] : 0, RowHeight);

    public enum Zone { None, Corner, Header, HeaderEdge, Gutter, Cell }

    /// <summary>What lies under a point: zone, display row, column (HeaderEdge: the column whose right edge it is).</summary>
    public (Zone Zone, int Row, int Col) Hit(Point p)
    {
        var col = -1;
        var edge = -1;
        if (p.X >= GutterWidth)
        {
            var x = GutterWidth - ScrollX;
            for (var i = 0; i < Widths.Length; i++)
            {
                var right = x + Widths[i];
                if (Math.Abs(p.X - right) <= 4) edge = i;
                if (p.X >= x && p.X < right) col = i;
                x = right;
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

    public static (double[] Widths, bool[] Numeric) Measure(CsvTable table, double pixelsPerDip)
    {
        var cols = table.ColumnCount;
        var widths = new double[cols];
        var numeric = new bool[cols];
        var headers = table.GetHeaders();
        var numbers = new int[cols];
        var filled = new int[cols];
        var rows = SampleRows(table);
        for (var c = 0; c < cols; c++)
            widths[c] = TextWidth(headers[c], HeaderFace, 12, pixelsPerDip) + CellPadX * 2 + 14; // room for the sort mark
        foreach (var r in rows)
        {
            var count = Math.Min(cols, table.GetFieldCount(r));
            for (var c = 0; c < count; c++)
            {
                var v = table.GetCell(r, c);
                if (v.Length == 0) continue;
                filled[c]++;
                if (CsvValue.Classify(v) == CsvValueKind.Number) numbers[c]++;
                var w = v.Length > 80 ? 9999 : TextWidth(Display(v), CellFace, 12.5, pixelsPerDip) + CellPadX * 2;
                if (w > widths[c]) widths[c] = w;
            }
        }
        for (var c = 0; c < cols; c++)
        {
            widths[c] = Math.Ceiling(Math.Clamp(widths[c], 56, 340));
            numeric[c] = filled[c] > 0 && numbers[c] >= filled[c] * 0.8;
        }
        return (widths, numeric);
    }

    private static IEnumerable<int> SampleRows(CsvTable table)
    {
        var first = table.HeaderRowCount;
        var n = table.RowCount;
        var head = Math.Min(n, first + 120);
        for (var r = first; r < head; r++) yield return r;
        if (n - head <= 0) yield break;
        // plus rows spread over the rest (long values further down still count)
        var step = Math.Max(1, (n - head) / 80);
        for (var r = head; r < n; r += step) yield return r;
    }

    private static double TextWidth(string s, Typeface face, double size, double ppd) =>
        new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, System.Windows.Media.Brushes.Black, ppd).WidthIncludingTrailingWhitespace;

    /// <summary>One display line for a cell: line breaks and tabs shown as marks.</summary>
    public static string Display(string v)
    {
        if (v.Length > 400) v = v[..400];
        return v.IndexOfAny(['\r', '\n', '\t']) < 0 ? v : v.Replace("\r\n", " ↵ ").Replace('\n', '↵').Replace('\r', '↵').Replace('\t', '→');
    }

    // ───────────── rendering ─────────────

    private sealed class Brushes
    {
        public Brush Bg = null!, Stripe = null!, Header = null!, Gutter = null!, Line = null!, LineStrong = null!, Text = null!, Dim = null!, Faint = null!,
            SelFill = null!, Accent = null!, SelHeader = null!;
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
        };
        return _brushes;
    }

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

        // visible columns
        var colX = new double[Widths.Length + 1];
        colX[0] = gutter - ScrollX;
        for (var c = 0; c < Widths.Length; c++) colX[c + 1] = colX[c] + Widths[c];
        var c0 = 0;
        while (c0 < Widths.Length && colX[c0 + 1] <= gutter) c0++;
        var c1 = c0;
        while (c1 < Widths.Length && colX[c1] < w) c1++;

        // body
        dc.PushClip(new RectangleGeometry(new Rect(gutter, HeaderHeight, Math.Max(0, w - gutter), Math.Max(0, h - HeaderHeight))));
        for (var r = first; r <= last; r++)
        {
            var y = HeaderHeight + r * RowHeight - ScrollY;
            if (r % 2 == 1) dc.DrawRectangle(b.Stripe, null, new Rect(gutter, y, Math.Max(0, Math.Min(w, lastColRight) - gutter), RowHeight));
            var src = View.SourceRow(r);
            var fields = Table.GetFieldCount(src);
            for (var c = c0; c < c1; c++)
            {
                var cell = new Rect(colX[c], y, Widths[c], RowHeight);
                if (IsSelected(r, c)) dc.DrawRectangle(b.SelFill, null, cell);
                if (c >= fields) continue;
                var v = Table.GetCell(src, c);
                if (v.Length == 0) continue;
                var ft = Text(Display(v), Numeric[c] ? NumberFace : CellFace, 12.5, b.Text, Widths[c] - CellPadX * 2, ppd);
                var tx = Numeric[c] ? cell.Right - CellPadX - ft.Width : cell.X + CellPadX;
                dc.DrawText(ft, new Point(tx, y + (RowHeight - ft.Height) / 2));
            }
            dc.DrawRectangle(b.Line, null, new Rect(gutter, Snap(y + RowHeight) - px, Math.Max(0, Math.Min(w, lastColRight) - gutter), px));
        }
        for (var c = c0; c < c1; c++)
            dc.DrawRectangle(b.Line, null, new Rect(Snap(colX[c + 1]) - px, HeaderHeight, px, Math.Max(0, Math.Min(h, HeaderHeight + ContentHeight - ScrollY) - HeaderHeight)));
        // active cell
        if (HasSelection && Active.Row >= first && Active.Row <= last && Active.Col < Widths.Length)
        {
            var a = CellRect(Active.Row, Active.Col);
            a.Inflate(-0.75, -0.75);
            dc.DrawRoundedRectangle(null, Pen(b.Accent, 1.5), a, 2, 2);
        }
        dc.Pop();

        // gutter (row numbers): data row number in the file, so a sorted view still says where a row lives
        dc.PushClip(new RectangleGeometry(new Rect(0, HeaderHeight, gutter, Math.Max(0, h - HeaderHeight))));
        dc.DrawRectangle(b.Gutter, null, new Rect(0, HeaderHeight, gutter, h - HeaderHeight));
        var (sr0, sr1) = HasSelection ? (Math.Min(Anchor.Row, Active.Row), Math.Max(Anchor.Row, Active.Row)) : (-1, -1);
        for (var r = first; r <= last; r++)
        {
            var y = HeaderHeight + r * RowHeight - ScrollY;
            var selected = r >= sr0 && r <= sr1;
            if (selected) dc.DrawRectangle(b.SelHeader, null, new Rect(0, y, gutter, RowHeight));
            var n = (View.SourceRow(r) - Table.HeaderRowCount + 1).ToString("N0", CultureInfo.CurrentCulture);
            var ft = Text(n, selected ? HeaderFace : CellFace, 11, selected ? b.Dim : b.Faint, gutter - 12, ppd);
            dc.DrawText(ft, new Point(gutter - 10 - ft.Width, y + (RowHeight - ft.Height) / 2));
            dc.DrawRectangle(b.Line, null, new Rect(0, Snap(y + RowHeight) - px, gutter, px));
        }
        dc.Pop();
        dc.DrawRectangle(b.LineStrong, null, new Rect(Snap(gutter) - px, 0, px, h));

        // sticky header
        dc.DrawRectangle(b.Header, null, new Rect(0, 0, w, HeaderHeight));
        dc.PushClip(new RectangleGeometry(new Rect(gutter, 0, Math.Max(0, w - gutter), HeaderHeight)));
        var headers = Table.GetHeaders();
        var (sc0, sc1) = HasSelection ? (Math.Min(Anchor.Col, Active.Col), Math.Max(Anchor.Col, Active.Col)) : (-1, -1);
        for (var c = c0; c < c1; c++)
        {
            var cell = new Rect(colX[c], 0, Widths[c], HeaderHeight);
            if (c >= sc0 && c <= sc1) dc.DrawRectangle(b.SelHeader, null, cell);
            var sorted = c == SortColumn;
            var room = Widths[c] - CellPadX * 2 - (sorted ? 14 : 0);
            var title = Table.HeaderRowCount > 0 ? headers[c] : CsvTable.ColumnLetters(c);
            var ft = Text(Display(title), HeaderFace, 12, sorted ? b.Text : b.Dim, room, ppd);
            var tx = Numeric[c] ? cell.Right - CellPadX - ft.Width - (sorted ? 14 : 0) : cell.X + CellPadX;
            dc.DrawText(ft, new Point(tx, (HeaderHeight - ft.Height) / 2));
            if (sorted) SortMark(dc, b.Accent, new Point(Numeric[c] ? cell.Right - CellPadX - 4 : Math.Min(tx + ft.Width + 9, cell.Right - CellPadX - 4), HeaderHeight / 2), SortDescending);
            dc.DrawRectangle(b.Line, null, new Rect(Snap(colX[c + 1]) - px, 6, px, HeaderHeight - 12));
        }
        dc.Pop();
        dc.DrawRectangle(b.LineStrong, null, new Rect(0, Snap(HeaderHeight) - px, w, px));
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

    private static FormattedText Text(string s, Typeface face, double size, Brush brush, double maxWidth, double ppd)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush, ppd)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        if (maxWidth > 4) ft.MaxTextWidth = maxWidth;
        return ft;
    }
}
