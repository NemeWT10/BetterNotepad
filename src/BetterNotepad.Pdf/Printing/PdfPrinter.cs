using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace BetterNotepad.Pdf;

public enum PdfPrintScaling
{
    /// <summary>Scale each page to fit the printable area (default).</summary>
    Fit,
    /// <summary>100% size; pages larger than the printable area are shrunk to fit.</summary>
    ActualSize,
}

/// <summary>Plain snapshot of what the print dialog selected - safe to hand to a background thread.</summary>
public sealed class PdfPrintSettings
{
    public string QueueFullName { get; init; } = "";
    public PrintTicket? Ticket { get; init; }
    /// <summary>Zero-based page indices to print, in order.</summary>
    public IReadOnlyList<int> Pages { get; init; } = [];
    /// <summary>Printable area in 1/96 inch units.</summary>
    public Size PageSize { get; init; }
    public int Dpi { get; init; } = 300;
    public bool AutoRotate { get; init; } = true;
    public PdfPrintScaling Scaling { get; init; } = PdfPrintScaling.Fit;
    public string JobName { get; init; } = "PDF";
}

/// <summary>1-based inclusive page range.</summary>
public readonly record struct PdfPageRange(int From, int To);

/// <summary>
/// Prints a PDF page by page: each page is rendered at printer resolution only when the spooler asks for it (low memory),
/// auto-rotated to the paper orientation and fitted into the printable area.
/// </summary>
public static class PdfPrinter
{
    /// <summary>A print dialog configured for a document: page range, current page.</summary>
    public static PrintDialog CreateDialog(int pageCount) => new()
    {
        UserPageRangeEnabled = true,
        CurrentPageEnabled = true,
        MinPage = 1,
        MaxPage = (uint)Math.Max(1, pageCount),
        PageRange = new PageRange(1, Math.Max(1, pageCount)),
    };

    /// <summary>Reads the dialog (call on the UI thread after a successful ShowDialog).</summary>
    public static PdfPrintSettings FromDialog(PrintDialog dialog, int pageCount, int currentPageIndex = 0, PdfPageRange? range = null,
        string? jobName = null, int dpi = 300, PdfPrintScaling scaling = PdfPrintScaling.Fit)
    {
        return new PdfPrintSettings
        {
            QueueFullName = dialog.PrintQueue?.FullName ?? "",
            Ticket = dialog.PrintTicket is { } t ? new PrintTicket(t.GetXmlStream()) : null,
            Pages = ResolvePages(dialog, pageCount, currentPageIndex, range),
            PageSize = new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight),
            Dpi = dpi,
            Scaling = scaling,
            JobName = jobName ?? "PDF",
        };
    }

    private static List<int> ResolvePages(PrintDialog dialog, int pageCount, int current, PdfPageRange? range)
    {
        int from = 1, to = pageCount;
        if (range is { } r) { from = r.From; to = r.To; }
        else
        {
            switch (dialog.PageRangeSelection)
            {
                case PageRangeSelection.CurrentPage: from = to = current + 1; break;
                case PageRangeSelection.UserPages: from = dialog.PageRange.PageFrom; to = dialog.PageRange.PageTo; break;
            }
        }
        from = Math.Clamp(from, 1, Math.Max(1, pageCount));
        to = Math.Clamp(to, from, Math.Max(1, pageCount));
        var pages = new List<int>();
        for (int i = from; i <= to; i++) pages.Add(i - 1);
        return pages;
    }

    /// <summary>
    /// Synchronous print through an already confirmed dialog; blocks the calling thread until the job is spooled
    /// (use <see cref="PrintAsync"/> from UI code). The pages come from the dialog's selection or from <paramref name="range"/>.
    /// </summary>
    public static void Print(PdfDocumentHandle doc, PrintDialog dialog, PdfPageRange? range = null, string? jobName = null, int currentPageIndex = 0)
    {
        var s = FromDialog(dialog, doc.PageCount, currentPageIndex, range, jobName);
        var paginator = CreatePaginator(doc, s.Pages, s.PageSize, s.Dpi, s.AutoRotate, s.Scaling, null, CancellationToken.None);
        dialog.PrintDocument(paginator, s.JobName);
    }

    /// <summary>
    /// Spools the job on a dedicated STA thread (the UI stays responsive). Pages are rendered lazily, one at a time.
    /// <paramref name="progress"/> reports the number of pages handed to the spooler.
    /// </summary>
    public static Task PrintAsync(PdfDocumentHandle doc, PdfPrintSettings settings, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var th = new Thread(() =>
        {
            try
            {
                var dlg = new PrintDialog();
                if (settings.QueueFullName.Length > 0)
                    dlg.PrintQueue = new PrintQueue(new LocalPrintServer(), settings.QueueFullName);
                if (settings.Ticket != null) dlg.PrintTicket = settings.Ticket;
                var paginator = CreatePaginator(doc, settings.Pages, settings.PageSize, settings.Dpi, settings.AutoRotate, settings.Scaling, progress, ct);
                dlg.PrintDocument(paginator, settings.JobName);
                tcs.TrySetResult();
            }
            catch (OperationCanceledException) { tcs.TrySetCanceled(ct); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }) { IsBackground = true, Name = "BetterNotepad.PdfPrint" };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        return tcs.Task;
    }

    /// <summary>The paginator used for printing (also usable for XPS export / print preview).</summary>
    public static DocumentPaginator CreatePaginator(PdfDocumentHandle doc, IReadOnlyList<int> pages, Size pageSize, int dpi = 300,
        bool autoRotate = true, PdfPrintScaling scaling = PdfPrintScaling.Fit, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (pageSize.Width < 10 || pageSize.Height < 10) pageSize = new Size(793.7, 1122.5); // A4 fallback
        return new PdfPaginator(doc, pages, pageSize, Math.Clamp(dpi, 72, 1200), autoRotate, scaling, progress, ct);
    }

    private sealed class PdfPaginator : DocumentPaginator
    {
        private readonly PdfDocumentHandle _doc;
        private readonly IReadOnlyList<int> _pages;
        private readonly int _dpi;
        private readonly bool _autoRotate;
        private readonly PdfPrintScaling _scaling;
        private readonly IProgress<int>? _progress;
        private readonly CancellationToken _ct;
        private Size _pageSize;

        public PdfPaginator(PdfDocumentHandle doc, IReadOnlyList<int> pages, Size pageSize, int dpi, bool autoRotate, PdfPrintScaling scaling, IProgress<int>? progress, CancellationToken ct)
        {
            _doc = doc; _pages = pages; _pageSize = pageSize; _dpi = dpi; _autoRotate = autoRotate; _scaling = scaling; _progress = progress; _ct = ct;
        }

        public override bool IsPageCountValid => true;
        public override int PageCount => _pages.Count;
        public override Size PageSize { get => _pageSize; set => _pageSize = value; }
        public override IDocumentPaginatorSource? Source => null;

        public override DocumentPage GetPage(int pageNumber)
        {
            _ct.ThrowIfCancellationRequested();
            if ((uint)pageNumber >= (uint)_pages.Count) return DocumentPage.Missing;
            int idx = _pages[pageNumber];
            var size = _doc.GetPageSize(idx);
            const double PtToDip = 96.0 / 72.0;
            double pw = size.Width * PtToDip, ph = size.Height * PtToDip;
            bool rotate = _autoRotate && (pw > ph) != (_pageSize.Width > _pageSize.Height) && Math.Abs(pw - ph) > 1;
            if (rotate) (pw, ph) = (ph, pw);
            double fit = Math.Min(_pageSize.Width / pw, _pageSize.Height / ph);
            double k = _scaling == PdfPrintScaling.Fit ? fit : Math.Min(1.0, fit);
            double dw = pw * k, dh = ph * k;
            int pxW = Math.Clamp((int)Math.Round(dw / 96.0 * _dpi), 1, 12000), pxH = Math.Clamp((int)Math.Round(dh / 96.0 * _dpi), 1, 12000);

            var bmp = _doc.RenderForPrintAsync(idx, pxW, pxH, rotate ? 3 : 0, _ct).GetAwaiter().GetResult();

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(_pageSize));
                dc.DrawImage(bmp, new Rect((_pageSize.Width - dw) / 2, (_pageSize.Height - dh) / 2, dw, dh));
            }
            _progress?.Report(pageNumber + 1);
            var full = new Rect(_pageSize);
            return new DocumentPage(visual, _pageSize, full, full);
        }
    }
}
