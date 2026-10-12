using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using BetterNotepad.Core.Commands;
using BetterNotepad.UI.Controls;

namespace BetterNotepad.UI.Palette;

/// <summary>Something the palette can list: a command, a file, a line, a symbol, a language.</summary>
public sealed class PaletteCandidate
{
    public required string Title { get; init; }

    /// <summary>Dim text after the title: command category, folder, parent symbol, extensions.</summary>
    public string? Detail { get; init; }

    /// <summary>Detail is code (go to line shows the target line).</summary>
    public bool DetailMono { get; init; }

    /// <summary>Faint text at the right edge (":42", "current", "method").</summary>
    public string? Hint { get; init; }

    public bool HintMono { get; init; }

    public KeyChord? Gesture { get; init; }

    /// <summary>Left icon (Segoe Fluent glyph) and its brush key.</summary>
    public string? Glyph { get; init; }

    public string GlyphBrush { get; init; } = "Brush.PanelTextDim";

    /// <summary>Or a letter badge (symbol kinds: f, m, C, H2…) tinted with a syntax brush.</summary>
    public string? Letter { get; init; }

    public string LetterBrush { get; init; } = "Brush.Syntax.Function";

    /// <summary>Group label shown on the first row of a group while the list is unfiltered.</summary>
    public string? Group { get; init; }

    /// <summary>More text to match (other language, search terms, extensions) — never highlighted.</summary>
    public string[]? Extra { get; init; }

    /// <summary>Also match "detail\title" as a path, highlighting folder characters too.</summary>
    public bool MatchPath { get; init; }

    /// <summary>A hint row (e.g. the go-to-line prompt): dimmed, Enter only closes.</summary>
    public bool IsInfo { get; init; }

    /// <summary>Stable identity (command id, path, language id) — handy for tests and default selection.</summary>
    public string? Id { get; init; }

    public Action? Accept { get; init; }

    /// <summary>Live preview while the row is selected (go to line / symbol).</summary>
    public Action? Preview { get; init; }

    public int Order { get; init; }

    /// <summary>Added to the fuzzy score (recently used commands, open tabs).</summary>
    public double Boost { get; init; }
}

/// <summary>A candidate as shown after filtering: highlights and group decoration.</summary>
public sealed class PaletteRow(PaletteCandidate item, int[]? titleHits, int[]? detailHits, bool showGroup, bool groupStart)
{
    public PaletteCandidate Item { get; } = item;
    public int[]? TitleHits { get; } = titleHits;
    public int[]? DetailHits { get; } = detailHits;
    public bool ShowGroup { get; } = showGroup;
    public bool GroupStart { get; } = groupStart;
    public double Score { get; init; }

    /// <summary>Extra air above a new group (the separator line sits in it).</summary>
    public Thickness ContainerMargin => GroupStart ? new Thickness(0, 9, 0, 0) : new Thickness(0);
}

public static class PaletteFilter
{
    private const string PathSep = "\\";

    /// <summary>
    /// Below this score per query character a match is mostly scattered letters ("sort" in "Close other
    /// tabs") — noise that buries the real hits. Word starts and runs stay well above it.
    /// </summary>
    public const double MinScorePerChar = 0.4;

    /// <summary>Unfiltered: source order with group labels. Filtered: fuzzy score, best first.</summary>
    public static List<PaletteRow> Apply(IReadOnlyList<PaletteCandidate> items, string query, double minPerChar = double.NegativeInfinity)
    {
        var rows = new List<PaletteRow>(items.Count);
        if (string.IsNullOrWhiteSpace(query))
        {
            string? group = null;
            for (var i = 0; i < items.Count; i++)
            {
                var c = items[i];
                var start = c.Group is not null && c.Group != group;
                group = c.Group;
                rows.Add(new PaletteRow(c, null, null, start, start && i > 0));
            }
            return rows;
        }

        var scored = new List<PaletteRow>();
        foreach (var c in items)
        {
            double? best = null;
            int[]? titleHits = null, detailHits = null;

            if (FuzzyMatcher.Match(query, c.Title, minPerChar) is { } t)
            {
                best = t.Score + 0.5;
                titleHits = t.Positions;
            }
            if (c.MatchPath && !string.IsNullOrEmpty(c.Detail))
            {
                var full = c.Detail + PathSep + c.Title;
                if (FuzzyMatcher.Match(query, full, minPerChar) is { } p && (best is null || p.Score - 0.4 > best))
                {
                    best = p.Score - 0.4;
                    var split = c.Detail.Length + PathSep.Length;
                    detailHits = p.Positions.Where(i => i < c.Detail.Length).ToArray();
                    titleHits = p.Positions.Where(i => i >= split).Select(i => i - split).ToArray();
                }
            }
            if (c.Extra is not null)
            {
                foreach (var e in c.Extra)
                {
                    if (FuzzyMatcher.Match(query, e, minPerChar) is { } x && (best is null || x.Score - 0.6 > best))
                    {
                        best = x.Score - 0.6;
                        titleHits = null;
                        detailHits = null;
                    }
                }
            }
            if (best is null) continue;
            scored.Add(new PaletteRow(c, titleHits, detailHits, false, false) { Score = best.Value + c.Boost });
        }
        scored.Sort((a, b) =>
        {
            var s = b.Score.CompareTo(a.Score);
            return s != 0 ? s : a.Item.Order.CompareTo(b.Item.Order);
        });
        return scored;
    }
}

/// <summary>Visual of one palette row (recycled by the virtualizing list; rebuilt on DataContext change).</summary>
internal sealed class PaletteRowView : Grid
{
    private static readonly IValueConverter SelectedToOpacity = new BoolToOpacityConverter();

    private readonly Border _bar = new()
    {
        Width = 3,
        Height = 16,
        CornerRadius = new CornerRadius(1.5),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(-9, 0, 0, 0),
        IsHitTestVisible = false,
    };

    private readonly Rectangle _separator = new()
    {
        Height = 1,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(-4, -13, -4, 0),
        IsHitTestVisible = false,
    };

    private readonly Icon _glyph = new() { FontSize = 14, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(1, 0, 0, 0) };
    private readonly Grid _letterBox = new() { Width = 20, Height = 18, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly Rectangle _letterBg = new() { RadiusX = 5, RadiusY = 5, Opacity = 0.16 };
    private readonly TextBlock _letter = new() { FontSize = 10, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
    private readonly StackPanel _right = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly ColumnDefinition _iconColumn = new() { Width = new GridLength(28) };

    public PaletteRowView()
    {
        ColumnDefinitions.Add(_iconColumn);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        MinHeight = 20;

        _bar.SetResourceReference(Border.BackgroundProperty, "Brush.AccentGradient");
        _bar.SetBinding(OpacityProperty, new Binding(nameof(ListBoxItem.IsSelected))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1),
            Converter = SelectedToOpacity,
        });
        _separator.SetResourceReference(Shape.FillProperty, "Brush.PanelSeparator");
        SetColumnSpan(_separator, 3);

        _letterBox.Children.Add(_letterBg);
        _letterBox.Children.Add(_letter);
        _letter.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");

        SetColumn(_text, 1);
        SetColumn(_right, 2);
        Children.Add(_separator);
        Children.Add(_bar);
        Children.Add(_glyph);
        Children.Add(_letterBox);
        Children.Add(_text);
        Children.Add(_right);

        DataContextChanged += (_, _) => Update();
    }

    private void Update()
    {
        if (DataContext is not PaletteRow row) return;
        var c = row.Item;

        _separator.Visibility = row.GroupStart ? Visibility.Visible : Visibility.Collapsed;

        // icon slot: glyph, letter badge or nothing (commands)
        _glyph.Visibility = c.Glyph is null ? Visibility.Collapsed : Visibility.Visible;
        _letterBox.Visibility = c.Letter is null ? Visibility.Collapsed : Visibility.Visible;
        _iconColumn.Width = new GridLength(c.Glyph is null && c.Letter is null ? 0 : 28);
        if (c.Glyph is not null)
        {
            _glyph.Glyph = c.Glyph;
            _glyph.SetResourceReference(TextBlock.ForegroundProperty, c.GlyphBrush);
        }
        if (c.Letter is not null)
        {
            _letter.Text = c.Letter;
            _letter.SetResourceReference(TextBlock.ForegroundProperty, c.LetterBrush);
            _letterBg.SetResourceReference(Shape.FillProperty, c.LetterBrush);
        }

        // title (+ highlighted matches) and dim detail
        _text.Inlines.Clear();
        AddRuns(_text.Inlines, c.Title, row.TitleHits, c.IsInfo ? "Brush.PanelTextDim" : "Brush.PanelText", 13, FontWeights.Normal);
        if (!string.IsNullOrEmpty(c.Detail))
        {
            _text.Inlines.Add(new Run("   ") { FontSize = 11.5 });
            var first = _text.Inlines.Count;
            AddRuns(_text.Inlines, c.Detail, row.DetailHits, "Brush.PanelTextDim", c.DetailMono ? 11 : 11.5, FontWeights.Normal);
            if (c.DetailMono)
                foreach (var run in _text.Inlines.Skip(first)) run.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
        }

        // right side: group label, hint, key chips
        _right.Children.Clear();
        if (row.ShowGroup && c.Group is not null)
        {
            var g = new TextBlock { Text = c.Group, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, c.Gesture is null && c.Hint is null ? 2 : 10, 0) };
            g.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
            g.Opacity = 0.85;
            _right.Children.Add(g);
        }
        if (c.Hint is not null)
        {
            var h = new TextBlock { Text = c.Hint, FontSize = c.HintMono ? 11 : 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) };
            h.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
            if (c.HintMono) h.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
            _right.Children.Add(h);
        }
        if (c.Gesture is { } chord)
            foreach (var part in KeyParts(chord))
                _right.Children.Add(KeyChip(part));
    }

    private static void AddRuns(InlineCollection inlines, string text, int[]? hits, string brush, double size, FontWeight weight)
    {
        if (hits is null || hits.Length == 0)
        {
            var r = new Run(text) { FontSize = size, FontWeight = weight };
            r.SetResourceReference(TextElement.ForegroundProperty, brush);
            inlines.Add(r);
            return;
        }
        var set = new HashSet<int>(hits);
        var i = 0;
        while (i < text.Length)
        {
            var hit = set.Contains(i);
            var j = i + 1;
            while (j < text.Length && set.Contains(j) == hit) j++;
            var r = new Run(text[i..j]) { FontSize = size, FontWeight = hit ? FontWeights.SemiBold : weight };
            r.SetResourceReference(TextElement.ForegroundProperty, hit ? "Brush.AccentText" : brush);
            inlines.Add(r);
            i = j;
        }
    }

    /// <summary>"Ctrl", "Shift", "P" — one chip per key.</summary>
    public static IEnumerable<string> KeyParts(KeyChord chord)
    {
        var m = chord.Modifiers;
        if (m.HasFlag(System.Windows.Input.ModifierKeys.Windows)) yield return "Win";
        if (m.HasFlag(System.Windows.Input.ModifierKeys.Control)) yield return "Ctrl";
        if (m.HasFlag(System.Windows.Input.ModifierKeys.Shift)) yield return "Shift";
        if (m.HasFlag(System.Windows.Input.ModifierKeys.Alt)) yield return "Alt";
        yield return chord.Key switch
        {
            System.Windows.Input.Key.Up => "↑",
            System.Windows.Input.Key.Down => "↓",
            System.Windows.Input.Key.Left => "←",
            System.Windows.Input.Key.Right => "→",
            _ => KeyChord.KeyName(chord.Key),
        };
    }

    public static Border KeyChip(string text)
    {
        var chip = new Border { Child = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } };
        chip.SetResourceReference(StyleProperty, "KeyChip");
        return chip;
    }

    private sealed class BoolToOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? 1.0 : 0.0;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
