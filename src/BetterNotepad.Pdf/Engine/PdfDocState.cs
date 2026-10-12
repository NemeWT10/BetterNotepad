using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace BetterNotepad.Pdf;

/// <summary>
/// Native state of one open document. ALL members must be used on the engine thread only.
/// File data is streamed on demand (FPDF_LoadCustomDocument) so the file is never read into memory as a whole and
/// Unicode paths work; the file stays shared for read/write/delete so external rebuilds (LaTeX...) are not blocked.
/// </summary>
internal sealed unsafe class PdfDocState
{
    private const int PageCacheSize = 2;
    private const int DeviceScale = 32;   // sub-point precision for the page transform calibration

    private SafeFileHandle? _file;
    private GCHandle _srcHandle;
    private Pdfium.FileAccess* _access;
    private nint _doc;
    private readonly PageCtx?[] _pages = new PageCtx?[PageCacheSize];
    private long _tick;

    public bool Closed { get; private set; }
    public int PageCount { get; private set; }
    public PdfPageSize[] Sizes { get; private set; } = [];
    public PdfMetadata Metadata { get; private set; } = new();

    private sealed class FileSource(SafeFileHandle handle) { public readonly SafeFileHandle Handle = handle; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetBlock(void* param, uint position, byte* buffer, uint size)
    {
        try
        {
            var src = (FileSource)GCHandle.FromIntPtr((nint)param).Target!;
            var span = new Span<byte>(buffer, checked((int)size));
            long pos = position;
            while (span.Length > 0)
            {
                int n = RandomAccess.Read(src.Handle, span, pos);
                if (n <= 0) return 0;
                span = span[n..];
                pos += n;
            }
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>Opens the document on the engine thread. Throws <see cref="PdfOpenException"/> on failure.</summary>
    public static PdfDocState Open(SafeFileHandle file, long length, string? password)
    {
        if (length > uint.MaxValue) { file.Dispose(); throw new PdfOpenException(PdfOpenError.InvalidFormat, "File is larger than 4 GB."); }
        var s = new PdfDocState { _file = file };
        s._srcHandle = GCHandle.Alloc(new FileSource(file));
        s._access = (Pdfium.FileAccess*)NativeMemory.AllocZeroed((nuint)sizeof(Pdfium.FileAccess));
        s._access->FileLen = (uint)length;
        s._access->GetBlock = &GetBlock;
        s._access->Param = (void*)GCHandle.ToIntPtr(s._srcHandle);

        byte[]? pw = password is { Length: > 0 } ? [.. Encoding.UTF8.GetBytes(password), 0] : null;
        fixed (byte* pwp = pw)
            s._doc = Pdfium.FPDF_LoadCustomDocument(s._access, pwp);
        if (s._doc == 0)
        {
            uint err = Pdfium.FPDF_GetLastError();
            s.Close();
            var kind = err switch
            {
                2 => PdfOpenError.FileNotFound,
                3 => PdfOpenError.InvalidFormat,
                4 => pw == null ? PdfOpenError.PasswordRequired : PdfOpenError.IncorrectPassword,
                5 => PdfOpenError.UnsupportedSecurity,
                _ => PdfOpenError.Unknown,
            };
            throw new PdfOpenException(kind, kind switch
            {
                PdfOpenError.PasswordRequired => "The PDF is password protected.",
                PdfOpenError.IncorrectPassword => "Incorrect PDF password.",
                PdfOpenError.InvalidFormat => "Not a valid PDF file.",
                PdfOpenError.UnsupportedSecurity => "Unsupported PDF security handler.",
                _ => "The PDF could not be opened (PDFium error " + err + ").",
            });
        }

        s.PageCount = Pdfium.FPDF_GetPageCount(s._doc);
        s.Sizes = new PdfPageSize[Math.Max(0, s.PageCount)];
        for (int i = 0; i < s.Sizes.Length; i++)
        {
            Pdfium.SizeF sz = default;
            s.Sizes[i] = Pdfium.FPDF_GetPageSizeByIndexF(s._doc, i, &sz) != 0 && sz.Width > 0 && sz.Height > 0
                ? new PdfPageSize(sz.Width, sz.Height)
                : new PdfPageSize(595.276, 841.89);
        }
        s.Metadata = s.ReadMetadata();
        return s;
    }

    public void Close()
    {
        if (Closed) return;
        Closed = true;
        ClearPageCache();
        if (_doc != 0) { Pdfium.FPDF_CloseDocument(_doc); _doc = 0; }
        if (_access != null) { NativeMemory.Free(_access); _access = null; }
        if (_srcHandle.IsAllocated) _srcHandle.Free();
        _file?.Dispose();
        _file = null;
    }

    private void ThrowIfClosed() { if (Closed) throw new ObjectDisposedException("PdfDocumentHandle"); }

    // ---------------------------------------------------------------- metadata

    private string? Meta(string tag)
    {
        var t = Encoding.ASCII.GetBytes(tag + "\0");
        fixed (byte* tp = t)
        {
            uint bytes = Pdfium.FPDF_GetMetaText(_doc, tp, null, 0);
            if (bytes <= 2) return null;
            var buf = new byte[bytes];
            fixed (byte* bp = buf) Pdfium.FPDF_GetMetaText(_doc, tp, bp, bytes);
            string s = Encoding.Unicode.GetString(buf, 0, (int)bytes - 2).Trim();
            return s.Length == 0 ? null : s;
        }
    }

    private PdfMetadata ReadMetadata()
    {
        string? cd = Meta("CreationDate"), md = Meta("ModDate");
        int ver = 0;
        Pdfium.FPDF_GetFileVersion(_doc, &ver);
        return new PdfMetadata
        {
            Title = Meta("Title"), Author = Meta("Author"), Subject = Meta("Subject"), Keywords = Meta("Keywords"),
            Creator = Meta("Creator"), Producer = Meta("Producer"),
            CreationDateRaw = cd, ModificationDateRaw = md,
            CreationDate = PdfMetadata.ParseDate(cd), ModificationDate = PdfMetadata.ParseDate(md),
            FileVersion = ver,
        };
    }

    // ---------------------------------------------------------------- page cache

    internal sealed class PageCtx
    {
        public int Index;
        public nint Page;
        public nint Text;
        public long Tick;
        public PageXform Xform;
        public bool HasXform;
    }

    /// <summary>Linear map from PDF user space (y up) to displayed page space in points (origin top-left, y down).</summary>
    internal readonly record struct PageXform(double A, double B, double C, double D, double E, double F)
    {
        public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

        public PdfRect Rect(double x1, double y1, double x2, double y2)
        {
            var (ax, ay) = Apply(x1, y1);
            var (bx, by) = Apply(x2, y2);
            return new PdfRect((float)Math.Min(ax, bx), (float)Math.Min(ay, by), (float)Math.Max(ax, bx), (float)Math.Max(ay, by));
        }
    }

    public PageCtx GetPage(int index)
    {
        ThrowIfClosed();
        if ((uint)index >= (uint)PageCount) throw new ArgumentOutOfRangeException(nameof(index));
        for (int i = 0; i < _pages.Length; i++)
            if (_pages[i] is { } c && c.Index == index) { c.Tick = ++_tick; return c; }

        nint page = Pdfium.FPDF_LoadPage(_doc, index);
        if (page == 0) throw new PdfException("PDFium could not load page " + (index + 1) + ".");

        int slot = 0;
        for (int i = 0; i < _pages.Length; i++)
        {
            if (_pages[i] == null) { slot = i; break; }
            if (_pages[i]!.Tick < _pages[slot]!.Tick) slot = i;
        }
        if (_pages[slot] is { } old) CloseCtx(old);
        var ctx = new PageCtx { Index = index, Page = page, Tick = ++_tick };
        _pages[slot] = ctx;
        return ctx;
    }

    private static void CloseCtx(PageCtx c)
    {
        if (c.Text != 0) { Pdfium.FPDFText_ClosePage(c.Text); c.Text = 0; }
        if (c.Page != 0) { Pdfium.FPDF_ClosePage(c.Page); c.Page = 0; }
    }

    public void ClearPageCache()
    {
        for (int i = 0; i < _pages.Length; i++)
            if (_pages[i] is { } c) { CloseCtx(c); _pages[i] = null; }
    }

    private static nint TextOf(PageCtx c)
    {
        if (c.Text == 0)
        {
            c.Text = Pdfium.FPDFText_LoadPage(c.Page);
            if (c.Text == 0) throw new PdfException("PDFium could not load the text layer of page " + (c.Index + 1) + ".");
        }
        return c.Text;
    }

    private static PageXform XformOf(PageCtx c)
    {
        if (c.HasXform) return c.Xform;
        float w = Pdfium.FPDF_GetPageWidthF(c.Page), h = Pdfium.FPDF_GetPageHeightF(c.Page);
        int sx = Math.Max(1, (int)Math.Round(w * DeviceScale)), sy = Math.Max(1, (int)Math.Round(h * DeviceScale));
        double kx = sx / (double)w, ky = sy / (double)h;
        int x0, y0, xa, ya, xb, yb;
        const double L = 1000.0;
        Pdfium.FPDF_PageToDevice(c.Page, 0, 0, sx, sy, 0, 0, 0, &x0, &y0);
        Pdfium.FPDF_PageToDevice(c.Page, 0, 0, sx, sy, 0, L, 0, &xa, &ya);
        Pdfium.FPDF_PageToDevice(c.Page, 0, 0, sx, sy, 0, 0, L, &xb, &yb);
        // The page transform is a 90-degree-step rotation/flip + translation: snap the linear part to integers.
        double a = Math.Round((xa - x0) / L / kx), b = Math.Round((ya - y0) / L / ky);
        double cc = Math.Round((xb - x0) / L / kx), d = Math.Round((yb - y0) / L / ky);
        c.Xform = new PageXform(a, b, cc, d, x0 / kx, y0 / ky);
        c.HasXform = true;
        return c.Xform;
    }

    // ---------------------------------------------------------------- rendering

    /// <summary>
    /// Renders a window of the page. The virtual full-page bitmap is fullW x fullH; the returned bitmap covers the
    /// rectangle (offX, offY, w, h) of it. Result: frozen Pbgra32 BitmapSource with opaque white background.
    /// </summary>
    public BitmapSource Render(int index, int fullW, int fullH, int offX, int offY, int w, int h, int flags, int rotate = 0)
    {
        var ctx = GetPage(index);
        nint bmp = Pdfium.FPDFBitmap_CreateEx(w, h, Pdfium.BitmapBgra, null, 0);
        if (bmp == 0) throw new OutOfMemoryException("Cannot allocate a " + w + "x" + h + " page bitmap.");
        try
        {
            Pdfium.FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);
            Pdfium.FPDF_RenderPageBitmap(bmp, ctx.Page, -offX, -offY, fullW, fullH, rotate, flags);
            int stride = Pdfium.FPDFBitmap_GetStride(bmp);
            var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, (nint)Pdfium.FPDFBitmap_GetBuffer(bmp), stride * h, stride);
            bs.Freeze();
            return bs;
        }
        finally { Pdfium.FPDFBitmap_Destroy(bmp); }
    }

    // ---------------------------------------------------------------- text

    public PdfPageText ReadPageText(int index, bool includeFontInfo)
    {
        var ctx = GetPage(index);
        nint tp = TextOf(ctx);
        var xf = XformOf(ctx);
        int n = Math.Max(0, Pdfium.FPDFText_CountChars(tp));
        string text = ReadText(tp, 0, n);
        n = text.Length; // keep boxes aligned with the characters actually returned
        var boxes = new PdfRect[n];
        for (int i = 0; i < n; i++)
        {
            char ch = text[i];
            if (ch is '\r' or '\n') continue;
            double l, r, b, t;
            if (Pdfium.FPDFText_GetCharBox(tp, i, &l, &r, &b, &t) != 0 && r >= l && t >= b)
                boxes[i] = xf.Rect(l, t, r, b);
        }
        float[]? sizes = null;
        bool[]? bold = null;
        if (includeFontInfo)
        {
            sizes = new float[n];
            bold = new bool[n];
            for (int i = 0; i < n; i++)
            {
                sizes[i] = (float)Pdfium.FPDFText_GetFontSize(tp, i);
                bold[i] = IsBold(tp, i);
            }
        }
        return new PdfPageText(index, text, boxes, sizes, bold);
    }

    private static bool IsBold(nint tp, int i)
    {
        if (Pdfium.FPDFText_GetFontWeight(tp, i) >= 600) return true;
        // Non-embedded base-14 fonts report no weight: fall back to the font name / descriptor flags.
        byte* name = stackalloc byte[96];
        int flags = 0;
        uint len = Pdfium.FPDFText_GetFontInfo(tp, i, name, 96, &flags);
        if ((flags & 0x40000) != 0) return true;   // ForceBold
        if (len < 5 || len > 96) return false;
        var n = new ReadOnlySpan<byte>(name, (int)len - 1);
        return n.IndexOf("Bold"u8) >= 0 || n.IndexOf("bold"u8) >= 0 || n.IndexOf("Black"u8) >= 0 || n.IndexOf("Heavy"u8) >= 0 || n.IndexOf("Demi"u8) >= 0;
    }

    public string ReadPageString(int index)
    {
        var ctx = GetPage(index);
        nint tp = TextOf(ctx);
        return ReadText(tp, 0, Math.Max(0, Pdfium.FPDFText_CountChars(tp)));
    }

    private static string ReadText(nint tp, int start, int count)
    {
        if (count <= 0) return "";
        char[] buf = ArrayPool<char>.Shared.Rent(count + 1);
        try
        {
            int got;
            fixed (char* p = buf) got = Pdfium.FPDFText_GetText(tp, start, count, p);
            int len = Math.Clamp(got - 1, 0, count); // result includes the terminating NUL
            for (int i = 0; i < len; i++)
            {
                char c = buf[i];
                if (c is '\u0002' or '\uFFFE') buf[i] = '\u00AD';           // PDFium end-of-line hyphen marker -> soft hyphen
                else if (c < ' ' && c is not ('\t' or '\r' or '\n')) buf[i] = ' ';
                else if (c == '\uFFFF') buf[i] = ' ';
            }
            return new string(buf, 0, len);
        }
        finally { ArrayPool<char>.Shared.Return(buf); }
    }

    public int HitTest(int index, double x, double y, double tolerance)
    {
        var ctx = GetPage(index);
        nint tp = TextOf(ctx);
        var xf = XformOf(ctx);
        // Invert the (axis aligned) page transform.
        double det = xf.A * xf.D - xf.B * xf.C;
        if (Math.Abs(det) < 1e-9) return -1;
        double dx = x - xf.E, dy = y - xf.F;
        double px = (xf.D * dx - xf.C * dy) / det, py = (-xf.B * dx + xf.A * dy) / det;
        int i = Pdfium.FPDFText_GetCharIndexAtPos(tp, px, py, tolerance, tolerance);
        return i < 0 ? -1 : i;
    }

    public List<PdfSearchHit> SearchPage(int index, string query, uint flags, int maxHits)
    {
        var hits = new List<PdfSearchHit>();
        var ctx = GetPage(index);
        nint tp = TextOf(ctx);
        var xf = XformOf(ctx);
        string? text = null;
        fixed (char* q = query + "\0")
        {
            nint sh = Pdfium.FPDFText_FindStart(tp, q, flags, 0);
            if (sh == 0) return hits;
            try
            {
                while (hits.Count < maxHits && Pdfium.FPDFText_FindNext(sh) != 0)
                {
                    int start = Pdfium.FPDFText_GetSchResultIndex(sh), count = Pdfium.FPDFText_GetSchCount(sh);
                    if (start < 0 || count <= 0) continue;
                    var rects = new List<PdfRect>();
                    int rc = Pdfium.FPDFText_CountRects(tp, start, count);
                    for (int r = 0; r < rc; r++)
                    {
                        double l, t, rt, b;
                        if (Pdfium.FPDFText_GetRect(tp, r, &l, &t, &rt, &b) != 0)
                            rects.Add(xf.Rect(l, t, rt, b));
                    }
                    text ??= ReadPageString(index);
                    var (ctxText, off) = MakeContext(text, start, count);
                    hits.Add(new PdfSearchHit { PageIndex = index, CharIndex = start, CharCount = count, Rects = rects, Context = ctxText, ContextMatchStart = off });
                }
            }
            finally { Pdfium.FPDFText_FindClose(sh); }
        }
        return hits;
    }

    private static (string, int) MakeContext(string text, int start, int count)
    {
        const int Side = 36;
        start = Math.Clamp(start, 0, text.Length);
        int end = Math.Clamp(start + count, start, text.Length);
        int a = start;
        while (a > 0 && start - a < Side && text[a - 1] is not ('\n' or '\r')) a--;
        int b = end;
        while (b < text.Length && b - end < Side && text[b] is not ('\n' or '\r')) b++;
        string s = text[a..b].Replace('\u00AD', ' ');
        return ((a > 0 ? "…" : "") + s + (b < text.Length ? "…" : ""), start - a + (a > 0 ? 1 : 0));
    }

    // ---------------------------------------------------------------- images

    public List<PdfPageImage> ReadImages(int index, long maxPixels)
    {
        var result = new List<PdfPageImage>();
        var ctx = GetPage(index);
        var xf = XformOf(ctx);
        int count = Pdfium.FPDFPage_CountObjects(ctx.Page);
        for (int i = 0; i < count; i++)
        {
            nint obj = Pdfium.FPDFPage_GetObject(ctx.Page, i);
            if (obj == 0 || Pdfium.FPDFPageObj_GetType(obj) != Pdfium.PageObjImage) continue;
            float l, b, r, t;
            if (Pdfium.FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) == 0) continue;
            var bounds = xf.Rect(l, t, r, b);
            if (bounds.Width < 4 || bounds.Height < 4) continue;

            Pdfium.ImageMetadata meta = default;
            bool haveMeta = Pdfium.FPDFImageObj_GetImageMetadata(obj, ctx.Page, &meta) != 0;
            if (haveMeta && (long)meta.Width * meta.Height > maxPixels) continue;
            if (haveMeta && (meta.Width < 8 || meta.Height < 8)) continue;

            var img = TryPassThroughJpeg(obj, meta, haveMeta, bounds) ?? RenderImage(obj, ctx, maxPixels, bounds);
            if (img != null) result.Add(img);
        }
        return result;
    }

    private PdfPageImage? TryPassThroughJpeg(nint obj, Pdfium.ImageMetadata meta, bool haveMeta, PdfRect bounds)
    {
        // A single DCTDecode (JPEG) filter, RGB/gray, upright and unmirrored: embed the original JPEG bytes (no re-encoding, tiny memory).
        if (!haveMeta || Pdfium.FPDFImageObj_GetImageFilterCount(obj) != 1) return null;
        if (meta.BitsPerPixel is not (8 or 24) || meta.Colorspace is not (1 or 2 or 4 or 5)) return null;
        Span<byte> name = stackalloc byte[32];
        uint nameLen;
        fixed (byte* np = name) nameLen = Pdfium.FPDFImageObj_GetImageFilter(obj, 0, np, 32);
        if (nameLen < 2 || Encoding.ASCII.GetString(name[..(int)(nameLen - 1)]) != "DCTDecode") return null;
        Pdfium.Matrix m = default;
        if (Pdfium.FPDFPageObj_GetMatrix(obj, &m) == 0 || Math.Abs(m.B) > 0.001f || Math.Abs(m.C) > 0.001f || m.A <= 0 || m.D <= 0) return null;
        uint len = Pdfium.FPDFImageObj_GetImageDataRaw(obj, null, 0);
        if (len < 4) return null;
        var data = new byte[len];
        fixed (byte* dp = data) len = Pdfium.FPDFImageObj_GetImageDataRaw(obj, dp, len);
        if (len != data.Length || data[0] != 0xFF || data[1] != 0xD8) return null;
        return new PdfPageImage { Bounds = bounds, Data = data, ContentType = "image/jpeg", PixelWidth = (int)meta.Width, PixelHeight = (int)meta.Height };
    }

    private PdfPageImage? RenderImage(nint obj, PageCtx ctx, long maxPixels, PdfRect bounds)
    {
        nint bmp = Pdfium.FPDFImageObj_GetRenderedBitmap(_doc, ctx.Page, obj);
        if (bmp == 0) return null;
        try
        {
            int w = Pdfium.FPDFBitmap_GetWidth(bmp), h = Pdfium.FPDFBitmap_GetHeight(bmp);
            if (w <= 0 || h <= 0 || (long)w * h > maxPixels) return null;
            int stride = Pdfium.FPDFBitmap_GetStride(bmp);
            var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, (nint)Pdfium.FPDFBitmap_GetBuffer(bmp), stride * h, stride);
            bs.Freeze();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bs));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return new PdfPageImage { Bounds = bounds, Data = ms.ToArray(), ContentType = "image/png", PixelWidth = w, PixelHeight = h };
        }
        finally { Pdfium.FPDFBitmap_Destroy(bmp); }
    }
}
