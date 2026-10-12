using System.Diagnostics;
using System.Printing;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using Bold = DocumentFormat.OpenXml.Wordprocessing.Bold;
using Drawing = DocumentFormat.OpenXml.Wordprocessing.Drawing;
using Paragraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;

namespace BetterNotepad.Pdf.Sample;

/// <summary>
/// End-to-end check of BetterNotepad.Pdf with PDFs generated here (raw PDF syntax). Silent on success: exit code 0 and
/// tests\output\pdf\report.txt; failures go to stderr (exit 1). "--word" additionally runs the Microsoft Word conversion.
/// </summary>
internal static class Program
{
    private static readonly List<string> Report = [];
    private static readonly List<string> Failures = [];
    private static string Out = "";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Out = FindOutDir();
            Directory.CreateDirectory(Out);
            if (args.Contains("--dump")) Dump().GetAwaiter().GetResult();
            else Run(args.Contains("--word")).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Failures.Add("EXCEPTION " + ex);
        }
        try { File.WriteAllLines(Path.Combine(Out, "report.txt"), Report.Concat(Failures.Select(f => "FAIL " + f))); } catch { }
        if (Failures.Count > 0)
        {
            foreach (var f in Failures) Console.Error.WriteLine("FAIL " + f);
            return 1;
        }
        return 0;
    }

    // Debug helper: --dump prints extracted text / font info of the generated sample to the console.
    private static async Task Dump()
    {
        string path = Path.Combine(Out, "sample.pdf");
        File.WriteAllBytes(path, SamplePdfBuilder.BuildSample());
        using var pdf = await PdfDocumentHandle.OpenAsync(path);
        for (int p = 0; p < pdf.PageCount; p++)
        {
            var t = await pdf.GetPageTextLayoutAsync(p, includeFontInfo: true);
            Console.WriteLine($"--- page {p + 1}: {t.Text.Length} chars");
            Console.WriteLine(t.Text.Replace("\r", "\r").Replace("\n", "\n\n").Replace("\u00AD", "<SHY>"));
            var seen = new HashSet<string>();
            for (int i = 0; i < t.Text.Length; i++)
            {
                string k = $"{t.FontSizes![i]:0.#}/{t.Bold![i]}";
                if (seen.Add(k)) Console.WriteLine($"  char {i} '{t.Text[i]}' size/bold {k}");
            }
        }
    }

    private static string FindOutDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "BetterNotepad.sln"))) d = d.Parent;
        string root = d?.FullName ?? Directory.GetCurrentDirectory();
        return Path.Combine(root, "tests", "output", "pdf");
    }

    private static void Check(bool ok, string what)
    {
        Report.Add((ok ? "ok   " : "FAIL ") + what);
        if (!ok) Failures.Add(what);
    }

    private static void Note(string s) => Report.Add("     " + s);

    private static async Task Run(bool useWord)
    {
        string samplePath = Path.Combine(Out, "sample.pdf");
        File.WriteAllBytes(samplePath, SamplePdfBuilder.BuildSample());
        string encPath = Path.Combine(Out, "encrypted.pdf");
        File.WriteAllBytes(encPath, SamplePdfBuilder.BuildEncrypted("secret", "owner"));
        string garbagePath = Path.Combine(Out, "garbage.pdf");
        File.WriteAllText(garbagePath, "this is not a pdf file at all, just some text");

        // ---------------------------------------------------------------- open errors
        await ExpectOpenError(Path.Combine(Out, "missing.pdf"), null, PdfOpenError.FileNotFound);
        await ExpectOpenError(garbagePath, null, PdfOpenError.InvalidFormat);
        await ExpectOpenError(encPath, null, PdfOpenError.PasswordRequired);
        await ExpectOpenError(encPath, "wrong", PdfOpenError.IncorrectPassword);
        using (var enc = await PdfDocumentHandle.OpenAsync(encPath, "secret"))
        {
            Check(enc.PageCount == 1, "encrypted pdf opens with the right password");
            string t = await enc.GetPageTextAsync(0);
            Check(t.Contains("Encrypted page secret text"), "encrypted pdf text decrypted: '" + t.Trim() + "'");
        }

        // ---------------------------------------------------------------- main document
        long ws0 = Process.GetCurrentProcess().WorkingSet64;
        var sw = Stopwatch.StartNew();
        using var pdf = await PdfDocumentHandle.OpenAsync(samplePath);
        Note("open: " + sw.ElapsedMilliseconds + " ms");
        Check(pdf.PageCount == 3, "page count 3");
        Check(Near(pdf.PageSizes[0].Width, 595) && Near(pdf.PageSizes[0].Height, 842), "page 1 size A4");
        Check(Near(pdf.PageSizes[1].Width, 612) && Near(pdf.PageSizes[1].Height, 792), "page 2 size Letter");
        Check(Near(pdf.PageSizes[2].Width, 842) && Near(pdf.PageSizes[2].Height, 595), "page 3 size rotated to landscape");
        var m = pdf.Metadata;
        Check(m.Title == "BetterNotepad PDF sample" && m.Author == "Sample Generator" && m.Subject == "Test" && m.Keywords == "pdf, test", "metadata title/author/subject/keywords");
        Check(m.Producer == "hand written" && m.CreationDate is { Year: 2026 } && m.FileVersion == 14, $"metadata producer/date/version ({m.CreationDate}, v{m.FileVersion})");

        // render page 1 at 1.5x
        sw.Restart();
        var bmp = await pdf.RenderPageAsync(0, 1.5);
        Note("render page 1 @1.5x: " + sw.ElapsedMilliseconds + " ms");
        Check(bmp.IsFrozen && bmp.Format == PixelFormats.Pbgra32, "bitmap frozen BGRA");
        Check(bmp.PixelWidth == 893 && bmp.PixelHeight == 1263, $"bitmap size 893x1263 (got {bmp.PixelWidth}x{bmp.PixelHeight})");
        SavePng(bmp, Path.Combine(Out, "page1.png"));
        var px = Pixels(bmp);
        Check(Argb(px, bmp, 5, 5) == 0xFFFFFFFF, "background is opaque white");
        Check(IsColor(Argb(px, bmp, (int)(500 * 1.5), (int)((842 - 790) * 1.5)), 0x1F, 0x4F, 0x8C, 24), "header bar painted blue (" + Argb(px, bmp, 750, 78).ToString("X8") + ")");
        Check(IsColor(Argb(px, bmp, (int)(100 * 1.5), (int)((842 - 480) * 1.5)), 0x33, 0x99, 0x4D, 24), "green rectangle painted");
        int dark = 0;
        for (int i = 0; i < px.Length; i += 4) if (px[i] < 100 && px[i + 1] < 100 && px[i + 2] < 100) dark++;
        Check(dark > 3000, "text pixels present (" + dark + ")");

        var again = await pdf.RenderPageAsync(0, 1.5);
        Check(ReferenceEquals(bmp, again), "second render served from cache");
        Check(pdf.CachedBytes == 893L * 1263 * 4, "cache accounting " + pdf.CachedBytes);
        pdf.Trim();
        Check(pdf.CachedBytes == 0, "Trim() empties the bitmap cache");
        pdf.CacheBudgetBytes = 8L << 20;
        await pdf.RenderPageAsync(0, 1.5);      // 4.5 MB fits
        await pdf.RenderPageAsync(1, 1.5);      // evicts the first
        Check(pdf.CachedBytes <= 8L << 20 && pdf.CachedBytes > 0, "LRU respects a small budget (" + pdf.CachedBytes + ")");
        pdf.CacheBudgetBytes = 64L << 20;

        // high zoom region render (never allocates the full bitmap)
        var region = await pdf.RenderRegionAsync(0, 8.0, 400, 400, 600, 400);
        Check(region.PixelWidth == 600 && region.PixelHeight == 400, "region render 600x400 of an 8x page");
        SavePng(region, Path.Combine(Out, "region8x.png"));

        // thumbnails
        for (int i = 0; i < 3; i++)
        {
            var th = await pdf.GetThumbnailAsync(i, 160);
            Check(Math.Max(th.PixelWidth, th.PixelHeight) == 160, $"thumbnail {i + 1} max side 160 ({th.PixelWidth}x{th.PixelHeight})");
            SavePng(th, Path.Combine(Out, $"thumb{i + 1}.png"));
        }

        // cancellation
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            try { await pdf.RenderPageAsync(1, 1.0, cts.Token); Check(false, "cancelled render throws"); }
            catch (OperationCanceledException) { Check(true, "cancelled render throws OperationCanceledException"); }
        }

        // concurrency: many mixed requests at once
        sw.Restart();
        var many = new List<Task>();
        for (int i = 0; i < 24; i++)
        {
            int page = i % 3;
            many.Add(pdf.RenderPageAsync(page, 0.5 + (i % 4) * 0.25));
            many.Add(pdf.GetPageTextAsync(page));
            many.Add(pdf.SearchPageAsync(page, "the"));
        }
        await Task.WhenAll(many);
        Note("72 concurrent requests: " + sw.ElapsedMilliseconds + " ms");

        // ---------------------------------------------------------------- text
        string p1 = await pdf.GetPageTextAsync(0);
        Check(p1.Contains("BetterNotepad PDF test page 1") && p1.Contains("Hello world."), "page 1 text extracted");
        Check(p1.Contains("café naïve über ñ"), "WinAnsi accents decoded to Unicode");
        Check(p1.Contains("Times-Roman line") && p1.Contains("Courier line") && p1.Contains("Text in the rectangle"), "other fonts / white text extracted");
        string all = await pdf.GetAllTextAsync();
        Check(all.Contains("Page 2 of the sample") && all.Contains("Rotated page three"), "all-document text");

        var layout = await pdf.GetPageTextLayoutAsync(0);
        Check(layout.Text == p1 && layout.CharBoxes.Length == p1.Length, "layout aligned with text");
        int hello = p1.IndexOf("Hello world.", StringComparison.Ordinal);
        var helloBox = layout.CharBoxes[hello];
        Check(helloBox.Left is > 49 and < 55 && helloBox.Top is > 842 - 745 and < 842 - 730, "char box of 'H' near (50, 735) in top-left space " + helloBox);
        int caret0 = layout.HitTestCaret(helloBox.Left + 1, (helloBox.Top + helloBox.Bottom) / 2);
        var endBox = layout.CharBoxes[hello + 11];   // the '.'
        int caret1 = layout.HitTestCaret(endBox.Right + 0.2, (endBox.Top + endBox.Bottom) / 2);
        Check(caret0 == hello && layout.GetSelectedText(caret0, caret1) == "Hello world.", $"drag selection text = '{layout.GetSelectedText(caret0, caret1)}'");
        Check(layout.GetSelectedText(caret1, caret0) == "Hello world.", "selection order independent");
        Check(layout.HitTestChar(helloBox.Left + 1, (helloBox.Top + helloBox.Bottom) / 2) == hello, "HitTestChar");
        Check(layout.GetWordRangeAt(hello + 7) is (int ws, int we) && p1[ws..we] == "world", "double-click word range");
        Check(layout.GetLineRangeAt(hello + 3) is (int ls, int le) && p1[ls..le].StartsWith("Hello world.") , "triple-click line range");
        int nat = await pdf.HitTestAsync(0, helloBox.Left + 1, (helloBox.Top + helloBox.Bottom) / 2);
        Check(nat == hello, "native hit test (PDFium) agrees: " + nat);
        var selRects = layout.GetSelectionRects(hello, hello + 12);
        Check(selRects.Count == 1 && selRects[0].Width > 40, "single-line selection gives one rectangle");
        var multi = layout.GetSelectionRects(hello, p1.IndexOf("Second paragraph", StringComparison.Ordinal) + 20);
        Check(multi.Count == 4, "multi-line selection gives a rectangle per line (" + multi.Count + ")");
        SavePng(Overlay(bmp, multi, 1.5, Colors.DodgerBlue, 0.35), Path.Combine(Out, "page1_selection.png"));

        // ---------------------------------------------------------------- search
        var hitsCI = await pdf.SearchAsync("needle");
        var hitsCS = await pdf.SearchAsync("needle", matchCase: true);
        Check(hitsCI.Count == 2 && hitsCS.Count == 1 && hitsCS[0].PageIndex == 2, $"search needle: case-insensitive {hitsCI.Count}, match-case {hitsCS.Count}");
        var pageAll = await pdf.SearchAsync("page");
        var pageWhole = await pdf.SearchAsync("page", wholeWord: true);
        Check(pageAll.Count > pageWhole.Count && pageWhole.Count >= 2, $"whole-word search: {pageWhole.Count} of {pageAll.Count}");
        Check(pageAll.All(h => h.Rects.Count > 0 && h.Rects.All(r => r.Width > 3 && r.Height > 3) && h.Context.Contains("page", StringComparison.OrdinalIgnoreCase)), "hits have rects and context");
        Check((await pdf.SearchAsync("zzzz-not-there")).Count == 0 && (await pdf.SearchAsync("")).Count == 0, "no hits / empty query");
        var progressLog = new List<int>();
        await pdf.SearchAsync("the", progress: new Progress<PdfSearchProgress>(p => { lock (progressLog) progressLog.Add(p.PagesDone); }));
        await Task.Delay(100);
        Check(progressLog.Count == 3, "search progress reported per page");
        SavePng(Overlay(bmp, pageAll.Where(h => h.PageIndex == 0).SelectMany(h => h.Rects), 1.5, Colors.Gold, 0.5), Path.Combine(Out, "page1_hits.png"));

        // rotated page: rect transform must follow the displayed page
        var needleHit = hitsCS[0];
        var nr = needleHit.Rects[0];
        Check(nr.Left is > 380 and < 420 && nr.Width is > 8 and < 20 && nr.Height > 25, "rotated page: hit rect follows /Rotate 90 " + nr);
        var cx = (nr.Left + nr.Right) / 2; var cy = (nr.Top + nr.Bottom) / 2;
        int nativeIdx = await pdf.HitTestAsync(2, cx, cy);
        Check(nativeIdx >= needleHit.CharIndex && nativeIdx < needleHit.CharIndex + needleHit.CharCount, "native hit test on rotated page inside the hit (" + nativeIdx + ")");
        var l3 = await pdf.GetPageTextLayoutAsync(2);
        int localIdx = l3.HitTestChar(cx, cy);
        Check(localIdx >= needleHit.CharIndex && localIdx < needleHit.CharIndex + needleHit.CharCount, "local hit test on rotated page agrees");
        var bmp3 = await pdf.RenderPageAsync(2, 1.5);
        SavePng(Overlay(bmp3, needleHit.Rects, 1.5, Colors.Red, 0.4), Path.Combine(Out, "page3_hits.png"));

        // ---------------------------------------------------------------- images
        var imgs = await pdf.GetPageImagesAsync(1);
        Check(imgs.Count == 2 && imgs.Count(i => i.ContentType == "image/jpeg") == 1 && imgs.Count(i => i.ContentType == "image/png") == 1,
            "page 2 images: " + string.Join(", ", imgs.Select(i => $"{i.ContentType} {i.PixelWidth}x{i.PixelHeight} {i.Data.Length}B @{i.Bounds}")));

        // ---------------------------------------------------------------- DOCX (built-in)
        string docx = Path.Combine(Out, "out.docx");
        sw.Restart();
        var conv = await FallbackDocxWriter.WriteAsync(pdf, docx);
        Note("fallback docx: " + sw.ElapsedMilliseconds + " ms");
        Check(conv.Success && conv.Method == PdfToWordMethod.BuiltIn && File.Exists(docx), "fallback DOCX written: " + (conv.Detail ?? "ok") + $" pages={conv.Pages} paragraphs={conv.Paragraphs} images={conv.Images}");
        VerifyDocx(docx);
        Check(!Directory.EnumerateFiles(Out, "~bn_*").Any(), "no temp files left");

        // orchestrator without Word
        var viaApi = await PdfToWord.ConvertAsync(pdf, Path.Combine(Out, "out_api.docx"), preferWord: false);
        Check(viaApi.Success && viaApi.Method == PdfToWordMethod.BuiltIn && viaApi.WordAttempt == null, "PdfToWord.ConvertAsync(preferWord:false)");
        Note("Word installed: " + WordConverter.IsWordInstalled);
        if (useWord)
        {
            sw.Restart();
            var wr = await PdfToWord.ConvertAsync(pdf, Path.Combine(Out, "out_word.docx"), preferWord: true);
            Note($"word run: {sw.ElapsedMilliseconds} ms status={wr.Status} method={wr.Method} wordAttempt={wr.WordAttempt} detail={wr.Detail}");
            Check(wr.Success && File.Exists(wr.OutputPath), "conversion with --word succeeded via " + wr.Method);
        }

        // ---------------------------------------------------------------- printing (paginator -> XPS, no printer involved)
        await Task.Run(() => PrintTest(pdf));

        // ---------------------------------------------------------------- memory + shutdown
        GC.Collect();
        long ws1 = Process.GetCurrentProcess().WorkingSet64;
        Note($"working set: {ws0 / 1048576} MB before, {ws1 / 1048576} MB after (incl. runtime, WPF and PDFium)");
        pdf.Dispose();
        await Task.Delay(300);
        Check(PdfRuntime.TryShutdown(), "engine shuts down when no document is open");
        Check(!PdfRuntime.IsEngineRunning, "engine thread gone");
        using (var again2 = await PdfDocumentHandle.OpenAsync(samplePath))
        {
            Check(again2.PageCount == 3 && PdfRuntime.IsEngineRunning, "engine restarts transparently");
            Check(!PdfRuntime.TryShutdown(), "shutdown refused while a document is open");
        }
    }

    private static async Task ExpectOpenError(string path, string? pw, PdfOpenError expected)
    {
        try
        {
            using var d = await PdfDocumentHandle.OpenAsync(path, pw);
            Check(false, $"open {Path.GetFileName(path)} should fail with {expected}");
        }
        catch (PdfOpenException ex)
        {
            Check(ex.Error == expected, $"open {Path.GetFileName(path)} -> {ex.Error} (expected {expected})");
        }
    }

    private static void VerifyDocx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart!.Document!.Body!;
        var paras = body.Elements<Paragraph>().ToList();
        int sections = body.Descendants<SectionProperties>().Count();
        Check(sections == 3, "docx has one section per page (" + sections + ")");
        var pg = body.Descendants<PageSize>().Select(p => (p.Width!.Value, p.Height!.Value)).ToList();
        Check(pg.Count == 3 && pg[0] == (11900u, 16840u) && pg[1] == (12240u, 15840u) && pg[2] == (16840u, 11900u), "docx page sizes follow the PDF pages: " + string.Join(" ", pg));
        string text = body.InnerText;
        Check(text.Contains("Hello world.") && text.Contains("Rotated page three") && text.Contains("Page 2 of the sample"), "docx contains text of all pages");
        var first = paras.FirstOrDefault(p => p.InnerText.Contains("Hello world."));
        Check(first != null && first.InnerText.Contains("merge the lines into one paragraph.") && first.InnerText.Contains("12345."), "wrapped lines merged into one paragraph: '" + first?.InnerText + "'");
        Check(text.Contains("hyphenated word"), "hyphenation removed");
        Check(text.Contains("café"), "docx keeps accents");
        Check(paras.Any(p => p.Descendants<Bold>().Any() && p.InnerText.StartsWith("BetterNotepad PDF test page 1")), "heading paragraph is bold");
        Check(doc.MainDocumentPart.ImageParts.Count() == 2, "docx has both images (" + doc.MainDocumentPart.ImageParts.Count() + ")");
        Check(body.Descendants<Drawing>().Count() == 2, "docx has 2 drawings");
        var errs = new OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019).Validate(doc).Take(10).ToList();
        Check(errs.Count == 0, "OpenXML schema validation: " + (errs.Count == 0 ? "clean" : string.Join(" | ", errs.Select(e => e.Description + " @" + e.Path?.XPath))));
        Note($"docx: {paras.Count} top-level paragraphs, {new FileInfo(path).Length} bytes");
    }

    private static void PrintTest(PdfDocumentHandle pdf)
    {
        string xps = Path.Combine(Out, "print.xps");
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var paginator = PdfPrinter.CreatePaginator(pdf, [0, 1, 2], new Size(793.7, 1122.5), 150);
                Check(paginator.PageCount == 3, "paginator has 3 pages");
                if (File.Exists(xps)) File.Delete(xps);
                using (var xd = new XpsDocument(xps, FileAccess.ReadWrite))
                {
                    var w = XpsDocument.CreateXpsDocumentWriter(xd);
                    w.Write(paginator);
                }
                Check(new FileInfo(xps).Length > 5000, "XPS written through the print paginator (" + new FileInfo(xps).Length + " bytes)");
                // look at the rotated landscape page 3 on portrait paper
                var page = paginator.GetPage(2);
                var rtb = new RenderTargetBitmap(794, 1123, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(page.Visual);
                rtb.Freeze();
                SavePng(rtb, Path.Combine(Out, "print_page3.png"));
                var px = Pixels(rtb);
                int dark = 0;
                for (int i = 0; i < px.Length; i += 4) if (px[i] < 120 && px[i + 1] < 120 && px[i + 2] < 120) dark++;
                Check(dark > 500, "printed page rendered with content (" + dark + " dark px)");
                page.Dispose();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) Check(false, "print test: " + error);
    }

    // ------------------------------------------------------------------ helpers

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1.0;

    private static byte[] Pixels(BitmapSource bs)
    {
        var src = bs.Format == PixelFormats.Pbgra32 || bs.Format == PixelFormats.Bgra32 ? bs : new FormatConvertedBitmap(bs, PixelFormats.Pbgra32, null, 0);
        int stride = src.PixelWidth * 4;
        var px = new byte[stride * src.PixelHeight];
        src.CopyPixels(px, stride, 0);
        return px;
    }

    private static uint Argb(byte[] px, BitmapSource bs, int x, int y)
    {
        int i = (y * bs.PixelWidth + x) * 4;
        return (uint)(px[i + 3] << 24 | px[i + 2] << 16 | px[i + 1] << 8 | px[i]);
    }

    private static bool IsColor(uint argb, int r, int g, int b, int tol) =>
        Math.Abs(((argb >> 16) & 255) - r) <= tol && Math.Abs(((argb >> 8) & 255) - g) <= tol && Math.Abs((argb & 255) - b) <= tol;

    private static void SavePng(BitmapSource bs, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bs));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static BitmapSource Overlay(BitmapSource src, IEnumerable<PdfRect> rects, double scale, System.Windows.Media.Color c, double alpha)
    {
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var px = Pixels(src);
        foreach (var r in rects)
        {
            int x0 = Math.Max(0, (int)Math.Floor(r.Left * scale)), x1 = Math.Min(w - 1, (int)Math.Ceiling(r.Right * scale));
            int y0 = Math.Max(0, (int)Math.Floor(r.Top * scale)), y1 = Math.Min(h - 1, (int)Math.Ceiling(r.Bottom * scale));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int i = y * stride + x * 4;
                    px[i] = (byte)(px[i] * (1 - alpha) + c.B * alpha);
                    px[i + 1] = (byte)(px[i + 1] * (1 - alpha) + c.G * alpha);
                    px[i + 2] = (byte)(px[i + 2] * (1 - alpha) + c.R * alpha);
                }
        }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, stride);
    }
}
