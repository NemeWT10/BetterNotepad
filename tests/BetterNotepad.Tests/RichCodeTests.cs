using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Xml.Linq;
using BetterNotepad.Editor.Export;
using WDoc = System.Windows.Documents;

namespace BetterNotepad.Tests;

public sealed class RichCodeTests
{
    // ---------------------------------------------------------------- helpers

    private static RawCodeLine Raw(string text, int startColumn = 0, bool partial = false)
        => new([new CodeRun(text)], startColumn, partial);

    /// <summary>Plain one-run lines, 4-wide tabs, no dedent.</summary>
    private static CodeBlock Code(params string[] lines) => CodeBlock.Build(lines.Select(l => Raw(l)), 4, false);

    private static CodeBlock CodeOf(params CodeRun[][] lines)
        => CodeBlock.Build(lines.Select(l => new RawCodeLine(l)), 4, false);

    private static string Texts(CodeBlock b) => string.Join("|", Enumerable.Range(0, b.Lines.Count).Select(b.LineText));

    private static List<(string Style, string Inner)> Paragraphs(string html)
        => Regex.Matches(html, "<p style=\"([^\"]*)\">(.*?)</p>", RegexOptions.Singleline)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();

    private static string StripTags(string inner) => Regex.Replace(inner, "<[^>]+>", "");

    private static XElement ParseHtml(string html)
        => XDocument.Parse("<root>" + html.Replace("&nbsp;", "&#160;") + "</root>").Root!;

    private static string[] RtfLines(string rtf) => rtf.Split("\r\n");

    private static string RtfLineWith(string rtf, string text) => RtfLines(rtf).First(l => l.Contains(text));

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static bool BracesBalanced(string rtf)
    {
        var depth = 0;
        for (var i = 0; i < rtf.Length; i++)
        {
            switch (rtf[i])
            {
                case '\\': i++; break; // escaped char or control word start: the next char is never a group delimiter
                case '{': depth++; break;
                case '}': if (--depth < 0) return false; break;
            }
        }
        return depth == 0;
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static WDoc.FlowDocument LoadRtf(string rtf)
    {
        var doc = new WDoc.FlowDocument();
        var range = new WDoc.TextRange(doc.ContentStart, doc.ContentEnd);
        using var ms = new MemoryStream(Encoding.ASCII.GetBytes(rtf));
        range.Load(ms, System.Windows.DataFormats.Rtf);
        return doc;
    }

    private static string DocText(WDoc.FlowDocument doc) => new WDoc.TextRange(doc.ContentStart, doc.ContentEnd).Text;

    private static string ParaText(WDoc.Paragraph p) => new WDoc.TextRange(p.ContentStart, p.ContentEnd).Text;

    // ---------------------------------------------------------------- CodeBlock.Build

    [Fact]
    public void Build_TabsRespectStartColumnAndTabSize()
    {
        var b = CodeBlock.Build([Raw("\tx", startColumn: 2)], 4, false);
        Assert.Equal("  x", b.LineText(0));
    }

    [Theory]
    [InlineData("\tx", 4, "    x")]
    [InlineData("a\tb", 4, "a   b")]
    [InlineData("ab\tc", 4, "ab  c")]
    [InlineData("abcd\te", 4, "abcd    e")]
    [InlineData("a\tb", 8, "a       b")]
    [InlineData("\t\tx", 2, "    x")]
    public void Build_TabsGoToNextTabStop(string text, int tabSize, string expected)
        => Assert.Equal(expected, CodeBlock.Build([Raw(text)], tabSize, false).LineText(0));

    [Fact]
    public void Build_TabStopCountsAcrossRuns()
    {
        var b = CodeBlock.Build([new RawCodeLine([new CodeRun("ab"), new CodeRun("\tc")])], 4, false);
        Assert.Equal("ab  c", b.LineText(0));
    }

    [Fact]
    public void Build_NonPositiveTabSizeIsTreatedAsOne()
        => Assert.Equal(" x", CodeBlock.Build([Raw("\tx")], 0, false).LineText(0));

    [Fact]
    public void Build_TrailingWhitespaceTrimmedAcrossRuns()
    {
        var red = Color.FromRgb(255, 0, 0);
        var b = CodeOf([new CodeRun("x ", red), new CodeRun("   ", Colors.Blue), new CodeRun("\t", null)]);
        var runs = Assert.Single(b.Lines);
        var run = Assert.Single(runs);
        Assert.Equal("x", run.Text);
        Assert.Equal(red, run.Color);
    }

    [Fact]
    public void Build_InnerSpacesAreKept()
        => Assert.Equal("a  b", Code("a  b   ").LineText(0));

    [Fact]
    public void Build_AdjacentRunsOfSameStyleMerge()
    {
        var red = Color.FromRgb(255, 0, 0);
        var b = CodeOf([new CodeRun("a", red), new CodeRun("b", red), new CodeRun("c"), new CodeRun("d")]);
        var runs = b.Lines[0];
        Assert.Equal(2, runs.Count);
        Assert.Equal("ab", runs[0].Text);
        Assert.Equal(red, runs[0].Color);
        Assert.Equal("cd", runs[1].Text);
    }

    [Fact]
    public void Build_RunsDifferingInBoldOrItalicDoNotMerge()
    {
        var b = CodeOf([new CodeRun("a"), new CodeRun("b", Bold: true), new CodeRun("c", Italic: true), new CodeRun("d", Bold: true, Italic: true)]);
        Assert.Equal(4, b.Lines[0].Count);
        Assert.Equal("abcd", b.LineText(0));
    }

    [Fact]
    public void Build_EmptyRunsAreDropped()
    {
        var b = CodeOf([new CodeRun(""), new CodeRun("a"), new CodeRun(""), new CodeRun("b")]);
        Assert.Single(b.Lines[0]);
        Assert.Equal("ab", b.LineText(0));
    }

    [Fact]
    public void Build_LeadingAndTrailingBlankLinesDropped_MiddleOnesKept()
    {
        var b = Code("", "   ", "a", "", "b", "  ", "");
        Assert.Equal(3, b.Lines.Count);
        Assert.Equal("a||b", Texts(b));
        Assert.Empty(b.Lines[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("  |\t|")]
    public void Build_NoContent_NoLines(string joined)
    {
        var b = Code(joined.Split('|'));
        Assert.Empty(b.Lines);
        Assert.Equal("", b.PlainText);
        Assert.Equal(0, b.MaxLength);
        Assert.Equal(0, b.CountLongerThan(0));
    }

    [Fact]
    public void Build_NoInput_NoLines()
        => Assert.Empty(CodeBlock.Build([], 4, true).Lines);

    [Fact]
    public void Build_DedentRemovesCommonIndentAndIgnoresBlankLines()
    {
        var b = CodeBlock.Build([Raw("    a"), Raw("      b"), Raw(""), Raw("   "), Raw("    c")], 4, true);
        Assert.Equal("a|  b|||c", Texts(b));
    }

    [Fact]
    public void Build_DedentUsesTheSmallestIndent()
    {
        var b = CodeBlock.Build([Raw("        deep"), Raw("  shallow"), Raw("    mid")], 4, true);
        Assert.Equal("      deep|shallow|  mid", Texts(b));
    }

    [Fact]
    public void Build_DedentFalseKeepsIndent()
    {
        var b = CodeBlock.Build([Raw("    a"), Raw("      b")], 4, false);
        Assert.Equal("    a|      b", Texts(b));
    }

    [Fact]
    public void Build_DedentCountsTabsAsExpandedSpaces()
    {
        var b = CodeBlock.Build([Raw("\ta"), Raw("\t\tb")], 4, true);
        Assert.Equal("a|    b", Texts(b));
    }

    [Fact]
    public void Build_DedentSpansIndentationSplitOverRuns()
    {
        var b = CodeBlock.Build([new RawCodeLine([new CodeRun("  ", Colors.Red), new CodeRun("x", Colors.Blue)]), Raw("    y")], 4, true);
        Assert.Equal("x|  y", Texts(b));
        Assert.Equal(Colors.Blue, b.Lines[0][0].Color);
    }

    [Fact]
    public void Build_PartialLineNeitherCountsNorGetsDedented()
    {
        // a selection that starts mid-line: its "indent" is just the tail of the first line
        var b = CodeBlock.Build([Raw("  tail", partial: true), Raw("    a"), Raw("      b")], 4, true);
        Assert.Equal("  tail|a|  b", Texts(b));
    }

    [Fact]
    public void Build_OnlyPartialLines_NothingDedented()
    {
        var b = CodeBlock.Build([Raw("    a", partial: true)], 4, true);
        Assert.Equal("    a", b.LineText(0));
    }

    [Fact]
    public void Build_ControlCharsBecomeSpaces_CrLfDropped()
    {
        var b = CodeBlock.Build([Raw("a\u0001b\u0000c\r\nd")], 4, false);
        Assert.Equal("a b cd", b.LineText(0));
    }

    [Fact]
    public void Build_ControlCharAdvancesTheColumnForTabStops()
        => Assert.Equal("a   b", CodeBlock.Build([Raw("a\u0001\tb")], 4, false).LineText(0));

    // ---------------------------------------------------------------- MaxLength / CountLongerThan / PlainText

    [Fact]
    public void MaxLength_IsTheLongestLine()
        => Assert.Equal(5, Code("ab", "abcde", "abc").MaxLength);

    [Fact]
    public void MaxLength_UsesExpandedTabs()
        => Assert.Equal(5, Code("\tx", "ab").MaxLength);

    [Fact]
    public void CountLongerThan_StrictlyLonger()
    {
        var b = Code("ab", "abcde", "abcd", "abc");
        Assert.Equal(2, b.CountLongerThan(3));
        Assert.Equal(0, b.CountLongerThan(5));
        Assert.Equal(4, b.CountLongerThan(1));
    }

    [Fact]
    public void PlainText_IsCrLfJoinedWithoutTrailingBreak()
    {
        var b = Code("a", "", "  b");
        Assert.Equal("a\r\n\r\n  b", b.PlainText);
    }

    // ---------------------------------------------------------------- HTML: caption, structure

    [Fact]
    public void Html_CaptionIsEscaped()
    {
        var html = RichCodeWriter.Html(Code("x"), new RichCodeStyle { Caption = "Listing 3.1. <Opis> & \"cytat\"" });
        Assert.Contains("Listing 3.1. &lt;Opis&gt; &amp; &quot;cytat&quot;", html);
        Assert.DoesNotContain("<Opis>", html);
    }

    [Fact]
    public void Html_CaptionAbove_PrecedesCode_AndKeepsWithNext()
    {
        var html = RichCodeWriter.Html(Code("CODEX"), new RichCodeStyle { Caption = "CAPTX" });
        Assert.True(html.IndexOf("CAPTX", StringComparison.Ordinal) < html.IndexOf("CODEX", StringComparison.Ordinal));
        var paras = Paragraphs(html);
        Assert.Equal(2, paras.Count);
        Assert.Contains("page-break-after:avoid", paras[0].Style);
        Assert.DoesNotContain("page-break-after", paras[1].Style);
    }

    [Fact]
    public void Html_CaptionBelow_FollowsCode_AndCodeKeepsWithIt()
    {
        var html = RichCodeWriter.Html(Code("CODEX"), new RichCodeStyle { Caption = "CAPTX", CaptionPlacement = CaptionPlacement.Below });
        Assert.True(html.IndexOf("CAPTX", StringComparison.Ordinal) > html.IndexOf("CODEX", StringComparison.Ordinal));
        var paras = Paragraphs(html);
        Assert.Equal(2, paras.Count);
        Assert.Contains("page-break-after:avoid", paras[0].Style); // last code line stays with the caption below
        Assert.DoesNotContain("page-break-after", paras[1].Style);
    }

    [Fact]
    public void Html_CaptionParagraph_IsArialCenteredWithSixPointSpacing()
    {
        var html = RichCodeWriter.Html(Code("x"), new RichCodeStyle { Caption = "Listing 3.1. Opis" });
        var (style, inner) = Paragraphs(html)[0];
        Assert.Contains("text-align:center", style);
        Assert.Contains("margin-top:6pt;margin-bottom:6pt", style);
        Assert.Contains("page-break-after:avoid", style);
        Assert.Contains("font-family:Arial", inner);
        Assert.Contains("font-size:10pt", inner);
        Assert.Equal("Listing 3.1. Opis", StripTags(inner));
    }

    [Fact]
    public void Html_CaptionIsTrimmed()
    {
        var html = RichCodeWriter.Html(Code("x"), new RichCodeStyle { Caption = "  Listing 1  " });
        Assert.Equal("Listing 1", StripTags(Paragraphs(html)[0].Inner));
    }

    [Fact]
    public void Html_CodeParagraph_HasZeroIndentsSingleSpacingAndFont()
    {
        var html = RichCodeWriter.Html(Code("CODEX"), new RichCodeStyle { Caption = "C" });
        var (style, inner) = Paragraphs(html).First(p => p.Inner.Contains("CODEX"));
        Assert.Contains("text-indent:0pt", style);
        Assert.Contains("margin-left:0pt", style);
        Assert.Contains("margin-right:0pt", style);
        Assert.Contains("line-height:100%", style);
        Assert.Contains("text-align:left", style);
        Assert.Contains("margin-top:0pt;margin-bottom:6pt", style); // caption above supplies the top space
        Assert.Contains("font-family:Consolas", inner);
        Assert.Contains("font-size:10pt", inner);
    }

    [Fact]
    public void Html_BlockSpacing_OnlyWhenNothingElseSurroundsTheCode()
    {
        var paras = Paragraphs(RichCodeWriter.Html(Code("a", "b", "c"), new RichCodeStyle()));
        Assert.Contains("margin-top:6pt;margin-bottom:0pt", paras[0].Style);
        Assert.Contains("margin-top:0pt;margin-bottom:0pt", paras[1].Style);
        Assert.Contains("margin-top:0pt;margin-bottom:6pt", paras[2].Style);
    }

    [Fact]
    public void Html_OneParagraphPerLine()
    {
        var paras = Paragraphs(RichCodeWriter.Html(Code("a", "b", "c"), new RichCodeStyle()));
        Assert.Equal(3, paras.Count);
    }

    [Fact]
    public void Html_FontWithSpaceIsQuoted_PlainOneIsNot()
    {
        var courier = RichCodeWriter.Html(Code("x"), new RichCodeStyle { FontFamily = "Courier New" });
        Assert.Contains("font-family:'Courier New'", courier);
        var consolas = RichCodeWriter.Html(Code("x"), new RichCodeStyle());
        Assert.Contains("font-family:Consolas,'Courier New',monospace;", consolas);
        Assert.DoesNotContain("'Consolas'", consolas);
        Assert.Contains("font-family:'Courier New',monospace;", courier); // no duplicate fallback
    }

    [Fact]
    public void LineSpacing_AppliesToCodeOnly()
    {
        var style = new RichCodeStyle { LineSpacing = 1.5, Caption = "Listing 1.1" };
        var html = RichCodeWriter.Html(Code("a", "b"), style);
        Assert.Equal(2, html.Split("line-height:150%").Length - 1); // two code lines
        Assert.Contains("line-height:100%;text-align:center", html); // caption stays single
        var rtf = RichCodeWriter.Rtf(Code("a", "b"), style);
        Assert.Equal(2, rtf.Split(@"\sl360\slmult1").Length - 1);
        Assert.Contains(@"\qc\li0\ri0\fi0\sb120\sa120\sl240\slmult1", rtf);
    }

    [Fact]
    public void Html_FontSizeAppearsInPoints()
    {
        var html = RichCodeWriter.Html(Code("x"), new RichCodeStyle { FontSize = 9.5 });
        Assert.Contains("font-size:9.5pt", html);
    }

    [Fact]
    public void Html_SourceLine_FollowsCode_CenteredWithCaptionFont()
    {
        var html = RichCodeWriter.Html(Code("CODEX"), new RichCodeStyle { Caption = "CAPTX", Source = "Źródło: opracowanie własne" });
        Assert.True(html.IndexOf("CODEX", StringComparison.Ordinal) < html.IndexOf("Źródło: opracowanie własne", StringComparison.Ordinal));
        var paras = Paragraphs(html);
        Assert.Equal(3, paras.Count);
        Assert.Contains("text-align:center", paras[2].Style);
        Assert.Contains("font-family:Arial", paras[2].Inner);
        Assert.DoesNotContain("page-break-after", paras[2].Style);
    }

    [Fact]
    public void Html_WhitespaceOnlyCaptionAndSource_ProduceNoParagraph()
    {
        var html = RichCodeWriter.Html(Code("a", "b"), new RichCodeStyle { Caption = "   ", Source = " \t " });
        Assert.Equal(2, Paragraphs(html).Count);
        Assert.DoesNotContain("Arial", html);
        Assert.DoesNotContain("text-align:center", html);
    }

    [Fact]
    public void Html_NoCaptionAtAll_NoCenteredParagraph()
    {
        var html = RichCodeWriter.Html(Code("a"), new RichCodeStyle());
        Assert.DoesNotContain("Arial", html);
        Assert.DoesNotContain("page-break-after", html);
    }

    // ---------------------------------------------------------------- HTML: spaces and runs

    [Theory]
    [InlineData("    x", "&nbsp;&nbsp;&nbsp; x")]    // leading spaces: all but the last one are no-break
    [InlineData(" x", "&nbsp;x")]                     // a single leading space is always no-break
    [InlineData("a b", "a b")]                        // a single inner space stays a normal space
    [InlineData("a  b", "a&nbsp; b")]                 // double inner space
    [InlineData("a   b", "a&nbsp;&nbsp; b")]
    [InlineData("  a  b c", "&nbsp; a&nbsp; b c")]
    public void Html_SpaceHandling(string line, string expected)
    {
        var inner = Paragraphs(RichCodeWriter.Html(Code(line), new RichCodeStyle()))[0].Inner;
        var text = Regex.Match(inner, ">([^<]*)</span>$").Groups[1].Value;
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Html_SpaceAtRunBoundaryBeforeText_StaysNormal()
    {
        var html = RichCodeWriter.Html(CodeOf([new CodeRun("int ", Colors.Blue), new CodeRun("x")]), new RichCodeStyle());
        Assert.Contains(">int </span>", html);
    }

    [Fact]
    public void Html_EmptyLineInTheMiddle_IsAParagraphWithNbsp()
    {
        var paras = Paragraphs(RichCodeWriter.Html(Code("a", "", "b"), new RichCodeStyle()));
        Assert.Equal(3, paras.Count);
        Assert.Equal("&nbsp;", StripTags(paras[1].Inner));
        Assert.Contains("font-family:Consolas", paras[1].Inner);
        Assert.Contains("font-size:10pt", paras[1].Inner);
    }

    [Fact]
    public void Html_BoldItalicAndColorRuns()
    {
        var block = CodeOf([new CodeRun("kw", Color.FromRgb(0x12, 0xAB, 0xEF), Bold: true), new CodeRun("it", null, Italic: true), new CodeRun("pl")]);
        var inner = Paragraphs(RichCodeWriter.Html(block, new RichCodeStyle { Text = Color.FromRgb(0x22, 0x33, 0x44) }))[0].Inner;
        var spans = Regex.Matches(inner, "<span style=\"([^\"]*)\">([^<]*)</span>").Select(m => (Style: m.Groups[1].Value, Text: m.Groups[2].Value)).ToList();
        Assert.Equal(3, spans.Count);
        Assert.Equal("kw", spans[0].Text);
        Assert.Contains("color:#12ABEF", spans[0].Style);
        Assert.Contains("font-weight:bold", spans[0].Style);
        Assert.Contains("font-style:normal", spans[0].Style);
        Assert.Contains("font-style:italic", spans[1].Style);
        Assert.Contains("font-weight:normal", spans[1].Style);
        Assert.Contains("color:#223344", spans[1].Style); // no run color = the style's text color
        Assert.Contains("font-weight:normal;font-style:normal", spans[2].Style);
    }

    [Fact]
    public void Html_CodeCharactersAreEscaped()
    {
        var html = RichCodeWriter.Html(Code("if (a < b && c > \"d\")"), new RichCodeStyle());
        Assert.Contains("a &lt; b &amp;&amp; c &gt; &quot;d&quot;", html.Replace("&nbsp;", " "));
        Assert.DoesNotContain("<b", html.Replace("<br", ""));
    }

    // ---------------------------------------------------------------- HTML: table mode

    [Fact]
    public void Html_NoBox_NoTable()
        => Assert.DoesNotContain("<table", RichCodeWriter.Html(Code("a", "b"), new RichCodeStyle()));

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Html_AnyBoxOption_SwitchesToTable(bool numbers, bool background, bool border)
    {
        var s = new RichCodeStyle
        {
            LineNumbers = numbers,
            Background = background ? Colors.WhiteSmoke : null,
            Border = border ? Colors.Gray : null,
        };
        Assert.True(s.Boxed);
        var html = RichCodeWriter.Html(Code("a", "b", "c"), s);
        Assert.Contains("<table", html);
        Assert.Equal(3, Count(html, "<tr>"));
    }

    [Fact]
    public void Html_LineNumbers_OneRowPerLine_RightAlignedNumbers()
    {
        var lines = Enumerable.Range(1, 12).Select(i => "line" + i).ToArray();
        var html = RichCodeWriter.Html(Code(lines), new RichCodeStyle { LineNumbers = true });
        var rows = ParseHtml(html).Descendants("tr").ToList();
        Assert.Equal(12, rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var cells = rows[i].Elements("td").ToList();
            Assert.Equal(2, cells.Count);
            var num = cells[0].Element("p")!;
            Assert.Equal((i + 1).ToString(), num.Value);
            Assert.Contains("text-align:right", (string)num.Attribute("style")!);
            Assert.Contains("text-indent:0pt", (string)num.Attribute("style")!);
            var code = cells[1].Element("p")!;
            Assert.Equal("line" + (i + 1), code.Value);
            Assert.Contains("text-align:left", (string)code.Attribute("style")!);
            Assert.Contains("line-height:100%", (string)code.Attribute("style")!);
        }
    }

    [Fact]
    public void Html_NumberColumnGrowsWithDigitCount()
    {
        int Width(int lines)
        {
            var html = RichCodeWriter.Html(Code(Enumerable.Repeat("x", lines).ToArray()), new RichCodeStyle { LineNumbers = true });
            return int.Parse(Regex.Match(html, "<td style=\"width:(\\d+)pt").Groups[1].Value);
        }
        Assert.Equal(Width(5), Width(50)); // at least two digits are reserved
        Assert.True(Width(500) > Width(50));
    }

    [Fact]
    public void Html_BackgroundAndBorder_AppearOnCells()
    {
        var html = RichCodeWriter.Html(Code("a", "b"), new RichCodeStyle { Background = Color.FromRgb(0xF5, 0xF5, 0xF5), Border = Color.FromRgb(0xCC, 0xCC, 0xCC), BorderWidth = 0.5 });
        Assert.Contains("background-color:#F5F5F5", html);
        foreach (var side in new[] { "top", "bottom", "left", "right" })
        {
            Assert.Contains($"border-{side}:solid #CCCCCC 0.75pt", html);
            Assert.Contains($"mso-border-{side}-alt:solid #CCCCCC 0.5pt", html);
        }
    }

    [Fact]
    public void Html_ThinBorder_IsOnePixelForDocsAndExactForWord()
    {
        var thin = RichCodeWriter.Html(Code("a"), new RichCodeStyle { Border = Colors.Black, BorderWidth = 0.5 });
        Assert.DoesNotContain("0.5pt solid", thin);
        Assert.Contains("border-top:solid #000000 0.75pt;mso-border-top-alt:solid #000000 0.5pt", thin);

        var thick = RichCodeWriter.Html(Code("a"), new RichCodeStyle { Border = Colors.Black, BorderWidth = 1 });
        Assert.Contains("border-top:solid #000000 1pt", thick);
        Assert.DoesNotContain("mso-border", thick);
    }

    [Fact]
    public void Html_Table_HasNoBorderAttribute()
    {
        var html = RichCodeWriter.Html(Code("a"), new RichCodeStyle { Border = Colors.Black });
        var table = ParseHtml(html).Descendants("table").Single();
        Assert.Null(table.Attribute("border"));
    }

    [Fact]
    public void Html_BorderOutlinesTheBlockNotEveryRow()
    {
        var rows = ParseHtml(RichCodeWriter.Html(Code("a", "b", "c"), new RichCodeStyle { Border = Colors.Gray })).Descendants("td").Select(td => (string)td.Attribute("style")!).ToList();
        Assert.Contains("border-top:solid", rows[0]);
        Assert.Contains("border-left:solid", rows[1]);
        Assert.Contains("border-right:solid", rows[1]);
        Assert.Contains("border-top:none", rows[1]);
        Assert.Contains("border-bottom:none", rows[1]);
        Assert.Contains("border-bottom:solid", rows[2]);
    }

    [Fact]
    public void Html_Gutter_SeparatesNumbersFromCode()
    {
        var html = RichCodeWriter.Html(Code("a"), new RichCodeStyle { LineNumbers = true, Gutter = Color.FromRgb(0xAA, 0xBB, 0xCC) });
        Assert.Contains("border-right:solid #AABBCC 0.75pt", html);
    }

    [Fact]
    public void Html_BoxedEmptyBlock_StillHasOneRow()
    {
        var html = RichCodeWriter.Html(Code(), new RichCodeStyle { LineNumbers = true });
        Assert.Equal(1, Count(html, "<tr>"));
    }

    [Fact]
    public void Html_Table_CaptionAboveAndSourceAround()
    {
        var html = RichCodeWriter.Html(Code("CODEX"), new RichCodeStyle { LineNumbers = true, Caption = "CAPTX", Source = "SRCX" });
        var iCap = html.IndexOf("CAPTX", StringComparison.Ordinal);
        var iTable = html.IndexOf("<table", StringComparison.Ordinal);
        var iCode = html.IndexOf("CODEX", StringComparison.Ordinal);
        var iSrc = html.IndexOf("SRCX", StringComparison.Ordinal);
        Assert.True(iCap < iTable && iTable < iCode && iCode < iSrc);
    }

    // ---------------------------------------------------------------- HTML: well-formedness

    public static TheoryData<string> StyleNames => new() { "plain", "caption-above", "caption-below+source", "numbers", "full-box", "courier" };

    private static RichCodeStyle NamedStyle(string name) => name switch
    {
        "plain" => new RichCodeStyle(),
        "caption-above" => new RichCodeStyle { Caption = "Listing 3.1. <Opis> & \"tekst\"" },
        "caption-below+source" => new RichCodeStyle { Caption = "Listing 3.2. Źródło", CaptionPlacement = CaptionPlacement.Below, Source = "Źródło: opracowanie własne" },
        "numbers" => new RichCodeStyle { LineNumbers = true },
        "full-box" => new RichCodeStyle
        {
            LineNumbers = true, Background = Colors.WhiteSmoke, Border = Colors.Gray, Gutter = Colors.Silver,
            Caption = "Cap", CaptionPlacement = CaptionPlacement.Below, Source = "Src",
        },
        _ => new RichCodeStyle { FontFamily = "Courier New", Background = Colors.WhiteSmoke },
    };

    private static CodeBlock TrickyCode() => CodeOf(
        [new CodeRun("if (a < b && c > \"d\") {", Colors.Blue, Bold: true)],
        [new CodeRun("    "), new CodeRun("zażółć → gęślą", Colors.Green, Italic: true), new CodeRun("  jaźń")],
        [],
        [new CodeRun("\\ } ' ; &amp; &nbsp;")]);

    [Theory]
    [MemberData(nameof(StyleNames))]
    public void Html_IsWellFormedXml(string name)
    {
        var html = RichCodeWriter.Html(TrickyCode(), NamedStyle(name));
        var root = ParseHtml(html); // throws when a tag is unbalanced or an entity is invalid
        Assert.Contains("zażółć → gęślą", root.Value);
        Assert.Contains("if (a < b && c > \"d\") {", root.Value.Replace('\u00A0', ' '));
        Assert.Contains("&amp; &nbsp;", root.Value.Replace('\u00A0', ' ')); // literal entity text in code survives as text
    }

    // ---------------------------------------------------------------- Escape

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("<a>", "&lt;a&gt;")]
    [InlineData("a & b", "a &amp; b")]
    [InlineData("\"q\"", "&quot;q&quot;")]
    [InlineData("&lt;", "&amp;lt;")]
    [InlineData("zażółć", "zażółć")]
    [InlineData("", "")]
    public void Escape_HtmlSpecials(string input, string expected)
        => Assert.Equal(expected, RichCodeWriter.Escape(input));

    // ---------------------------------------------------------------- CF_HTML

    private static (int StartHtml, int EndHtml, int StartFragment, int EndFragment) ParseHeader(byte[] bytes)
    {
        var head = Encoding.ASCII.GetString(bytes, 0, 120);
        int Get(string key) => int.Parse(Regex.Match(head, key + ":(\\d{10})").Groups[1].Value);
        return (Get("StartHTML"), Get("EndHTML"), Get("StartFragment"), Get("EndFragment"));
    }

    [Theory]
    [InlineData("<p>abc</p>")]
    [InlineData("<p>zażółć gęślą jaźń → ∑ 😀</p>")]
    [InlineData("")]
    public void ClipboardHtml_OffsetsAreUtf8ByteOffsets(string fragment)
    {
        var bytes = RichCodeWriter.ClipboardHtml(fragment);
        var (startHtml, endHtml, startFragment, endFragment) = ParseHeader(bytes);

        Assert.Equal((byte)'V', bytes[0]); // no BOM
        Assert.StartsWith("Version:0.9\r\n", Encoding.UTF8.GetString(bytes));
        Assert.Equal(bytes.Length, endHtml);
        Assert.Equal("<html>", Encoding.UTF8.GetString(bytes, startHtml, 6));
        Assert.Equal(fragment, Encoding.UTF8.GetString(bytes, startFragment, endFragment - startFragment));
        Assert.Equal(Encoding.UTF8.GetByteCount(fragment), endFragment - startFragment);
        Assert.EndsWith("<!--StartFragment-->", Encoding.UTF8.GetString(bytes, startHtml, startFragment - startHtml));
        Assert.StartsWith("<!--EndFragment-->", Encoding.UTF8.GetString(bytes, endFragment, bytes.Length - endFragment));
        Assert.EndsWith("</html>", Encoding.UTF8.GetString(bytes, startHtml, endHtml - startHtml));
    }

    [Fact]
    public void ClipboardHtml_MultiByteFragment_ByteOffsetsDifferFromCharOffsets()
    {
        const string fragment = "<p>zażółć →</p>";
        var bytes = RichCodeWriter.ClipboardHtml(fragment);
        var (_, endHtml, startFragment, endFragment) = ParseHeader(bytes);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.True(endHtml > text.Length); // an offset counted in chars would point short of the end
        Assert.True(endFragment - startFragment > fragment.Length);
        Assert.Equal(fragment, text.Substring(text.IndexOf("<!--StartFragment-->", StringComparison.Ordinal) + "<!--StartFragment-->".Length, fragment.Length));
    }

    [Fact]
    public void ClipboardHtml_DeclaresUtf8()
        => Assert.Contains("<meta charset=\"utf-8\">", Encoding.UTF8.GetString(RichCodeWriter.ClipboardHtml("x")));

    [Fact]
    public void ClipboardHtml_WrapsRealWriterOutput()
    {
        var fragment = RichCodeWriter.Html(Code("zażółć"), new RichCodeStyle { Caption = "Źródło" });
        var bytes = RichCodeWriter.ClipboardHtml(fragment);
        var (_, _, s, e) = ParseHeader(bytes);
        Assert.Equal(fragment, Encoding.UTF8.GetString(bytes, s, e - s));
    }

    // ---------------------------------------------------------------- RTF: text level

    [Theory]
    [MemberData(nameof(StyleNames))]
    public void Rtf_StartsWithHeader_BracesBalanced_PureAscii(string name)
    {
        var rtf = RichCodeWriter.Rtf(TrickyCode(), NamedStyle(name));
        Assert.StartsWith(@"{\rtf1", rtf);
        Assert.EndsWith("}", rtf);
        Assert.True(BracesBalanced(rtf));
        Assert.All(rtf, ch => Assert.True(ch < 0x80, $"non-ASCII char U+{(int)ch:X4}"));
    }

    [Fact]
    public void Rtf_FontTableHasCodeAndCaptionFonts()
    {
        var rtf = RichCodeWriter.Rtf(Code("x"), new RichCodeStyle());
        var table = Regex.Match(rtf, @"\{\\fonttbl.*?\}\}", RegexOptions.Singleline).Value;
        Assert.Matches(@"\\f0[^;]*Consolas;", table);
        Assert.Matches(@"\\f1[^;]*Arial;", table);

        var courier = RichCodeWriter.Rtf(Code("x"), new RichCodeStyle { FontFamily = "Courier New" });
        Assert.Contains("Courier New;", courier);
    }

    [Fact]
    public void Rtf_PolishLettersAndArrowAreUnicodeEscaped()
    {
        var rtf = RichCodeWriter.Rtf(Code("ąęłżźćńóś → ĄŁ"), new RichCodeStyle());
        Assert.Contains(@"\u261?", rtf);   // ą
        Assert.Contains(@"\u322?", rtf);   // ł
        Assert.Contains(@"\u380?", rtf);   // ż
        Assert.Contains(@"\u243?", rtf);   // ó
        Assert.Contains(@"\u8594?", rtf);  // →
        Assert.Contains(@"\u260?", rtf);   // Ą
        Assert.DoesNotContain("ą", rtf);
    }

    [Theory]
    [InlineData("ął", @"\u261?\u322?")]
    [InlineData("a\\b", @"a\\b")]
    [InlineData("{x}", @"\{x\}")]
    [InlineData("a\tb", @"a\tab b")]
    [InlineData("a\u0001b\rc", "abc")]
    [InlineData("é", @"\u233?")]
    [InlineData("\uFFFD", @"\u-3?")]            // code points above 0x7FFF are written as negative 16-bit values
    [InlineData("😀", @"\u-10179?\u-8704?")]   // surrogate halves one by one
    public void RtfEscape_Cases(string input, string expected)
    {
        var sb = new StringBuilder();
        RichCodeWriter.RtfEscape(sb, input);
        Assert.Equal(expected, sb.ToString());
    }

    [Fact]
    public void Rtf_BackslashAndBracesInCodeAreEscaped()
    {
        var rtf = RichCodeWriter.Rtf(Code(@"path = C:\dir\{x}"), new RichCodeStyle());
        Assert.Contains(@"path = C:\\dir\\\{x\}", rtf);
        Assert.True(BracesBalanced(rtf));
    }

    [Fact]
    public void Rtf_CaptionParagraph_CenteredSpacedKeptWithNext()
    {
        var rtf = RichCodeWriter.Rtf(Code("CODEX"), new RichCodeStyle { Caption = "Listing 3.1. Opis" });
        var line = RtfLineWith(rtf, "Listing 3.1. Opis");
        Assert.Contains(@"\qc", line);
        Assert.Contains(@"\sb120\sa120", line);
        Assert.Contains(@"\keepn", line);
        Assert.Contains(@"\f1\fs20", line);
        Assert.Contains(@"\li0", line);
        Assert.Contains(@"\fi0", line);
        Assert.Contains(@"\sl240\slmult1", line);
        Assert.EndsWith(@"\par", line);
        Assert.True(rtf.IndexOf("Listing 3.1.", StringComparison.Ordinal) < rtf.IndexOf("CODEX", StringComparison.Ordinal));
    }

    [Fact]
    public void Rtf_CaptionBelow_DoesNotKeepWithNext_ButLastCodeLineDoes()
    {
        var rtf = RichCodeWriter.Rtf(Code("CODE1", "CODE2"), new RichCodeStyle { Caption = "CAPTX", CaptionPlacement = CaptionPlacement.Below });
        Assert.True(rtf.IndexOf("CAPTX", StringComparison.Ordinal) > rtf.IndexOf("CODE2", StringComparison.Ordinal));
        Assert.DoesNotContain(@"\keepn", RtfLineWith(rtf, "CAPTX"));
        Assert.DoesNotContain(@"\keepn", RtfLineWith(rtf, "CODE1"));
        Assert.Contains(@"\keepn", RtfLineWith(rtf, "CODE2"));
    }

    [Fact]
    public void Rtf_CodeParagraph_ZeroIndentsSingleSpacingConsolas10()
    {
        var rtf = RichCodeWriter.Rtf(Code("CODEX"), new RichCodeStyle { Caption = "C" });
        var line = RtfLineWith(rtf, "CODEX");
        Assert.Contains(@"\ql", line);
        Assert.Contains(@"\li0", line);
        Assert.Contains(@"\ri0", line);
        Assert.Contains(@"\fi0", line);
        Assert.Contains(@"\sl240\slmult1", line);
        Assert.Contains(@"\f0\fs20", line);
        Assert.Contains(@"\sb0", line); // the caption above supplies the space
        Assert.Contains(@"\sa120", line);
        Assert.EndsWith(@"\par", line);
    }

    [Theory]
    [InlineData(10, "fs20")]
    [InlineData(11, "fs22")]
    [InlineData(10.5, "fs21")]
    public void Rtf_FontSizeIsInHalfPoints(double pt, string expected)
    {
        var rtf = RichCodeWriter.Rtf(Code("CODEX"), new RichCodeStyle { FontSize = pt });
        Assert.Contains(@"\" + expected, RtfLineWith(rtf, "CODEX"));
    }

    [Fact]
    public void Rtf_NoCaption_BlockSpacingOnFirstAndLastParagraph()
    {
        var rtf = RichCodeWriter.Rtf(Code("A1", "B2", "C3"), new RichCodeStyle());
        Assert.Contains(@"\sb120\sa0", RtfLineWith(rtf, "A1"));
        Assert.Contains(@"\sb0\sa0", RtfLineWith(rtf, "B2"));
        Assert.Contains(@"\sb0\sa120", RtfLineWith(rtf, "C3"));
        Assert.DoesNotContain(@"\keepn", rtf);
    }

    [Fact]
    public void Rtf_WhitespaceOnlyCaption_NoCaptionParagraph()
    {
        var rtf = RichCodeWriter.Rtf(Code("a"), new RichCodeStyle { Caption = "  " });
        Assert.DoesNotContain(@"\qc", rtf);
        Assert.DoesNotContain(@"\f1\fs", rtf.Substring(rtf.IndexOf(@"\pard", StringComparison.Ordinal)));
    }

    [Fact]
    public void Rtf_SourceLine_AfterCode_Centered()
    {
        var rtf = RichCodeWriter.Rtf(Code("CODEX"), new RichCodeStyle { Source = "SRCX" });
        Assert.True(rtf.IndexOf("SRCX", StringComparison.Ordinal) > rtf.IndexOf("CODEX", StringComparison.Ordinal));
        var line = RtfLineWith(rtf, "SRCX");
        Assert.Contains(@"\qc", line);
        Assert.Contains(@"\f1\fs20", line);
        Assert.DoesNotContain(@"\keepn", line);
    }

    [Fact]
    public void Rtf_EmptyLineIsAnEmptyParagraphWithCodeFont()
    {
        var rtf = RichCodeWriter.Rtf(Code("a", "", "b"), new RichCodeStyle());
        var paragraphs = RtfLines(rtf).Where(l => l.StartsWith(@"\pard")).ToList();
        Assert.Equal(3, paragraphs.Count);
        Assert.Contains(@"\f0\fs20", paragraphs[1]);
        Assert.EndsWith(@"\par", paragraphs[1]);
        Assert.DoesNotContain("{", paragraphs[1]);
    }

    [Fact]
    public void Rtf_ColorTableAndRunFormatting()
    {
        var block = CodeOf([new CodeRun("kw", Color.FromRgb(255, 0, 0), Bold: true), new CodeRun("it", Color.FromRgb(0, 128, 0), Italic: true), new CodeRun("pl")]);
        var rtf = RichCodeWriter.Rtf(block, new RichCodeStyle());
        var entries = Regex.Matches(Regex.Match(rtf, @"\{\\colortbl;(.*?)\}", RegexOptions.Singleline).Groups[1].Value, @"\\red(\d+)\\green(\d+)\\blue(\d+);")
            .Select(m => (R: int.Parse(m.Groups[1].Value), G: int.Parse(m.Groups[2].Value), B: int.Parse(m.Groups[3].Value))).ToList();
        int Index(int r, int g, int b) => entries.IndexOf((r, g, b)) + 1; // 0 = auto
        Assert.True(Index(255, 0, 0) > 0);
        Assert.True(Index(0, 128, 0) > 0);
        Assert.True(Index(0, 0, 0) > 0);
        Assert.Equal(entries.Count, entries.Distinct().Count());
        Assert.Contains($@"{{\cf{Index(255, 0, 0)}\b kw}}", rtf);
        Assert.Contains($@"{{\cf{Index(0, 128, 0)}\i it}}", rtf);
        Assert.Contains($@"{{\cf{Index(0, 0, 0)} pl}}", rtf);
    }

    [Fact]
    public void Rtf_NoBox_NoTable()
    {
        var rtf = RichCodeWriter.Rtf(Code("a", "b"), new RichCodeStyle { Caption = "C" });
        Assert.DoesNotContain(@"\trowd", rtf);
        Assert.DoesNotContain(@"\intbl", rtf);
    }

    [Fact]
    public void Rtf_LineNumbers_OneRowPerLineWithTwoCells()
    {
        var rtf = RichCodeWriter.Rtf(Code("a1", "b2", "c3", "d4"), new RichCodeStyle { LineNumbers = true });
        Assert.Equal(4, Count(rtf, @"\trowd"));
        Assert.Equal(4, Count(rtf, @"\cell\row"));
        Assert.Equal(8, Regex.Matches(rtf, @"\\cell(?![a-z])").Count);
        var row = RtfLineWith(rtf, "b2");
        Assert.Contains(@"\fi0", row);
        Assert.Contains(@"\sl240\slmult1", row);
        var numberCell = RtfLines(rtf).First(l => l.EndsWith(@" 2\cell"));
        Assert.Contains(@"\qr", numberCell);
    }

    [Fact]
    public void Rtf_BackgroundAndBorder_UseColorTableEntries()
    {
        var rtf = RichCodeWriter.Rtf(Code("a", "b"), new RichCodeStyle { Background = Color.FromRgb(0xF5, 0xF5, 0xF5), Border = Color.FromRgb(0xCC, 0xCC, 0xCC) });
        Assert.Contains(@"\red245\green245\blue245;", rtf);
        Assert.Contains(@"\red204\green204\blue204;", rtf);
        Assert.Equal(2, Count(rtf, @"\trowd"));
        Assert.Contains(@"\clbrdrt\brdrs", rtf);
        Assert.Contains(@"\clbrdrb\brdrs", rtf);
        Assert.Contains(@"\clcbpat", rtf);
    }

    [Fact]
    public void Rtf_TableWidthMatchesWidthCm()
    {
        var rtf = RichCodeWriter.Rtf(Code("a"), new RichCodeStyle { Background = Colors.WhiteSmoke, WidthCm = 10 });
        var total = (int)Math.Round(10 / 2.54 * 72 * 20);
        Assert.Contains(@"\cellx" + total, rtf);
    }

    // ---------------------------------------------------------------- RTF: loaded by WPF's RTF parser

    [Fact]
    public void Rtf_LoadsIntoFlowDocument_Listing()
    {
        var rtf = RichCodeWriter.Rtf(
            CodeOf([new CodeRun("int x = 0;", Colors.Blue, Bold: true)], [new CodeRun("    // zażółć gęślą jaźń → ok")], [new CodeRun(@"path = C:\dir\{x}")]),
            new RichCodeStyle { Caption = "Listing 3.1. Opis" });
        RunSta(() =>
        {
            var doc = LoadRtf(rtf);
            var text = DocText(doc);
            Assert.Contains("Listing 3.1. Opis", text);
            Assert.Contains("int x = 0;", text);
            Assert.Contains("// zażółć gęślą jaźń → ok", text);
            Assert.Contains(@"path = C:\dir\{x}", text);

            var paragraphs = doc.Blocks.OfType<WDoc.Paragraph>().ToList();
            var caption = paragraphs.First(p => ParaText(p).Contains("Listing 3.1."));
            Assert.Equal(System.Windows.TextAlignment.Center, caption.TextAlignment);
            Assert.Equal(0, caption.TextIndent);
            Assert.Contains("Arial", caption.Inlines.SelectMany(Flatten).First().FontFamily.Source);
            // \keepn is honoured by Word but ignored by WPF's RTF reader, so KeepWithNext is not asserted here

            var codeParagraphs = paragraphs.Where(p => p != caption && ParaText(p).Trim().Length > 0).ToList();
            Assert.Equal(3, codeParagraphs.Count);
            foreach (var p in codeParagraphs)
            {
                Assert.Equal(0, p.TextIndent);
                Assert.Equal(0, p.Margin.Left);
                var font = new WDoc.TextRange(p.ContentStart, p.ContentEnd).GetPropertyValue(WDoc.TextElement.FontFamilyProperty);
                Assert.Contains("Consolas", ((FontFamily)font).Source);
                Assert.Equal(System.Windows.TextAlignment.Left, p.TextAlignment);
            }
        });
    }

    [Fact]
    public void Rtf_LoadsIntoFlowDocument_LeadingSpacesSurvive()
    {
        var rtf = RichCodeWriter.Rtf(Code("    indented"), new RichCodeStyle());
        RunSta(() => Assert.Contains("    indented", DocText(LoadRtf(rtf))));
    }

    [Fact]
    public void Rtf_LoadsIntoFlowDocument_ColoredBoldRun()
    {
        var rtf = RichCodeWriter.Rtf(CodeOf([new CodeRun("kw", Color.FromRgb(255, 0, 0), Bold: true), new CodeRun(" rest")]), new RichCodeStyle());
        RunSta(() =>
        {
            var doc = LoadRtf(rtf);
            var run = doc.Blocks.OfType<WDoc.Paragraph>().SelectMany(p => p.Inlines.SelectMany(Flatten)).First(r => r.Text == "kw");
            Assert.Equal(System.Windows.FontWeights.Bold, run.FontWeight);
            Assert.Equal(Color.FromRgb(255, 0, 0), ((SolidColorBrush)run.Foreground).Color);
        });
    }

    private static IEnumerable<WDoc.Run> Flatten(WDoc.Inline inline)
    {
        if (inline is WDoc.Run r) yield return r;
        else if (inline is WDoc.Span span)
            foreach (var child in span.Inlines.SelectMany(Flatten)) yield return child;
    }

    [Fact]
    public void Rtf_LoadsIntoFlowDocument_LineNumbersTable()
    {
        var lines = new[] { "alpha", "zażółć", "gamma", "delta" };
        var rtf = RichCodeWriter.Rtf(Code(lines), new RichCodeStyle { LineNumbers = true, Background = Color.FromRgb(0xF5, 0xF5, 0xF5), Border = Color.FromRgb(0xCC, 0xCC, 0xCC), Caption = "Listing 1. Tabela" });
        RunSta(() =>
        {
            var doc = LoadRtf(rtf);
            var table = Assert.Single(doc.Blocks.OfType<WDoc.Table>());
            var rows = table.RowGroups.SelectMany(g => g.Rows).ToList();
            Assert.Equal(4, rows.Count);
            for (var i = 0; i < rows.Count; i++)
            {
                Assert.Equal(2, rows[i].Cells.Count);
                var number = new WDoc.TextRange(rows[i].Cells[0].ContentStart, rows[i].Cells[0].ContentEnd).Text.Trim();
                var code = new WDoc.TextRange(rows[i].Cells[1].ContentStart, rows[i].Cells[1].ContentEnd).Text.Trim();
                Assert.Equal((i + 1).ToString(), number);
                Assert.Equal(lines[i], code);
            }
            Assert.Contains("Listing 1. Tabela", DocText(doc));
        });
    }

    [Fact]
    public void Rtf_LoadsIntoFlowDocument_BorderOnlyTableHasOneCellPerRow()
    {
        var rtf = RichCodeWriter.Rtf(Code("one", "two", "three"), new RichCodeStyle { Border = Colors.Gray });
        RunSta(() =>
        {
            var doc = LoadRtf(rtf);
            var table = Assert.Single(doc.Blocks.OfType<WDoc.Table>());
            var rows = table.RowGroups.SelectMany(g => g.Rows).ToList();
            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.Single(r.Cells));
        });
    }
}
