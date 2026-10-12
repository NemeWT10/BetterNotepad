using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using Markdig.Syntax;
using WBlock = System.Windows.Documents.Block;

namespace BetterNotepad.Markdown;

internal sealed partial class MarkdownBuilder
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static readonly HashSet<string> BlockTags = new(StringComparer.Ordinal)
    {
        "p", "div", "center", "section", "article", "header", "footer", "main", "nav", "aside", "figure", "figcaption",
        "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "details", "summary", "blockquote", "pre", "hr",
        "table", "thead", "tbody", "tfoot", "tr", "dl", "dt", "dd", "address", "body", "html", "head",
    };

    /// <summary>
    /// Raw HTML block (README style): comments vanish, paragraphs / alignment / headings / images / links / simple
    /// inline tags are rendered; anything heavier is flagged as <see cref="RichContent.Html"/>.
    /// </summary>
    private WBlock? HtmlOf(HtmlBlock hb)
    {
        var raw = hb.Lines.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var section = new Section { Margin = new Thickness(0, 0, 0, _gap), Tag = Rel(hb) };
        Paragraph? para = null;
        var target = (InlineCollection?)null;
        List<(string Tag, InlineCollection Parent)>? stack = null;
        var alignStack = new Stack<(string Tag, TextAlignment Align)>();
        var align = TextAlignment.Left;
        var headingLevel = 0;
        var skipUntil = (string?)null;
        var any = false;

        void EndPara()
        {
            para = null;
            target = null;
            stack = null;
        }

        InlineCollection Ensure()
        {
            if (para is null)
            {
                para = new Paragraph { Margin = new Thickness(0, 0, 0, 8), TextAlignment = align, Tag = Rel(hb) };
                if (headingLevel > 0)
                {
                    StyleHeading(para, headingLevel);
                    para.Margin = new Thickness(0, 2, 0, 8);
                }

                section.Blocks.Add(para);
                target = para.Inlines;
                stack = null;
            }

            return target!;
        }

        foreach (var (text, tag) in HtmlTag.Tokenize(raw))
        {
            if (skipUntil is not null)
            {
                if (tag is { Closing: true } && tag.Name == skipUntil) skipUntil = null;
                continue;
            }

            if (tag is null)
            {
                var t = System.Net.WebUtility.HtmlDecode(Whitespace.Replace(text!, " "));
                if (string.IsNullOrWhiteSpace(t) && para is null) continue;
                if (para is null) t = t.TrimStart();
                var into = Ensure();
                into.Add(new Run(t));
                any = true;
                continue;
            }

            var name = tag.Name;
            if (name is "script" or "style")
            {
                Rich |= RichContent.Html;
                if (!tag.Closing && !tag.SelfClosing) skipUntil = name;
                continue;
            }

            if (name is "td" or "th")
            {
                Rich |= RichContent.Html;
                if (!tag.Closing && para is not null) Ensure().Add(new Run("   2502   "));
                continue;
            }

            if (BlockTags.Contains(name))
            {
                EndPara();
                if (name is "table" or "tr" or "td" or "th" or "thead" or "tbody" or "tfoot" or "pre" or "dl") Rich |= RichContent.Html;

                if (tag.Closing)
                {
                    if (name is "p" or "div" or "center" or "section" or "article" or "header" or "footer" or "main" or "figure" or "td" or "th")
                    {
                        if (alignStack.Count > 0 && alignStack.Peek().Tag == name) align = alignStack.Pop().Align;
                    }

                    if (name.Length == 2 && name[0] == 'h' && char.IsDigit(name[1])) headingLevel = 0;
                    continue;
                }

                if (name == "hr")
                {
                    section.Blocks.Add(Rule());
                    any = true;
                    continue;
                }

                if (name is "p" or "div" or "center" or "section" or "article" or "header" or "footer" or "main" or "figure" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
                {
                    var newAlign = name == "center" ? TextAlignment.Center : (tag.Attr("align")?.ToLowerInvariant()) switch
                    {
                        "center" => TextAlignment.Center,
                        "right" => TextAlignment.Right,
                        "left" => TextAlignment.Left,
                        _ => align,
                    };
                    if (name is not ("h1" or "h2" or "h3" or "h4" or "h5" or "h6"))
                        alignStack.Push((name, align));
                    align = newAlign;
                    if (name.Length == 2 && name[0] == 'h' && char.IsDigit(name[1])) headingLevel = name[1] - '0';
                }
                else if (name == "li")
                {
                    var into = Ensure();
                    into.Add(new Run("•  "));
                    any = true;
                }
                else if (name == "summary")
                {
                    var into = Ensure();
                    para!.FontWeight = FontWeights.SemiBold;
                    para.Margin = new Thickness(0, 4, 0, 6);
                    _ = into;
                }

                continue;
            }

            // inline-level tag
            var cur = para is null ? null : target;
            if (name is "br" or "img" or "a" or "b" or "strong" or "i" or "em" or "u" or "ins" or "s" or "strike" or "del" or "code" or "kbd" or "mark" or "sub" or "sup" or "small" or "tt" or "samp" or "cite" or "var" or "dfn")
            {
                if (tag.Closing && cur is null) continue;
                var coll = cur ?? Ensure();
                if (!ApplyTag(tag, ref coll, ref stack, hb.Line)) continue;
                target = coll;
                any = true;
            }
            else if (HeavyTags.Contains(name))
            {
                Rich |= RichContent.Html;
            }
        }

        foreach (var b in section.Blocks)
            if (b is Paragraph hp) FitImages(hp);

        return any ? section : null;
    }
}
