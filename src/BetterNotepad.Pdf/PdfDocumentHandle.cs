using System.Windows.Media.Imaging;

namespace BetterNotepad.Pdf;

/// <summary>Progress of <see cref="PdfDocumentHandle.SearchAsync"/>: hits found on the pages processed since the last report.</summary>
public sealed record PdfSearchProgress(int PagesDone, int PageCount, IReadOnlyList<PdfSearchHit> NewHits);

/// <summary>
/// An open PDF document. All members are safe to call from the UI thread: native work runs on the shared PDFium
/// engine thread and results come back as Tasks. Pages are rendered on demand (callers render only what is visible);
/// bitmaps are kept in a size-capped LRU cache (<see cref="CacheBudgetBytes"/>, default 64 MB; <see cref="Trim"/> drops it).
/// </summary>
public sealed class PdfDocumentHandle : IDisposable, IAsyncDisposable
{
    private const long MaxRenderPixels = 48_000_000;   // ~190 MB BGRA; larger views must use RenderRegionAsync
    private const int MaxSide = 30_000;

    private readonly PdfEngine _engine;
    private readonly PdfDocState _state;
    private readonly ByteLru<RenderKey, BitmapSource> _renderCache = new(64L << 20);
    private readonly ByteLru<RenderKey, BitmapSource> _thumbCache = new(16L << 20);
    private readonly ByteLru<int, PdfPageText> _textCache = new(8L << 20);
    private int _disposed;

    private readonly record struct RenderKey(int Page, int W, int H, int X, int Y, int Flags);

    private PdfDocumentHandle(PdfEngine engine, PdfDocState state, string path)
    {
        _engine = engine;
        _state = state;
        FilePath = path;
        PageCount = state.PageCount;
        PageSizes = state.Sizes;
        Metadata = state.Metadata;
    }

    ~PdfDocumentHandle()
    {
        if (_disposed == 0) { try { Dispose(); } catch { } }
    }

    public string FilePath { get; }
    public int PageCount { get; }
    /// <summary>Page sizes in points (including page rotation).</summary>
    public IReadOnlyList<PdfPageSize> PageSizes { get; }
    public PdfMetadata Metadata { get; }

    /// <summary>Pixels per point for a zoom factor where 1.0 = 100% (96 dpi screen), times the monitor DPI scale.</summary>
    public static double ScaleForZoom(double zoom, double dpiScale = 1.0) => zoom * (96.0 / 72.0) * dpiScale;

    public PdfPageSize GetPageSize(int pageIndex) => PageSizes[pageIndex];

    // ------------------------------------------------------------------ open / close

    /// <summary>
    /// Opens a PDF. Throws <see cref="PdfOpenException"/> (check <see cref="PdfOpenException.Error"/>: PasswordRequired /
    /// IncorrectPassword / InvalidFormat / FileNotFound...) or <see cref="PdfException"/> when pdfium.dll cannot be loaded.
    /// The file is streamed from disk (not loaded into memory) and stays shared for write/delete.
    /// </summary>
    public static async Task<PdfDocumentHandle> OpenAsync(string path, string? password = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ct.ThrowIfCancellationRequested();
        var (file, length) = await Task.Run(() =>
        {
            try
            {
                var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
                return (h, RandomAccess.GetLength(h));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new PdfOpenException(PdfOpenError.FileNotFound, "File not found: " + path, ex);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw new PdfOpenException(PdfOpenError.AccessDenied, "Cannot read file: " + ex.Message, ex);
            }
        }, CancellationToken.None).ConfigureAwait(false);

        var engine = PdfEngine.Acquire();
        PdfDocState state;
        try
        {
            state = await engine.Run(() => PdfDocState.Open(file, length, password)).ConfigureAwait(false);
        }
        catch
        {
            file.Dispose();
            engine.Release();
            throw;
        }
        if (ct.IsCancellationRequested)
        {
            _ = engine.Run(() => { try { state.Close(); } finally { engine.Release(); } });
            ct.ThrowIfCancellationRequested();
        }
        return new PdfDocumentHandle(engine, state, path);
    }

    /// <summary>Closes the document without blocking; native memory is freed on the engine thread.</summary>
    public void Dispose() => _ = DisposeCoreAsync();

    public ValueTask DisposeAsync() => new(DisposeCoreAsync());

    private Task DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return Task.CompletedTask;
        GC.SuppressFinalize(this);
        _renderCache.Trim(0); _thumbCache.Trim(0); _textCache.Trim(0);
        var engine = _engine; var state = _state;
        return engine.Run(() => { try { state.Close(); } finally { engine.Release(); } });
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private void CheckPage(int pageIndex)
    {
        ThrowIfDisposed();
        if ((uint)pageIndex >= (uint)PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
    }

    // ------------------------------------------------------------------ cache control

    /// <summary>Budget of the page bitmap cache in bytes (default 64 MB). Thumbnails have their own small cache.</summary>
    public long CacheBudgetBytes { get => _renderCache.Budget; set => _renderCache.Budget = value; }
    /// <summary>Bytes currently held by cached page bitmaps.</summary>
    public long CachedBytes => _renderCache.Bytes;
    public long CachedThumbnailBytes => _thumbCache.Bytes;

    /// <summary>
    /// Releases memory for a hidden / idle document: drops cached page bitmaps and text layers and closes the native page cache.
    /// Thumbnails are kept (they are tiny) unless <paramref name="includeThumbnails"/>.
    /// </summary>
    public void Trim(bool includeThumbnails = false)
    {
        if (_disposed != 0) return;
        _renderCache.Trim(0);
        _textCache.Trim(0);
        if (includeThumbnails) _thumbCache.Trim(0);
        var state = _state;
        _ = _engine.Run(() => { if (!state.Closed) state.ClearPageCache(); }, PdfPriority.Low);
    }

    // ------------------------------------------------------------------ rendering

    private static (int W, int H) FitPixels(double w, double h)
    {
        double k = 1;
        if (w * h > MaxRenderPixels) k = Math.Sqrt(MaxRenderPixels / (w * h));
        double side = Math.Max(w, h) * k;
        if (side > MaxSide) k *= MaxSide / side;
        return (Math.Max(1, (int)Math.Round(w * k, MidpointRounding.AwayFromZero)), Math.Max(1, (int)Math.Round(h * k, MidpointRounding.AwayFromZero)));
    }

    /// <summary>
    /// Pixel size a page gets at <paramref name="scale"/> (pixels per point; zoom 100% on a 96 dpi screen = 1.3333).
    /// Large scales are clamped to ~48 megapixels - use <see cref="RenderRegionAsync"/> beyond that.
    /// </summary>
    public (int Width, int Height) GetPixelSize(int pageIndex, double scale)
    {
        CheckPage(pageIndex);
        var s = PageSizes[pageIndex];
        return FitPixels(s.Width * scale, s.Height * scale);
    }

    /// <summary>
    /// Renders a whole page (white background, BGRA, frozen - usable from any thread). Round the scale to a few steps
    /// (e.g. multiples of 0.05) so cache hits are likely. Queued requests cancelled via <paramref name="ct"/> are skipped.
    /// </summary>
    public Task<BitmapSource> RenderPageAsync(int pageIndex, double scale, CancellationToken ct = default, PdfRenderOptions? options = null)
    {
        var (w, h) = GetPixelSize(pageIndex, scale);
        return RenderAsync(pageIndex, w, h, 0, 0, w, h, (options ?? PdfRenderOptions.Default).Flags, PdfPriority.Normal, _renderCache, ct);
    }

    /// <summary>
    /// Renders only a window of the page at <paramref name="scale"/>: the rectangle (x, y, width, height) in pixels of the
    /// full-page bitmap. For high zoom of big pages - the full bitmap is never allocated.
    /// </summary>
    public Task<BitmapSource> RenderRegionAsync(int pageIndex, double scale, int x, int y, int width, int height, CancellationToken ct = default, PdfRenderOptions? options = null)
    {
        CheckPage(pageIndex);
        var s = PageSizes[pageIndex];
        int fullW = (int)Math.Clamp(Math.Round(s.Width * scale, MidpointRounding.AwayFromZero), 1, 100_000), fullH = (int)Math.Clamp(Math.Round(s.Height * scale, MidpointRounding.AwayFromZero), 1, 100_000);
        x = Math.Clamp(x, 0, fullW - 1); y = Math.Clamp(y, 0, fullH - 1);
        width = Math.Clamp(width, 1, fullW - x); height = Math.Clamp(height, 1, fullH - y);
        var (cw, ch) = FitPixels(width, height);
        if (cw != width || ch != height) { width = cw; height = ch; }
        return RenderAsync(pageIndex, fullW, fullH, x, y, width, height, (options ?? PdfRenderOptions.Default).Flags, PdfPriority.Normal, _renderCache, ct);
    }

    /// <summary>Small page image for the page list; low priority so visible pages render first.</summary>
    public Task<BitmapSource> GetThumbnailAsync(int pageIndex, int maxSize = 160, CancellationToken ct = default)
    {
        CheckPage(pageIndex);
        var s = PageSizes[pageIndex];
        double k = maxSize / Math.Max(s.Width, s.Height);
        int w = Math.Max(1, (int)Math.Round(s.Width * k)), h = Math.Max(1, (int)Math.Round(s.Height * k));
        return RenderAsync(pageIndex, w, h, 0, 0, w, h, Pdfium.RenderAnnot | Pdfium.RenderLimitedImageCache, PdfPriority.Low, _thumbCache, ct);
    }

    private Task<BitmapSource> RenderAsync(int page, int fullW, int fullH, int x, int y, int w, int h, int flags, PdfPriority prio, ByteLru<RenderKey, BitmapSource> cache, CancellationToken ct)
    {
        ThrowIfDisposed();
        var key = new RenderKey(page, fullW, fullH, x, y, flags);
        if (cache.TryGet(key, out var hit)) return Task.FromResult(hit);
        var state = _state;
        return RenderAndCacheAsync();

        async Task<BitmapSource> RenderAndCacheAsync()
        {
            var bmp = await _engine.Run(() => state.Render(page, fullW, fullH, x, y, w, h, flags), prio, ct).ConfigureAwait(false);
            if (_disposed == 0) cache.Add(key, bmp, (long)w * h * 4);
            return bmp;
        }
    }

    /// <summary>Renders for printing (printer-optimised flags, not cached). <paramref name="rotate"/> = quarter turns clockwise (PDFium: 0..3).</summary>
    internal Task<BitmapSource> RenderForPrintAsync(int pageIndex, int w, int h, int rotate, CancellationToken ct)
    {
        CheckPage(pageIndex);
        var state = _state;
        return _engine.Run(() => state.Render(pageIndex, w, h, 0, 0, w, h, Pdfium.RenderAnnot | Pdfium.RenderPrinting | Pdfium.RenderLimitedImageCache, rotate), PdfPriority.Normal, ct);
    }

    // ------------------------------------------------------------------ text

    /// <summary>Plain text of a page (PDFium order, "\r\n" between lines); index-aligned with <see cref="PdfSearchHit.CharIndex"/>.</summary>
    public Task<string> GetPageTextAsync(int pageIndex, CancellationToken ct = default)
    {
        CheckPage(pageIndex);
        if (_textCache.TryGet(pageIndex, out var t)) return Task.FromResult(t.Text);
        var state = _state;
        return _engine.Run(() => state.ReadPageString(pageIndex), PdfPriority.Normal, ct);
    }

    /// <summary>Text of the whole document, pages separated by a blank line (form feed free). Runs page by page at low priority.</summary>
    public async Task<string> GetAllTextAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var state = _state;
            int idx = i;
            string t = await _engine.Run(() => state.ReadPageString(idx), PdfPriority.Low, ct).ConfigureAwait(false);
            if (i > 0) sb.Append("\r\n\r\n");
            sb.Append(t.Replace("\u00AD\r\n", "").Replace("\u00AD", ""));
            progress?.Report(i + 1);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Text layer with per-character boxes for selection / copy. Cached (small LRU). With <paramref name="includeFontInfo"/>
    /// font size / bold are filled too (used by the DOCX converter; not cached).
    /// </summary>
    public Task<PdfPageText> GetPageTextLayoutAsync(int pageIndex, bool includeFontInfo = false, CancellationToken ct = default)
    {
        CheckPage(pageIndex);
        if (!includeFontInfo && _textCache.TryGet(pageIndex, out var t)) return Task.FromResult(t);
        return Load();

        async Task<PdfPageText> Load()
        {
            var state = _state;
            var pt = await _engine.Run(() => state.ReadPageText(pageIndex, includeFontInfo), includeFontInfo ? PdfPriority.Low : PdfPriority.Normal, ct).ConfigureAwait(false);
            if (!includeFontInfo && _disposed == 0) _textCache.Add(pageIndex, pt, pt.EstimatedBytes);
            return pt;
        }
    }

    /// <summary>Native hit test (PDFium): character index at a point in page space (points, top-left origin) or -1.</summary>
    public Task<int> HitTestAsync(int pageIndex, double x, double y, double tolerance = 2, CancellationToken ct = default)
    {
        CheckPage(pageIndex);
        var state = _state;
        return _engine.Run(() => state.HitTest(pageIndex, x, y, tolerance), PdfPriority.Normal, ct);
    }

    // ------------------------------------------------------------------ search

    public Task<IReadOnlyList<PdfSearchHit>> SearchPageAsync(int pageIndex, string query, bool matchCase = false, bool wholeWord = false, CancellationToken ct = default, int maxHits = 5000)
    {
        CheckPage(pageIndex);
        if (string.IsNullOrEmpty(query)) return Task.FromResult<IReadOnlyList<PdfSearchHit>>([]);
        uint flags = (matchCase ? Pdfium.MatchCase : 0) | (wholeWord ? Pdfium.MatchWholeWord : 0);
        var state = _state;
        return _engine.Run<IReadOnlyList<PdfSearchHit>>(() => state.SearchPage(pageIndex, query, flags, maxHits), PdfPriority.Low, ct);
    }

    /// <summary>
    /// Searches the whole document. Runs page by page at low priority, so page renders requested meanwhile are not delayed
    /// by more than one page. <paramref name="progress"/> receives the hits of each processed page as they are found.
    /// </summary>
    public async Task<IReadOnlyList<PdfSearchHit>> SearchAsync(string query, bool matchCase = false, bool wholeWord = false,
        IProgress<PdfSearchProgress>? progress = null, CancellationToken ct = default, int maxHits = 10_000)
    {
        ThrowIfDisposed();
        var all = new List<PdfSearchHit>();
        if (string.IsNullOrEmpty(query)) return all;
        for (int i = 0; i < PageCount && all.Count < maxHits; i++)
        {
            ct.ThrowIfCancellationRequested();
            var hits = await SearchPageAsync(i, query, matchCase, wholeWord, ct, Math.Max(1, maxHits - all.Count)).ConfigureAwait(false);
            all.AddRange(hits);
            progress?.Report(new PdfSearchProgress(i + 1, PageCount, hits));
        }
        return all;
    }

    // ------------------------------------------------------------------ images (used by the DOCX converter)

    /// <summary>Embedded raster images of a page (JPEGs passed through untouched, others as PNG). Skips images above <paramref name="maxPixels"/>.</summary>
    public Task<IReadOnlyList<PdfPageImage>> GetPageImagesAsync(int pageIndex, long maxPixels = 30_000_000, CancellationToken ct = default)
    {
        CheckPage(pageIndex);
        var state = _state;
        return _engine.Run<IReadOnlyList<PdfPageImage>>(() => state.ReadImages(pageIndex, maxPixels), PdfPriority.Low, ct);
    }
}
