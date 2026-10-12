#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.UI.Controls;
using BetterNotepad.UI.Themes;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.UI;

/// <summary>
/// Debug-only check of the settings page, run by <c>Core/SelfTest</c> (test mode, window off-screen, no popup
/// or file dialog is ever opened). Opens every page, toggles settings, records shortcuts (refusing Ctrl+Alt+S),
/// resolves a conflict, round-trips export/import and, with BETTERNOTEPAD_SNAPSHOT=&lt;dir&gt;, renders every page
/// offscreen to settings-*.png (Aurora and Daylight in Polish, Aurora in English).
/// </summary>
internal static class SettingsSelfTest
{
    public static async Task Run(MainWindow w, List<string> failures)
    {
        void Check(bool ok, string what)
        {
            if (!ok) failures.Add("settings: " + what);
        }

        var appearance = SettingsService.I.Get<AppearanceSettings>();
        var editor = SettingsService.I.Get<EditorSettings>();
        var system = SettingsService.I.Get<SystemSettings>();
        var keymap = SettingsService.I.Get<KeymapSettings>();

        w.ShowSettings(SettingsPageIds.General);
        var page = SettingsPage.For(w);
        Check(page is not null, "page did not open");
        if (page is null) return;
        await Settle(w, 400);
        Check(page.CurrentPage == SettingsPageIds.General, "ShowSettings(general) opened " + page.CurrentPage);

        // every page builds
        foreach (var (id, _) in SettingsPage.Pages)
        {
            page.Navigate(id);
            await Settle(w, 20);
            Check(page.CurrentPage == id && page.PageContent is Panel { Children.Count: > 2 }, "page " + id + " is empty");
        }
        w.ShowSettings("keys");
        Check(page.CurrentPage == SettingsPageIds.Shortcuts, "alias 'keys' did not open the shortcuts page");

        // controls apply live, and follow changes made elsewhere
        page.Navigate(SettingsPageIds.Editor);
        await Settle(w, 20);
        if (page.FindById<CheckBox>("editor.wordWrapCode") is { } wrap)
        {
            var before = editor.WordWrapCode;
            wrap.IsChecked = !before;
            Check(editor.WordWrapCode == !before, "wrap switch did not apply");
            wrap.IsChecked = before;
            Check(editor.WordWrapCode == before, "wrap switch did not turn back");
        }
        else failures.Add("settings: no wrap switch");
        editor.ShowWhitespace = !editor.ShowWhitespace;
        Check(page.FindById<CheckBox>("editor.whitespace")?.IsChecked == editor.ShowWhitespace, "whitespace switch did not follow the setting");
        editor.ShowWhitespace = !editor.ShowWhitespace;
        if (page.FindById<Stepper>("editor.fontSize") is { } size)
        {
            var s0 = editor.FontSize;
            size.Step(+1);
            Check(Math.Abs(editor.FontSize - (s0 + 1)) < 1e-9, "font size step");
            size.Step(-1);
        }
        else failures.Add("settings: no font size stepper");
        if (page.FindById<RadioButton>("editor.minimapMode.never") is { } never)
        {
            never.IsChecked = true;
            Check(editor.MinimapMode == "never", "minimap segment did not apply");
            page.FindById<RadioButton>("editor.minimapMode.auto")!.IsChecked = true;
            Check(editor.MinimapMode == "auto", "minimap segment did not turn back");
        }
        else failures.Add("settings: no minimap segment");

        // interface language switches live and the page rebuilds in it
        page.Navigate(SettingsPageIds.General);
        await Settle(w, 20);
        page.FindById<RadioButton>("general.language.en")!.IsChecked = true;
        await Settle(w, 60);
        Check(Loc.I.Language == "en" && NavName(page, "general") == "General", "language → English");
        page.FindById<RadioButton>("general.language.pl")!.IsChecked = true;
        await Settle(w, 60);
        Check(Loc.I.Language == "pl" && NavName(page, "general") == "Ogólne", "language → Polish");
        page.FindById<RadioButton>("general.language.system")!.IsChecked = true;
        await Settle(w, 60);
        Check(appearance.Language == "system", "language → system");

        // theme cards
        page.Navigate(SettingsPageIds.Appearance);
        await Settle(w, 20);
        page.SelectTheme("daylight");
        await Settle(w, 20);
        Check(ThemeManager.I.Current.Id == "daylight" && page.FindById<ThemeCard>("appearance.theme.daylight")?.IsChecked == true, "theme card daylight");
        page.FindById<ThemeCard>("appearance.theme.sunset")!.IsChecked = true;
        Check(ThemeManager.I.Current.Id == "sunset", "clicking the sunset card");
        page.SelectTheme("system");

        // export / import round trip; a foreign file changes nothing
        var file = Path.Combine(AppEnv.LocalDir, "selftest-settings.json");
        var bogus = Path.Combine(AppEnv.LocalDir, "selftest-bogus.json");
        editor.TabSize = 3;
        Check(page.ExportTo(file) && File.Exists(file), "export");
        editor.TabSize = 4;
        Check(page.ImportFrom(file) && editor.TabSize == 3, "import did not restore the exported value");
        editor.TabSize = 4;
        File.WriteAllText(bogus, "[1, 2, 3]");
        Check(!page.ImportFrom(bogus) && editor.TabSize == 4, "an invalid import must change nothing");
        File.Delete(file);
        File.Delete(bogus);

        // shortcuts: record, refuse AltGr chords and lone keys, conflicts, clear, cancel, search, reset
        page.Navigate(SettingsPageIds.Shortcuts);
        await Settle(w, 20);
        Check(page.ShortcutRowCount == CommandRegistry.All.Count, $"rows {page.ShortcutRowCount} ≠ commands {CommandRegistry.All.Count}");
        page.BeginShortcutCapture("file.saveAll");
        Check(page.IsCapturing, "recording did not start");
        page.OfferChord(new KeyChord(ModifierKeys.Control | ModifierKeys.Alt, Key.S));
        Check(page.IsCapturing && page.LastWarning is not null && Keymap.GestureText("file.saveAll") == "", "Ctrl+Alt+S must be refused");
        page.OfferChord(new KeyChord(ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt, Key.L));
        Check(page.IsCapturing && Keymap.GestureText("file.saveAll") == "", "Ctrl+Shift+Alt+L must be refused");
        page.OfferChord(new KeyChord(ModifierKeys.None, Key.A));
        Check(page.IsCapturing && Keymap.GestureText("file.saveAll") == "", "a lone letter must be refused");
        page.OfferChord(new KeyChord(ModifierKeys.Control | ModifierKeys.Shift, Key.F5));
        Check(!page.IsCapturing && Keymap.GestureText("file.saveAll") == "Ctrl+Shift+F5", "recording Ctrl+Shift+F5 gave " + Keymap.GestureText("file.saveAll"));
        await Settle(w, 20);
        Check(MenuShows(w, "Ctrl+Shift+F5"), "the menu did not pick up the new shortcut");

        page.BeginShortcutCapture("file.saveAll");
        page.OfferChord(new KeyChord(ModifierKeys.Control, Key.S));
        Check(page.HasPendingConflict && Keymap.GestureText("file.saveAll") == "Ctrl+Shift+F5", "conflict with Ctrl+S not offered");
        page.ResolveConflict("replace");
        Check(Keymap.GestureText("file.saveAll") == "Ctrl+S" && Keymap.GestureText("file.save") == "", "replace: saveAll=" + Keymap.GestureText("file.saveAll") + " save=" + Keymap.GestureText("file.save"));
        page.BeginShortcutCapture("file.save");
        page.OfferChord(new KeyChord(ModifierKeys.Control, Key.S));
        page.ResolveConflict("keep");
        Check(Keymap.GestureText("file.save") == "Ctrl+S" && Keymap.GestureText("file.saveAll") == "Ctrl+S"
              && Keymap.Conflicts("file.save", new KeyChord(ModifierKeys.Control, Key.S)).Any(), "keep both");
        page.BeginShortcutCapture("file.saveAll");
        page.ClearCaptured();
        Check(!page.IsCapturing && Keymap.GestureText("file.saveAll") == "", "Backspace did not clear the shortcut");
        page.BeginShortcutCapture("file.save");
        page.CancelCapture();
        Check(!page.IsCapturing && Keymap.GestureText("file.save") == "Ctrl+S", "Esc must leave the shortcut unchanged");

        page.SetShortcutSearch("ctrl+shift+p");
        Check(page.VisibleShortcutRows == 1, "chord search found " + page.VisibleShortcutRows);
        page.SetShortcutSearch("zzzz-nothing");
        Check(page.VisibleShortcutRows == 0, "search for nonsense still shows rows");
        page.SetShortcutSearch(Loc.T("cmd.file.save"));
        Check(page.VisibleShortcutRows >= 1, "search by title");
        page.SetShortcutSearch("");
        Check(page.VisibleShortcutRows == page.ShortcutRowCount, "clearing the search");

        page.ResetAllShortcuts();
        Check(keymap.Overrides.Count == 0 && Keymap.GestureText("file.save") == "Ctrl+S" && Keymap.GestureText("file.saveAll") == "", "reset all");

        // global hotkeys: Win allowed, AltGr and Ctrl+Shift+N refused
        page.Navigate(SettingsPageIds.System);
        await Settle(w, 20);
        page.BeginHotkeyCapture("system.globalHotkey");
        page.OfferChord(new KeyChord(ModifierKeys.Control | ModifierKeys.Shift, Key.N));
        Check(page.IsCapturing && system.GlobalHotkey == "Win+Shift+N", "Ctrl+Shift+N must be refused as a global hotkey");
        page.OfferChord(new KeyChord(ModifierKeys.Control | ModifierKeys.Alt, Key.N));
        Check(page.IsCapturing && system.GlobalHotkey == "Win+Shift+N", "Ctrl+Alt+N must be refused as a global hotkey");
        page.OfferChord(new KeyChord(ModifierKeys.Windows | ModifierKeys.Shift, Key.M));
        Check(!page.IsCapturing && system.GlobalHotkey == "Win+Shift+M", "recording Win+Shift+M");
        system.GlobalHotkey = "Win+Shift+N";

        // offscreen renders for review
        var shots = Environment.GetEnvironmentVariable("BETTERNOTEPAD_SNAPSHOT");
        if (!string.IsNullOrWhiteSpace(shots))
        {
            Directory.CreateDirectory(shots);
            RenderGlyphSheet(page, Path.Combine(shots, "settings-glyphs.png"));
            foreach (var (theme, lang) in new[] { ("aurora", "pl"), ("daylight", "pl"), ("aurora", "en") })
            {
                appearance.Language = lang;
                appearance.Theme = theme;
                await Settle(w, 150);
                var suffix = lang == "pl" ? theme : theme + "-en";
                foreach (var (id, _) in SettingsPage.Pages)
                {
                    page.Navigate(id);
                    await Settle(w, 520);
                    Render(page, Path.Combine(shots, $"settings-{id}-{suffix}.png"));
                    if (page.Scroller.ScrollableHeight > 1) RenderContent(page, Path.Combine(shots, $"settings-{id}-{suffix}-full.png"));
                }
                page.Navigate(SettingsPageIds.Shortcuts);
                await Settle(w, 300);
                page.BeginShortcutCapture("file.saveAll");
                page.OfferChord(new KeyChord(ModifierKeys.Control | ModifierKeys.Alt, Key.S));
                await Settle(w, 450);
                Render(page, Path.Combine(shots, $"settings-shortcuts-altgr-{suffix}.png"));
                page.OfferChord(new KeyChord(ModifierKeys.Control, Key.S));
                await Settle(w, 450);
                Render(page, Path.Combine(shots, $"settings-shortcuts-conflict-{suffix}.png"));
                page.ResolveConflict("cancel");
            }

            // a narrow window: the section list collapses to icons
            appearance.Language = "pl";
            appearance.Theme = "aurora";
            var width = w.Width;
            w.Width = 760;
            await Settle(w, 200);
            Check(page.IsCompact, "narrow window did not switch to the compact list");
            foreach (var id in new[] { SettingsPageIds.Appearance, SettingsPageIds.Shortcuts })
            {
                page.Navigate(id);
                await Settle(w, 520);
                Render(page, Path.Combine(shots, $"settings-{id}-compact.png"));
            }
            w.Width = width;
            await Settle(w, 200);
            appearance.Language = "system";
            appearance.Theme = "system";
            await Settle(w, 100);
        }

        page.Close();
        await Settle(w, 400);
        Check(SettingsPage.For(w) is null && !w.OverlayLayer.Children.OfType<SettingsPage>().Any(), "the page did not close");
    }

    private static async Task Settle(MainWindow w, int ms)
    {
        if (ms > 0) await Task.Delay(ms);
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static string? NavName(SettingsPage page, string id)
        => page.FindById<RadioButton>("settings.nav." + id) is { } rb ? System.Windows.Automation.AutomationProperties.GetName(rb) : null;

    private static bool MenuShows(MainWindow w, string gesture)
    {
        static bool Walk(ItemsControl parent, string g)
            => parent.Items.OfType<MenuItem>().Any(i => i.InputGestureText == g || Walk(i, g));
        return Walk(w.TopMenu, gesture);
    }

    private static void Render(FrameworkElement el, string path)
    {
        el.UpdateLayout();
        Save(el, (int)Math.Ceiling(el.ActualWidth), (int)Math.Ceiling(el.ActualHeight), 0, 0, el, path);
    }

    /// <summary>The whole scrollable page, not just the viewport.</summary>
    private static void RenderContent(SettingsPage page, string path)
    {
        if (page.PageContent is not { } content) return;
        content.UpdateLayout();
        const int pad = 32;
        Save(content, (int)Math.Ceiling(content.ActualWidth) + 2 * pad, (int)Math.Ceiling(content.ActualHeight) + 2 * pad, pad, pad, page, path);
    }

    private static void Save(FrameworkElement el, int width, int height, double x, double y, FrameworkElement resources, string path)
    {
        if (width <= 0 || height <= 0) return;
        var bg = resources.TryFindResource("Brush.Bg") as Brush ?? Brushes.Black;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(bg, null, new Rect(0, 0, width, height));
            var w = el.ActualWidth;
            var h = el.ActualHeight;
            var offset = el.Parent is null ? new Vector() : VisualTreeHelper.GetOffset(el); // the brush maps the parent's coordinates
            dc.DrawRectangle(new VisualBrush(el)
            {
                Stretch = Stretch.None,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(offset.X, offset.Y, w, h),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(x, y, w, h),
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            }, null, new Rect(x, y, w, h));
        }
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        png.Save(fs);
    }

    /// <summary>Glyphs used by the page (and candidates) with their code points, to check the icon font has them.</summary>
    private static void RenderGlyphSheet(SettingsPage page, string path)
    {
        int[] codes =
        [
            0xE713, 0xE790, 0xE943, 0xE8A5, 0xE765, 0xE770, 0xE895, 0xE946, 0xE72B, 0xE774, 0xE945, 0xE81C, 0xEDE1, 0xE8B5,
            0xE771, 0xE8D2, 0xE8E9, 0xE71E, 0xE8E4, 0xE75D, 0xE8B3, 0xE890, 0xE8E2, 0xE70D, 0xE8FD, 0xE73E, 0xE89A, 0xE751,
            0xE7C3, 0xE74E, 0xF57E, 0xEB9F, 0xE9D5, 0xE7E8, 0xE737, 0xE7B3, 0xEA18, 0xE72C, 0xE8B7, 0xE838, 0xE70F, 0xE7A7,
            0xE738, 0xE710, 0xE721, 0xE7BA, 0xE7F4, 0xE7F8, 0xE8A7, 0xEA86, 0xE8EC, 0xE8CB, 0xE916, 0xE823, 0xE9D9, 0xE7FC,
            0xE8F1, 0xE8D7, 0xE72E, 0xE896, 0xE898, 0xE8AB, 0xE8C8, 0xE8F4, 0xE8E5, 0xE7AC, 0xE787, 0xE762, 0xE8C9, 0xE897,
            0xE80F, 0xE71D, 0xE90F, 0xE81E, 0xE91B, 0xE9F9, 0xE8A1, 0xE8BC, 0xE8FB, 0xE8EF, 0xE7EF, 0xE9CE, 0xEA37, 0xEC4F,
        ];
        var wrap = new WrapPanel { Width = 760, Margin = new Thickness(16) };
        foreach (var code in codes)
        {
            var cell = new StackPanel { Width = 84, Margin = new Thickness(0, 0, 0, 12) };
            var icon = new Icon { Glyph = char.ConvertFromUtf32(code), FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            cell.Children.Add(icon);
            var label = new TextBlock { Text = code.ToString("X4"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
            cell.Children.Add(label);
            wrap.Children.Add(cell);
        }
        var host = new Border { Child = wrap };
        host.SetResourceReference(Border.BackgroundProperty, "Brush.Bg");
        host.Measure(new Size(800, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        Save(host, (int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 0, 0, page, path);
    }
}
#endif
