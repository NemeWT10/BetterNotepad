using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Documents;
using BetterNotepad.UI.Palette;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.UI;

/// <summary>
/// Ctrl+Tab switcher over the active group's most recently used tabs. Contract: <see cref="Open"/> starts a
/// switch with the next (or, reversed, the oldest) tab preselected; while Ctrl is held the window forwards
/// keys: Tab / Shift+Tab / arrows cycle, Esc cancels, releasing Ctrl activates. A quick tap switches at once
/// without ever showing the panel; the panel appears only when Ctrl stays down a moment.
/// </summary>
public sealed class TabSwitcher
{
    private const int ShowDelayMs = 140;

    private readonly MainWindow _w;
    private readonly TabGroup _group;
    private readonly List<Document> _items;
    private int _index;
    private DispatcherTimer? _delay;
    private SwitcherPanel? _panel;

    private TabSwitcher(MainWindow w, bool reverse)
    {
        _w = w;
        _group = w.Model.ActiveGroup;
        _items = _group.Mru.Where(_group.Tabs.Contains).ToList();
        foreach (var t in _group.Tabs)
            if (!_items.Contains(t)) _items.Add(t);
        _index = _items.Count < 2 ? 0 : reverse ? _items.Count - 1 : 1;
    }

    public bool IsOpen { get; private set; }

    internal IReadOnlyList<Document> Items => _items;

    internal int SelectedIndex => _index;

    internal FrameworkElement? PanelCard => _panel?.Card;

    public static TabSwitcher Open(MainWindow w, bool reverse) => Open(w, reverse, CtrlHeld(), showNow: false);

    /// <summary><paramref name="held"/>: Ctrl is down (keyboard switch) — stay open until it is released.</summary>
    internal static TabSwitcher Open(MainWindow w, bool reverse, bool held, bool showNow)
    {
        CommandPalette.Close(w);
        var s = new TabSwitcher(w, reverse);
        if (s._items.Count < 2) return s;
        if (!held)
        {
            // a menu / palette call or a tap too quick to see: just switch
            s.Activate(s._items[s._index]);
            return s;
        }
        s.IsOpen = true;
        w.Deactivated += s.OnDeactivated;
        if (showNow) s.ShowPanel();
        else
        {
            s._delay = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(ShowDelayMs) };
            s._delay.Tick += (_, _) =>
            {
                s._delay?.Stop();
                if (s.IsOpen) s.ShowPanel();
            };
            s._delay.Start();
        }
        return s;
    }

    private static bool CtrlHeld() => Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);

    /// <summary>Key presses while open; true = consumed.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsOpen) return false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (key)
        {
            case Key.Tab:
                Step(shift ? -1 : 1);
                break;
            case Key.Down or Key.Right or Key.PageDown:
                Step(1);
                break;
            case Key.Up or Key.Left or Key.PageUp:
                Step(-1);
                break;
            case Key.Home:
                Select(0);
                break;
            case Key.End:
                Select(_items.Count - 1);
                break;
            case Key.Escape:
                Cancel();
                break;
            case Key.Enter or Key.Space:
                Commit();
                break;
            default:
                // any other shortcut: take the chosen tab, then let the key do its job there
                Commit();
                return false;
        }
        e.Handled = true;
        return true;
    }

    public void HandleKeyUp(KeyEventArgs e)
    {
        if (!IsOpen) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl && !CtrlHeld())
        {
            e.Handled = true;
            Commit();
        }
    }

    internal void Step(int delta) => Select(((_index + delta) % _items.Count + _items.Count) % _items.Count);

    private void Select(int index)
    {
        _index = Math.Clamp(index, 0, _items.Count - 1);
        _panel?.SetSelected(_index);
    }

    /// <summary>Activates the selected tab and closes.</summary>
    internal void Commit() => Finish(_items[_index]);

    /// <summary>Esc: nothing changes.</summary>
    internal void Cancel() => Finish(null);

    private void Finish(Document? target)
    {
        if (!IsOpen) return;
        IsOpen = false;
        _delay?.Stop();
        _w.Deactivated -= OnDeactivated;
        _panel?.Hide();
        _panel = null;
        if (target is not null) Activate(target);
    }

    private void OnDeactivated(object? sender, EventArgs e) => Commit(); // the Ctrl release will never arrive

    private void Activate(Document doc)
    {
        if (!_group.Tabs.Contains(doc)) return;
        _group.Active = doc;
        if (_w.Model.ActiveGroup != _group) _w.Model.ActiveGroup = _group;
        if (AppEnv.TestMode && !_w.IsActive) return;
        _w.Dispatcher.BeginInvoke(() => _w.ActiveView?.FocusContent(), DispatcherPriority.Input);
    }

    private void ShowPanel()
    {
        if (_panel is not null) return;
        _panel = new SwitcherPanel(this);
        _w.OverlayLayer.Children.Add(_panel);
        _panel.SetSelected(_index);
        _panel.Appear();
    }

    private void Click(int index)
    {
        Select(index);
        Commit();
    }

    /// <summary>The centered card: header with position, MRU rows (state icon, name, folder), key hints.</summary>
    private sealed class SwitcherPanel : Grid
    {
        private readonly TabSwitcher _owner;
        private readonly Border _card = new();
        private readonly FrostedHost _host;
        private readonly ListBox _list = new();
        private readonly TextBlock _position = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        private readonly List<PaletteRow> _rows;

        public SwitcherPanel(TabSwitcher owner)
        {
            _owner = owner;
            Background = Brushes.Transparent;
            MouseDown += (_, e) =>
            {
                if (e.OriginalSource == this) owner.Cancel();
            };

            _card.SetResourceReference(StyleProperty, "Ov.Panel");
            _host = new FrostedHost(_card, owner._w.GroupsHost)
            {
                MaxWidth = 520,
                Margin = new Thickness(32, 24, 32, 72),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0,
            };
            SizeChanged += (_, _) => _host.Sync();

            var layout = new StackPanel();
            var header = new Grid { Margin = new Thickness(12, 8, 12, 6) };
            var title = new TextBlock { Text = Loc.T("switcher.title"), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
            header.Children.Add(title);
            _position.HorizontalAlignment = HorizontalAlignment.Right;
            _position.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
            _position.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
            header.Children.Add(_position);
            layout.Children.Add(header);

            var sep = new System.Windows.Shapes.Rectangle();
            sep.SetResourceReference(StyleProperty, "Ov.Separator");
            sep.Margin = new Thickness(2, 0, 2, 4);
            layout.Children.Add(sep);

            _rows = owner._items.Select((d, i) => new PaletteRow(Candidate(d, i), null, null, false, false)).ToList();
            _list.SetResourceReference(StyleProperty, "BareList");
            _list.Focusable = false;
            _list.MaxHeight = 452;
            _list.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(PaletteRowView)) };
            var itemStyle = new Style(typeof(ListBoxItem), (Style)Application.Current.FindResource("PaletteItem"));
            itemStyle.Setters.Add(new Setter(FocusableProperty, false));
            _list.ItemContainerStyle = itemStyle;
            _list.ItemsSource = _rows;
            _list.PreviewMouseLeftButtonDown += OnRowClick;
            layout.Children.Add(_list);

            var footSep = new System.Windows.Shapes.Rectangle();
            footSep.SetResourceReference(StyleProperty, "Ov.Separator");
            footSep.Margin = new Thickness(2, 4, 2, 0);
            layout.Children.Add(footSep);

            var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 10, 5) };
            footer.Children.Add(Hint(["Tab"], "switcher.next"));
            footer.Children.Add(Hint(["Shift", "Tab"], "switcher.prev"));
            footer.Children.Add(Hint(["Ctrl"], "switcher.release"));
            layout.Children.Add(footer);

            _card.Child = layout;
            Children.Add(_host);
        }

        public FrameworkElement Card => _host;

        private static PaletteCandidate Candidate(Document doc, int order)
        {
            var (glyph, brush) = PaletteView.StateGlyph(doc);
            return new PaletteCandidate
            {
                Title = doc.Title,
                Detail = doc.FilePath is { } p ? PaletteView.DisplayFolder(Path.GetDirectoryName(p)) : Loc.T("pal.file.unsaved"),
                Glyph = glyph,
                GlyphBrush = brush,
                Hint = doc.IsDirty ? Loc.T("pal.file.modified") : null,
                Order = order,
            };
        }

        private static FrameworkElement Hint(string[] keys, string labelKey)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            foreach (var k in keys)
            {
                var chip = PaletteRowView.KeyChip(k);
                chip.Margin = new Thickness(0, 0, 3, 0);
                row.Children.Add(chip);
            }
            var label = new TextBlock { Text = Loc.T(labelKey), FontSize = 11.5, Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
            row.Children.Add(label);
            return row;
        }

        public void SetSelected(int index)
        {
            _list.SelectedIndex = index;
            if (index >= 0 && index < _rows.Count) _list.ScrollIntoView(_rows[index]);
            _position.Text = $"{index + 1} / {_rows.Count}";
        }

        public void Appear()
        {
            Motion.Appear(_host, rise: 10, fromScale: 0.96, ms: 240);
        }

        public void Hide()
        {
            IsHitTestVisible = false;
            Motion.Disappear(_host, () => (Parent as Panel)?.Children.Remove(this), sink: 4, ms: 110);
        }

        private void OnRowClick(object sender, MouseButtonEventArgs e)
        {
            DependencyObject? d = e.OriginalSource as DependencyObject;
            while (d is not null and not ListBox)
            {
                if (d is ListBoxItem { DataContext: PaletteRow row })
                {
                    e.Handled = true;
                    _owner.Click(_rows.IndexOf(row));
                    return;
                }
                d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
        }
    }
}
