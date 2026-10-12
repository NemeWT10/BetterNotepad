using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace BetterNotepad.Hex;

/// <summary>
/// Read only hex view for files of any size: offset | 16 bytes in hex (grouped by 8) | text column. Only the visible rows are
/// read (from a memory buffer or a paged file source) and drawn, with glyph runs, so a 4 GB file scrolls like a 4 KB one.
/// Selection works by mouse and keyboard and is shared by both panes; copy as hex / as text; go to offset; smooth wheel scrolling.
/// <para>
/// Colours come from the app theme through <c>SetResourceReference</c> (Brush.Editor.Bg, Brush.Editor.Text, Brush.TextFaint,
/// Brush.Editor.Selection, Brush.Editor.SelectionInactive, Brush.Editor.CurrentLine, Brush.Editor.Caret, Brush.Border), so a live theme
/// switch repaints it. The scroll bar is a normal <see cref="ScrollBar"/> and picks up the app's slim style. The control has no text
/// of its own: header labels and the empty-file message are properties for the host to fill (localized).
/// </para>
/// </summary>
public sealed partial class HexView : FrameworkElement
{
    private const double ScrollTau = 0.06; // seconds; the smoothing time constant of wheel scrolling

    private readonly ScrollBar _scroll = new() { Orientation = Orientation.Vertical, Minimum = 0, SmallChange = 1, Focusable = false };
    private IHexSource? _source;
    private bool _ownsSource;
    private long _length;

    private double _scrollRow;
    private double _targetRow;
    private bool _syncingScroll;
    private bool _animating;
    private long _lastFrame;

    private long _anchor;
    private long _caret;
    private HexPane _activePane = HexPane.Hex;

    private HexGeometry _geo;
    private bool _metricsDirty = true;
    private GlyphTypeface? _glyphs;
    private double _fontSize;
    private double _pixelsPerDip = 1;
    private double _baseline;
    private double _headerHeight;
    private double _scrollBarWidth;
    private double _bodyHeight;
    private int _effectiveBytesPerRow;
    private readonly ushort[] _byteGlyph = new ushort[256];
    private readonly ushort[] _digitGlyph = new ushort[16];
    private readonly ushort[] _textGlyph = new ushort[256];
    private readonly bool[] _textShown = new bool[256];
    private ushort _spaceGlyph;
    private ushort _dotGlyph;
    private HexTextTable _table = HexTextTable.For(HexTextEncoding.Windows1250);
    private byte[] _rowBuffer = new byte[16 * 64];

    static HexView()
    {
        FocusableProperty.OverrideMetadata(typeof(HexView), new FrameworkPropertyMetadata(true));
        FocusVisualStyleProperty.OverrideMetadata(typeof(HexView), new FrameworkPropertyMetadata((Style?)null));
        ClipToBoundsProperty.OverrideMetadata(typeof(HexView), new FrameworkPropertyMetadata(true));
        CursorProperty.OverrideMetadata(typeof(HexView), new FrameworkPropertyMetadata(Cursors.IBeam));
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(HexView), new FrameworkPropertyMetadata(true));
    }

    public HexView()
    {
        SetResourceReference(BackgroundProperty, "Brush.Editor.Bg");
        SetResourceReference(ForegroundProperty, "Brush.Editor.Text");
        SetResourceReference(FaintForegroundProperty, "Brush.TextFaint");
        SetResourceReference(SelectionBrushProperty, "Brush.Editor.Selection");
        SetResourceReference(InactiveSelectionBrushProperty, "Brush.Editor.SelectionInactive");
        SetResourceReference(CurrentLineBrushProperty, "Brush.Editor.CurrentLine");
        SetResourceReference(CaretBrushProperty, "Brush.Editor.Caret");
        SetResourceReference(SeparatorBrushProperty, "Brush.Border");

        AddVisualChild(_scroll);
        AddLogicalChild(_scroll);
        _scroll.ValueChanged += OnScrollBarChanged;
        Unloaded += (_, _) => StopAnimation();
    }

    // ------------------------------------------------------------------ dependency properties

    private static DependencyProperty Reg<T>(string name, T defaultValue, FrameworkPropertyMetadataOptions options, PropertyChangedCallback? changed = null)
        => DependencyProperty.Register(name, typeof(T), typeof(HexView), new FrameworkPropertyMetadata(defaultValue, options, changed));

    private static void MetricsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((HexView)d).OnMetricsChanged();

    private const FrameworkPropertyMetadataOptions Render = FrameworkPropertyMetadataOptions.AffectsRender;

    public static readonly DependencyProperty BackgroundProperty = Reg<Brush?>(nameof(Background), null, Render);
    public static readonly DependencyProperty ForegroundProperty = Reg<Brush?>(nameof(Foreground), null, Render);
    public static readonly DependencyProperty FaintForegroundProperty = Reg<Brush?>(nameof(FaintForeground), null, Render);
    public static readonly DependencyProperty SelectionBrushProperty = Reg<Brush?>(nameof(SelectionBrush), null, Render);
    public static readonly DependencyProperty InactiveSelectionBrushProperty = Reg<Brush?>(nameof(InactiveSelectionBrush), null, Render);
    public static readonly DependencyProperty CurrentLineBrushProperty = Reg<Brush?>(nameof(CurrentLineBrush), null, Render);
    public static readonly DependencyProperty CaretBrushProperty = Reg<Brush?>(nameof(CaretBrush), null, Render);
    public static readonly DependencyProperty SeparatorBrushProperty = Reg<Brush?>(nameof(SeparatorBrush), null, Render);

    public static readonly DependencyProperty CodeFontFamilyProperty = Reg("CodeFontFamily", "Cascadia Mono, Consolas", Render, MetricsChanged);
    public static readonly DependencyProperty CodeFontSizeProperty = Reg("CodeFontSize", 13.0, Render, MetricsChanged);
    public static readonly DependencyProperty TextEncodingProperty = Reg("TextEncoding", HexTextEncoding.Windows1250, Render, MetricsChanged);
    public static readonly DependencyProperty BytesPerRowProperty = Reg("BytesPerRow", 16, Render, MetricsChanged);
    public static readonly DependencyProperty AutoFitProperty = Reg("AutoFit", true, Render, MetricsChanged);
    public static readonly DependencyProperty ShowHeaderProperty = Reg("ShowHeader", true, Render, MetricsChanged);
    public static readonly DependencyProperty OffsetLabelProperty = Reg("OffsetLabel", "", Render);
    public static readonly DependencyProperty TextLabelProperty = Reg("TextLabel", "", Render);
    public static readonly DependencyProperty EmptyTextProperty = Reg("EmptyText", "", Render);
    public static readonly DependencyProperty SmoothScrollingProperty = Reg("SmoothScrolling", true, FrameworkPropertyMetadataOptions.None);
    public static readonly DependencyProperty AlwaysActiveSelectionProperty = Reg("AlwaysActiveSelection", false, Render);
    public static readonly DependencyProperty MaxCopyBytesProperty = Reg("MaxCopyBytes", 8L * 1024 * 1024, FrameworkPropertyMetadataOptions.None);

    public Brush? Background { get => (Brush?)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    public Brush? Foreground { get => (Brush?)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>Offsets, zero bytes and bytes without a printable character.</summary>
    public Brush? FaintForeground { get => (Brush?)GetValue(FaintForegroundProperty); set => SetValue(FaintForegroundProperty, value); }

    public Brush? SelectionBrush { get => (Brush?)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }

    public Brush? InactiveSelectionBrush { get => (Brush?)GetValue(InactiveSelectionBrushProperty); set => SetValue(InactiveSelectionBrushProperty, value); }

    public Brush? CurrentLineBrush { get => (Brush?)GetValue(CurrentLineBrushProperty); set => SetValue(CurrentLineBrushProperty, value); }

    public Brush? CaretBrush { get => (Brush?)GetValue(CaretBrushProperty); set => SetValue(CaretBrushProperty, value); }

    public Brush? SeparatorBrush { get => (Brush?)GetValue(SeparatorBrushProperty); set => SetValue(SeparatorBrushProperty, value); }

    /// <summary>Font family list of the grid; the first installed monospaced family wins.</summary>
    public string CodeFontFamily { get => (string)GetValue(CodeFontFamilyProperty); set => SetValue(CodeFontFamilyProperty, value); }

    /// <summary>Font size in DIPs (pt * 96 / 72).</summary>
    public double CodeFontSize { get => (double)GetValue(CodeFontSizeProperty); set => SetValue(CodeFontSizeProperty, value); }

    public HexTextEncoding TextEncoding { get => (HexTextEncoding)GetValue(TextEncodingProperty); set => SetValue(TextEncodingProperty, value); }

    /// <summary>8, 16 (default), 24 or 32 bytes per row.</summary>
    public int BytesPerRow { get => (int)GetValue(BytesPerRowProperty); set => SetValue(BytesPerRowProperty, value); }

    /// <summary>When the width does not fit <see cref="BytesPerRow"/>, fall back to 8 bytes per row instead of clipping.</summary>
    public bool AutoFit { get => (bool)GetValue(AutoFitProperty); set => SetValue(AutoFitProperty, value); }

    public bool ShowHeader { get => (bool)GetValue(ShowHeaderProperty); set => SetValue(ShowHeaderProperty, value); }

    /// <summary>Header caption above the offset column (localized by the host).</summary>
    public string OffsetLabel { get => (string)GetValue(OffsetLabelProperty); set => SetValue(OffsetLabelProperty, value ?? ""); }

    /// <summary>Header caption above the text column (localized by the host).</summary>
    public string TextLabel { get => (string)GetValue(TextLabelProperty); set => SetValue(TextLabelProperty, value ?? ""); }

    /// <summary>Message centered in the view when the file is empty (localized by the host).</summary>
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value ?? ""); }

    /// <summary>Animate wheel and keyboard scrolling (bind to the app's animation switch).</summary>
    public bool SmoothScrolling { get => (bool)GetValue(SmoothScrollingProperty); set => SetValue(SmoothScrollingProperty, value); }

    /// <summary>Draw the selection in its active colour even when the control has no keyboard focus (e.g. while a go-to box has it).</summary>
    public bool AlwaysActiveSelection { get => (bool)GetValue(AlwaysActiveSelectionProperty); set => SetValue(AlwaysActiveSelectionProperty, value); }

    /// <summary>Largest selection the copy commands accept.</summary>
    public long MaxCopyBytes { get => (long)GetValue(MaxCopyBytesProperty); set => SetValue(MaxCopyBytesProperty, value); }

    // ------------------------------------------------------------------ state for the host

    public IHexSource? Source => _source;

    /// <summary>Number of bytes in the source.</summary>
    public long Length => _length;

    /// <summary>The byte under the caret; the selection is always at least this byte (when the file is not empty).</summary>
    public long CaretOffset => _caret;

    public long SelectionStart => Math.Min(_anchor, _caret);

    /// <summary>Selected bytes (0 for an empty file).</summary>
    public long SelectionLength => _length == 0 ? 0 : Math.Abs(_caret - _anchor) + 1;

    /// <summary>The pane that last received a click (where the caret is drawn emphasized).</summary>
    public HexPane ActivePane
    {
        get => _activePane;
        set
        {
            if (_activePane == value) return;
            _activePane = value;
            InvalidateVisual();
        }
    }

    /// <summary>Column layout currently in use (DIPs): where the offset, hex and text columns are. Valid after the first layout.</summary>
    public HexGeometry Geometry => _geo;

    /// <summary>Height of the header row (0 when hidden); rows start below it.</summary>
    public double HeaderHeight => _headerHeight;

    /// <summary>Bytes per row currently used (smaller than <see cref="BytesPerRow"/> when <see cref="AutoFit"/> narrowed it).</summary>
    public int EffectiveBytesPerRow => _effectiveBytesPerRow == 0 ? NormalizeBytesPerRow(BytesPerRow) : _effectiveBytesPerRow;

    /// <summary>Name of the font family actually used (the first installed one of <see cref="CodeFontFamily"/>).</summary>
    public string ResolvedFontName { get; private set; } = "";

    /// <summary>First visible row, fractional while a smooth scroll is running.</summary>
    public double TopRow => _scrollRow;

    /// <summary>Offset of the first byte of the first visible row.</summary>
    public long TopOffset => (long)Math.Floor(_scrollRow) * EffectiveBytesPerRow;

    /// <summary>Number of rows that fit (fractional).</summary>
    public double ViewportRows => ViewportRowsCore;

    /// <summary>Raised when the caret or the selection changed.</summary>
    public event EventHandler? SelectionChanged;

    // ------------------------------------------------------------------ source

    /// <summary>Shows a file (read only, shared with other programs). The previous source is released.</summary>
    public void OpenFile(string path) => SetSource(FileHexSource.Open(path), ownsSource: true);

    /// <summary>Shows bytes held in memory.</summary>
    public void SetBytes(ReadOnlyMemory<byte> bytes) => SetSource(new MemoryHexSource(bytes), ownsSource: true);

    /// <summary>Shows any source. With <paramref name="ownsSource"/> the view disposes it when replaced or closed.</summary>
    public void SetSource(IHexSource? source, bool ownsSource = true)
    {
        ReleaseSource();
        _source = source;
        _ownsSource = ownsSource;
        _length = source?.Length ?? 0;
        _anchor = _caret = 0;
        _scrollRow = _targetRow = 0;
        StopAnimation();
        UpdateScrollState();
        InvalidateMeasure();
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Releases the source (closes the file).</summary>
    public void Close() => SetSource(null);

    /// <summary>Re-reads the length of the source after the file changed on disk, keeping the position.</summary>
    public void Refresh()
    {
        if (_source is FileHexSource f) f.Refresh();
        _length = _source?.Length ?? 0;
        if (_length == 0) _anchor = _caret = 0;
        else
        {
            _anchor = Math.Min(_anchor, _length - 1);
            _caret = Math.Min(_caret, _length - 1);
        }

        UpdateScrollState();
        InvalidateVisual();
    }

    private void ReleaseSource()
    {
        if (_ownsSource) _source?.Dispose();
        _source = null;
    }

    // ------------------------------------------------------------------ visual tree

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => index == 0 ? _scroll : throw new ArgumentOutOfRangeException(nameof(index));

    protected override IEnumerator LogicalChildren => new[] { _scroll }.GetEnumerator();

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureMetrics();
        _scroll.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _scrollBarWidth = _scroll.DesiredSize.Width;
        var width = double.IsInfinity(availableSize.Width) ? _geo.ContentWidth + _scrollBarWidth : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? _headerHeight + _geo.RowHeight * 16 : availableSize.Height;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        EnsureMetrics();
        _scroll.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _scrollBarWidth = _scroll.DesiredSize.Width;
        ApplyBytesPerRow(finalSize.Width - _scrollBarWidth);
        _bodyHeight = Math.Max(0, finalSize.Height - _headerHeight);
        _scroll.Arrange(new Rect(finalSize.Width - _scrollBarWidth, _headerHeight, _scrollBarWidth, _bodyHeight));
        UpdateScrollState();
        return finalSize;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _metricsDirty = true;
        InvalidateMeasure();
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ metrics

    private void OnMetricsChanged()
    {
        _metricsDirty = true;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private double ViewportRowsCore => _geo.RowHeight <= 0 ? 1 : Math.Max(1, _bodyHeight / _geo.RowHeight);

    private long TotalRows => (_length + EffectiveBytesPerRow - 1) / EffectiveBytesPerRow;

    private void EnsureMetrics()
    {
        if (!_metricsDirty && _glyphs is not null) return;
        _metricsDirty = false;
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _table = HexTextTable.For(TextEncoding);
        _fontSize = Math.Max(6, CodeFontSize);

        var typeface = ResolveTypeface(CodeFontFamily);
        if (!typeface.TryGetGlyphTypeface(out var gt))
        {
            typeface = new Typeface("Consolas");
            if (!typeface.TryGetGlyphTypeface(out gt)) return;
        }

        _glyphs = gt;
        ResolvedFontName = gt.FamilyNames.Values.FirstOrDefault() ?? "";
        var map = gt.CharacterToGlyphMap;
        ushort Glyph(char c) => map.TryGetValue(c, out var g) ? g : (map.TryGetValue('?', out var q) ? q : (ushort)0);
        _spaceGlyph = Glyph(' ');
        _dotGlyph = Glyph('.');
        for (var i = 0; i < 16; i++) _digitGlyph[i] = Glyph("0123456789ABCDEF"[i]);
        for (var b = 0; b < 256; b++)
        {
            var ch = _table.Map((byte)b);
            _textShown[b] = _table.IsPrintable((byte)b) && map.ContainsKey(ch);
            _textGlyph[b] = _textShown[b] ? map[ch] : _dotGlyph;
        }

        var zero = Glyph('0');
        var advance = gt.AdvanceWidths[zero] * _fontSize;
        var cw = Math.Max(1.0 / _pixelsPerDip, Math.Round(advance * _pixelsPerDip) / _pixelsPerDip); // whole device pixels: crisp, even columns
        var rowHeight = Math.Round(_fontSize * 1.62 * _pixelsPerDip) / _pixelsPerDip;
        var natural = gt.Height * _fontSize;
        _baseline = Math.Round(((rowHeight - natural) / 2 + gt.Baseline * _fontSize) * _pixelsPerDip) / _pixelsPerDip;
        _headerHeight = ShowHeader ? rowHeight + 2 : 0;
        _geo = new HexGeometry(cw, rowHeight, EffectiveBytesPerRow, _length);
    }

    private static int NormalizeBytesPerRow(int value) => value switch
    {
        <= 8 => 8,
        <= 16 => 16,
        <= 24 => 24,
        _ => 32,
    };

    /// <summary>Chooses the row width for the available width and keeps the first visible byte where it is when it changes.</summary>
    private void ApplyBytesPerRow(double availableWidth)
    {
        var wanted = NormalizeBytesPerRow(BytesPerRow);
        var cw = _geo.CharWidth;
        var rowHeight = _geo.RowHeight;
        var chosen = wanted;
        if (AutoFit)
        {
            while (chosen > 8 && new HexGeometry(cw, rowHeight, chosen, _length).ContentWidth > availableWidth) chosen -= 8;
        }

        var geo = new HexGeometry(cw, rowHeight, chosen, _length);
        var old = EffectiveBytesPerRow;
        if (chosen != old)
        {
            _scrollRow = _scrollRow * old / chosen;
            _targetRow = _targetRow * old / chosen;
        }

        _effectiveBytesPerRow = chosen;
        _geo = geo;
    }

    private static Typeface ResolveTypeface(string families)
    {
        foreach (var raw in (families ?? "").Split(','))
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            var tf = new Typeface(name);
            if (tf.TryGetGlyphTypeface(out var gt) && (gt.FamilyNames.Values.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
                                                      gt.Win32FamilyNames.Values.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))))
                return tf;
        }

        return new Typeface("Consolas");
    }

    // ------------------------------------------------------------------ scrolling

    private void OnScrollBarChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingScroll) return;
        StopAnimation();
        _scrollRow = _targetRow = ClampRow(e.NewValue);
        InvalidateVisual();
    }

    private double MaxScroll => Math.Max(0, TotalRows - ViewportRowsCore);

    private double ClampRow(double row) => Math.Clamp(row, 0, MaxScroll);

    private void UpdateScrollState()
    {
        var max = MaxScroll;
        _scrollRow = Math.Clamp(_scrollRow, 0, max);
        _targetRow = Math.Clamp(_targetRow, 0, max);
        _syncingScroll = true;
        try
        {
            _scroll.Maximum = max;
            // a file with millions of rows would get a thumb of a few pixels: report a larger viewport so the thumb keeps a grabbable length
            var view = ViewportRowsCore;
            var minRatio = Math.Min(0.5, 32 / Math.Max(32, _bodyHeight));
            _scroll.ViewportSize = view / (max + view) >= minRatio ? view : minRatio / (1 - minRatio) * max;
            _scroll.LargeChange = Math.Max(1, ViewportRowsCore - 1);
            _scroll.Value = _scrollRow;
            _scroll.Visibility = max > 0 ? Visibility.Visible : Visibility.Hidden;
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    /// <summary>Scrolls so that <paramref name="row"/> (fractional allowed) is the first visible row.</summary>
    public void ScrollToRow(double row, bool animate = false)
    {
        _targetRow = ClampRow(row);
        if (animate && SmoothScrolling && Math.Abs(_targetRow - _scrollRow) < ViewportRowsCore * 6) StartAnimation();
        else
        {
            StopAnimation();
            _scrollRow = _targetRow;
            ApplyScroll();
        }
    }

    private void ApplyScroll()
    {
        _syncingScroll = true;
        try
        {
            _scroll.Value = _scrollRow;
        }
        finally
        {
            _syncingScroll = false;
        }

        InvalidateVisual();
    }

    private void StartAnimation()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = Stopwatch.GetElapsedTime(_lastFrame, now).TotalSeconds;
        _lastFrame = now;
        var diff = _targetRow - _scrollRow;
        if (Math.Abs(diff) < 0.004)
        {
            _scrollRow = _targetRow;
            StopAnimation();
        }
        else _scrollRow += diff * (1 - Math.Exp(-Math.Max(dt, 0.001) / ScrollTau));

        ApplyScroll();
    }

    /// <summary>Makes the byte visible (minimal scroll; centers it when it is far away).</summary>
    public void ScrollIntoView(long offset, bool animate = true)
    {
        if (_length == 0) return;
        var bpr = EffectiveBytesPerRow;
        double row = Math.Clamp(offset, 0, _length - 1) / bpr;
        var view = ViewportRowsCore;
        double top = _targetRow;
        if (row < top) top = row;
        else if (row + 1 > top + view) top = row + 1 - view;
        else return;
        if (Math.Abs(top - _targetRow) > view * 2) top = row - (view - 1) / 2; // a long jump: land with the byte in the middle
        ScrollToRow(top, animate);
    }
}
