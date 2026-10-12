using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace BetterNotepad.Pdf;

public sealed class DocxWriterOptions
{
    /// <summary>Embed raster images of the pages (JPEGs are copied untouched).</summary>
    public bool IncludeImages { get; init; } = true;
    public long MaxImagePixels { get; init; } = 30_000_000;
}

/// <summary>
/// Built-in PDF -> DOCX converter (used when Word is not installed): one Word section per PDF page (same page size and margins),
/// paragraphs rebuilt from the extracted lines (line breaks merged, hyphenation removed, indents / alignment / font size / bold
/// approximated from geometry) and page images in reading order. Streams the document (OpenXmlWriter) so large PDFs stay cheap.
/// No tables, columns or vector graphics - for those Microsoft Word gives better results.
/// </summary>
public static class FallbackDocxWriter
{
    private const double TwipsPerPoint = 20;
    private const double EmuPerPoint = 12700;

    private sealed record Line(PdfRect Box, string Text, double Size, bool Bold);

    private sealed class Para
    {
        public readonly List<Line> Lines = [];
        public PdfRect Box;
    }

    private abstract record Block(float Top, float Bottom);
    private sealed record TextBlock(Para Para, float Top2, float Bottom2) : Block(Top2, Bottom2);
    private sealed record ImageBlock(PdfPageImage Image, float Top2, float Bottom2) : Block(Top2, Bottom2);

    private static readonly Regex s_bullet = new(@"^\s*([•●◦▪■□‣∙·\-–—\*]|\d{1,3}[.)]|[A-Za-z][.)]|\(\d{1,3}\)|\([a-zA-Z]\))(\s|$)", RegexOptions.Compiled);
    private static readonly Regex s_badXml = new(@"[\x00-\x08\x0B\x0C\x0E-\x1F\uFFFE\uFFFF]", RegexOptions.Compiled);

    public static async Task<PdfToWordResult> WriteAsync(PdfDocumentHandle pdf, string docxPath, DocxWriterOptions? options = null,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        options ??= new DocxWriterOptions();
        string full = Path.GetFullPath(docxPath);
        string tmp = Path.Combine(Path.GetDirectoryName(full) ?? ".", "~bn_" + Guid.NewGuid().ToString("N")[..8] + ".docx");
        int paragraphs = 0, images = 0;
        try
        {
            using (var doc = WordprocessingDocument.Create(tmp, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                AddStyles(main);
                var meta = pdf.Metadata;
                doc.PackageProperties.Title = meta.Title;
                doc.PackageProperties.Creator = meta.Author;
                doc.PackageProperties.Subject = meta.Subject;
                doc.PackageProperties.Keywords = meta.Keywords;
                doc.PackageProperties.Created = meta.CreationDate?.UtcDateTime ?? DateTime.UtcNow;

                using (var w = OpenXmlWriter.Create(main))
                {
                    w.WriteStartElement(new Document());
                    w.WriteStartElement(new Body());
                    uint imageId = 1;
                    for (int p = 0; p < pdf.PageCount; p++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var size = pdf.PageSizes[p];
                        var text = await pdf.GetPageTextLayoutAsync(p, includeFontInfo: true, ct).ConfigureAwait(false);
                        IReadOnlyList<PdfPageImage> imgs = options.IncludeImages
                            ? await pdf.GetPageImagesAsync(p, options.MaxImagePixels, ct).ConfigureAwait(false) : [];

                        var lines = ExtractLines(text);
                        var (mL, mR, mT, mB) = Margins(size, lines, imgs);
                        double textW = Math.Max(72, size.Width - mL - mR);
                        var paras = GroupParagraphs(lines, textW);
                        var blocks = Layout(paras, imgs);

                        var elements = new List<OpenXmlElement>();
                        float prevBottom = float.NaN;
                        double colLeft = lines.Count > 0 ? lines.Min(l => l.Box.Left) : mL;
                        double colRight = lines.Count > 0 ? lines.Max(l => l.Box.Right) : size.Width - mR;
                        foreach (var b in blocks)
                        {
                            double before = float.IsNaN(prevBottom) ? 0 : Math.Clamp(b.Top - prevBottom, 0, 40);
                            switch (b)
                            {
                                case TextBlock tb:
                                    elements.Add(BuildParagraph(tb.Para, mL, colLeft, colRight, textW, before));
                                    paragraphs++;
                                    break;
                                case ImageBlock ib:
                                    var part = ib.Image.ContentType == "image/jpeg" ? main.AddImagePart(ImagePartType.Jpeg) : main.AddImagePart(ImagePartType.Png);
                                    using (var ms = new MemoryStream(ib.Image.Data, false)) part.FeedData(ms);
                                    elements.Add(BuildImageParagraph(main.GetIdOfPart(part), ib.Image, mL, textW, size.Height - mT - mB - 6, before, imageId++));
                                    images++;
                                    break;
                            }
                            prevBottom = b.Bottom;
                        }
                        if (elements.Count == 0) elements.Add(new Paragraph());

                        var sect = BuildSection(size, mL, mR, mT, mB);
                        bool last = p == pdf.PageCount - 1;
                        if (!last)
                        {
                            var lastPara = (Paragraph)elements[^1];
                            (lastPara.ParagraphProperties ??= new ParagraphProperties()).Append(sect);
                        }
                        foreach (var e in elements) w.WriteElement(e);
                        if (last) w.WriteElement(sect);
                        progress?.Report(p + 1);
                    }
                    if (pdf.PageCount == 0) w.WriteElement(new Paragraph());
                    w.WriteEndElement();   // Body
                    w.WriteEndElement();   // Document
                }
            }
            File.Move(tmp, full, true);
            return new PdfToWordResult { Status = PdfToWordStatus.Ok, Method = PdfToWordMethod.BuiltIn, OutputPath = full, Pages = pdf.PageCount, Paragraphs = paragraphs, Images = images };
        }
        catch (OperationCanceledException)
        {
            TryDelete(tmp);
            return new PdfToWordResult { Status = PdfToWordStatus.Canceled, Method = PdfToWordMethod.None, OutputPath = full };
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            return new PdfToWordResult { Status = PdfToWordStatus.Failed, Method = PdfToWordMethod.None, OutputPath = full, Detail = ex.GetType().Name + ": " + ex.Message, Error = ex };
        }
    }

    private static void TryDelete(string p) { try { File.Delete(p); } catch { } }

    private static void AddStyles(MainDocumentPart main)
    {
        var sp = main.AddNewPart<StyleDefinitionsPart>();
        sp.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Calibri", ComplexScript = "Calibri" },
                    new FontSize { Val = "22" }, new FontSizeComplexScript { Val = "22" },
                    new Languages { Val = "en-US", EastAsia = "en-US", Bidi = "ar-SA" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(new SpacingBetweenLines { After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto }))),
            new Style(new StyleName { Val = "Normal" }, new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });
        sp.Styles.Save();
    }

    // ------------------------------------------------------------ geometry / text analysis

    private static List<Line> ExtractLines(PdfPageText t)
    {
        var res = new List<Line>();
        string s = t.Text;
        int i = 0;
        while (i <= s.Length)
        {
            int e = i;
            while (e < s.Length && s[e] is not ('\r' or '\n')) e++;
            if (e > i)
            {
                PdfRect box = default;
                double sizeSum = 0; int n = 0, bold = 0;
                for (int k = i; k < e; k++)
                {
                    char c = s[k];
                    if (char.IsWhiteSpace(c)) continue;
                    var b = t.CharBoxes[k];
                    if (!b.IsEmpty) box = box.Union(b);
                    if (t.FontSizes != null && t.FontSizes[k] > 0) { sizeSum += t.FontSizes[k]; n++; if (t.Bold![k]) bold++; }
                }
                string line = Clean(s[i..e]);
                if (line.Length > 0 && !box.IsEmpty)
                    res.Add(new Line(box, line, n > 0 ? sizeSum / n : Math.Max(6, box.Height / 1.2), n > 0 && bold * 2 > n));
            }
            i = e + 1;
            if (e < s.Length && s[e] == '\r' && i < s.Length && s[i] == '\n') i++;
            if (e >= s.Length) break;
        }
        return res;
    }

    private static string Clean(string raw)
    {
        raw = s_badXml.Replace(raw, " ");
        var sb = new StringBuilder(raw.Length);
        bool sp = false;
        foreach (char c in raw)
        {
            if (c == '\t' || (char.IsWhiteSpace(c) && c != '\u00AD')) { if (!sp && sb.Length > 0) sb.Append(' '); sp = true; }
            else { sb.Append(c); sp = false; }
        }
        return sb.ToString().TrimEnd();
    }

    private static (double L, double R, double T, double B) Margins(PdfPageSize size, List<Line> lines, IReadOnlyList<PdfPageImage> imgs)
    {
        double l = double.MaxValue, r = double.MaxValue, t = double.MaxValue, b = double.MaxValue;
        foreach (var ln in lines)
        {
            l = Math.Min(l, ln.Box.Left); r = Math.Min(r, size.Width - ln.Box.Right);
            t = Math.Min(t, ln.Box.Top); b = Math.Min(b, size.Height - ln.Box.Bottom);
        }
        foreach (var im in imgs)
        {
            l = Math.Min(l, im.Bounds.Left); r = Math.Min(r, size.Width - im.Bounds.Right);
            t = Math.Min(t, im.Bounds.Top); b = Math.Min(b, size.Height - im.Bounds.Bottom);
        }
        if (l == double.MaxValue) return (36, 36, 36, 36);
        double mx = size.Width * 0.35, my = size.Height * 0.3;
        return (Math.Clamp(l, 18, mx), Math.Clamp(r, 18, mx), Math.Clamp(t, 18, my), Math.Clamp(b, 18, my));
    }

    private static List<Para> GroupParagraphs(List<Line> lines, double textW)
    {
        var paras = new List<Para>();
        if (lines.Count == 0) return paras;
        double colRight = lines.Max(l => l.Box.Right);
        Para? cur = null;
        foreach (var c in lines)
        {
            if (cur != null && Continues(cur, c, colRight, textW)) { cur.Lines.Add(c); cur.Box = cur.Box.Union(c.Box); }
            else { cur = new Para(); cur.Lines.Add(c); cur.Box = c.Box; paras.Add(cur); }
        }
        return paras;
    }

    private static bool Continues(Para para, Line c, double colRight, double textW)
    {
        var p = para.Lines[^1];
        double h = Math.Max(p.Box.Height, c.Box.Height);
        double gap = c.Box.Top - p.Box.Bottom;
        if (Math.Abs(p.Size - c.Size) > 0.9 || p.Bold != c.Bold) return false;
        if (gap > 0.45 * h || gap < -0.9 * h || c.Box.Top < p.Box.Top) return false;
        if (s_bullet.IsMatch(c.Text)) return false;   // list item / numbered heading starts a new paragraph
        // Previous line clearly short (did not reach the right edge): paragraph ended there.
        if (p.Box.Right < colRight - 0.18 * textW)
        {
            char last = p.Text[^1];
            if (last is '.' or '!' or '?' or ':' or ';' or '…' || p.Box.Right < colRight - 0.35 * textW) return false;
        }
        double bodyLeft = para.Lines.Count >= 2 ? para.Lines[1].Box.Left : p.Box.Left;
        if (para.Lines.Count == 1)
        {
            if (c.Box.Left > p.Box.Left + h) return false;   // deeper indent: new block
        }
        else if (Math.Abs(c.Box.Left - bodyLeft) > 0.6 * h) return false;
        return true;
    }

    private static List<Block> Layout(List<Para> paras, IReadOnlyList<PdfPageImage> imgs)
    {
        var blocks = new List<Block>();
        var sortedImgs = imgs.OrderBy(i => i.Bounds.Top).ToList();
        int ii = 0;
        foreach (var p in paras)
        {
            while (ii < sortedImgs.Count && sortedImgs[ii].Bounds.Top <= p.Box.Top + 1)
            {
                var im = sortedImgs[ii++];
                blocks.Add(new ImageBlock(im, im.Bounds.Top, im.Bounds.Bottom));
            }
            blocks.Add(new TextBlock(p, p.Box.Top, p.Box.Bottom));
        }
        while (ii < sortedImgs.Count) { var im = sortedImgs[ii++]; blocks.Add(new ImageBlock(im, im.Bounds.Top, im.Bounds.Bottom)); }
        return blocks;
    }

    // ------------------------------------------------------------ OpenXML building

    private static Paragraph BuildParagraph(Para para, double marginLeft, double colLeft, double colRight, double textW, double spaceBeforePt)
    {
        var lines = para.Lines;
        // text with line merging / de-hyphenation
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            string t = l.Text;
            if (sb.Length == 0) { sb.Append(t); continue; }
            if (sb[^1] == '\u00AD') { sb.Length--; sb.Append(t.TrimStart()); }
            else if (sb[^1] == '-' && sb.Length >= 2 && char.IsLetter(sb[^2]) && t.Length > 0 && char.IsLower(t[0])) { sb.Length--; sb.Append(t); }
            else { sb.Append(' ').Append(t); }
        }
        string text = sb.ToString().Replace("\u00AD", "");

        double size = lines.Average(l => l.Size);
        bool bold = lines.Count(l => l.Bold) * 2 > lines.Count;

        double bodyLeft = lines.Count >= 2 ? lines.Skip(1).Min(l => l.Box.Left) : lines[0].Box.Left;
        double first = lines[0].Box.Left - bodyLeft;
        double leftIndent = Math.Max(0, bodyLeft - marginLeft);
        var ind = new Indentation();
        bool hasInd = false;
        if (leftIndent > 4) { ind.Left = Tw(leftIndent); hasInd = true; }
        if (first > 3) { ind.FirstLine = Tw(first); hasInd = true; }
        else if (first < -3) { ind.Hanging = Tw(-first); hasInd = true; }

        var pPr = new ParagraphProperties();
        if (spaceBeforePt > 2) pPr.Append(new SpacingBetweenLines { Before = Tw(spaceBeforePt), After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto });
        if (hasInd) pPr.Append(ind);
        var jc = DetectAlignment(para, colLeft, colRight, textW);
        if (jc != null) pPr.Append(new Justification { Val = jc });

        var rPr = new RunProperties();
        if (bold) rPr.Append(new Bold());
        int hp = (int)Math.Clamp(Math.Round(size * 2), 8, 144);
        rPr.Append(new FontSize { Val = hp.ToString() }, new FontSizeComplexScript { Val = hp.ToString() });

        var p = new Paragraph();
        if (pPr.HasChildren) p.Append(pPr);
        p.Append(new Run(rPr, new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        return p;
    }

    private static EnumValue<JustificationValues>? DetectAlignment(Para para, double colLeft, double colRight, double textW)
    {
        var lines = para.Lines;
        double colCenter = (colLeft + colRight) / 2;
        if (lines.Count <= 4 && lines.All(l => Math.Abs((l.Box.Left + l.Box.Right) / 2 - colCenter) < 3) && para.Box.Left - colLeft > 0.06 * textW)
            return JustificationValues.Center;
        if (lines.All(l => Math.Abs(l.Box.Right - colRight) < 2) && para.Box.Left - colLeft > 0.3 * textW)
            return JustificationValues.Right;
        if (lines.Count >= 3)
        {
            int flush = lines.Take(lines.Count - 1).Count(l => Math.Abs(l.Box.Right - colRight) < 2.5);
            if (flush >= (lines.Count - 1) * 0.7) return JustificationValues.Both;
        }
        return null;
    }

    private static Paragraph BuildImageParagraph(string relId, PdfPageImage img, double marginLeft, double textW, double maxH, double spaceBeforePt, uint id)
    {
        double w = Math.Max(4, img.Bounds.Width), h = Math.Max(4, img.Bounds.Height);
        double k = Math.Min(1.0, Math.Min(textW / w, maxH / h));
        w *= k; h *= k;
        long cx = (long)(w * EmuPerPoint), cy = (long)(h * EmuPerPoint);

        var drawing = new Drawing(new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
            new DW.DocProperties { Id = id, Name = "Picture " + id },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = 0U, Name = "image" + id }, new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = relId }, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(
                        new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = cx, Cy = cy }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U });

        var pPr = new ParagraphProperties();
        if (spaceBeforePt > 2) pPr.Append(new SpacingBetweenLines { Before = Tw(spaceBeforePt), After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto });
        double off = img.Bounds.Left - marginLeft;
        double centerOff = Math.Abs((img.Bounds.Left + img.Bounds.Width / 2) - (marginLeft + textW / 2));
        if (centerOff < 8 && w < textW * 0.95) pPr.Append(new Justification { Val = JustificationValues.Center });
        else if (off > 6) pPr.Append(new Indentation { Left = Tw(Math.Min(off, textW - w)) });
        var p = new Paragraph();
        if (pPr.HasChildren) p.Append(pPr);
        p.Append(new Run(drawing));
        return p;
    }

    private static SectionProperties BuildSection(PdfPageSize size, double mL, double mR, double mT, double mB)
    {
        uint w = (uint)Math.Round(size.Width * TwipsPerPoint), h = (uint)Math.Round(size.Height * TwipsPerPoint);
        return new SectionProperties(
            new SectionType { Val = SectionMarkValues.NextPage },
            new PageSize { Width = w, Height = h, Orient = w > h ? PageOrientationValues.Landscape : PageOrientationValues.Portrait },
            new PageMargin
            {
                Top = (int)Math.Round(mT * TwipsPerPoint), Bottom = (int)Math.Round(mB * TwipsPerPoint),
                Left = (uint)Math.Round(mL * TwipsPerPoint), Right = (uint)Math.Round(mR * TwipsPerPoint),
                Header = 360U, Footer = 360U, Gutter = 0U,
            });
    }

    private static string Tw(double pt) => ((long)Math.Round(pt * TwipsPerPoint)).ToString();
}
