using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BetterNotepad.Modules.Pdf;

/// <summary>One realized page of a <see cref="PdfStrip"/>: the bitmap plus an overlay for search hits and the text selection.</summary>
internal sealed class PageSlot : Grid
{
    public PageSlot(int index)
    {
        Index = index;
        Children.Add(Image);
        Children.Add(Overlay);
        RenderOptions.SetBitmapScalingMode(Image, BitmapScalingMode.HighQuality);
    }

    public int Index { get; }

    public Image Image { get; } = new() { Stretch = Stretch.Fill };

    public Canvas Overlay { get; } = new() { IsHitTestVisible = false };

    /// <summary>Selection range drawn on the overlay ((0, 0) = none), so a drag only redraws the pages whose range changed.</summary>
    public (int A, int B) DrawnSelection { get; set; }

    /// <summary>Render scale of the bitmap currently shown (0 = none yet).</summary>
    public double RenderedScale { get; set; }

    public CancellationTokenSource? Cts { get; set; }
}

/// <summary>
/// A vertical list of pages that only keeps the visible ones (plus one above and below) as elements. Page sizes come
/// from a callback in DIPs, so the same strip serves the main view (zoomed pages) and the thumbnails sidebar.
/// </summary>
internal sealed class PdfStrip : Grid
{
    // top-aligned: an explicit Height smaller than the viewport would otherwise be centered (one thumbnail mid-sidebar)
    private readonly Canvas _canvas = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly Dictionary<int, PageSlot> _live = new();
    private Size[] _sizes = [];
    private double[] _tops = [];

    public PdfStrip(double gap, Thickness padding)
    {
        Gap = gap;
        Padding = padding;
        Scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false,
            Content = _canvas,
        };
        Scroll.ScrollChanged += (_, _) => UpdateVisible();
        SizeChanged += (_, _) =>
        {
            Arrange();
            UpdateVisible();
        };
        Children.Add(Scroll);
    }

    public ScrollViewer Scroll { get; }

    public double Gap { get; }

    public Thickness Padding { get; }

    public int Count => _sizes.Length;

    public IReadOnlyDictionary<int, PageSlot> Live => _live;

    /// <summary>Fills a new slot (start rendering).</summary>
    public event Action<PageSlot>? Realized;

    /// <summary>A slot left the visible range (cancel its rendering).</summary>
    public event Action<PageSlot>? Released;

    /// <summary>The page at the top of the viewport changed.</summary>
    public event Action<int>? CurrentChanged;

    public int Current { get; private set; } = -1;

    public Rect SlotRect(int index) => index >= 0 && index < _sizes.Length ? new Rect(Left(index), _tops[index], _sizes[index].Width, _sizes[index].Height) : Rect.Empty;

    /// <summary>New page sizes (zoom changed / document opened); keeps the page at the top in view.</summary>
    public void SetSizes(IReadOnlyList<Size> sizes, bool keepPosition = true)
    {
        var anchor = Current >= 0 && keepPosition ? Current : 0;
        var within = anchor < _tops.Length && _sizes[anchor].Height > 0 ? (Scroll.VerticalOffset - _tops[anchor]) / _sizes[anchor].Height : 0;
        _sizes = [.. sizes];
        _tops = new double[_sizes.Length];
        var y = Padding.Top;
        for (var i = 0; i < _sizes.Length; i++)
        {
            _tops[i] = y;
            y += _sizes[i].Height + Gap;
        }
        _canvas.Height = Math.Max(0, y - Gap + Padding.Bottom);
        Arrange();
        foreach (var slot in _live.Values.ToList()) Release(slot);
        if (keepPosition && anchor < _sizes.Length) Scroll.ScrollToVerticalOffset(_tops[anchor] + Math.Clamp(within, 0, 1) * _sizes[anchor].Height);
        UpdateLayout();
        UpdateVisible();
    }

    private double ContentWidth => _sizes.Length == 0 ? 0 : _sizes.Max(s => s.Width) + Padding.Left + Padding.Right;

    private double Left(int i) => Math.Max(Padding.Left, (Math.Max(ContentWidth, Scroll.ViewportWidth > 0 ? Scroll.ViewportWidth : ActualWidth) - _sizes[i].Width) / 2);

    private void Arrange()
    {
        _canvas.Width = Math.Max(ContentWidth, Math.Max(0, Scroll.ViewportWidth > 0 ? Scroll.ViewportWidth : ActualWidth - 2));
        foreach (var slot in _live.Values) Canvas.SetLeft(slot, Left(slot.Index));
    }

    public int PageAt(double y)
    {
        if (_tops.Length == 0) return -1;
        var lo = 0;
        var hi = _tops.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_tops[mid] <= y) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    public void ScrollToPage(int index, double offsetWithin = 0)
    {
        if (index < 0 || index >= _tops.Length) return;
        Scroll.ScrollToVerticalOffset(Math.Max(0, _tops[index] - Padding.Top + offsetWithin));
    }

    /// <summary>Scrolls so that a rectangle of a page (DIPs within the page) is comfortably visible.</summary>
    public void BringIntoView(int index, Rect r)
    {
        if (index < 0 || index >= _tops.Length) return;
        var top = _tops[index] + r.Top;
        var bottom = _tops[index] + r.Bottom;
        var v = Scroll.VerticalOffset;
        var h = Scroll.ViewportHeight;
        if (top < v + 24 || bottom > v + h - 24) Scroll.ScrollToVerticalOffset(Math.Max(0, top - h / 3));
        var left = Left(index) + r.Left;
        if (left < Scroll.HorizontalOffset || left + r.Width > Scroll.HorizontalOffset + Scroll.ViewportWidth)
            Scroll.ScrollToHorizontalOffset(Math.Max(0, left - Scroll.ViewportWidth / 3));
    }

    public void UpdateVisible()
    {
        if (_sizes.Length == 0)
        {
            foreach (var slot in _live.Values.ToList()) Release(slot);
            return;
        }
        var top = Scroll.VerticalOffset;
        var bottom = top + Math.Max(Scroll.ViewportHeight, ActualHeight);
        var first = Math.Max(0, PageAt(top) - 1);
        var last = Math.Min(_sizes.Length - 1, PageAt(bottom) + 1);
        foreach (var slot in _live.Values.Where(s => s.Index < first || s.Index > last).ToList()) Release(slot);
        for (var i = first; i <= last; i++)
        {
            if (_live.ContainsKey(i)) continue;
            var slot = new PageSlot(i) { Width = _sizes[i].Width, Height = _sizes[i].Height };
            Canvas.SetLeft(slot, Left(i));
            Canvas.SetTop(slot, _tops[i]);
            _live[i] = slot;
            _canvas.Children.Add(slot);
            Realized?.Invoke(slot);
        }
        var current = PageAt(top + Math.Min(Scroll.ViewportHeight * 0.3, 120));
        if (current != Current)
        {
            Current = current;
            CurrentChanged?.Invoke(current);
        }
    }

    private void Release(PageSlot slot)
    {
        _live.Remove(slot.Index);
        _canvas.Children.Remove(slot);
        Released?.Invoke(slot);
    }

    /// <summary>Page and point in page DIPs under a position relative to this strip.</summary>
    public bool HitPage(Point p, out int index, out Point inPage)
    {
        var c = TranslatePoint(p, _canvas);
        index = PageAt(c.Y);
        inPage = default;
        if (index < 0) return false;
        var r = SlotRect(index);
        inPage = new Point(c.X - r.Left, c.Y - r.Top);
        return r.Contains(c);
    }

    public Point ToCanvas(Point p) => TranslatePoint(p, _canvas);

    public Point FromCanvas(Point p) => _canvas.TranslatePoint(p, this);
}
