using System.Globalization;
using System.Text;
using System.Windows.Media;

namespace BetterNotepad.Editor.Export;

/// <summary>One styled piece of a code line. No color = the block's text color.</summary>
public readonly record struct CodeRun(string Text, Color? Color = null, bool Bold = false, bool Italic = false)
{
    public bool SameStyle(CodeRun other) => Color == other.Color && Bold == other.Bold && Italic == other.Italic;
}

/// <summary>A captured line before normalization: where it starts on screen (for tab stops) and whether it starts mid-line.</summary>
public sealed record RawCodeLine(IReadOnlyList<CodeRun> Runs, int StartColumn = 0, bool Partial = false);

/// <summary>Code ready for rich copying: lines of styled runs, tabs expanded, no trailing whitespace.</summary>
public sealed class CodeBlock
{
    public CodeBlock(IEnumerable<IReadOnlyList<CodeRun>> lines) => Lines = lines.ToList();

    public IReadOnlyList<IReadOnlyList<CodeRun>> Lines { get; }

    public string LineText(int index) => string.Concat(Lines[index].Select(r => r.Text));

    public string PlainText => string.Join("\r\n", Enumerable.Range(0, Lines.Count).Select(LineText));

    public int MaxLength => Lines.Count == 0 ? 0 : Enumerable.Range(0, Lines.Count).Max(i => LineText(i).Length);

    public int CountLongerThan(int max) => Enumerable.Range(0, Lines.Count).Count(i => LineText(i).Length > max);

    /// <summary>
    /// Tabs become spaces up to the next tab stop, trailing whitespace goes, adjacent runs of one style merge,
    /// blank lines at both ends are dropped and, with <paramref name="dedent"/>, the indentation shared by all
    /// lines is removed (a line that starts mid-line neither counts nor moves).
    /// </summary>
    public static CodeBlock Build(IEnumerable<RawCodeLine> raw, int tabSize, bool dedent)
    {
        tabSize = Math.Max(1, tabSize);
        var lines = new List<(List<CodeRun> Runs, bool Partial)>();
        foreach (var line in raw)
        {
            var runs = new List<CodeRun>();
            var col = line.StartColumn;
            foreach (var run in line.Runs)
            {
                var sb = new StringBuilder(run.Text.Length);
                foreach (var ch in run.Text)
                {
                    if (ch == '\t')
                    {
                        var n = tabSize - col % tabSize;
                        sb.Append(' ', n);
                        col += n;
                    }
                    else if (ch is '\r' or '\n') continue;
                    else if (char.IsControl(ch)) { sb.Append(' '); col++; }
                    else { sb.Append(ch); col++; }
                }
                Add(runs, run with { Text = sb.ToString() });
            }
            TrimEnd(runs);
            lines.Add((runs, line.Partial));
        }

        if (dedent)
        {
            var common = lines.Where(l => !l.Partial && l.Runs.Count > 0).Select(l => LeadingSpaces(l.Runs)).DefaultIfEmpty(0).Min();
            if (common > 0)
                foreach (var l in lines)
                    if (!l.Partial && l.Runs.Count > 0) RemoveLeading(l.Runs, common);
        }

        var first = lines.FindIndex(l => l.Runs.Count > 0);
        var last = lines.FindLastIndex(l => l.Runs.Count > 0);
        if (first < 0) return new CodeBlock([]);
        return new CodeBlock(lines.Skip(first).Take(last - first + 1).Select(l => (IReadOnlyList<CodeRun>)l.Runs));
    }

    private static void Add(List<CodeRun> runs, CodeRun run)
    {
        if (run.Text.Length == 0) return;
        if (runs.Count > 0 && runs[^1].SameStyle(run)) runs[^1] = runs[^1] with { Text = runs[^1].Text + run.Text };
        else runs.Add(run);
    }

    private static void TrimEnd(List<CodeRun> runs)
    {
        while (runs.Count > 0)
        {
            var t = runs[^1].Text.TrimEnd(' ');
            if (t.Length > 0)
            {
                runs[^1] = runs[^1] with { Text = t };
                return;
            }
            runs.RemoveAt(runs.Count - 1);
        }
    }

    private static int LeadingSpaces(List<CodeRun> runs)
    {
        var n = 0;
        foreach (var r in runs)
        {
            var i = 0;
            while (i < r.Text.Length && r.Text[i] == ' ') i++;
            n += i;
            if (i < r.Text.Length) break;
        }
        return n;
    }

    private static void RemoveLeading(List<CodeRun> runs, int count)
    {
        while (count > 0 && runs.Count > 0)
        {
            var t = runs[0].Text;
            if (t.Length <= count)
            {
                count -= t.Length;
                runs.RemoveAt(0);
            }
            else
            {
                runs[0] = runs[0] with { Text = t[count..] };
                count = 0;
            }
        }
    }
}

public enum CaptionPlacement { Above, Below }

/// <summary>How a copied code block looks in Word / Google Docs. Sizes in points.</summary>
public sealed record RichCodeStyle
{
    public string FontFamily { get; init; } = "Consolas";
    public double FontSize { get; init; } = 10;

    /// <summary>Line spacing of the code as a multiple (1 = single; captions stay single).</summary>
    public double LineSpacing { get; init; } = 1;

    public Color Text { get; init; } = Colors.Black;
    public Color? Background { get; init; }
    public Color? Border { get; init; }
    public double BorderWidth { get; init; } = 0.5;
    public bool LineNumbers { get; init; }
    public Color LineNumberColor { get; init; } = Color.FromRgb(0x80, 0x80, 0x80);

    /// <summary>Line between the numbers and the code.</summary>
    public Color? Gutter { get; init; }

    /// <summary>Inside the box (only with a background, a border or line numbers).</summary>
    public double PaddingX { get; init; } = 6;
    public double PaddingY { get; init; } = 3;

    /// <summary>Space above and below the code when it is not boxed.</summary>
    public double BlockSpacing { get; init; } = 6;

    /// <summary>Width of the text column (RTF tables need absolute widths; HTML uses 100 %).</summary>
    public double WidthCm { get; init; } = 15.5;

    public string? Caption { get; init; }
    public CaptionPlacement CaptionPlacement { get; init; } = CaptionPlacement.Above;

    /// <summary>A line under the listing, e.g. "Źródło: opracowanie własne".</summary>
    public string? Source { get; init; }

    public string CaptionFont { get; init; } = "Arial";
    public double CaptionSize { get; init; } = 10;
    public double CaptionSpacing { get; init; } = 6;
    public Color CaptionColor { get; init; } = Colors.Black;

    public bool Boxed => LineNumbers || Background is not null || Border is not null;
}

/// <summary>
/// Writes a <see cref="CodeBlock"/> as clipboard HTML (Google Docs, Word) and RTF (Word): one paragraph per line with
/// explicit font, size, zero indents and single spacing, so the destination's body style (first-line indent,
/// 1.5 spacing) cannot leak in. Line numbers, a background or a border turn it into a table, one row per line.
/// </summary>
public static class RichCodeWriter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ───────────── HTML ─────────────

    public static string Html(CodeBlock block, RichCodeStyle s)
    {
        var sb = new StringBuilder();
        var (above, below, source) = Parts(s);
        if (above is not null) HtmlCaption(sb, above, s, s.CaptionSpacing, s.CaptionSpacing, keepNext: true);
        if (s.Boxed) HtmlTable(sb, block, s, keepLast: below is not null);
        else HtmlParagraphs(sb, block, s, top: above is not null ? 0 : s.BlockSpacing, bottom: below is not null || source is not null ? 0 : s.BlockSpacing,
            keepLast: below is not null);
        if (below is not null) HtmlCaption(sb, below, s, s.CaptionSpacing, source is not null ? 0 : s.CaptionSpacing, keepNext: source is not null);
        if (source is not null) HtmlCaption(sb, source, s, below is not null ? 0 : s.CaptionSpacing, s.CaptionSpacing, keepNext: false);
        return sb.ToString();
    }

    private static (string? Above, string? Below, string? Source) Parts(RichCodeStyle s)
    {
        var caption = string.IsNullOrWhiteSpace(s.Caption) ? null : s.Caption.Trim();
        var source = string.IsNullOrWhiteSpace(s.Source) ? null : s.Source.Trim();
        return s.CaptionPlacement == CaptionPlacement.Above ? (caption, null, source) : (null, caption, source);
    }

    private static string ParaCss(double top, double bottom, string align, bool keepNext, double lineSpacing = 1)
        => $"margin-top:{Pt(top)};margin-bottom:{Pt(bottom)};margin-left:0pt;margin-right:0pt;text-indent:0pt;line-height:{Math.Round(lineSpacing * 100).ToString(Inv)}%;text-align:{align}"
           + (keepNext ? ";page-break-after:avoid" : "");

    private static string FontCss(string family, double size, Color color, bool bold = false, bool italic = false, bool code = false)
        => $"font-family:{(code ? CodeFont(family) : CssFont(family))};font-size:{Pt(size)};color:{Hex(color)};font-weight:{(bold ? "bold" : "normal")};font-style:{(italic ? "italic" : "normal")}";

    private static void HtmlCaption(StringBuilder sb, string text, RichCodeStyle s, double top, double bottom, bool keepNext)
    {
        sb.Append("<p style=\"").Append(ParaCss(top, bottom, "center", keepNext)).Append("\"><span style=\"")
            .Append(FontCss(s.CaptionFont, s.CaptionSize, s.CaptionColor)).Append("\">").Append(Escape(text)).Append("</span></p>");
    }

    private static void HtmlParagraphs(StringBuilder sb, CodeBlock block, RichCodeStyle s, double top, double bottom, bool keepLast)
    {
        for (var i = 0; i < block.Lines.Count; i++)
        {
            var last = i == block.Lines.Count - 1;
            sb.Append("<p style=\"").Append(ParaCss(i == 0 ? top : 0, last ? bottom : 0, "left", keepLast && last, s.LineSpacing)).Append("\">");
            HtmlLine(sb, block.Lines[i], s);
            sb.Append("</p>");
        }
    }

    private static void HtmlLine(StringBuilder sb, IReadOnlyList<CodeRun> runs, RichCodeStyle s)
    {
        if (runs.Count == 0)
        {
            sb.Append("<span style=\"").Append(FontCss(s.FontFamily, s.FontSize, s.Text, code: true)).Append("\">&nbsp;</span>");
            return;
        }
        var text = string.Concat(runs.Select(r => r.Text));
        var pos = 0;
        foreach (var run in runs)
        {
            sb.Append("<span style=\"").Append(FontCss(s.FontFamily, s.FontSize, run.Color ?? s.Text, run.Bold, run.Italic, code: true))
                .Append(";white-space:pre-wrap\">");
            for (var k = 0; k < run.Text.Length; k++, pos++)
            {
                var ch = run.Text[k];
                if (ch == ' ')
                {
                    // the last space of a run before a non-space stays a normal space (lines can still wrap there);
                    // every other one is a no-break space, so Word's HTML import cannot collapse indentation
                    var keep = pos > 0 && pos + 1 < text.Length && text[pos + 1] != ' ';
                    sb.Append(keep ? " " : "&nbsp;");
                }
                else AppendEscaped(sb, ch);
            }
            sb.Append("</span>");
        }
    }

    private static void HtmlTable(StringBuilder sb, CodeBlock block, RichCodeStyle s, bool keepLast)
    {
        var numW = NumberColumnWidth(block, s);
        sb.Append("<table cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"border-collapse:collapse;border:none;width:100%;margin-left:0pt;margin-right:0pt\"><tbody>");
        var count = Math.Max(1, block.Lines.Count);
        for (var i = 0; i < count; i++)
        {
            var first = i == 0;
            var last = i == count - 1;
            var line = i < block.Lines.Count ? block.Lines[i] : [];
            var top = first ? s.PaddingY : 0;
            var bottom = last ? s.PaddingY : 0;
            sb.Append("<tr>");
            if (s.LineNumbers)
            {
                sb.Append("<td style=\"width:").Append(Pt(numW)).Append(';').Append(CellCss(s, first, last, left: true, right: false, gutter: true))
                    .Append(";padding:0pt ").Append(Pt(s.PaddingX)).Append(" 0pt ").Append(Pt(s.PaddingX)).Append("\">");
                sb.Append("<p style=\"").Append(ParaCss(top, bottom, "right", keepLast && last, s.LineSpacing)).Append("\"><span style=\"")
                    .Append(FontCss(s.FontFamily, s.FontSize, s.LineNumberColor, code: true)).Append("\">").Append((i + 1).ToString(Inv)).Append("</span></p></td>");
            }
            sb.Append("<td style=\"").Append(CellCss(s, first, last, left: !s.LineNumbers, right: true, gutter: false))
                .Append(";padding:0pt ").Append(Pt(s.PaddingX)).Append(" 0pt ").Append(Pt(s.PaddingX)).Append("\">");
            sb.Append("<p style=\"").Append(ParaCss(top, bottom, "left", keepLast && last, s.LineSpacing)).Append("\">");
            HtmlLine(sb, line, s);
            sb.Append("</p></td></tr>");
        }
        sb.Append("</tbody></table>");
    }

    private static string CellCss(RichCodeStyle s, bool first, bool last, bool left, bool right, bool gutter)
    {
        string Side(bool on, Color? c) => on && c is { } color ? $"{Pt(s.BorderWidth)} solid {Hex(color)}" : "none";
        var css = $"vertical-align:top;border-top:{Side(first, s.Border)};border-bottom:{Side(last, s.Border)};border-left:{Side(left, s.Border)};"
                  + $"border-right:{(gutter ? Side(true, s.Gutter) : Side(right, s.Border))}";
        if (s.Background is { } bg) css += ";background-color:" + Hex(bg);
        return css;
    }

    /// <summary>Digits × an em fraction wide enough for Consolas and Courier New, plus the cell padding.</summary>
    private static double NumberColumnWidth(CodeBlock block, RichCodeStyle s)
    {
        var digits = Math.Max(2, Math.Max(1, block.Lines.Count).ToString(Inv).Length);
        return Math.Ceiling(digits * s.FontSize * 0.6 + 2 * s.PaddingX);
    }

    /// <summary>CF_HTML ("HTML Format"): a header with UTF-8 byte offsets around the fragment.</summary>
    public static byte[] ClipboardHtml(string fragment)
    {
        const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string pre = "<html><head><meta charset=\"utf-8\"></head><body><!--StartFragment-->";
        const string post = "<!--EndFragment--></body></html>";
        var utf8 = new UTF8Encoding(false);
        var headerLen = utf8.GetByteCount(string.Format(Inv, header, 0, 0, 0, 0));
        var startFragment = headerLen + utf8.GetByteCount(pre);
        var endFragment = startFragment + utf8.GetByteCount(fragment);
        var endHtml = endFragment + utf8.GetByteCount(post);
        return utf8.GetBytes(string.Format(Inv, header, headerLen, endHtml, startFragment, endFragment) + pre + fragment + post);
    }

    // ───────────── RTF ─────────────

    public static string Rtf(CodeBlock block, RichCodeStyle s)
    {
        var colors = new List<Color>();
        int C(Color c)
        {
            var i = colors.IndexOf(c);
            if (i < 0) { colors.Add(c); i = colors.Count - 1; }
            return i + 1; // 0 = auto
        }

        var body = new StringBuilder();
        var (above, below, source) = Parts(s);
        var fs = Half(s.FontSize);
        var sl = (int)Math.Round(240 * s.LineSpacing); // \slN\slmult1: N/240 lines
        if (above is not null) RtfCaption(body, above, s, C(s.CaptionColor), s.CaptionSpacing, s.CaptionSpacing, keepNext: true);

        if (s.Boxed)
        {
            var total = Tw(s.WidthCm / 2.54 * 72);
            var numW = s.LineNumbers ? Tw(NumberColumnWidth(block, s)) : 0;
            var count = Math.Max(1, block.Lines.Count);
            for (var i = 0; i < count; i++)
            {
                var first = i == 0;
                var last = i == count - 1;
                var line = i < block.Lines.Count ? block.Lines[i] : [];
                var sbTw = first ? Tw(s.PaddingY) : 0;
                var saTw = last ? Tw(s.PaddingY) : 0;
                var keep = below is not null && last ? @"\keepn" : "";
                body.Append(@"\trowd\trgaph0\trleft0\trftsWidth2\trwWidth5000\trpaddl0\trpaddr0\trpaddfl3\trpaddfr3");
                if (s.LineNumbers)
                {
                    body.Append(@"\clvertalt").Append(RtfBorders(s, C, first, last, left: true, right: false, gutter: true));
                    if (s.Background is { } bg) body.Append(@"\clcbpat").Append(C(bg));
                    body.Append(@"\clftsWidth3\clwWidth").Append(numW).Append(@"\cellx").Append(numW);
                }
                body.Append(@"\clvertalt").Append(RtfBorders(s, C, first, last, left: !s.LineNumbers, right: true, gutter: false));
                if (s.Background is { } bg2) body.Append(@"\clcbpat").Append(C(bg2));
                body.Append(@"\clftsWidth3\clwWidth").Append(total - numW).Append(@"\cellx").Append(total).Append("\r\n");

                var pad = Tw(s.PaddingX);
                if (s.LineNumbers)
                {
                    body.Append($@"\pard\plain\intbl\qr\li{pad}\ri{pad}\fi0\sb{sbTw}\sa{saTw}\sl{sl}\slmult1{keep}\f0\fs{fs}\cf{C(s.LineNumberColor)} ")
                        .Append((i + 1).ToString(Inv)).Append(@"\cell").Append("\r\n");
                }
                body.Append($@"\pard\plain\intbl\ql\li{pad}\ri{pad}\fi0\sb{sbTw}\sa{saTw}\sl{sl}\slmult1{keep}\f0\fs{fs}\cf{C(s.Text)} ");
                RtfRuns(body, line, s, C);
                body.Append(@"\cell\row").Append("\r\n");
            }
        }
        else
        {
            var top = Tw(above is not null ? 0 : s.BlockSpacing);
            var bottom = Tw(below is not null || source is not null ? 0 : s.BlockSpacing);
            for (var i = 0; i < block.Lines.Count; i++)
            {
                var last = i == block.Lines.Count - 1;
                body.Append($@"\pard\plain\ql\li0\ri0\fi0\sb{(i == 0 ? top : 0)}\sa{(last ? bottom : 0)}\sl{sl}\slmult1{(below is not null && last ? @"\keepn" : "")}\f0\fs{fs}\cf{C(s.Text)} ");
                RtfRuns(body, block.Lines[i], s, C);
                body.Append(@"\par").Append("\r\n");
            }
        }

        if (below is not null) RtfCaption(body, below, s, C(s.CaptionColor), s.CaptionSpacing, source is not null ? 0 : s.CaptionSpacing, keepNext: source is not null);
        if (source is not null) RtfCaption(body, source, s, C(s.CaptionColor), below is not null ? 0 : s.CaptionSpacing, s.CaptionSpacing, keepNext: false);

        var rtf = new StringBuilder();
        rtf.Append(@"{\rtf1\ansi\ansicpg1252\deff0\uc1").Append("\r\n");
        rtf.Append(@"{\fonttbl{\f0\fmodern\fprq1\fcharset0 ").Append(RtfFontName(s.FontFamily)).Append(@";}{\f1\fswiss\fprq2\fcharset0 ")
            .Append(RtfFontName(s.CaptionFont)).Append(";}}\r\n");
        rtf.Append(@"{\colortbl;");
        foreach (var c in colors) rtf.Append(@"\red").Append(c.R).Append(@"\green").Append(c.G).Append(@"\blue").Append(c.B).Append(';');
        rtf.Append("}\r\n");
        rtf.Append(body);
        rtf.Append('}');
        return rtf.ToString();
    }

    private static string RtfBorders(RichCodeStyle s, Func<Color, int> c, bool first, bool last, bool left, bool right, bool gutter)
    {
        var w = Math.Max(2, Tw(s.BorderWidth));
        string Side(string name, bool on, Color? color) => on && color is { } col ? $@"\clbrdr{name}\brdrs\brdrw{w}\brdrcf{c(col)}" : "";
        return Side("t", first, s.Border) + Side("l", left, s.Border) + Side("b", last, s.Border)
               + (gutter ? Side("r", true, s.Gutter) : Side("r", right, s.Border));
    }

    private static void RtfCaption(StringBuilder sb, string text, RichCodeStyle s, int color, double top, double bottom, bool keepNext)
    {
        sb.Append($@"\pard\plain\qc\li0\ri0\fi0\sb{Tw(top)}\sa{Tw(bottom)}\sl240\slmult1{(keepNext ? @"\keepn" : "")}\f1\fs{Half(s.CaptionSize)}\cf{color} ");
        RtfEscape(sb, text);
        sb.Append(@"\par").Append("\r\n");
    }

    private static void RtfRuns(StringBuilder sb, IReadOnlyList<CodeRun> runs, RichCodeStyle s, Func<Color, int> c)
    {
        foreach (var run in runs)
        {
            sb.Append(@"{\cf").Append(c(run.Color ?? s.Text));
            if (run.Bold) sb.Append(@"\b");
            if (run.Italic) sb.Append(@"\i");
            sb.Append(' ');
            RtfEscape(sb, run.Text);
            sb.Append('}');
        }
    }

    public static void RtfEscape(StringBuilder sb, string text)
    {
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\': sb.Append(@"\\"); break;
                case '{': sb.Append(@"\{"); break;
                case '}': sb.Append(@"\}"); break;
                case '\t': sb.Append(@"\tab "); break;
                default:
                    if (ch < 0x20) break;
                    if (ch < 0x80) sb.Append(ch);
                    else sb.Append(@"\u").Append(((short)ch).ToString(Inv)).Append('?'); // surrogate halves pass one by one
                    break;
            }
        }
    }

    private static string RtfFontName(string family)
    {
        var sb = new StringBuilder();
        RtfEscape(sb, family.Replace(";", ""));
        return sb.ToString();
    }

    // ───────────── helpers ─────────────

    private static string Pt(double v) => v.ToString("0.##", Inv) + "pt";

    private static int Tw(double pt) => (int)Math.Round(pt * 20);

    private static int Half(double pt) => (int)Math.Round(pt * 2);

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static string CssFont(string family) => family.Contains(' ') ? "'" + family.Replace("'", "") + "'" : family;

    /// <summary>Code font with monospace fallbacks: Google Docs may not offer Consolas, and proportional code would lose its alignment.</summary>
    private static string CodeFont(string family)
        => CssFont(family) + (family.Equals("Courier New", StringComparison.OrdinalIgnoreCase) ? "" : ",'Courier New'") + ",monospace";

    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var ch in text) AppendEscaped(sb, ch);
        return sb.ToString();
    }

    private static void AppendEscaped(StringBuilder sb, char ch)
    {
        switch (ch)
        {
            case '&': sb.Append("&amp;"); break;
            case '<': sb.Append("&lt;"); break;
            case '>': sb.Append("&gt;"); break;
            case '"': sb.Append("&quot;"); break;
            default: sb.Append(ch); break;
        }
    }
}
