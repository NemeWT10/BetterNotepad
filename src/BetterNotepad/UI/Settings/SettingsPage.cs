using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.UI.Controls;
using Glyphs = BetterNotepad.UI.Controls.Glyphs;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.UI;

/// <summary>Ids of the settings pages (<see cref="SettingsPage.Show"/>, <c>MainWindow.ShowSettings(page)</c>).</summary>
public static class SettingsPageIds
{
    public const string General = "general";
    public const string Appearance = "appearance";
    public const string Editor = "editor";
    public const string Files = "files";
    public const string Shortcuts = "shortcuts";
    public const string System = "system";
    public const string Updates = "updates";
    public const string About = "about";
}

/// <summary>
/// Settings overlay over the window content: section list on the left, scrollable cards of rows on the right.
/// Every change applies live and is persisted by <see cref="SettingsService"/>. Esc or the back button closes it.
/// Contract: <see cref="Show"/> opens it in a window, optionally at a page (<see cref="SettingsPageIds"/>).
/// </summary>
public sealed partial class SettingsPage : Grid
{
    internal static readonly (string Id, string Glyph)[] Pages =
    [
        (SettingsPageIds.General, "\uE713"),
        (SettingsPageIds.Appearance, "\uE790"),
        (SettingsPageIds.Editor, "\uE943"),
        (SettingsPageIds.Files, "\uE8A5"),
        (SettingsPageIds.Shortcuts, "\uE765"),
        (SettingsPageIds.System, "\uE770"),
        (SettingsPageIds.Updates, "\uE895"),
        (SettingsPageIds.About, "\uE946"),
    ];

    private const double NavWidth = 248;
    private const double NavCompactWidth = 64;
    private const double ContentMaxWidth = 880;

    private static string _lastPage = SettingsPageIds.General;

    private readonly MainWindow _window;
    private readonly DockPanel _navDock = new();
    private readonly ScrollViewer _scroll;
    private readonly Border _host = new();
    private readonly Dictionary<string, (RadioButton Item, Icon Icon, TextBlock Text)> _navItems = new(StringComparer.Ordinal);
    private readonly List<Action> _refresh = new();
    private readonly List<FrameworkElement> _navLabels = new();
    private string _page;
    private bool _syncing;
    private bool _closing;
    private bool _compact;

    private SettingsPage(MainWindow window, string page)
    {
        _window = window;
        _page = page;
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SetResourceReference(BackgroundProperty, "Brush.Bg");
        SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");
        SetResourceReference(TextElement.FontFamilyProperty, "Font.Ui");
        AutomationProperties.SetAutomationId(this, "settings");
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NavWidth) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var navPane = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Child = _navDock };
        navPane.SetResourceReference(Border.BackgroundProperty, "Brush.BgAlt");
        navPane.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        _navDock.Margin = new Thickness(12, 14, 12, 14);
        _navDock.LastChildFill = true;
        Children.Add(navPane);

        _host.Padding = new Thickness(44, 30, 44, 56);
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = _host,
        };
        Grid.SetColumn(_scroll, 1);
        Children.Add(_scroll);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        KeyDown += OnKeyDown;
        PreviewKeyUp += OnCapturePreviewKeyUp;
        PreviewMouseDown += (_, e) =>
        {
            // a click anywhere but on the recorder stops recording
            if (_capture is { } c && e.OriginalSource is DependencyObject d && !IsInside(c.Box, d)) EndCapture();
            if (!IsKeyboardFocusWithin) Focus();
        };
        SizeChanged += (_, _) => UpdateCompact();

        BuildNav();
        ShowPage(page, animate: false);
    }

    // ───────────── contract ─────────────

    /// <summary>Opens the settings in a window (or switches the open one to <paramref name="page"/>).</summary>
    public static void Show(MainWindow window, string? page = null)
    {
        var id = Normalize(page);
        if (For(window) is { } open)
        {
            if (id is not null) open.Navigate(id);
            return;
        }
        var p = new SettingsPage(window, id ?? _lastPage);
        window.OverlayLayer.Children.Add(p);
        p.Open();
    }

    /// <summary>The settings page open in a window, if any.</summary>
    public static SettingsPage? For(MainWindow window)
        => window.OverlayLayer.Children.OfType<SettingsPage>().FirstOrDefault(p => !p._closing);

    public static bool IsOpenIn(MainWindow window) => For(window) is not null;

    public string CurrentPage => _page;

    private static string? Normalize(string? page)
    {
        if (string.IsNullOrWhiteSpace(page)) return null;
        var p = page.Trim().ToLowerInvariant();
        p = p switch
        {
            "keys" or "keymap" or "keyboard" or "keybindings" => SettingsPageIds.Shortcuts,
            "theme" or "themes" => SettingsPageIds.Appearance,
            "update" => SettingsPageIds.Updates,
            _ => p,
        };
        return Pages.Any(x => x.Id == p) ? p : null;
    }

    private void Open()
    {
        Motion.SlideIn(this, fromX: 28, ms: 320);
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing) return;
            if (_navItems.TryGetValue(_page, out var nav)) nav.Item.Focus();
            else Focus();
        }, DispatcherPriority.Input);
    }

    /// <summary>Closes the page (slides out); the active tab gets the keyboard back.</summary>
    public void Close()
    {
        if (_closing) return;
        EndCapture();
        _closing = true;
        IsHitTestVisible = false;

        void Remove()
        {
            _window.OverlayLayer.Children.Remove(this);
            if (!_window.OverlayLayer.IsKeyboardFocusWithin) _window.ActiveView?.FocusContent();
        }

        if (!Motion.I.Enabled)
        {
            Remove();
            return;
        }
        var (_, t) = Motion.Transforms(this);
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170)) { EasingFunction = Motion.EaseOut };
        fade.Completed += (_, _) => Remove();
        BeginAnimation(OpacityProperty, fade);
        t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(22, TimeSpan.FromMilliseconds(170)) { EasingFunction = Motion.EaseOut });
    }

    public void Navigate(string? page)
    {
        if (Normalize(page) is not { } id) return;
        if (_navItems.TryGetValue(id, out var nav) && nav.Item.IsChecked != true)
        {
            _syncing = true;
            nav.Item.IsChecked = true;
            _syncing = false;
        }
        if (id == _page && _host.Child is not null) return;
        ShowPage(id, animate: true);
        if (_navItems.TryGetValue(id, out nav)) Motion.Bounce(nav.Icon, 1.2);
    }

    // ───────────── lifetime ─────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SettingsService.I.Changed += OnSettingsChanged;
        Loc.LanguageChanged += OnLanguageChanged;
        UpdateCompact();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SettingsService.I.Changed -= OnSettingsChanged;
        Loc.LanguageChanged -= OnLanguageChanged;
        EndCapture();
    }

    /// <summary>Values changed (here, by a command, by an import): controls re-read them.</summary>
    private void OnSettingsChanged(SettingsSection section, string? property)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            foreach (var r in _refresh.ToArray()) r();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Texts are built in code: rebuild in the new language (later — the click may come from this very page).</summary>
    private void OnLanguageChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (_closing) return;
        var offset = _scroll.VerticalOffset;
        BuildNav();
        ShowPage(_page, animate: false);
        _scroll.UpdateLayout();
        _scroll.ScrollToVerticalOffset(offset);
    }, DispatcherPriority.Background);

    // ───────────── navigation pane ─────────────

    private void BuildNav()
    {
        _navDock.Children.Clear();
        _navItems.Clear();
        _navLabels.Clear();

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 18), LastChildFill = true };
        var back = SettingsUi.IconButton("\uE72B", Loc.T("settings.back"), (_, _) => Close(), 13, "settings.back");
        back.Margin = new Thickness(2, 0, 8, 0);
        DockPanel.SetDock(back, Dock.Left);
        head.Children.Add(back);
        var title = SettingsUi.Label(Loc.T("settings.title"), 18, "Brush.Text", FontWeights.SemiBold);
        title.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        head.Children.Add(title);
        _navLabels.Add(title);
        DockPanel.SetDock(head, Dock.Top);
        _navDock.Children.Add(head);

        var foot = new StackPanel { Margin = new Thickness(12, 12, 0, 2) };
        var esc = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-2, 0, 0, 8) };
        esc.Children.Add(SettingsUi.KeyChips(["Esc"], 10.5));
        esc.Children.Add(SettingsUi.Label(Loc.T("settings.escCloses"), 11.5, "Brush.TextFaint"));
        ((FrameworkElement)esc.Children[1]).Margin = new Thickness(6, 0, 0, 0);
        foot.Children.Add(esc);
        foot.Children.Add(SettingsUi.Label($"{AppInfo.ProductName} {AppInfo.Version}", 11.5, "Brush.TextFaint"));
        _navLabels.Add(foot);
        DockPanel.SetDock(foot, Dock.Bottom);
        _navDock.Children.Add(foot);

        var list = new StackPanel();
        var group = "settings-nav-" + GetHashCode();
        foreach (var (id, glyph) in Pages)
        {
            var icon = new Icon { Glyph = glyph, FontSize = 15, Margin = new Thickness(1, 0, 13, 0) };
            var text = new TextBlock { Text = Loc.T("settings.page." + id), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(icon);
            content.Children.Add(text);
            var item = new RadioButton { Content = content, GroupName = group, IsChecked = id == _page, ToolTip = null };
            item.SetResourceReference(StyleProperty, "NavItem");
            AutomationProperties.SetAutomationId(item, "settings.nav." + id);
            AutomationProperties.SetName(item, text.Text);
            var target = id;
            item.Checked += (_, _) =>
            {
                icon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
                if (!_syncing) Navigate(target);
            };
            item.Unchecked += (_, _) => icon.ClearValue(TextBlock.ForegroundProperty);
            if (id == _page) icon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
            list.Children.Add(item);
            _navItems[id] = (item, icon, text);
            _navLabels.Add(text);
        }
        _navDock.Children.Add(list);
        ApplyCompact();
    }

    /// <summary>Narrow windows: the section list shrinks to icons (names in tooltips).</summary>
    private void UpdateCompact()
    {
        var compact = ActualWidth > 0 && ActualWidth < 820;
        if (compact == _compact) return;
        _compact = compact;
        ApplyCompact();
    }

    private void ApplyCompact()
    {
        ColumnDefinitions[0].Width = new GridLength(_compact ? NavCompactWidth : NavWidth);
        _navDock.Margin = _compact ? new Thickness(8, 14, 8, 14) : new Thickness(12, 14, 12, 14);
        _host.Padding = _compact ? new Thickness(24, 24, 24, 48) : new Thickness(44, 30, 44, 56);
        foreach (var l in _navLabels) l.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (item, _, text) in _navItems.Values) item.ToolTip = _compact ? text.Text : null;
    }

    // ───────────── pages ─────────────

    private void ShowPage(string id, bool animate)
    {
        EndCapture();
        _page = id;
        _lastPage = id;
        _refresh.Clear();
        ResetPageState();

        var panel = new StackPanel { MaxWidth = ContentMaxWidth };
        AutomationProperties.SetAutomationId(panel, "settings.page." + id);
        var title = new TextBlock { Text = Loc.T("settings.page." + id) };
        title.SetResourceReference(StyleProperty, "PageTitle");
        title.Margin = new Thickness(2, 0, 0, 4);
        panel.Children.Add(title);
        var subtitle = SettingsUi.Label(Loc.T("settings.page." + id + ".sub"), 13, "Brush.TextDim", wrap: true);
        subtitle.Margin = new Thickness(2, 0, 0, 26);
        panel.Children.Add(subtitle);

        switch (id)
        {
            case SettingsPageIds.General: BuildGeneral(panel); break;
            case SettingsPageIds.Appearance: BuildAppearance(panel); break;
            case SettingsPageIds.Editor: BuildEditor(panel); break;
            case SettingsPageIds.Files: BuildFiles(panel); break;
            case SettingsPageIds.Shortcuts: BuildShortcuts(panel); break;
            case SettingsPageIds.System: BuildSystem(panel); break;
            case SettingsPageIds.Updates: BuildUpdates(panel); break;
            case SettingsPageIds.About: BuildAbout(panel); break;
        }

        _host.Child = panel;
        _scroll.ScrollToVerticalOffset(0);
        if (animate) Motion.Appear(panel, rise: 10, fromScale: 1, ms: 260);
    }

    /// <summary>Per-page state that a rebuild throws away.</summary>
    private void ResetPageState()
    {
        _themeCards.Clear();
        _shortcutRows.Clear();
        _shortcutGroups.Clear();
        _shortcutSearch = null;
        _shortcutEmpty = null;
        _modifiedLabel = null;
        _resetAllButton = null;
        _hotkeyRecorders.Clear();
        _pending = null;
        LastWarning = null;
    }

    /// <summary>Group heading + card.</summary>
    private static SettingsCard Group(Panel panel, string? title)
    {
        if (title is not null)
        {
            var t = new TextBlock { Text = title };
            t.SetResourceReference(StyleProperty, "SectionTitle");
            if (panel.Children.OfType<SettingsCard>().Any()) t.Margin = new Thickness(4, 10, 0, 8);
            panel.Children.Add(t);
        }
        var card = new SettingsCard();
        panel.Children.Add(card);
        return card;
    }

    // ───────────── bound controls ─────────────

    private CheckBox Switch(string id, Func<bool> get, Action<bool> set)
    {
        var cb = new CheckBox { IsChecked = get() };
        cb.SetResourceReference(StyleProperty, "Switch");
        AutomationProperties.SetAutomationId(cb, id);
        cb.Checked += (_, _) => { if (!_syncing) set(true); };
        cb.Unchecked += (_, _) => { if (!_syncing) set(false); };
        _refresh.Add(() => cb.IsChecked = get());
        return cb;
    }

    private Border Segment(string id, IReadOnlyList<(string Value, string Label)> options, Func<string> get, Action<string> set)
    {
        var host = new Border();
        host.SetResourceReference(StyleProperty, "SegmentHost");
        AutomationProperties.SetAutomationId(host, id);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var group = id + "-" + Guid.NewGuid().ToString("N");
        var buttons = new List<(RadioButton Button, string Value)>();
        foreach (var (value, label) in options)
        {
            var rb = new RadioButton { Content = label, GroupName = group, IsChecked = get() == value };
            rb.SetResourceReference(StyleProperty, "Segment");
            AutomationProperties.SetAutomationId(rb, id + "." + value);
            AutomationProperties.SetName(rb, label);
            var v = value;
            rb.Checked += (_, _) => { if (!_syncing) set(v); };
            row.Children.Add(rb);
            buttons.Add((rb, value));
        }
        _refresh.Add(() =>
        {
            var current = get();
            foreach (var (b, v) in buttons) b.IsChecked = v == current;
        });
        host.Child = row;
        return host;
    }

    private Stepper Number(string id, double min, double max, Func<double, int, double> next, Func<double> get, Action<double> set, Func<double, string> format, double width = 64)
    {
        var s = new Stepper(id, min, max, next, get, set, format, width);
        _refresh.Add(s.Refresh);
        return s;
    }

    private ComboBox Choice(string id, IReadOnlyList<(string Value, string Label)> options, Func<string> get, Action<string> set, double width = 200)
    {
        var combo = new ComboBox { Width = width };
        combo.SetResourceReference(StyleProperty, "Combo");
        AutomationProperties.SetAutomationId(combo, id);
        foreach (var (_, label) in options) combo.Items.Add(label);
        int IndexOf(string v)
        {
            for (var i = 0; i < options.Count; i++)
                if (string.Equals(options[i].Value, v, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }
        combo.SelectedIndex = IndexOf(get());
        combo.SelectionChanged += (_, _) =>
        {
            if (!_syncing && combo.SelectedIndex >= 0) set(options[combo.SelectedIndex].Value);
        };
        _refresh.Add(() => combo.SelectedIndex = IndexOf(get()));
        return combo;
    }

    /// <summary>Runs <paramref name="update"/> now and whenever settings change.</summary>
    private void Bind(Action update)
    {
        update();
        _refresh.Add(update);
    }

    private static T Section<T>() where T : SettingsSection, ISettingsSection, new() => SettingsService.I.Get<T>();

    // ───────────── keyboard ─────────────

    /// <summary>
    /// Called by the window before its own shortcuts. While the page is open no shortcut runs behind it
    /// (except the palette and Ctrl+, which closes the page); keys go to the page's controls instead,
    /// and while a shortcut is being recorded every key belongs to the recorder.
    /// </summary>
    internal static bool InterceptKey(MainWindow window, KeyEventArgs e)
    {
        if (For(window) is not { } page) return false;
        var focus = Keyboard.FocusedElement as DependencyObject;
        var inPage = focus is not null && IsInside(page, focus);
        // another overlay (command palette…) owns the keyboard: leave it alone
        if (!inPage && focus is not null && IsInside(window.OverlayLayer, focus)) return false;
        // the keyboard is on something hidden behind the page (the editor…) — the title-bar menu stays usable
        var behind = focus is null || IsInside(window.GroupsHost, focus) || IsInside(window.Status, focus);
        return page.OnWindowKey(e, behind);
    }

    private bool OnWindowKey(KeyEventArgs e, bool behind)
    {
        var chord = KeyChord.FromEvent(e);
        if (_capture is not null)
        {
            HandleCaptureKey(e, chord);
            return true;
        }
        if (chord.IsModifierOnly) return true;
        if (chord == Keymap.GestureFor("app.settings"))
        {
            e.Handled = true;
            Close();
            return true;
        }
        if (chord == Keymap.GestureFor("palette.commands") || chord == Keymap.GestureFor("palette.commandsAlt") || chord == Keymap.GestureFor("palette.files"))
            return false; // the palette opens above the page
        if (behind)
        {
            // never type into something the page covers: take the keyboard back instead
            e.Handled = true;
            Focus();
            return true;
        }
        if (chord is { Modifiers: ModifierKeys.Control, Key: Key.F } && _shortcutSearch is not null)
        {
            e.Handled = true;
            _shortcutSearch.Focus();
            _shortcutSearch.SelectAll();
        }
        return true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        if (_capture is not null)
        {
            EndCapture();
            return;
        }
        if (_shortcutSearch is { IsKeyboardFocusWithin: true, Text.Length: > 0 })
        {
            _shortcutSearch.Clear();
            return;
        }
        Close();
    }

    private static bool IsInside(DependencyObject ancestor, DependencyObject node)
    {
        for (DependencyObject? d = node; d is not null; d = ParentOf(d))
            if (ReferenceEquals(d, ancestor)) return true;
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject d)
        => d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    // ───────────── test / automation helpers ─────────────

    /// <summary>Finds a control of the current page by its automation id.</summary>
    internal T? FindById<T>(string id) where T : DependencyObject => Find<T>(this, id);

    private static T? Find<T>(DependencyObject root, string id) where T : DependencyObject
    {
        if (root is T t && AutomationProperties.GetAutomationId(root) == id) return t;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (Find<T>(child, id) is { } found) return found;
        return null;
    }

    /// <summary>The scrollable content of the current page (for full-length renders).</summary>
    internal FrameworkElement? PageContent => _host.Child as FrameworkElement;

    internal FrameworkElement ContentHost => _host;

    internal ScrollViewer Scroller => _scroll;

    internal bool IsCompact => _compact;
}
