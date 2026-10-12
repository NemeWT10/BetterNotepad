using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.UI.Controls;

namespace BetterNotepad.UI;

/// <summary>Building blocks of the settings page. Colors are theme tokens (DynamicResource); styles come from Base.xaml.</summary>
internal static class SettingsUi
{
    public static TextBlock Label(string text, double size = 13, string brush = "Brush.Text", FontWeight? weight = null, bool wrap = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        if (wrap) t.TextWrapping = TextWrapping.Wrap;
        if (weight is { } w) t.FontWeight = w;
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    /// <summary>A line icon; without <paramref name="brush"/> it inherits the parent's foreground.</summary>
    public static Icon Glyph(string glyph, double size = 14, string? brush = "Brush.TextDim")
    {
        var icon = new Icon { Glyph = glyph, FontSize = size };
        if (brush is not null) icon.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return icon;
    }

    public static Button Button(string text, RoutedEventHandler click, string style = "SecondaryButton", string? glyph = null, string? id = null)
    {
        object content = text;
        if (glyph is not null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Icon { Glyph = glyph, FontSize = 12, Margin = new Thickness(0, 0, 8, 0) });
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            content = row;
        }
        var b = new Button { Content = content, VerticalAlignment = VerticalAlignment.Center };
        b.SetResourceReference(FrameworkElement.StyleProperty, style);
        b.Click += click;
        AutomationProperties.SetName(b, text);
        if (id is not null) AutomationProperties.SetAutomationId(b, id);
        return b;
    }

    public static Button IconButton(string glyph, string tip, RoutedEventHandler click, double size = 13, string? id = null)
    {
        var b = new Button { Content = new Icon { Glyph = glyph, FontSize = size }, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
        b.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        b.Click += click;
        AutomationProperties.SetName(b, tip);
        if (id is not null) AutomationProperties.SetAutomationId(b, id);
        return b;
    }

    /// <summary>Small rounded tag (file kinds, license ids, version).</summary>
    public static Border Chip(string text, string background = "Brush.SurfaceHover", string foreground = "Brush.TextDim", bool mono = false, double size = 11)
    {
        var t = Label(text, size, foreground);
        if (mono) t.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
        var b = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 1.5, 7, 2),
            Child = t,
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.SetResourceReference(Border.BackgroundProperty, background);
        return b;
    }

    /// <summary>Key caps of a chord: [Ctrl] [Shift] [P].</summary>
    public static StackPanel KeyChips(KeyChord chord, double size = 11.5) => KeyChips(chord.Parts, size);

    public static StackPanel KeyChips(IEnumerable<string> parts, double size = 11.5)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var part in parts)
        {
            var chip = new Border
            {
                MinWidth = 24,
                Child = new TextBlock { Text = DisplayKey(part), FontSize = size, HorizontalAlignment = HorizontalAlignment.Center },
            };
            chip.SetResourceReference(FrameworkElement.StyleProperty, "KeyChip");
            row.Children.Add(chip);
        }
        return row;
    }

    public static string DisplayKey(string part) => part switch
    {
        "Up" => "↑",
        "Down" => "↓",
        "Left" => "←",
        "Right" => "→",
        _ => part,
    };

    /// <summary>A tinted strip with an icon: info banners, warnings, conflicts (optional buttons on the right).</summary>
    public static Border Notice(string kind, string text, params UIElement[] actions)
    {
        var (bg, fg, glyph) = kind switch
        {
            "warning" => ("Brush.WarningSoft", "Brush.Warning", Glyphs.Warning),
            "danger" => ("Brush.DangerSoft", "Brush.Danger", Glyphs.Error),
            "accent" => ("Brush.AccentSoft", "Brush.AccentText", Glyphs.Info),
            _ => ("Brush.InfoSoft", "Brush.Info", Glyphs.Info),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Glyph(glyph, 14, fg);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, 10, 0);
        grid.Children.Add(icon);
        var label = Label(text, 12.5, "Brush.Text", wrap: true);
        label.LineHeight = 18;
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        if (actions.Length > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, -3, -2, -3), VerticalAlignment = VerticalAlignment.Center };
            foreach (var a in actions)
            {
                if (a is FrameworkElement fe) fe.Margin = new Thickness(6, 0, 0, 0);
                row.Children.Add(a);
            }
            Grid.SetColumn(row, 2);
            grid.Children.Add(row);
        }
        var b = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9), Child = grid };
        b.SetResourceReference(Border.BackgroundProperty, bg);
        return b;
    }

    /// <summary>Compact button for a notice (smaller padding than the dialog buttons).</summary>
    public static Button SmallButton(string text, RoutedEventHandler click, string style = "SecondaryButton", string? id = null)
    {
        var b = Button(text, click, style, id: id);
        b.Padding = new Thickness(11, 4, 11, 5);
        b.FontSize = 12;
        return b;
    }

    /// <summary>Fades an element to a target opacity (instant when animations are off).</summary>
    public static void FadeTo(UIElement el, double to, double ms = 160)
    {
        if (!Motion.I.Enabled)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = to;
            return;
        }
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Motion.EaseOut });
    }

    /// <summary>Enables / disables a dependent row: dimmed, not clickable.</summary>
    public static void SetEnabled(UIElement el, bool enabled)
    {
        if (el.IsEnabled == enabled) return;
        el.IsEnabled = enabled;
        FadeTo(el, enabled ? 1 : 0.45, 140);
    }
}

/// <summary>A rounded group of rows with hairline separators (inset to the text column, like grouped lists).</summary>
internal sealed class SettingsCard : Border
{
    private readonly StackPanel _stack = new();

    public SettingsCard()
    {
        SetResourceReference(StyleProperty, "Card");
        Child = _stack;
    }

    public IEnumerable<FrameworkElement> Rows => _stack.Children.OfType<CardItem>().Select(i => i.Content);

    public T Add<T>(T row) where T : FrameworkElement
    {
        var inset = row is SettingRow { HasIcon: true } or SettingRow { Indented: true } ? 46 : 0;
        _stack.Children.Add(new CardItem(row, inset));
        UpdateSeparators();
        return row;
    }

    /// <summary>Hides the separator above the first visible row (rows can be filtered out).</summary>
    public void UpdateSeparators()
    {
        var first = true;
        foreach (var item in _stack.Children.OfType<CardItem>())
        {
            item.Visibility = item.Content.Visibility;
            if (item.Content.Visibility != Visibility.Visible) continue;
            item.Line.Visibility = first ? Visibility.Collapsed : Visibility.Visible;
            first = false;
        }
    }

    public bool AnyVisible => _stack.Children.OfType<CardItem>().Any(i => i.Content.Visibility == Visibility.Visible);

    private sealed class CardItem : StackPanel
    {
        public CardItem(FrameworkElement content, double inset)
        {
            Content = content;
            Line = new Rectangle { Height = 1, Margin = new Thickness(inset, 0, -18, 0), IsHitTestVisible = false };
            Line.SetResourceReference(Shape.FillProperty, "Brush.Border");
            Children.Add(Line);
            Children.Add(content);
        }

        public FrameworkElement Content { get; }
        public Rectangle Line { get; }
    }
}

/// <summary>One setting: [icon] title + description … control. <see cref="Below"/> holds notices under it.</summary>
internal sealed class SettingRow : StackPanel
{
    private readonly Grid _main = new();

    public SettingRow(string? glyph, string title, string? description, UIElement? control, bool indent = false, double vpad = 13, double minHeight = 36)
    {
        _main.MinHeight = minHeight;
        HasIcon = glyph is not null;
        Indented = indent && glyph is null;
        _main.Margin = new Thickness(Indented ? 46 : 0, vpad, 0, vpad);
        _main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (glyph is not null)
        {
            var box = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(9),
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = SettingsUi.Glyph(glyph, 15),
            };
            box.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceHover");
            IconBox = box;
            _main.Children.Add(box);
        }

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        TitleBlock = SettingsUi.Label(title, 13.5, "Brush.Text", wrap: true);
        texts.Children.Add(TitleBlock);
        DescriptionBlock = SettingsUi.Label(description ?? "", 12, "Brush.TextDim", wrap: true);
        DescriptionBlock.Margin = new Thickness(0, 2, 0, 0);
        DescriptionBlock.LineHeight = 17;
        DescriptionBlock.Visibility = string.IsNullOrEmpty(description) ? Visibility.Collapsed : Visibility.Visible;
        texts.Children.Add(DescriptionBlock);
        Texts = texts;
        Grid.SetColumn(texts, 1);
        _main.Children.Add(texts);

        if (control is not null)
        {
            if (control is FrameworkElement fe)
            {
                fe.Margin = new Thickness(24, 0, 0, 0);
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            Grid.SetColumn(control, 2);
            _main.Children.Add(control);
            Control = control;
            if (control is FrameworkElement named) AutomationProperties.SetName(named, title);
        }

        Children.Add(_main);
        Below = new StackPanel { Margin = new Thickness(HasIcon || Indented ? 46 : 0, 0, 0, 0) };
        Children.Add(Below);
    }

    public bool HasIcon { get; }
    public bool Indented { get; }
    public Border? IconBox { get; }
    public TextBlock TitleBlock { get; }
    public TextBlock DescriptionBlock { get; }
    public StackPanel Texts { get; }
    public UIElement? Control { get; }

    /// <summary>Notices / extra content under the row (warnings, conflicts, previews).</summary>
    public StackPanel Below { get; }

    public void SetDescription(string? text)
    {
        DescriptionBlock.Text = text ?? "";
        DescriptionBlock.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private string? _noticeKey;

    /// <summary>Shows the notice for <paramref name="key"/> (null clears); rebuilt only when the key changes.</summary>
    public void SetNotice(string? key, Func<UIElement> make)
    {
        if (key == _noticeKey && (key is null || Below.Children.Count > 0)) return;
        _noticeKey = key;
        SetNotice(key is null ? null : make());
        _noticeKey = key;
    }

    /// <summary>Replaces the notice under the row (null clears it); notices appear with a small rise.</summary>
    public void SetNotice(UIElement? notice)
    {
        _noticeKey = null;
        Below.Children.Clear();
        if (notice is null) return;
        if (notice is FrameworkElement fe) fe.Margin = new Thickness(0, -4, 0, 13);
        Below.Children.Add(notice);
        Motion.Appear(notice, rise: 6, fromScale: 0.99, ms: 220);
    }

    /// <summary>Dims the row's control and texts (dependent setting whose parent is off).</summary>
    public void SetActive(bool active) => SettingsUi.SetEnabled(_main, active);
}

/// <summary>[−] value [+] for numbers (font size, tab size, seconds…). Hold to repeat; arrow keys when focused.</summary>
internal sealed class Stepper : Border
{
    private readonly Func<double> _get;
    private readonly Action<double> _set;
    private readonly Func<double, string> _format;
    private readonly Func<double, int, double> _next;
    private readonly double _min, _max;
    private readonly TextBlock _text;
    private readonly RepeatButton _minus, _plus;

    public Stepper(string id, double min, double max, Func<double, int, double> next, Func<double> get, Action<double> set, Func<double, string> format, double valueWidth = 64)
    {
        _get = get;
        _set = set;
        _format = format;
        _next = next;
        _min = min;
        _max = max;
        SetResourceReference(StyleProperty, "SegmentHost");
        Focusable = true;
        FocusVisualStyle = null;
        AutomationProperties.SetAutomationId(this, id);

        _minus = Make("\uE738", -1);
        _plus = Make("\uE710", +1);
        _text = SettingsUi.Label("", 13, "Brush.Text", FontWeights.SemiBold);
        _text.MinWidth = valueWidth;
        _text.TextAlignment = TextAlignment.Center;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_minus);
        row.Children.Add(_text);
        row.Children.Add(_plus);
        Child = row;

        GotKeyboardFocus += (_, _) => SetResourceReference(BorderBrushProperty, "Brush.Accent");
        LostKeyboardFocus += (_, _) => SetResourceReference(BorderBrushProperty, "Brush.Border");
        KeyDown += (_, e) =>
        {
            var dir = e.Key switch { Key.Up or Key.Right or Key.Add or Key.OemPlus => 1, Key.Down or Key.Left or Key.Subtract or Key.OemMinus => -1, _ => 0 };
            if (dir == 0) return;
            e.Handled = true;
            Step(dir);
        };
        Refresh();
    }

    private RepeatButton Make(string glyph, int dir)
    {
        var b = new RepeatButton { Content = new Icon { Glyph = glyph, FontSize = 10 }, Width = 28, Height = 28, Delay = 380, Interval = 70 };
        b.SetResourceReference(StyleProperty, "IconButton");
        b.Click += (_, _) => Step(dir);
        return b;
    }

    public static Func<double, int, double> Linear(double step) => (v, d) => v + d * step;

    /// <summary>Steps through preset values (a value between presets snaps to the neighbour).</summary>
    public static Func<double, int, double> Presets(params double[] values) => (v, d) =>
    {
        if (d > 0) return values.FirstOrDefault(x => x > v + 1e-9, values[^1]);
        return values.LastOrDefault(x => x < v - 1e-9, values[0]);
    };

    public double Value => _get();

    public void Step(int dir)
    {
        var v = Math.Clamp(_next(_get(), dir), _min, _max);
        if (Math.Abs(v - _get()) < 1e-9) return;
        _set(v);
        Refresh();
        Motion.Bounce(_text, 1.14);
    }

    public void Refresh()
    {
        var v = _get();
        _text.Text = _format(v);
        _minus.IsEnabled = v > _min + 1e-9 && IsEnabled;
        _plus.IsEnabled = v < _max - 1e-9 && IsEnabled;
    }
}

/// <summary>
/// A shortcut as key caps; click to record a new one. While recording it shows "Press keys…" with the
/// modifiers held so far and a softly pulsing dot.
/// </summary>
internal sealed class ChordBox : Border
{
    private readonly StackPanel _live = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

    public ChordBox(string id)
    {
        Cursor = Cursors.Hand;
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(6, 4, 6, 4);
        MinHeight = 30;
        BorderThickness = new Thickness(1);
        Background = Brushes.Transparent;
        BorderBrush = Brushes.Transparent;
        HorizontalAlignment = HorizontalAlignment.Right;
        AutomationProperties.SetAutomationId(this, id);
        MouseEnter += (_, _) => { if (!IsCapturing) SetResourceReference(BorderBrushProperty, "Brush.BorderStrong"); };
        MouseLeave += (_, _) => { if (!IsCapturing) BorderBrush = Brushes.Transparent; };
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Activated?.Invoke();
        };
    }

    public event Action? Activated;

    public bool IsCapturing { get; private set; }

    public void ShowChord(KeyChord? chord, string emptyText, bool warn = false)
    {
        IsCapturing = false;
        ClearValue(BackgroundProperty);
        Background = Brushes.Transparent;
        BorderBrush = IsMouseOver ? (Brush)FindResource("Brush.BorderStrong") : Brushes.Transparent;
        if (warn) SetResourceReference(BorderBrushProperty, "Brush.Warning");
        Child = chord is { } c ? SettingsUi.KeyChips(c) : SettingsUi.Label(emptyText, 12, "Brush.TextFaint");
    }

    public void BeginCapture(string prompt)
    {
        IsCapturing = true;
        SetResourceReference(BackgroundProperty, "Brush.AccentSoft");
        SetResourceReference(BorderBrushProperty, "Brush.Accent");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 0) };
        var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, "Brush.Accent");
        Motion.WhileVisible(dot,
            () => dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(650))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = Motion.EaseOut,
            }),
            () => dot.BeginAnimation(OpacityProperty, null));
        row.Children.Add(dot);
        row.Children.Add(SettingsUi.Label(prompt, 12, "Brush.AccentText"));
        _live.Children.Clear();
        _live.Margin = new Thickness(8, 0, 0, 0);
        (_live.Parent as Panel)?.Children.Remove(_live); // still inside the previous recording's row
        row.Children.Add(_live);
        Child = row;
        Motion.Bounce(this, 1.05);
    }

    /// <summary>Shows the modifiers held right now while recording.</summary>
    public void ShowLive(ModifierKeys mods)
    {
        if (!IsCapturing) return;
        _live.Children.Clear();
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (parts.Count > 0) _live.Children.Add(SettingsUi.KeyChips(parts));
    }

    public void Shake() => Motion.Shake(this);
}
