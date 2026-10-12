using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.UI.Controls;
using Glyphs = BetterNotepad.UI.Controls.Glyphs;
using BetterNotepad.UI.Themes;

namespace BetterNotepad.UI;

/// <summary>Appearance page: theme cards with live mini previews, Mica.</summary>
public sealed partial class SettingsPage
{
    private readonly List<ThemeCard> _themeCards = new();

    private void BuildAppearance(StackPanel panel)
    {
        var a = Section<AppearanceSettings>();

        var title = new TextBlock { Text = Loc.T("settings.appearance.themeGroup") };
        title.SetResourceReference(StyleProperty, "SectionTitle");
        panel.Children.Add(title);

        var grid = new UniformGrid { Columns = 3, Margin = new Thickness(-6, -6, -6, 8) };
        grid.SizeChanged += (_, e) => grid.Columns = e.NewSize.Width < 600 ? 2 : 3;
        var group = "settings-theme-" + GetHashCode();
        foreach (var id in new[] { "system" }.Concat(Palettes.All.Select(p => p.Id)))
        {
            var card = new ThemeCard(id, group) { Margin = new Thickness(6) };
            card.Checked += (_, _) =>
            {
                if (!_syncing) SelectTheme(card.ThemeId);
            };
            grid.Children.Add(card);
            _themeCards.Add(card);
        }
        panel.Children.Add(grid);
        Bind(UpdateThemeSelection);

        var window = Group(panel, Loc.T("settings.appearance.windowGroup"));
        var supported = Environment.OSVersion.Version.Build >= 22621;
        var mica = window.Add(new SettingRow("\uE771", Loc.T("settings.appearance.mica"),
            Loc.T(supported ? "settings.appearance.micaDesc" : "settings.appearance.micaUnsupported"),
            Switch("appearance.mica", () => a.Mica, v => a.Mica = v)));
        if (!supported) mica.SetActive(false);
    }

    /// <summary>Switches the theme live (the whole app, this page included, recolors at once).</summary>
    internal void SelectTheme(string id) => Section<AppearanceSettings>().Theme = id;

    private void UpdateThemeSelection()
    {
        var current = Section<AppearanceSettings>().Theme;
        var was = _syncing;
        _syncing = true;
        try
        {
            foreach (var c in _themeCards) c.SetSelected(c.ThemeId == current);
        }
        finally
        {
            _syncing = was;
        }
    }
}

/// <summary>
/// A theme to pick: a miniature window in that theme's own colors (chrome with a tab, editor with three
/// highlighted lines, accent pill), its name, and a check that bounces in when it becomes active.
/// </summary>
internal sealed class ThemeCard : RadioButton
{
    private static readonly ControlTemplate PresenterTemplate = new(typeof(RadioButton))
    {
        VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)),
    };

    private readonly Border _frame;
    private readonly Border _ring;
    private readonly Grid _badge;
    private bool? _selected;

    public ThemeCard(string id, string group)
    {
        ThemeId = id;
        GroupName = group;
        Cursor = Cursors.Hand;
        FocusVisualStyle = null;
        Template = PresenterTemplate;
        var palette = id == "system" ? null : Palettes.ById(id);
        var name = palette is null ? Loc.T("settings.appearance.system") : Loc.T(palette.NameKey);
        AutomationProperties.SetAutomationId(this, "appearance.theme." + id);
        AutomationProperties.SetName(this, name);

        // preview, clipped to rounded corners
        var preview = palette is null ? SystemPreview() : MiniPreview(palette);
        var previewHost = new Grid { Height = 108, Children = { preview } };
        previewHost.SizeChanged += (_, e) => previewHost.Clip = new RectangleGeometry(new Rect(e.NewSize), 9, 9);
        var previewEdge = new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), IsHitTestVisible = false };
        previewEdge.BorderBrush = Frozen(Color.FromArgb(0x1F, 0x80, 0x80, 0x90));
        var previewStack = new Grid { Children = { previewHost, previewEdge } };

        // footer: name, light/dark, check badge
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(SettingsUi.Label(name, 13, "Brush.Text", FontWeights.SemiBold));
        var sub = palette is null
            ? Loc.T("settings.appearance.systemSub", Loc.T(Palettes.Daylight.NameKey), Loc.T(Palettes.Aurora.NameKey))
            : Loc.T(palette.IsDark ? "settings.appearance.dark" : "settings.appearance.light");
        var subText = SettingsUi.Label(sub, 11.5, "Brush.TextFaint");
        subText.Margin = new Thickness(0, 1, 0, 0);
        subText.TextTrimming = TextTrimming.CharacterEllipsis;
        texts.Children.Add(subText);

        var circle = new Ellipse();
        circle.SetResourceReference(Shape.FillProperty, "Brush.AccentGradient");
        var check = SettingsUi.Glyph(Glyphs.Checkmark, 10, "Brush.OnAccent");
        check.FontWeight = FontWeights.Bold;
        _badge = new Grid { Width = 20, Height = 20, Opacity = 0, VerticalAlignment = VerticalAlignment.Center, Children = { circle, check } };

        var footer = new Grid { Margin = new Thickness(4, 10, 2, 1) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(texts);
        Grid.SetColumn(_badge, 1);
        footer.Children.Add(_badge);

        var content = new StackPanel();
        content.Children.Add(previewStack);
        content.Children.Add(footer);

        _frame = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(8), Child = content };
        _frame.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        _frame.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        _ring = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(2), Opacity = 0, IsHitTestVisible = false };
        _ring.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");

        var root = new Grid { Children = { _frame, _ring } };
        Motion.SetHoverLift(root, 2);
        Content = root;

        MouseEnter += (_, _) => UpdateEdge();
        MouseLeave += (_, _) => UpdateEdge();
        GotKeyboardFocus += (_, _) => UpdateEdge();
        LostKeyboardFocus += (_, _) => UpdateEdge();
    }

    public string ThemeId { get; }

    public void SetSelected(bool selected)
    {
        var changed = _selected != selected;
        var first = _selected is null;
        _selected = selected;
        IsChecked = selected;
        if (!changed) return;
        if (first)
        {
            _ring.Opacity = selected ? 1 : 0;
            _badge.Opacity = selected ? 1 : 0;
        }
        else
        {
            SettingsUi.FadeTo(_ring, selected ? 1 : 0, 180);
            SettingsUi.FadeTo(_badge, selected ? 1 : 0, 140);
            if (selected) Motion.Bounce(_badge, 1.35);
        }
        UpdateEdge();
    }

    private void UpdateEdge()
    {
        _frame.SetResourceReference(Border.BorderBrushProperty, IsMouseOver || IsKeyboardFocused ? "Brush.BorderStrong" : "Brush.Border");
        if (_selected != true) SettingsUi.FadeTo(_ring, IsKeyboardFocused ? 0.45 : 0, 120);
    }

    // ───────────── miniature window in a palette's own colors ─────────────

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static LinearGradientBrush Gradient(Color[] stops)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        for (var i = 0; i < stops.Length; i++) g.GradientStops.Add(new GradientStop(stops[i], stops.Length == 1 ? 0 : (double)i / (stops.Length - 1)));
        g.Freeze();
        return g;
    }

    private static Border Bar(Color c, double width, double height = 3) => new()
    {
        Width = width,
        Height = height,
        CornerRadius = new CornerRadius(height / 2),
        Background = Frozen(c),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    internal static FrameworkElement MiniPreview(ThemePalette p)
    {
        var s = p.Syntax;
        var root = new Grid { Background = Frozen(p.Bg), SnapsToDevicePixels = true };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // chrome: an active tab with its accent line, an inactive tab, the palette pill
        var chrome = new Grid { Background = Frozen(p.ChromeSolid) };
        var tab = new Grid
        {
            Width = 56,
            Height = 14,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 0, 0),
        };
        tab.Children.Add(new Border { CornerRadius = new CornerRadius(5, 5, 0, 0), Background = Frozen(p.TabActive) });
        tab.Children.Add(new Border
        {
            Height = 1.6,
            CornerRadius = new CornerRadius(1),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(7, 0, 7, 0),
            Background = Gradient(p.AccentGradient),
        });
        var tabText = Bar(p.TextDim, 26, 2.6);
        tabText.Margin = new Thickness(9, 1, 0, 0);
        tab.Children.Add(tabText);
        chrome.Children.Add(tab);
        var inactive = Bar(p.TextFaint, 22, 2.6);
        inactive.Margin = new Thickness(76, 4, 0, 0);
        chrome.Children.Add(inactive);
        chrome.Children.Add(new Border
        {
            Width = 58,
            Height = 9,
            CornerRadius = new CornerRadius(3),
            Background = Frozen(p.BgAlt),
            BorderBrush = Frozen(p.Border),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        root.Children.Add(chrome);

        // editor: gutter + highlighted lines (a selection on line 1, the caret on the current line 2), minimap
        var editor = new Grid { Background = Frozen(p.EditorBg) };
        Grid.SetRow(editor, 1);
        var lines = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        (string Text, Color Color, bool Bold, bool Italic)[][] code =
        [
            [("fn", s.Keyword, s.BoldKeywords, false), (" ", s.Variable, false, false), ("greet", s.Function, false, false), ("(", s.Punctuation, false, false), ("name", s.Variable, false, false), (") {", s.Punctuation, false, false)],
            [("  let", s.Keyword, s.BoldKeywords, false), (" n ", s.Variable, false, false), ("=", s.Operator, false, false), (" ", s.Variable, false, false), ("42", s.Number, false, false), (";", s.Punctuation, false, false)],
            [("  say", s.Function, false, false), ("(", s.Punctuation, false, false), ("\"hi\"", s.String, false, false), (");", s.Punctuation, false, false), (" // ok", s.Comment, false, s.ItalicComments)],
            [("}", s.Punctuation, false, false)],
        ];
        for (var i = 0; i < code.Length; i++)
        {
            var current = i == 1;
            var line = new Grid { Height = 16, Background = current ? Frozen(p.CurrentLine) : Brushes.Transparent };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var number = new TextBlock
            {
                Text = (i + 1).ToString(),
                FontSize = 9,
                Foreground = Frozen(current ? p.LineNumberActive : p.LineNumber),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0),
            };
            number.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
            line.Children.Add(number);
            var text = new TextBlock { FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
            foreach (var (t, c, bold, italic) in code[i])
                text.Inlines.Add(new Run(t)
                {
                    Foreground = Frozen(c),
                    FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                    FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
                    Background = i == 0 && t == "greet" ? Frozen(p.Selection) : null,
                });
            if (current) text.Inlines.Add(new InlineUIContainer(new Border { Width = 1.3, Height = 11, Background = Frozen(p.Caret), Margin = new Thickness(1, 0, 0, -2) }));
            Grid.SetColumn(text, 1);
            line.Children.Add(text);
            lines.Children.Add(line);
        }
        editor.Children.Add(lines);

        // minimap: tiny bars in the syntax colors under a viewport band
        var minimap = new Grid { Width = 26, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 9, 0) };
        minimap.Children.Add(new Border { Height = 18, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(2), Background = Frozen(ThemeManager.WithAlpha(p.Text, p.IsDark ? (byte)0x14 : (byte)0x10)) });
        var bars = new StackPanel { Margin = new Thickness(3, 2, 0, 0) };
        (Color Color, double Width, double Indent)[] rows =
        [
            (s.Keyword, 9, 0), (s.Function, 14, 3), (s.String, 11, 3), (s.Punctuation, 3, 0), (s.Comment, 16, 0),
            (s.Keyword, 7, 0), (s.Variable, 12, 3), (s.Number, 6, 6), (s.Punctuation, 3, 0),
        ];
        foreach (var (c, width, indent) in rows)
        {
            var bar = Bar(ThemeManager.WithAlpha(c, 0xB0), width, 1.6);
            bar.Margin = new Thickness(indent, 0, 0, 1.6);
            bars.Children.Add(bar);
        }
        minimap.Children.Add(bars);
        editor.Children.Add(minimap);

        // accent pill (a primary button in that theme)
        var pill = new Grid
        {
            Width = 38,
            Height = 13,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 10, 9),
        };
        pill.Children.Add(new Border { CornerRadius = new CornerRadius(6.5), Background = Gradient(p.AccentGradient) });
        var label = Bar(p.OnAccent, 16, 2.4);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        pill.Children.Add(label);
        editor.Children.Add(pill);
        root.Children.Add(editor);
        return root;
    }

    /// <summary>"Like the system": Daylight and Aurora split by a diagonal.</summary>
    private static FrameworkElement SystemPreview()
    {
        var dark = MiniPreview(Palettes.Aurora);
        var light = MiniPreview(Palettes.Daylight);
        var seam = new Line { Stroke = Frozen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1, IsHitTestVisible = false };
        var grid = new Grid { Children = { dark, light, seam } };
        grid.SizeChanged += (_, e) =>
        {
            var w = e.NewSize.Width;
            var h = e.NewSize.Height;
            var figure = new PathFigure { StartPoint = new Point(0, 0), IsClosed = true };
            figure.Segments.Add(new LineSegment(new Point(w * 0.62, 0), false));
            figure.Segments.Add(new LineSegment(new Point(w * 0.40, h), false));
            figure.Segments.Add(new LineSegment(new Point(0, h), false));
            var clip = new PathGeometry { Figures = { figure } };
            clip.Freeze();
            light.Clip = clip;
            seam.X1 = w * 0.62;
            seam.Y1 = 0;
            seam.X2 = w * 0.40;
            seam.Y2 = h;
        };
        return grid;
    }
}
