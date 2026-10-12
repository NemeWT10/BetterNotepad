using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BetterNotepad.Markdown;

/// <summary>
/// Read-only Markdown preview: a FlowDocumentScrollViewer around <see cref="MarkdownRenderer"/>.
/// All source lines in the API are 1-based (editor convention). Use on the UI thread only.
/// </summary>
public sealed class MarkdownPreview : UserControl, IDisposable
{
    private readonly FlowDocumentScrollViewer _viewer;
    private readonly MarkdownRenderer _renderer;
    private ScrollViewer? _scroll;
    private DispatcherTimer? _timer;
    private string _text = "";
    private string? _base;
    private bool _dirty;
    private bool _rendered;
    private bool _disposed;
    private double _expectedOffset = double.NaN;
    private (int Line, double Fraction)? _pendingScroll;
    private MarkdownBlockInfo? _anchorBlock;
    private double _anchorRel;
    private int _lastReportedLine = -1;
    private RichContent _lastRich;
    private double _lastColumnWidth = -1;

    public MarkdownPreview(MarkdownOptions? options = null)
    {
        _renderer = new MarkdownRenderer(options);
        _renderer.LinkClicked += OnRendererLink;
        _renderer.TaskToggled += (line, isChecked) => TaskToggled?.Invoke(line, isChecked);
        _renderer.ScrollRequested += ScrollToElement;
        _renderer.ViewState = () => _scroll is null ? (0, 1) : (_scroll.VerticalOffset, ZoomFactor);

        _viewer = new FlowDocumentScrollViewer
        {
            Document = _renderer.Document,
            IsToolBarVisible = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsSelectionEnabled = true,
            MinZoom = 50,
            MaxZoom = 300,
            BorderThickness = new Thickness(0),
            SelectionOpacity = 1,
        };
        _viewer.SetResourceReference(FlowDocumentScrollViewer.SelectionBrushProperty, K.Selection);
        _viewer.ContextMenu = BuildContextMenu();
        SetResourceReference(BackgroundProperty, K.Bg);
        Content = _viewer;
        Focusable = false;

        _viewer.SizeChanged += OnViewerSizeChanged;
        _viewer.MouseDoubleClick += OnDoubleClick;
        IsVisibleChanged += OnVisibleChanged;
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(FlowDocumentScrollViewer.ZoomProperty, typeof(FlowDocumentScrollViewer))
            .AddValueChanged(_viewer, OnZoomChanged);
        Loaded += (_, _) => EnsureScrollViewer();
    }

    // ---------------------------------------------------------------- public surface

    public MarkdownOptions Options => _renderer.Options;

    /// <summary>The live FlowDocument (selection, find, zoom live on the viewer).</summary>
    public FlowDocument Document => _renderer.Document;

    public IReadOnlyList<MarkdownHeading> Headings => _renderer.Headings;

    public RichContent RichContent => _renderer.RichContent;

    public IReadOnlyList<MarkdownBlockInfo> Blocks => _renderer.Blocks;

    /// <summary>Zoom in percent (50..300). Ctrl+wheel zooms too.</summary>
    public double Zoom
    {
        get => _viewer.Zoom;
        set => _viewer.Zoom = Math.Clamp(value, 50, 300);
    }

    /// <summary>The headings list changed (rebuild the table of contents).</summary>
    public event Action? HeadingsChanged;

    /// <summary>Double-click on a block: its 1-based source line.</summary>
    public event Action<int>? LineRequested;

    /// <summary>A task checkbox was clicked: 1-based source line of the item and the new state. The app edits the text.</summary>
    public event Action<int, bool>? TaskToggled;

    /// <summary>A link other than an in-page "#anchor" was clicked (raw URL; resolve relative paths against the base folder).</summary>
    public event Action<string>? LinkClicked;

    /// <summary>The user scrolled the preview: 1-based top line and progress through it (for editor sync).</summary>
    public event Action<int, double>? TopLineChanged;

    /// <summary>Mermaid / math / complex HTML appeared; the app may switch to a WebView2 renderer. Fires when the set changes.</summary>
    public event Action<RichContent>? NeedsRichRenderer;

    /// <summary>
    /// Sets the document. Debounced (~35-260 ms depending on size) and incremental; the scroll position is kept.
    /// Rendering is deferred until the control is visible.
    /// </summary>
    public void Update(string markdown, string? baseFolder)
    {
        if (_disposed) return;
        markdown ??= "";
        if (_rendered && !_dirty && markdown == _text && baseFolder == _base) return;
        _text = markdown;
        _base = baseFolder;
        _dirty = true;
        if (!IsVisible) return;
        if (!_rendered) RenderNow();
        else Schedule();
    }

    /// <summary>Renders a pending update immediately (before printing, tests).</summary>
    public void FlushPending()
    {
        if (_disposed) return;
        if (_dirty && IsVisible) RenderNow();
    }

    /// <summary>Full re-render with the current options (theme-dependent highlighting changed).</summary>
    public void Refresh()
    {
        _renderer.Invalidate();
        _dirty = true;
        if (IsVisible) RenderNow();
    }

    /// <summary>Frees the rendered document and decoded images of a hidden tab; the next visible Update renders again.</summary>
    public void ReleaseContent()
    {
        StopTimer();
        _renderer.Invalidate();
        _renderer.ReleaseImages();
        _renderer.Render("", null);
        _anchorBlock = null;
        _rendered = false;
        _dirty = true;
    }

    /// <summary>Editor to preview sync: scrolls so source <paramref name="line"/> (1-based) is at the top. <paramref name="fraction"/> is the progress through that line.</summary>
    public void ScrollToLine(int line, double fraction)
    {
        if (_disposed) return;
        if (_dirty || !_rendered)
        {
            _pendingScroll = (line, fraction);
            return;
        }

        ApplyScroll(line, fraction, 0);
    }

    /// <summary>
    /// Like <see cref="ScrollToLine(int,double)"/> but blends toward the end of the document as the editor nears its end
    /// (<paramref name="scrollFraction"/> = editor scroll position 0..1), so both reach the bottom together.
    /// </summary>
    public void ScrollToLine(int line, double lineFraction, double scrollFraction)
    {
        if (_disposed) return;
        if (_dirty || !_rendered)
        {
            _pendingScroll = (line, lineFraction);
            return;
        }

        ApplyScroll(line, lineFraction, scrollFraction);
    }

    /// <summary>Scrolls to a fraction (0..1) of the scrollable height.</summary>
    public void ScrollToFraction(double fraction)
    {
        EnsureScrollViewer();
        if (_scroll is null) return;
        SetOffset(Math.Clamp(fraction, 0, 1) * _scroll.ScrollableHeight);
    }

    public void ScrollToHeading(MarkdownHeading heading)
    {
        if (_disposed || !_rendered) return;
        if (_renderer.OffsetOfLine(heading.Line, 0) is { } y) SetOffset(Math.Max(0, y - 14));
    }

    /// <summary>1-based first visible source line and the progress through it.</summary>
    public int GetTopLine(out double fraction)
    {
        fraction = 0;
        EnsureScrollViewer();
        if (_scroll is null || !_rendered) return 1;
        return _renderer.LineAtOffset(_scroll.VerticalOffset, out fraction);
    }

    /// <summary>Vertical scroll offset (ScrollViewer units) that puts a source line at the top, or null before layout.</summary>
    public double? OffsetOfLine(int line, double fraction = 0) =>
        _renderer.OffsetOfLine(line, fraction) is { } y ? y : null;

    /// <summary>Source line (1-based) at a vertical scroll offset (ScrollViewer units).</summary>
    public int LineAtOffset(double verticalOffset, out double fraction) =>
        _renderer.LineAtOffset(verticalOffset, out fraction);

    public MarkdownBlockInfo? FindBlock(int line) => _renderer.FindBlock(line);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopTimer();
        IsVisibleChanged -= OnVisibleChanged;
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(FlowDocumentScrollViewer.ZoomProperty, typeof(FlowDocumentScrollViewer))
            .RemoveValueChanged(_viewer, OnZoomChanged);
        _viewer.SizeChanged -= OnViewerSizeChanged;
        _viewer.MouseDoubleClick -= OnDoubleClick;
        if (_scroll is not null) _scroll.ScrollChanged -= OnScrollChanged;
        _viewer.Document = null;
        _renderer.Dispose();
        _anchorBlock = null;
    }

    // ---------------------------------------------------------------- rendering

    private double ZoomFactor => Math.Max(0.1, _viewer.Zoom / 100.0);

    private void Schedule()
    {
        if (_timer is not null) return; // throttle: the pending tick renders the latest text
        var ms = Math.Clamp(35 + _text.Length / 1024.0 * 0.6, 35, 260);
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(ms) };
        _timer.Tick += OnTimer;
        _timer.Start();
    }

    private void OnTimer(object? sender, EventArgs e)
    {
        StopTimer();
        if (_dirty && IsVisible && !_disposed) RenderNow();
    }

    private void StopTimer()
    {
        if (_timer is null) return;
        _timer.Stop();
        _timer.Tick -= OnTimer;
        _timer = null;
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // deferred: this can fire in the middle of a layout pass
        if (IsVisible && _dirty) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_disposed && _dirty && IsVisible) RenderNow();
        }));
    }

    private void OnZoomChanged(object? sender, EventArgs e)
    {
        _lastColumnWidth = -1;
        UpdateColumn();
        _renderer.InvalidateLayout();
    }

    private void RenderNow()
    {
        StopTimer();
        if (_disposed) return;
        _dirty = false;
        EnsureScrollViewer();
        UpdateColumn();

        var offset = _scroll?.VerticalOffset ?? 0;
        (MarkdownBlockInfo? Block, double Rel, int Line, double Fraction, (int Length, int Hash, int Index) Key)? anchor = null;
        if (_rendered && offset > 1) anchor = CaptureAnchor(offset);

        var result = _renderer.Render(_text, _base);
        _rendered = true;
        UpdateLayout();
        _renderer.InvalidateLayout();

        if (_pendingScroll is { } ps)
        {
            _pendingScroll = null;
            ApplyScroll(ps.Line, ps.Fraction, 0);
        }
        else if (anchor is { } a)
        {
            RestoreAnchor(a);
        }

        CaptureViewAnchor();

        if (result.HeadingsChanged) HeadingsChanged?.Invoke();
        var rich = _renderer.RichContent;
        if (rich != _lastRich)
        {
            _lastRich = rich;
            if (rich != RichContent.None) NeedsRichRenderer?.Invoke(rich);
        }
    }

    private (MarkdownBlockInfo? Block, double Rel, int Line, double Fraction, (int Length, int Hash, int Index) Key) CaptureAnchor(double offset)
    {
        var y = offset;
        var line = _renderer.LineAtOffset(y, out var fraction);
        var block = _renderer.FindBlock(line);
        double rel = 0;
        if (block?.Element is { } el && _renderer.TopOfElement(el) is { } top) rel = top - offset;
        else block = null;
        var key = block is null ? default : _renderer.KeyOf(block);
        return (block, rel, line, fraction, key);
    }

    private void RestoreAnchor((MarkdownBlockInfo? Block, double Rel, int Line, double Fraction, (int Length, int Hash, int Index) Key) a)
    {
        var target = a.Block is { Alive: true } ? a.Block : a.Block is null ? null : _renderer.FindByKey(a.Key);
        if (target?.Element is { } el && _renderer.TopOfElement(el) is { } top)
        {
            SetOffset(Math.Max(0, top - a.Rel));
            return;
        }

        if (_renderer.OffsetOfLine(a.Line, a.Fraction) is { } y) SetOffset(Math.Max(0, y));
    }

    /// <summary>Remembers which block sits at the top of the view, so late layout changes (images) can be compensated.</summary>
    private void CaptureViewAnchor()
    {
        if (_scroll is null || !_rendered) return;
        var offset = _scroll.VerticalOffset;
        if (offset <= 1)
        {
            _anchorBlock = null;
            return;
        }

        var a = CaptureAnchor(offset);
        _anchorBlock = a.Block;
        _anchorRel = a.Rel;
    }

    // ---------------------------------------------------------------- layout and scrolling

    private void EnsureScrollViewer()
    {
        if (_scroll is not null) return;
        _viewer.ApplyTemplate();
        _scroll = _viewer.Template?.FindName("PART_ContentHost", _viewer) as ScrollViewer ?? FindScrollViewer(_viewer);
        if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }

        return null;
    }

    private void OnViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        UpdateColumn();
        _renderer.InvalidateLayout();
    }

    /// <summary>Centers a column of at most MaxContentWidth by padding the page; images follow the column width.</summary>
    private void UpdateColumn()
    {
        var width = (_viewer.ActualWidth - 12) / ZoomFactor;
        if (width <= 40) return;
        var max = Options.MaxContentWidth;
        var min = width < 560 ? 18.0 : 32.0;
        var side = Math.Max(min, Math.Round((width - max) / 2));
        var column = Math.Max(120, width - 2 * side);
        if (Math.Abs(column - _lastColumnWidth) < 0.5) return;
        _lastColumnWidth = column;
        _renderer.SetColumnWidth(column);
        var pad = _renderer.Document.PagePadding;
        _renderer.Document.PagePadding = new Thickness(side, pad.Top, side, pad.Bottom);
    }

    private void ApplyScroll(int line, double fraction, double scrollFraction)
    {
        EnsureScrollViewer();
        if (_scroll is null) return;
        if (_renderer.OffsetOfLine(line, fraction) is not { } y) return;
        var target = Math.Max(0, y);
        // blend toward the bottom as the editor nears its end so both finish together
        if (scrollFraction > 0.85)
        {
            var w = Math.Pow((scrollFraction - 0.85) / 0.15, 2);
            target += (_scroll.ScrollableHeight - target) * Math.Clamp(w, 0, 1);
        }

        SetOffset(target);
    }

    private void SetOffset(double offset)
    {
        if (_scroll is null) return;
        offset = Math.Clamp(offset, 0, Math.Max(0, _scroll.ScrollableHeight));
        if (Math.Abs(offset - _scroll.VerticalOffset) < 0.5) return;
        _expectedOffset = offset;
        _scroll.ScrollToVerticalOffset(offset);
    }

    private void ScrollToElement(TextElement element)
    {
        if (_renderer.TopOfElement(element) is { } top) SetOffset(Math.Max(0, top - 80));
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0 || e.ExtentWidthChange != 0 || e.ViewportHeightChange != 0)
        {
            _renderer.InvalidateLayout();
            // content above the view changed height (image loaded): keep what the reader looks at in place
            if (e.VerticalChange == 0 && _anchorBlock is { Alive: true, Element: { } el } && _renderer.TopOfElement(el) is { } top)
            {
                var wanted = Math.Max(0, top - _anchorRel);
                if (Math.Abs(wanted - _scroll!.VerticalOffset) > 1.5)
                {
                    SetOffset(wanted);
                    return;
                }
            }
        }

        if (e.VerticalChange == 0) return;

        if (!double.IsNaN(_expectedOffset) && Math.Abs(_scroll!.VerticalOffset - _expectedOffset) < 1.5)
        {
            _expectedOffset = double.NaN;
            CaptureViewAnchor();
            return;
        }

        _expectedOffset = double.NaN;
        CaptureViewAnchor();
        if (TopLineChanged is null || !_rendered) return;
        var line = GetTopLine(out var fraction);
        if (line == _lastReportedLine && fraction < 0.001) return;
        _lastReportedLine = line;
        TopLineChanged(line, fraction);
    }

    // ---------------------------------------------------------------- interaction

    private void OnRendererLink(string url)
    {
        if (url.StartsWith('#'))
        {
            var slug = Uri.UnescapeDataString(url[1..]).ToLowerInvariant();
            var h = Headings.FirstOrDefault(x => x.Slug == slug);
            if (h is not null) ScrollToHeading(h);
            return;
        }

        LinkClicked?.Invoke(url);
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || LineRequested is null) return;
        int? line = null;
        if (e.OriginalSource is DependencyObject d) line = _renderer.SourceLineOf(d);

        if (line is null)
        {
            try
            {
                line = _renderer.SourceLineOf(_viewer.Selection?.Start.Parent);
            }
            catch
            {
                // selection not available
            }
        }

        if (line is null && _scroll is not null)
        {
            var y = _scroll.VerticalOffset + e.GetPosition(_scroll).Y / ZoomFactor;
            line = _renderer.LineAtOffset(y, out _);
        }

        if (line is { } l) LineRequested(l);
    }

    private ContextMenu BuildContextMenu()
    {
        var labels = Options.Labels;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = labels.Copy, Command = ApplicationCommands.Copy, CommandTarget = _viewer });
        menu.Items.Add(new MenuItem { Header = labels.SelectAll, Command = ApplicationCommands.SelectAll, CommandTarget = _viewer });
        return menu;
    }
}
