using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig.Extensions.Emoji;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MBlock = Markdig.Syntax.Block;
using MInline = Markdig.Syntax.Inlines.Inline;
using WInline = System.Windows.Documents.Inline;

namespace BetterNotepad.Markdown;

internal sealed partial class MarkdownBuilder
{
    private static readonly FontFamily Cambria = new("Cambria Math, Segoe UI Symbol");

    // ---------------------------------------------------------------- inline tree

    private void AppendInlines(ContainerInline? container, InlineCollection into)
    {
        if (container is null) return;
        var cur = into;
        List<(string Tag, InlineCollection Parent)>? stack = null;
        var trimNext = false;
        for (var i = container.FirstChild; i != null; i = i.NextSibling)
        {
            if (i is HtmlInline hi)
            {
                ApplyHtmlInline(hi.Tag, ref cur, ref stack);
                continue;
            }

            if (trimNext && i is LiteralInline lit && lit is not EmojiInline)
            {
                trimNext = false;
                var text = lit.Content.ToString().TrimStart();
                if (text.Length > 0) cur.Add(new Run(text));
                continue;
            }

            trimNext = i is TaskList;
            AppendInline(i, cur);
        }
    }

    private void AppendInline(MInline i, InlineCollection t)
    {
        switch (i)
        {
            case EmojiInline e:
                t.Add(new Run(e.Content.ToString()) { FontFamily = Res.Emoji });
                break;
            case LiteralInline lit:
                t.Add(new Run(lit.Content.ToString()));
                break;
            case HtmlEntityInline ent:
                t.Add(new Run(ent.Transcoded.ToString()));
                break;
            case LineBreakInline lb:
                if (lb.IsHard || _o.HardLineBreaks) t.Add(new LineBreak());
                else t.Add(new Run(" "));
                break;
            case CodeInline c:
                t.Add(CodeSpan(c.Content));
                break;
            case EmphasisInline em:
            {
                var span = new Span();
                if (em.DelimiterChar == '~')
                {
                    span.TextDecorations = TextDecorations.Strikethrough;
                    span.Fg(K.TextDim);
                }
                else
                {
                    if (em.DelimiterCount >= 2) span.FontWeight = FontWeights.SemiBold;
                    if (em.DelimiterCount is 1 or 3) span.FontStyle = FontStyles.Italic;
                }

                AppendInlines(em, span.Inlines);
                t.Add(span);
                break;
            }
            case LinkInline li:
                if (li.IsImage)
                {
                    t.Add(ImageInline(li.Url ?? "", PlainText(li), li.Title, null, null, li.Line));
                }
                else if (string.IsNullOrWhiteSpace(li.Url))
                {
                    AppendInlines(li, t);
                }
                else
                {
                    var h = NewLink(li.Url!, li.Title);
                    AppendInlines(li, h.Inlines);
                    if (h.Inlines.Count == 0) h.Inlines.Add(new Run(li.Url));
                    t.Add(h);
                }

                break;
            case AutolinkInline al:
            {
                var url = al.IsEmail ? "mailto:" + al.Url : al.Url;
                var h = NewLink(url, null);
                h.Inlines.Add(new Run(al.Url));
                t.Add(h);
                break;
            }
            case TaskList task:
                t.Add(TaskBox(task.Checked, task.Line));
                break;
            case FootnoteLink fl:
                t.Add(FootnoteRef(fl));
                break;
            case MathInline mi:
                Rich |= RichContent.Math;
                t.Add(new Run(mi.Content.ToString()) { FontFamily = Cambria, FontStyle = FontStyles.Italic, FontSize = _o.FontSize * 1.04 }.Fg(K.AccentText));
                break;
            case DelimiterInline di:
                t.Add(new Run(di.ToLiteral()));
                AppendInlines(di, t);
                break;
            case ContainerInline ci:
                AppendInlines(ci, t);
                break;
        }
    }

    private Span CodeSpan(string content)
    {
        var span = new Span { FontFamily = _mono, FontSize = Math.Round(_o.FontSize * 0.9, 1) };
        span.Bg(K.TableHeader).Fg(K.AccentText);
        span.Inlines.Add(new Run(" ") { FontFamily = _body });
        span.Inlines.Add(new Run(content));
        span.Inlines.Add(new Run(" ") { FontFamily = _body });
        return span;
    }

    internal static string PlainText(ContainerInline? c)
    {
        if (c is null) return "";
        var sb = new StringBuilder();
        Plain(c, sb);
        return sb.ToString();
    }

    private static void Plain(ContainerInline c, StringBuilder sb)
    {
        for (var i = c.FirstChild; i != null; i = i.NextSibling)
        {
            switch (i)
            {
                case LiteralInline lit: sb.Append(lit.Content.AsSpan()); break;
                case HtmlEntityInline ent: sb.Append(ent.Transcoded.AsSpan()); break;
                case CodeInline code: sb.Append(code.Content); break;
                case LineBreakInline: sb.Append(' '); break;
                case AutolinkInline al: sb.Append(al.Url); break;
                case MathInline mi: sb.Append(mi.Content.AsSpan()); break;
                case ContainerInline ci: Plain(ci, sb); break;
            }
        }
    }

    // ---------------------------------------------------------------- links, tasks, footnotes, images

    private Hyperlink NewLink(string url, string? title)
    {
        var h = new Hyperlink { Tag = url, TextDecorations = null };
        h.Fg(K.AccentText);
        h.ToolTip = string.IsNullOrWhiteSpace(title) ? url : title + "\n" + url;
        h.Click += OnLinkClick;
        h.MouseEnter += OnLinkEnter;
        h.MouseLeave += OnLinkLeave;
        return h;
    }

    private static void OnLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink { Tag: string url } h && Res.TryResolve(h, out var block, out _))
            block!.Owner.RaiseLink(url);
    }

    private static void OnLinkEnter(object sender, System.Windows.Input.MouseEventArgs e) => ((Hyperlink)sender).TextDecorations = TextDecorations.Underline;

    private static void OnLinkLeave(object sender, System.Windows.Input.MouseEventArgs e) => ((Hyperlink)sender).TextDecorations = null;

    private InlineUIContainer TaskBox(bool isChecked, int line)
    {
        var cb = new CheckBox
        {
            IsChecked = isChecked,
            Style = MdStyles.CheckBox,
            Margin = new Thickness(0, 0, 9, -3),
            Tag = Res.Box(Math.Max(0, line - _baseLine)),
        };
        cb.Click += OnTaskClick;
        return new InlineUIContainer(cb) { BaselineAlignment = BaselineAlignment.Center };
    }

    private static void OnTaskClick(object sender, RoutedEventArgs e)
    {
        var cb = (CheckBox)sender;
        if (Res.TryResolve(cb, out var block, out var line0))
            block!.Owner.RaiseTask(line0 + 1, cb.IsChecked == true);
    }

    private WInline FootnoteRef(FootnoteLink fl)
    {
        var fn = fl.Footnote;
        var h = new Hyperlink(new Run(fl.IsBackLink ? "↩" : fl.Index.ToString()))
        {
            TextDecorations = null,
            BaselineAlignment = fl.IsBackLink ? BaselineAlignment.Baseline : BaselineAlignment.TextTop,
            FontSize = Math.Round(_o.FontSize * (fl.IsBackLink ? 0.9 : 0.72), 1),
        };
        h.Fg(K.AccentText);
        if (fl.IsBackLink)
        {
            h.ToolTip = _o.Labels.BackToReference;
            h.Click += (_, _) =>
            {
                if (_footRefs.TryGetValue(fn, out var r)) _scrollTo(r);
            };
        }
        else
        {
            _footRefs.TryAdd(fn, h);
            h.Click += (_, _) =>
            {
                if (_footTargets.TryGetValue(fn, out var li)) _scrollTo(li);
            };
        }

        return h;
    }

    private InlineUIContainer ImageInline(string url, string alt, string? title, double? width, double? height, int line)
    {
        var host = new ImageHost(alt, title, width, height, _o.Labels, _column, Math.Max(0, line - _baseLine));
        var container = new InlineUIContainer(host) { BaselineAlignment = BaselineAlignment.Bottom };
        host.Container = container;
        _images.Load(host, url, _baseFolder);
        return container;
    }

    // ---------------------------------------------------------------- inline HTML subset

    private void ApplyHtmlInline(string raw, ref InlineCollection cur, ref List<(string Tag, InlineCollection Parent)>? stack)
    {
        if (raw.StartsWith("<!--", StringComparison.Ordinal)) return;
        var tag = HtmlTag.Parse(raw);
        if (tag is null)
        {
            cur.Add(new Run(raw));
            return;
        }

        ApplyTag(tag, ref cur, ref stack, 0);
    }

    private static readonly HashSet<string> HeavyTags = new(StringComparer.Ordinal)
    {
        "table", "script", "iframe", "video", "audio", "svg", "canvas", "form", "input", "button", "object", "embed", "math", "style", "picture", "source",
    };

    /// <summary>Applies one inline-level tag to the current target. Returns false for tags it does not handle.</summary>
    private bool ApplyTag(HtmlTag tag, ref InlineCollection cur, ref List<(string Tag, InlineCollection Parent)>? stack, int line)
    {
        var name = tag.Name;
        if (HeavyTags.Contains(name)) Rich |= RichContent.Html;

        if (tag.Closing)
        {
            if (stack is null) return false;
            for (var k = stack.Count - 1; k >= 0; k--)
            {
                if (stack[k].Tag != name) continue;
                cur = stack[k].Parent;
                stack.RemoveRange(k, stack.Count - k);
                return true;
            }

            return false;
        }

        Span? span = null;
        switch (name)
        {
            case "br":
                cur.Add(new LineBreak());
                return true;
            case "img":
            {
                var src = tag.Attr("src");
                if (string.IsNullOrWhiteSpace(src)) return true;
                cur.Add(ImageInline(src, tag.Attr("alt") ?? "", tag.Attr("title"), ParseSize(tag.Attr("width")), ParseSize(tag.Attr("height")), line));
                return true;
            }
            case "b" or "strong": span = new Span { FontWeight = FontWeights.SemiBold }; break;
            case "i" or "em" or "cite" or "var" or "dfn": span = new Span { FontStyle = FontStyles.Italic }; break;
            case "u" or "ins": span = new Span { TextDecorations = TextDecorations.Underline }; break;
            case "s" or "strike" or "del":
                span = new Span { TextDecorations = TextDecorations.Strikethrough };
                span.Fg(K.TextDim);
                break;
            case "code" or "tt" or "samp":
                span = new Span { FontFamily = _mono, FontSize = Math.Round(_o.FontSize * 0.9, 1) };
                span.Bg(K.TableHeader).Fg(K.AccentText);
                break;
            case "kbd":
                span = new Span { FontFamily = _mono, FontSize = Math.Round(_o.FontSize * 0.86, 1), FontWeight = FontWeights.SemiBold };
                span.Bg(K.TableHeader);
                break;
            case "mark":
                span = new Span();
                span.Bg(K.WarningSoft);
                break;
            case "sub": span = new Span { BaselineAlignment = BaselineAlignment.TextBottom, FontSize = Math.Round(_o.FontSize * 0.75, 1) }; break;
            case "sup": span = new Span { BaselineAlignment = BaselineAlignment.TextTop, FontSize = Math.Round(_o.FontSize * 0.75, 1) }; break;
            case "small":
                span = new Span { FontSize = Math.Round(_o.FontSize * 0.86, 1) };
                span.Fg(K.TextDim);
                break;
            case "a":
            {
                var href = tag.Attr("href");
                if (string.IsNullOrWhiteSpace(href) || (stack?.Any(s => s.Tag == "a") ?? false)) return false;
                span = NewLink(href, tag.Attr("title"));
                break;
            }
            default:
                return false;
        }

        if (tag.SelfClosing) return true;
        cur.Add(span);
        (stack ??= new()).Add((name, cur));
        cur = span.Inlines;
        return true;
    }

    private static double? ParseSize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimEnd('p', 'x', 'P', 'X');
        return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
    }
}
