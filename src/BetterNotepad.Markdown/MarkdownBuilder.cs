using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MBlock = Markdig.Syntax.Block;
using MInline = Markdig.Syntax.Inlines.Inline;
using MTable = Markdig.Extensions.Tables.Table;
using MTableCell = Markdig.Extensions.Tables.TableCell;
using MTableRow = Markdig.Extensions.Tables.TableRow;
using WBlock = System.Windows.Documents.Block;
using WList = System.Windows.Documents.List;
using WTable = System.Windows.Documents.Table;
using WTableCell = System.Windows.Documents.TableCell;
using WTableRow = System.Windows.Documents.TableRow;

namespace BetterNotepad.Markdown;

/// <summary>Turns Markdig blocks into WPF flow blocks. One instance per Render call; holds no document state.</summary>
internal sealed partial class MarkdownBuilder
{
    private readonly MarkdownOptions _o;
    private readonly string? _baseFolder;
    private readonly ImageService _images;
    private readonly ColumnInfo _column;
    private readonly FontFamily _body;
    private readonly FontFamily _heading;
    private readonly FontFamily _mono;
    private readonly Action<TextElement> _scrollTo;
    private readonly double _scale;

    private MarkdownBlockInfo _top = null!;
    private int _baseLine;
    private double _gap = Gap;
    private int _depth;
    private bool _inQuote;
    private readonly Dictionary<Footnote, ListItem> _footTargets = new();
    private readonly Dictionary<Footnote, Hyperlink> _footRefs = new();

    private const double Gap = 14;

    public RichContent Rich;

    public MarkdownBuilder(MarkdownOptions options, string? baseFolder, ImageService images, ColumnInfo column, Action<TextElement> scrollTo)
    {
        _o = options;
        _baseFolder = baseFolder;
        _images = images;
        _column = column;
        _scrollTo = scrollTo;
        _body = new FontFamily(options.BodyFont);
        _heading = new FontFamily(options.HeadingFont);
        _mono = new FontFamily(options.CodeFont);
        _scale = options.FontSize / 14.5;
    }

    public WBlock? BuildTop(MBlock b, MarkdownBlockInfo info)
    {
        _top = info;
        _baseLine = info.StartLine0;
        Rich = RichContent.None;
        _gap = Gap;
        _depth = 0;
        _inQuote = false;
        var el = Convert(b);
        if (el != null)
        {
            el.Tag = info;
            info.DefaultTopMargin = el.Margin.Top;
        }

        info.Rich = Rich;
        info.Kind = el is null ? MarkdownBlockKind.Other : KindOf(b);
        return el;
    }

    private static MarkdownBlockKind KindOf(MBlock b) => b switch
    {
        HeadingBlock => MarkdownBlockKind.Heading,
        ParagraphBlock => MarkdownBlockKind.Paragraph,
        AlertBlock => MarkdownBlockKind.Callout,
        QuoteBlock => MarkdownBlockKind.Quote,
        ListBlock => MarkdownBlockKind.List,
        CodeBlock => MarkdownBlockKind.Code,
        ThematicBreakBlock => MarkdownBlockKind.Rule,
        HtmlBlock => MarkdownBlockKind.Html,
        MTable => MarkdownBlockKind.Table,
        FootnoteGroup => MarkdownBlockKind.Footnotes,
        _ => MarkdownBlockKind.Other,
    };

    private object Rel(MBlock b) => Res.Box(Math.Max(0, b.Line - _baseLine));

    private WBlock? Convert(MBlock b)
    {
        switch (b)
        {
            case HeadingBlock h: return Heading(h);
            case ParagraphBlock p: return Para(p);
            case AlertBlock a: return Alert(a);
            case QuoteBlock q: return Quote(q);
            case ListBlock l: return ListOf(l);
            case MathBlock m:
                Rich |= RichContent.Math;
                return CodeOf("math", CleanCode(m.Lines.ToString()), m);
            case YamlFrontMatterBlock y: return CodeOf("yaml", CleanCode(y.Lines.ToString()), y);
            case CodeBlock c: return FencedOrIndented(c);
            case ThematicBreakBlock: return Rule();
            case HtmlBlock hb: return HtmlOf(hb);
            case MTable t: return TableOf(t);
            case FootnoteGroup g: return Footnotes(g);
            case LinkReferenceDefinitionGroup: return null;
            case LeafBlock { Inline: not null } lb:
            {
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, _gap), Tag = Rel(lb) };
                AppendInlines(lb.Inline, p.Inlines);
                return p;
            }
            case ContainerBlock cb:
            {
                var s = new Section { Tag = Rel(cb) };
                Children(cb, s.Blocks, _gap);
                return s;
            }
            default: return null;
        }
    }

    private void Children(ContainerBlock c, BlockCollection into, double gap, bool trimLast = true)
    {
        var old = _gap;
        _gap = gap;
        foreach (var child in c)
        {
            var e = Convert(child);
            if (e != null) into.Add(e);
        }

        _gap = old;
        if (trimLast && into.LastBlock is { } last)
        {
            var m = last.Margin;
            last.Margin = new Thickness(m.Left, m.Top, m.Right, 0);
        }
    }

    // ---------------------------------------------------------------- headings / paragraphs

    private static readonly (double Size, double Line, double Top, double Bottom)[] HeadingMetrics =
    [
        (30, 38, 26, 16),
        (23, 31, 20, 14),
        (18.5, 27, 12, 10),
        (16, 24, 8, 8),
        (14.5, 22, 4, 6),
        (13.5, 22, 4, 6),
    ];

    private Paragraph Heading(HeadingBlock h)
    {
        var p = new Paragraph { Tag = Rel(h) };
        StyleHeading(p, h.Level);
        AppendInlines(h.Inline, p.Inlines);
        return p;
    }

    private void StyleHeading(Paragraph p, int level)
    {
        level = Math.Clamp(level, 1, 6);
        var m = HeadingMetrics[level - 1];
        p.FontSize = Math.Round(m.Size * _scale, 1);
        p.LineHeight = Math.Round(m.Line * _scale);
        p.FontFamily = level <= 3 ? _heading : _body;
        p.FontWeight = level <= 4 ? FontWeights.SemiBold : FontWeights.SemiBold;
        p.Margin = new Thickness(0, m.Top, 0, m.Bottom);
        p.Fg(level == 6 ? K.TextDim : K.Text);
        if (level <= 2)
        {
            p.BorderThickness = new Thickness(0, 0, 0, 1);
            p.Padding = new Thickness(0, 0, 0, level == 1 ? 9 : 7);
            p.BorderFrom(K.Border);
        }
    }

    private Paragraph Para(ParagraphBlock pb)
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, _gap), Tag = Rel(pb) };
        AppendInlines(pb.Inline, p.Inlines);
        FitImages(p);
        return p;
    }

    /// <summary>
    /// The document uses a fixed line height (BlockLineHeight), which would squash pictures to one text line.
    /// Paragraphs that contain an image let their lines grow instead.
    /// </summary>
    internal static void FitImages(Paragraph p)
    {
        if (!ContainsImage(p.Inlines)) return;
        p.LineStackingStrategy = LineStackingStrategy.MaxHeight;
        p.LineHeight = double.NaN;
    }

    private static bool ContainsImage(InlineCollection inlines)
    {
        foreach (var i in inlines)
        {
            if (i is InlineUIContainer { Child: ImageHost }) return true;
            if (i is Span s && ContainsImage(s.Inlines)) return true;
        }

        return false;
    }

    private static Paragraph Rule() =>
        new Paragraph(new Run("")) { FontSize = 1, LineHeight = 1, Margin = new Thickness(0, 18, 0, 22), BorderThickness = new Thickness(0, 0, 0, 1) }
            .BorderFrom(K.BorderStrong);

    // ---------------------------------------------------------------- quotes / callouts

    private Section Quote(QuoteBlock q)
    {
        var s = new Section
        {
            Margin = new Thickness(0, 0, 0, _gap),
            Padding = new Thickness(16, 1, 8, 1),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Tag = Rel(q),
        };
        s.BorderFrom(K.QuoteBar).Fg(K.TextDim);
        var was = _inQuote;
        _inQuote = true;
        Children(q, s.Blocks, 10);
        _inQuote = was;
        return s;
    }

    private Section Alert(AlertBlock a)
    {
        var kind = a.Kind.ToString().Trim().ToUpperInvariant() switch
        {
            "TIP" => AlertKind.Tip,
            "IMPORTANT" => AlertKind.Important,
            "WARNING" => AlertKind.Warning,
            "CAUTION" => AlertKind.Caution,
            _ => AlertKind.Note,
        };
        var (color, soft) = kind switch
        {
            AlertKind.Tip => (K.Success, K.SuccessSoft),
            AlertKind.Important => (K.AccentText, K.AccentSoft),
            AlertKind.Warning => (K.Warning, K.WarningSoft),
            AlertKind.Caution => (K.Danger, K.DangerSoft),
            _ => (K.Info, K.InfoSoft),
        };

        var s = new Section
        {
            Margin = new Thickness(0, 2, 0, _gap),
            Padding = new Thickness(18, 13, 18, 13),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Tag = Rel(a),
        };
        s.BorderFrom(color == K.AccentText ? K.Accent : color).Fg(K.Text).Bg(soft);

        var title = new Paragraph { Margin = new Thickness(0, 0, 0, 7), FontWeight = FontWeights.SemiBold, Tag = Rel(a) };
        title.Fg(color);
        title.Inlines.Add(new InlineUIContainer(MdStyles.AlertIcon(kind, color)) { BaselineAlignment = BaselineAlignment.Center });
        title.Inlines.Add(new Run(_o.AlertTitle(kind)));
        s.Blocks.Add(title);

        var was = _inQuote;
        _inQuote = false;
        Children(a, s.Blocks, 8);
        _inQuote = was;
        return s;
    }

    // ---------------------------------------------------------------- lists

    private WList ListOf(ListBlock lb)
    {
        var tasks = lb.Count > 0 && lb.All(it => it is ListItemBlock { Count: > 0 } li && li[0] is ParagraphBlock { Inline.FirstChild: Markdig.Extensions.TaskLists.TaskList });
        var list = new WList
        {
            Margin = new Thickness(0, 0, 0, _gap),
            Tag = Rel(lb),
            MarkerOffset = 7,
            Padding = new Thickness(tasks ? (_depth == 0 ? 0 : 22) : 28, 0, 0, 0),
        };

        if (tasks) list.MarkerStyle = TextMarkerStyle.None;
        else if (lb.IsOrdered) list.MarkerStyle = TextMarkerStyle.Decimal;
        else list.MarkerStyle = _depth switch { 0 => TextMarkerStyle.Disc, 1 => TextMarkerStyle.Circle, _ => TextMarkerStyle.Square };

        if (lb.IsOrdered && int.TryParse(lb.OrderedStart, out var start) && start > 0) list.StartIndex = start;
        list.Fg(lb.IsOrdered ? K.TextDim : K.Accent);

        var itemGap = lb.IsLoose ? 9.0 : 3.0;
        _depth++;
        foreach (var child in lb)
        {
            if (child is not ListItemBlock item) continue;
            var li = new ListItem { Tag = Rel(item) };
            if (!_inQuote) li.Fg(K.Text);
            else li.Fg(K.TextDim);
            Children(item, li.Blocks, itemGap);
            list.ListItems.Add(li);
        }

        _depth--;
        return list;
    }

    // ---------------------------------------------------------------- code

    private WBlock FencedOrIndented(CodeBlock c)
    {
        string? lang = null;
        if (c is FencedCodeBlock f)
        {
            var info = (f.Info ?? "").Trim();
            var space = info.IndexOfAny([' ', '\t', '{']);
            lang = (space > 0 ? info[..space] : info).Trim().ToLowerInvariant();
            if (lang.Length == 0) lang = null;
        }

        if (lang is "mermaid") Rich |= RichContent.Mermaid;
        else if (lang is "math" or "latex" or "tex" or "katex") Rich |= RichContent.Math;

        return CodeOf(lang, CleanCode(c.Lines.ToString()), c);
    }

    private static string CleanCode(string code)
    {
        code = code.TrimEnd('\r', '\n');
        if (code.IndexOf('\t') < 0) return code;
        var sb = new System.Text.StringBuilder(code.Length + 16);
        var col = 0;
        foreach (var ch in code)
        {
            if (ch == '\t')
            {
                var n = 4 - col % 4;
                sb.Append(' ', n);
                col += n;
            }
            else
            {
                sb.Append(ch);
                col = ch is '\n' or '\r' ? 0 : col + 1;
            }
        }

        return sb.ToString();
    }

    private BlockUIContainer CodeOf(string? lang, string code, MBlock source)
    {
        var rel = Math.Max(0, source.Line - _baseLine);
        return new BlockUIContainer(new CodeBlockView(lang, code, _o, rel)) { Margin = new Thickness(0, 0, 0, _gap), Tag = Res.Box(rel) };
    }

    // ---------------------------------------------------------------- tables

    private WTable TableOf(MTable t)
    {
        var colCount = t.ColumnDefinitions.Count;
        foreach (var row in t.OfType<MTableRow>())
            colCount = Math.Max(colCount, row.OfType<MTableCell>().Sum(c => Math.Max(1, c.ColumnSpan)));

        var table = new WTable
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 4, 0, _gap + 2),
            BorderThickness = new Thickness(1),
            FontSize = Math.Round(_o.FontSize * 0.96, 1),
            LineHeight = Math.Round(_o.FontSize * 1.42),
            Tag = Rel(t),
        };
        table.BorderFrom(K.Border);
        var widest = new int[colCount];
        var longestWord = new int[colCount];
        foreach (var row in t.OfType<MTableRow>())
        {
            var pos = 0;
            foreach (var cell in row.OfType<MTableCell>())
            {
                var span = Math.Max(1, cell.ColumnSpan);
                if (span == 1 && pos < colCount)
                {
                    var text = string.Concat(cell.OfType<ParagraphBlock>().Select(p => PlainText(p.Inline)));
                    widest[pos] = Math.Max(widest[pos], text.Length);
                    foreach (var word in text.Split(' ')) longestWord[pos] = Math.Max(longestWord[pos], word.Length);
                }

                pos += span;
            }
        }

        // small tables keep their natural width; big ones fill the column with widths proportional to the content
        var natural = widest.Select(w => Math.Max(64.0, w * 7.6 + 30)).ToArray();
        var compact = natural.Sum() <= 440;
        for (var i = 0; i < colCount; i++)
        {
            table.Columns.Add(new TableColumn
            {
                Width = compact ? new GridLength(natural[i]) : new GridLength(Math.Max(Math.Clamp(widest[i], 7, 46), longestWord[i] + 2) + 3, GridUnitType.Star),
            });
        }

        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        var rows = t.OfType<MTableRow>().ToList();
        var bodyIndex = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var wr = new WTableRow { Tag = Rel(row) };
            if (row.IsHeader)
            {
                wr.Bg(K.TableHeader);
                wr.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                if (bodyIndex % 2 == 1) wr.Bg(K.TableStripe);
                bodyIndex++;
            }

            var lastRow = r == rows.Count - 1;
            var cells = row.OfType<MTableCell>().ToList();
            var colPos = 0;
            for (var ci = 0; ci < cells.Count; ci++)
            {
                var c = cells[ci];
                var span = Math.Max(1, c.ColumnSpan);
                var lastCol = colPos + span >= colCount;
                var cell = new WTableCell
                {
                    Padding = new Thickness(13, 8, 13, 8),
                    ColumnSpan = span,
                    RowSpan = Math.Max(1, c.RowSpan),
                    BorderThickness = new Thickness(0, 0, lastCol ? 0 : 1, lastRow ? 0 : 1),
                    Tag = Rel(c),
                };
                cell.SetResourceReference(WTableCell.BorderBrushProperty, row.IsHeader ? K.BorderStrong : K.Border);

                var align = colPos < t.ColumnDefinitions.Count ? t.ColumnDefinitions[colPos].Alignment : null;
                colPos += span;
                cell.TextAlignment = align switch
                {
                    Markdig.Extensions.Tables.TableColumnAlign.Center => TextAlignment.Center,
                    Markdig.Extensions.Tables.TableColumnAlign.Right => TextAlignment.Right,
                    _ => TextAlignment.Left,
                };

                Children(c, cell.Blocks, 0);
                if (cell.Blocks.Count == 0) cell.Blocks.Add(new Paragraph());
                wr.Cells.Add(cell);
            }

            group.Rows.Add(wr);
        }

        return table;
    }

    // ---------------------------------------------------------------- footnotes

    private Section Footnotes(FootnoteGroup g)
    {
        var s = new Section { Margin = new Thickness(0, 10, 0, 0), Tag = Rel(g) };
        s.Blocks.Add(Rule());
        var list = new WList
        {
            MarkerStyle = TextMarkerStyle.Decimal,
            MarkerOffset = 6,
            Padding = new Thickness(26, 0, 0, 0),
            FontSize = Math.Round(_o.FontSize * 0.92, 1),
            Margin = new Thickness(0),
        };
        list.Fg(K.TextDim);
        var first = true;
        foreach (var child in g)
        {
            if (child is not Footnote fn) continue;
            if (first && fn.Order > 0) list.StartIndex = fn.Order;
            first = false;
            var li = new ListItem { Tag = Rel(fn) };
            li.Fg(K.TextDim);
            Children(fn, li.Blocks, 4);
            _footTargets[fn] = li;
            list.ListItems.Add(li);
        }

        s.Blocks.Add(list);
        return s;
    }
}
