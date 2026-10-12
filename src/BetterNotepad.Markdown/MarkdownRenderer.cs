using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Footnotes;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MBlock = Markdig.Syntax.Block;
using WBlock = System.Windows.Documents.Block;

namespace BetterNotepad.Markdown;

/// <summary>
/// Markdown to WPF FlowDocument, natively. Keeps its own document and patches it incrementally: top-level blocks whose
/// source text did not change are reused (no re-layout, images stay loaded). Must be used on the UI thread.
/// All source lines in the public API are 1-based.
/// </summary>
public sealed class MarkdownRenderer : IDisposable
{
    private static readonly MarkdownPipeline SoftPipeline = BuildPipeline(false);
    private static readonly MarkdownPipeline HardPipeline = BuildPipeline(true);

    private static MarkdownPipeline BuildPipeline(bool hardBreaks)
    {
        var b = new MarkdownPipelineBuilder()
            .UsePreciseSourceLocation()
            .UsePipeTables()
            .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
            .UseAutoLinks()
            .UseTaskLists()
            .UseFootnotes()
            .UseAlertBlocks()
            .UseEmojiAndSmiley(false)
            .UseMathematics()
            .UseYamlFrontMatter();
        if (hardBreaks) b.UseSoftlineBreakAsHardlineBreak();
        return b.Build();
    }

    private readonly ImageService _images;
    private readonly ColumnInfo _column = new();
    private List<MarkdownBlockInfo> _entries = new();
    private List<MarkdownHeading> _headings = new();
    private string _markdown = "";
    private string? _baseFolder;
    private string _defsSignature = "";
    private bool _hadFootnotes;
    private bool _forceFull = true;
    private MarkdownBlockInfo? _firstWithElement;
    private int _layoutVersion;
    private bool _disposed;

    public MarkdownRenderer(MarkdownOptions? options = null)
    {
        Options = options ?? new MarkdownOptions();
        _images = new ImageService(Options.MaxImagePixelWidth);
        _column.ContentWidth = Options.MaxContentWidth;
        Document = new FlowDocument
        {
            FontFamily = new FontFamily(Options.BodyFont),
            FontSize = Options.FontSize,
            LineHeight = Math.Round(Options.FontSize * Options.LineHeightFactor, 1),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextAlignment = TextAlignment.Left,
            PagePadding = new Thickness(32, 36, 32, 72),
            IsOptimalParagraphEnabled = false,
            IsHyphenationEnabled = false,
            IsColumnWidthFlexible = false,
            ColumnWidth = 100000,
        };
        Document.SetResourceReference(TextElement.ForegroundProperty, K.Text);
    }

    public MarkdownOptions Options { get; }

    public FlowDocument Document { get; }

    public IReadOnlyList<MarkdownHeading> Headings => _headings;

    public RichContent RichContent { get; private set; }

    /// <summary>Top-level blocks in source order (also those that render to nothing).</summary>
    public IReadOnlyList<MarkdownBlockInfo> Blocks => _entries;

    /// <summary>A task-list checkbox was clicked: 1-based source line of the item, new state.</summary>
    public event Action<int, bool>? TaskToggled;

    /// <summary>A link was clicked (raw URL as written in the source, including "#anchor").</summary>
    public event Action<string>? LinkClicked;

    /// <summary>An element wants to be scrolled into view (footnote jumps).</summary>
    public event Action<TextElement>? ScrollRequested;

    /// <summary>Renders into a fresh FlowDocument (print / export). The caller owns it; async images keep loading.</summary>
    public static FlowDocument RenderDocument(string markdown, string? baseFolder = null, MarkdownOptions? options = null)
    {
        var r = new MarkdownRenderer(options);
        r.Render(markdown, baseFolder);
        return r.Document;
    }

    /// <summary>Scroll offset (document units, as the ScrollViewer reports it) and zoom. Text-view rectangles are viewport-relative and zoomed (screen units).</summary>
    internal Func<(double Offset, double Zoom)>? ViewState { get; set; }

    private double ToDocument(double rawTop)
    {
        var (offset, zoom) = ViewState?.Invoke() ?? (0, 1);
        return rawTop / zoom + offset;
    }

    internal void RaiseLink(string url) => LinkClicked?.Invoke(url);

    internal void RaiseTask(int line, bool isChecked) => TaskToggled?.Invoke(line, isChecked);

    /// <summary>Width of the readable column; images are limited to it.</summary>
    public void SetColumnWidth(double width)
    {
        if (width > 0 && Math.Abs(_column.ContentWidth - width) > 0.5) _column.ContentWidth = width;
    }

    /// <summary>Forces the next Render to rebuild everything (options or theme-dependent code highlighting changed).</summary>
    public void Invalidate() => _forceFull = true;

    /// <summary>Drops decoded images (hidden tab). They are reloaded on demand by the next full render.</summary>
    public void ReleaseImages()
    {
        _images.Clear();
        _forceFull = true;
    }

    /// <summary>Layout moved (resize, image loaded): cached block positions are stale.</summary>
    public void InvalidateLayout() => _layoutVersion++;

    // ---------------------------------------------------------------- render

    public MarkdownRenderResult Render(string markdown, string? baseFolder = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        markdown ??= "";
        var pipeline = Options.HardLineBreaks ? HardPipeline : SoftPipeline;
        var doc = Markdig.Markdown.Parse(markdown, pipeline);

        var tops = new List<MBlock>(doc.Count);
        var defs = "";
        MBlock? footnotes = null;
        foreach (var b in doc)
        {
            if (b is LinkReferenceDefinitionGroup g)
            {
                defs = Signature(g);
                continue;
            }

            if (b is FootnoteGroup) footnotes = b;
            else tops.Add(b);
        }

        if (!IsSorted(tops)) tops = tops.OrderBy(t => t.Line).ToList();
        var hasFootnotes = footnotes is not null;
        if (footnotes is not null) tops.Add(footnotes); // the footnote list always renders last

        var starts = LineStarts(markdown);
        var lineCount = starts.Count;
        var n = tops.Count;
        var news = new MarkdownBlockInfo[n];
        var lines = new int[n];
        for (var i = 0; i < n; i++)
        {
            lines[i] = tops[i] is FootnoteGroup { Count: > 0 } fg ? Math.Max(fg[0].Line, i > 0 ? lines[i - 1] + 1 : 0) : tops[i].Line;
        }

        for (var i = 0; i < n; i++)
        {
            var line = Math.Clamp(lines[i], 0, Math.Max(0, lineCount - 1));
            var next = i + 1 < n ? Math.Clamp(lines[i + 1], line + 1, lineCount) : lineCount;
            var from = lineCount == 0 ? 0 : starts[line];
            var to = next >= lineCount ? markdown.Length : starts[next];
            var e = new MarkdownBlockInfo(this)
            {
                StartLine0 = line,
                RegionEnd0 = next,
                TextStart = from,
                TextLength = to - from,
                SourceType = tops[i].GetType(),
            };
            e.EndLine0 = LastContentLine(markdown, starts, line, next);
            news[i] = e;
        }

        var full = _forceFull || _entries.Count == 0 || hasFootnotes || _hadFootnotes || defs != _defsSignature || baseFolder != _baseFolder;
        var prefix = 0;
        var suffix = 0;
        if (!full)
        {
            var m = Math.Min(_entries.Count, n);
            while (prefix < m && Same(_entries[prefix], news[prefix], markdown)) prefix++;
            while (suffix < m - prefix && Same(_entries[^(suffix + 1)], news[n - 1 - suffix], markdown)) suffix++;
        }

        var oldCount = _entries.Count;
        var removed = _entries.GetRange(prefix, oldCount - prefix - suffix);
        WBlock? anchor = null;
        for (var i = oldCount - suffix; i < oldCount && anchor is null; i++) anchor = _entries[i].Element;

        if (full)
        {
            Document.Blocks.Clear();
            foreach (var old in _entries) old.Alive = false;
            anchor = null;
        }
        else
        {
            foreach (var old in removed)
            {
                if (old.Element is not null) Document.Blocks.Remove(old.Element);
                old.Alive = false;
            }
        }

        var builder = new MarkdownBuilder(Options, baseFolder, _images, _column, e => ScrollRequested?.Invoke(e));
        var list = new List<MarkdownBlockInfo>(n);
        var fresh = new List<WBlock>();
        for (var i = 0; i < n; i++)
        {
            if (i < prefix)
            {
                Adopt(_entries[i], news[i]);
                list.Add(_entries[i]);
            }
            else if (i >= n - suffix)
            {
                var old = _entries[oldCount - (n - i)];
                Adopt(old, news[i]);
                list.Add(old);
            }
            else
            {
                var e = news[i];
                e.Element = builder.BuildTop(tops[i], e);
                list.Add(e);
                if (e.Element is not null) fresh.Add(e.Element);
            }
        }

        if (fresh.Count > 0)
        {
            if (anchor is null) Document.Blocks.AddRange(fresh);
            else
            {
                foreach (var el in fresh) Document.Blocks.InsertBefore(anchor, el);
            }
        }

        _entries = list;
        _markdown = markdown;
        _baseFolder = baseFolder;
        _defsSignature = defs;
        _hadFootnotes = hasFootnotes;
        _forceFull = false;
        _layoutVersion++;
        FixFirstMargin();

        var rich = RichContent.None;
        foreach (var e in list) rich |= e.Rich;
        var richChanged = rich != RichContent;
        RichContent = rich;

        var headings = CollectHeadings(doc);
        var headingsChanged = !headings.SequenceEqual(_headings);
        _headings = headings;

        return new MarkdownRenderResult(!full, prefix + suffix, n - prefix - suffix, headingsChanged, richChanged);
    }

    private static void Adopt(MarkdownBlockInfo keep, MarkdownBlockInfo fresh)
    {
        keep.StartLine0 = fresh.StartLine0;
        keep.EndLine0 = fresh.EndLine0;
        keep.RegionEnd0 = fresh.RegionEnd0;
        keep.TextStart = fresh.TextStart;
        keep.TextLength = fresh.TextLength;
        keep.CachedVersion = -1;
    }

    private bool Same(MarkdownBlockInfo old, MarkdownBlockInfo now, string markdown) =>
        old.TextLength == now.TextLength
        && old.SourceType == now.SourceType
        && _markdown.AsSpan(old.TextStart, old.TextLength).SequenceEqual(markdown.AsSpan(now.TextStart, now.TextLength));

    private void FixFirstMargin()
    {
        var first = _entries.FirstOrDefault(e => e.Element is not null);
        if (ReferenceEquals(first, _firstWithElement) && first is not null && first.Element!.Margin.Top == 0) return;
        if (_firstWithElement is { Alive: true, Element: { } was })
        {
            var m = was.Margin;
            was.Margin = new Thickness(m.Left, _firstWithElement.DefaultTopMargin, m.Right, m.Bottom);
        }

        _firstWithElement = first;
        if (first?.Element is { } el)
        {
            var m = el.Margin;
            el.Margin = new Thickness(m.Left, 0, m.Right, m.Bottom);
        }
    }

    private static bool IsSorted(List<MBlock> blocks)
    {
        for (var i = 1; i < blocks.Count; i++)
            if (blocks[i].Line < blocks[i - 1].Line) return false;
        return true;
    }

    private static string Signature(LinkReferenceDefinitionGroup g)
    {
        var sb = new StringBuilder();
        foreach (var kv in g.Links.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append('\u0001').Append(kv.Value.Url).Append('\u0001').Append(kv.Value.Title).Append('\u0002');
        return sb.ToString();
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int>(Math.Max(16, text.Length / 40)) { 0 };
        var span = text.AsSpan();
        var pos = 0;
        while (pos < span.Length)
        {
            var idx = span[pos..].IndexOfAny('\n', '\r');
            if (idx < 0) break;
            pos += idx;
            if (span[pos] == '\r' && pos + 1 < span.Length && span[pos + 1] == '\n') pos++;
            pos++;
            starts.Add(pos);
        }

        // a trailing line terminator opens no extra line
        if (starts.Count > 1 && starts[^1] >= text.Length) starts.RemoveAt(starts.Count - 1);
        return starts;
    }

    private static int LastContentLine(string text, List<int> starts, int first, int endExclusive)
    {
        for (var l = endExclusive - 1; l > first; l--)
        {
            var from = starts[l];
            var to = l + 1 < starts.Count ? starts[l + 1] : text.Length;
            if (!text.AsSpan(from, to - from).IsWhiteSpace()) return l;
        }

        return first;
    }

    // ---------------------------------------------------------------- headings

    private static List<MarkdownHeading> CollectHeadings(MarkdownDocument doc)
    {
        var list = new List<MarkdownHeading>();
        var used = new Dictionary<string, int>(StringComparer.Ordinal);

        void Walk(ContainerBlock c)
        {
            foreach (var b in c)
            {
                switch (b)
                {
                    case HeadingBlock h:
                    {
                        var text = MarkdownBuilder.PlainText(h.Inline).Trim();
                        var slug = Slugify(text);
                        if (used.TryGetValue(slug, out var count))
                        {
                            used[slug] = count + 1;
                            slug = slug + "-" + count;
                        }
                        else
                        {
                            used[slug] = 1;
                        }

                        list.Add(new MarkdownHeading(Math.Clamp(h.Level, 1, 6), text, h.Line + 1, slug));
                        break;
                    }
                    case FootnoteGroup:
                    case Markdig.Extensions.Tables.Table:
                        break;
                    case ContainerBlock cb:
                        Walk(cb);
                        break;
                }
            }
        }

        Walk(doc);
        return list;
    }

    /// <summary>GitHub-style anchor: lowercase, spaces to hyphens, punctuation dropped, letters of any script kept.</summary>
    public static string Slugify(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-') sb.Append(ch);
            else if (char.IsWhiteSpace(ch)) sb.Append('-');
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------------- source mapping

    /// <summary>The top-level block that contains a source line (1-based), or the nearest one before it.</summary>
    public MarkdownBlockInfo? FindBlock(int line)
    {
        var i = IndexOfLine(line - 1);
        return i < 0 ? null : _entries[i];
    }

    private int IndexOfLine(int line0)
    {
        if (_entries.Count == 0) return -1;
        int lo = 0, hi = _entries.Count - 1, best = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (_entries[mid].StartLine0 <= line0)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return best;
    }

    /// <summary>
    /// Vertical position (document layout coordinates) of a source line, interpolated inside its block;
    /// null while nothing is laid out. <paramref name="fraction"/> is the progress through the line, 0..1.
    /// </summary>
    public double? OffsetOfLine(int line, double fraction = 0)
    {
        var line0 = Math.Max(0, line - 1);
        var i = IndexOfLine(line0);
        if (i < 0) return null;
        var e = _entries[i];
        var top = TopOf(i);
        var bottom = BottomOf(i);
        if (top is null || bottom is null) return null;
        var span = Math.Max(1, e.RegionEnd0 - e.StartLine0);
        var t = Math.Clamp((line0 - e.StartLine0 + Math.Clamp(fraction, 0, 1)) / span, 0, 1);
        return top.Value + (bottom.Value - top.Value) * t;
    }

    /// <summary>Source line (1-based) at a vertical document position, with the progress through that line.</summary>
    public int LineAtOffset(double y, out double fraction)
    {
        fraction = 0;
        if (_entries.Count == 0) return 1;
        int lo = 0, hi = _entries.Count - 1, best = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var top = TopOf(mid);
            if (top is not null && top.Value <= y + 0.01)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        var e = _entries[best];
        var t = TopOf(best) ?? 0;
        var b = BottomOf(best) ?? t + 1;
        var span = Math.Max(1, e.RegionEnd0 - e.StartLine0);
        var rel = b > t ? Math.Clamp((y - t) / (b - t), 0, 0.9999) : 0;
        var pos = rel * span;
        var lineOffset = (int)Math.Floor(pos);
        fraction = pos - lineOffset;
        return e.StartLine0 + lineOffset + 1;
    }

    private double? TopOf(int index)
    {
        if (index <= 0) return _entries.Count > 0 ? 0 : null;
        var e = _entries[index];
        if (e.CachedVersion == _layoutVersion) return double.IsNaN(e.CachedTop) ? null : e.CachedTop;
        double? top = TopOfElement(e.Element);
        if (top is null)
        {
            // block without an element: use the next block that has one
            for (var j = index + 1; j < _entries.Count && top is null; j++)
                top = TopOfElement(_entries[j].Element);
        }

        e.CachedVersion = _layoutVersion;
        e.CachedTop = top ?? double.NaN;
        return top;
    }

    private double? BottomOf(int index)
    {
        for (var j = index + 1; j < _entries.Count; j++)
        {
            var t = TopOf(j);
            if (t is not null) return t;
        }

        return EndOfDocument();
    }

    private double? EndOfDocument()
    {
        try
        {
            var r = Document.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
            return r.IsEmpty ? null : ToDocument(r.Bottom);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Top of the first text line of an element, in document layout coordinates.</summary>
    public double? TopOfElement(TextElement? element)
    {
        if (element is null) return null;
        try
        {
            var p = element.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            var r = p.GetCharacterRect(LogicalDirection.Forward);
            if (r.IsEmpty || double.IsInfinity(r.Top)) return null;
            return ToDocument(r.Top);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Identity of a block by its source text; survives full rebuilds (scroll anchoring).</summary>
    internal (int Length, int Hash, int Index) KeyOf(MarkdownBlockInfo b) =>
        (b.TextLength, string.GetHashCode(_markdown.AsSpan(b.TextStart, b.TextLength)), _entries.IndexOf(b));

    /// <summary>The block whose source text equals a previously captured key, nearest to its old position.</summary>
    internal MarkdownBlockInfo? FindByKey((int Length, int Hash, int Index) key)
    {
        MarkdownBlockInfo? best = null;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.TextLength != key.Length || e.Element is null) continue;
            var d = Math.Abs(i - key.Index);
            if (d >= bestDistance) continue;
            if (string.GetHashCode(_markdown.AsSpan(e.TextStart, e.TextLength)) != key.Hash) continue;
            best = e;
            bestDistance = d;
        }

        return best;
    }

    /// <summary>Source line (1-based) of a rendered element, if it belongs to this document.</summary>
    public int? SourceLineOf(DependencyObject? element) =>
        Res.TryResolve(element, out var block, out var line0) && block!.Owner == this ? line0 + 1 : null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _images.Dispose();
        Document.Blocks.Clear();
        _entries = new List<MarkdownBlockInfo>();
        _headings = new List<MarkdownHeading>();
        _markdown = "";
    }
}
