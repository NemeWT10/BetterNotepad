using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Documents.Session;
using BetterNotepad.UI;
using BetterNotepad.UI.Controls;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Modules.Sys;

/// <summary>
/// The tray icon's menu as a themed popup (Ov.Panel card, same look as the palette): New note, Show/Hide,
/// Recent files (expands in place), Settings, Exit. Closes on click-away or Esc.
/// </summary>
internal sealed class TrayMenu : Window
{
    private const double ShadowRoom = 18;

    private readonly StackPanel _items = new();
    private readonly Border _card = new();
    private bool _closing;

    public TrayMenu()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Opacity = 0;
        Title = AppInfo.ProductName;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        _card.SetResourceReference(StyleProperty, "Ov.Panel");
        _card.Margin = new Thickness(ShadowRoom);
        _card.Width = 252;
        _card.Child = _items;
        Content = _card;
        Build();

        Deactivated += (_, _) => CloseMenu();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            CloseMenu();
        };
        Loaded += OnLoaded;
    }

    public void CloseMenu()
    {
        if (_closing) return;
        _closing = true;
        try { Close(); } catch { /* already closing */ }
    }

    // ───────────── content ─────────────

    /// <summary>The visual card (for tests that render it offscreen).</summary>
    public FrameworkElement Card => _card;

    internal void Build()
    {
        _items.Children.Clear();
        _items.Children.Add(Header());

        if (UpdateFlow.Pending is { } update)
            Row(Glyphs.Download, Loc.T("tray.menu.update", update.Tag), () => UpdateFlow.OfferPending(), accent: true);

        // ghost windows ignore the mouse: the way back is always one click away here (with the hotkey next to it)
        if (PinnedWindows.AnyGhost)
            Row(Glyphs.Ghost, Loc.T("tray.menu.ghostOff"), PinnedWindows.AllGhostOff, PinnedWindows.GhostChord(), accent: true);

        var chord = HotkeyService.StateOf(HotkeyService.Summon) == HotkeyState.Registered ? HotkeyService.ChordOf(HotkeyService.Summon)?.ToString() : null;
        Row(Glyphs.Add, Loc.T("tray.menu.newNote"), SystemModule.QuickNote, chord);

        var main = App.Current.MainAppWindow;
        var visible = main is { IsVisible: true } && main.WindowState != WindowState.Minimized;
        Row(Glyphs.Window, Loc.T(visible ? "tray.menu.hide" : "tray.menu.show"), SystemTray.ToggleMainWindow);

        var recent = RecentFiles.I.Items.Where(File.Exists).Take(6).ToList();
        if (recent.Count > 0) AddRecent(recent);

        _items.Children.Add(Separator());
        Row(Glyphs.Settings, Loc.T("tray.menu.settings"), () =>
        {
            var w = App.Current.MainAppWindow;
            if (w is null) return;
            w.BringToFront();
            w.ShowSettings();
        });
        _items.Children.Add(Separator());
        Row("", Loc.T("tray.menu.exit"), () => App.Current.Quit(), danger: true);
    }

    private FrameworkElement Header()
    {
        var grid = new Grid { Margin = new Thickness(8, 6, 8, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new Image { Source = MainWindow.AppIconSource, Width = 20, Height = 20, Margin = new Thickness(0, 0, 10, 0) };
        grid.Children.Add(icon);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = AppInfo.ProductName, FontSize = 13.5, FontWeight = FontWeights.SemiBold };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelText");
        name.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");
        var version = new TextBlock { Text = "v" + AppInfo.Version, FontSize = 11 };
        version.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
        texts.Children.Add(name);
        texts.Children.Add(version);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        return grid;
    }

    private static Rectangle Separator()
    {
        var r = new Rectangle();
        r.SetResourceReference(StyleProperty, "Ov.Separator");
        return r;
    }

    private Border Row(string glyph, string text, Action onClick, string? hint = null, bool danger = false, bool accent = false, bool chevron = false)
    {
        var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Icon { Glyph = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        icon.SetResourceReference(TextBlock.ForegroundProperty, danger ? "Brush.Danger" : accent ? "Brush.AccentText" : "Brush.PanelTextDim");
        grid.Children.Add(icon);

        var label = new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(TextBlock.ForegroundProperty, danger ? "Brush.Danger" : accent ? "Brush.AccentText" : "Brush.PanelText");
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (hint is not null || chevron)
        {
            UIElement right;
            if (chevron)
            {
                var c = new Icon { Glyph = Glyphs.ChevronRight, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                c.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
                right = c;
            }
            else
            {
                var h = new TextBlock { Text = hint, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                h.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
                right = h;
            }
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
        }

        row.Child = grid;
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "Brush.PanelHover");
        row.MouseLeave += (_, _) => { row.ClearValue(Border.BackgroundProperty); row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!chevron) CloseMenu();
            onClick();
        };
        AutomationProperties.SetName(row, text);
        _items.Children.Add(row);
        return row;
    }

    private void AddRecent(List<string> files)
    {
        var list = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(26, 0, 0, 2) };
        var chevron = Row(Glyphs.History, Loc.T("tray.menu.recent"), () =>
        {
            var open = list.Visibility != Visibility.Visible;
            list.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (open) Motion.Appear(list, rise: 4, fromScale: 1, ms: 160);
        }, chevron: true);
        _ = chevron;
        _items.Children.Add(list);
        foreach (var path in files)
        {
            var p = path;
            var item = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 6, 10, 6), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = p };
            var name = new TextBlock { Text = System.IO.Path.GetFileName(p), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelText");
            item.Child = name;
            item.MouseEnter += (_, _) => item.SetResourceReference(Border.BackgroundProperty, "Brush.PanelHover");
            item.MouseLeave += (_, _) => { item.ClearValue(Border.BackgroundProperty); item.Background = Brushes.Transparent; };
            item.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                CloseMenu();
                var w = App.Current.MainAppWindow;
                if (w is null) return;
                App.Current.OpenFiles([p], w);
                w.BringToFront();
            };
            list.Children.Add(item);
        }
    }

    // ───────────── placement ─────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        Native.GetCursorPos(out var cur);
        var monitor = SysNative.MonitorFromPoint(cur, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(monitor, ref info);
        SysNative.GetDpiForMonitor(monitor, 0, out var dpi, out _);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        SysNative.GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var work = info.rcWork;
        var pad = (int)(ShadowRoom * scale);
        var gap = (int)(6 * scale);

        var x = cur.X - w / 2;
        var y = cur.Y < (work.Top + work.Bottom) / 2
            ? cur.Y - pad + gap // taskbar on top: open downwards
            : cur.Y - h + pad - gap; // taskbar at the bottom: open upwards
        // taskbar on a side: stay beside it
        if (cur.X > work.Right) x = work.Right - w + pad - gap;
        else if (cur.X < work.Left) x = work.Left - pad + gap;
        x = Math.Clamp(x, work.Left - pad + gap, Math.Max(work.Left, work.Right - w + pad - gap));
        y = Math.Clamp(y, work.Top - pad + gap, Math.Max(work.Top, work.Bottom - h + pad - gap));
        SysNative.SetWindowPos(hwnd, 0, x, y, 0, 0, SysNative.SWP_NOSIZE | SysNative.SWP_NOZORDER | SysNative.SWP_NOACTIVATE);

        Opacity = 1;
        Motion.Appear(_card, rise: 8, fromScale: 0.97, ms: 170);
        Activate();
        Native.SetForegroundWindow(hwnd);
    }
}
