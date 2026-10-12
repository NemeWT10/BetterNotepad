#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Session;
using BetterNotepad.Modules.Sys;

namespace BetterNotepad.UI.Windows;

/// <summary>
/// Pinned on top + ghost mode (Debug SelfTest, hidden desktop): compact topmost window, per-file bounds saved and
/// restored, Alt+wheel resizing, unpin, ghost ex-style + layered alpha, the badge naming the configured chord,
/// the tray item, the global toggle, session capture. The global hotkey is only validated in test mode.
/// </summary>
internal static class PinSelfTest
{
    private static void Check(List<string> failures, bool ok, string what)
    {
        if (!ok) failures.Add("pin: " + what);
    }

    private static async Task Idle(Window w)
    {
        for (var i = 0; i < 3; i++) await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        w.UpdateLayout();
    }

    private static bool Near(double a, double b, double tolerance = 1.5) => Math.Abs(a - b) <= tolerance;

    public static async Task Run(MainWindow w, List<string> failures)
    {
        var path = Path.Combine(AppEnv.LocalDir, "selftest-lab.txt");
        File.WriteAllText(path, string.Join("\r\n", Enumerable.Range(1, 200).Select(i => $"Lab step {i}: measure, write down, compare.")));
        FileStateStore.I.Update(path, s => s with { PinBounds = [100, 120, 420, 520], PinOpacity = 0.4 });
        var system = SettingsService.I.Get<SystemSettings>();
        var appearance = SettingsService.I.Get<AppearanceSettings>();
        appearance.Language = "en";

        w.OpenFile(path);
        var doc = w.ActiveDocument;
        var group = w.Model.ActiveGroup;
        if (doc is null || !FileUtil.PathEquals(doc.FilePath, path))
        {
            failures.Add("pin: fixture not opened");
            return;
        }
        Check(failures, CanRun("view.pinOnTop", w) && !CanRun("view.unpinFromTop", w), "pin command applies to a tab of the main window, unpin does not");

        // ── pin: compact, topmost, remembered bounds and opacity ──
        w.PinOnTop(group, doc);
        var model = Workspace.I.Windows.FirstOrDefault(m => m.Pinned && m.AllDocuments.Contains(doc));
        var pinned = model is null ? null : App.Current.WindowFor(model);
        if (model is null || pinned is null)
        {
            failures.Add("pin: no pinned window");
            return;
        }
        await Idle(pinned);
        Check(failures, !group.Tabs.Contains(doc) && pinned.ActiveDocument == doc, "the tab moved into the pinned window");
        Check(failures, pinned.Topmost && model.Topmost && !model.IsMain, "pinned window is topmost");
        Check(failures, pinned.TitleBar.Visibility == Visibility.Collapsed && pinned.Status.Visibility == Visibility.Collapsed, "no title bar / status bar");
        Check(failures, pinned.ActiveGroupView?.Compact == true && pinned.MinWidth == PinGeometry.MinSize.Width, "content edge to edge, small minimum size");
        Check(failures, Near(pinned.Width, 420) && Near(pinned.Height, 520), $"remembered per-file size used: {pinned.Width}x{pinned.Height}");
        Check(failures, Near(model.GhostOpacity, 0.4, 0.001), "remembered ghost opacity used: " + model.GhostOpacity);
        Check(failures, pinned.PinBar is { } bar && bar.NameText == "selftest-lab.txt", "the bar names the file");
        Check(failures, pinned.PinBarShown && pinned.PinBar?.Visibility == Visibility.Visible, "the bar shows right after pinning");
        Check(failures, CanRun("view.unpinFromTop", pinned) && !CanRun("view.pinOnTop", pinned), "unpin applies inside the pinned window");
        pinned.HidePinBar(animate: false);
        Check(failures, !pinned.PinBarShown && pinned.PinBar?.Visibility == Visibility.Collapsed && pinned.PinBar.ActualHeight < 1, "the bar hides");
        pinned.ShowPinBar();
        await Task.Delay(320); // the slide (190 ms) when animations are on
        await Idle(pinned);
        Check(failures, pinned.PinBar is { ActualHeight: > 30 }, "the bar pushes the content down (a real row): " + pinned.PinBar?.ActualHeight);
        pinned.HidePinBar(animate: false);

        // ── wheel: Alt+wheel = window size (center + ratio kept), plain wheel stays with the content ──
        var plain = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        pinned.RaiseEvent(plain);
        Check(failures, !plain.Handled && Near(pinned.Width, 420), "a plain wheel does not resize the window");
        var cx = pinned.Left + pinned.Width / 2;
        pinned.ResizePinned(PinGeometry.WheelStep);
        Check(failures, Near(pinned.Width, 462) && Near(pinned.Height, 572), $"Alt+wheel up grows by 10 %: {pinned.Width}x{pinned.Height}");
        Check(failures, Near(pinned.Left + pinned.Width / 2, cx), "Alt+wheel keeps the center");
        for (var i = 0; i < 40; i++) pinned.ResizePinned(1 / PinGeometry.WheelStep);
        Check(failures, pinned.Width >= PinGeometry.MinSize.Width - 0.5 && Math.Abs(pinned.Width / pinned.Height - 420.0 / 520) < 0.01,
            $"Alt+wheel down stops at the minimum, ratio kept: {pinned.Width:0.#}x{pinned.Height:0.#}");
        pinned.Width = 440;
        pinned.Height = 560;
        pinned.SavePinState();
        var stored = FileStateStore.I.Get(path).PinBounds;
        Check(failures, stored is [_, _, 440, 560], "per-file bounds saved: " + (stored is null ? "null" : string.Join(",", stored)));

        // ── commands inside a pinned window: new tabs go to the main window, no split ──
        var extra = pinned.NewTab();
        Check(failures, w.Model.AllDocuments.Contains(extra) && !model.AllDocuments.Contains(extra), "a new tab from the pinned window opens in the main window");
        foreach (var g in w.Model.Groups.Where(g => g.Tabs.Contains(extra)).ToList()) g.Remove(extra);
        DocumentManager.I.Close(extra);
        pinned.Split(Orientation.Horizontal);
        Check(failures, model.Groups.Count == 1, "a pinned window does not split");

        // ── opacity preview: translucent, still clickable ──
        Check(failures, pinned.AllowsTransparency && pinned.WindowStyle == WindowStyle.None, "pinned window is per-pixel transparent (real see-through)");
        pinned.SetOpacityPreview(0.6);
        var (ex, alpha) = pinned.GhostState();
        Check(failures, (ex & Native.WS_EX_LAYERED) != 0 && (ex & Native.WS_EX_TRANSPARENT) == 0 && Near(alpha, 0.6, 0.001), $"opacity preview: ex={ex:X} opacity={alpha}");
        pinned.SetOpacityPreview(null);
        (ex, alpha) = pinned.GhostState();
        Check(failures, (ex & Native.WS_EX_TRANSPARENT) == 0 && alpha == 1, "preview off: opaque again");

        // ── ghost: click-through + alpha, badge with the configured chord, tray item, global toggle ──
        Check(failures, HotkeyService.StateOf(HotkeyService.Ghost) == HotkeyState.SkippedTest && HotkeyService.ChordOf(HotkeyService.Ghost)?.ToString() == system.GhostHotkey,
            "ghost hotkey validated, never registered in test mode: " + HotkeyService.StateOf(HotkeyService.Ghost));
        Check(failures, !MessageWindow.Exists, "no hotkey window in test mode");
        pinned.SetGhost(true);
        (ex, alpha) = pinned.GhostState();
        Check(failures, model.Ghost && (ex & Native.WS_EX_TRANSPARENT) != 0 && (ex & Native.WS_EX_LAYERED) != 0, $"ghost sets WS_EX_TRANSPARENT|WS_EX_LAYERED: {ex:X}");
        Check(failures, Near(alpha, 0.4, 0.001), "ghost opacity follows the chosen value: " + alpha);
        Check(failures, pinned.Badge is { Visibility: Visibility.Visible } && !pinned.PinBarShown, "badge visible, bar hidden in ghost mode");
        Check(failures, pinned.Badge?.Text == "Ghost · " + system.GhostHotkey, "badge names the configured chord: " + pinned.Badge?.Text);
        system.GhostHotkey = "Win+Shift+J";
        await Idle(pinned);
        Check(failures, pinned.Badge?.Text == "Ghost · Win+Shift+J", "badge follows a changed chord: " + pinned.Badge?.Text);
        appearance.Language = "pl";
        await Idle(pinned);
        Check(failures, pinned.Badge?.Text == "Duch · Win+Shift+J", "badge in Polish: " + pinned.Badge?.Text);
        appearance.Language = "en";
        system.GhostHotkey = "Win+Shift+G";
        await Idle(pinned);
        Check(failures, pinned.Badge?.Text == "Ghost · Win+Shift+G", "badge back to the default chord: " + pinned.Badge?.Text);
        var tray = new TrayMenu();
        Check(failures, TextsOf(tray.Card).Contains(Loc.T("tray.menu.ghostOff")), "tray menu offers \"Turn off ghost mode\"");
        tray.CloseMenu();

        PinnedWindows.ToggleGhostGlobal();
        (ex, alpha) = pinned.GhostState();
        Check(failures, !model.Ghost && (ex & Native.WS_EX_TRANSPARENT) == 0 && alpha == 1 && pinned.Badge?.Visibility == Visibility.Collapsed, "hotkey: ghost off for all");
        var trayOff = new TrayMenu();
        Check(failures, !TextsOf(trayOff.Card).Contains(Loc.T("tray.menu.ghostOff")), "no tray item without ghost windows");
        trayOff.CloseMenu();
        PinnedWindows.ToggleGhostGlobal();
        Check(failures, model.Ghost, "hotkey: ghost on for the last pinned window");
        w.RunCommand("view.toggleGhost");
        Check(failures, !model.Ghost, "palette (main window): ghost off");
        w.Model.Ghost = true;
        (ex, _) = w.GhostState();
        Check(failures, (ex & Native.WS_EX_TRANSPARENT) == 0, "the main window never becomes a ghost");
        w.Model.Ghost = false;

        // ── session: pinned windows come back on top; ghost never restores ──
        pinned.SetGhost(true);
        var session = SessionService.I.Capture().Windows.FirstOrDefault(sw => sw.Id == model.Id);
        Check(failures, session is { Pinned: true, Topmost: true }, "session keeps the pinned window on top");
        var restored = new WindowModel();
        restored.Pinned = session?.Pinned == true;
        Check(failures, restored.Topmost && !restored.Ghost, "a restored pinned window is topmost and interactive");
        pinned.SetGhost(false);

        // ── unpin: back to the main window, the pinned window closes; pinning again reuses the remembered size ──
        pinned.UnpinFromTop();
        await Idle(w);
        Check(failures, w.Model.ActiveGroup.Tabs.Contains(doc) && w.ActiveDocument == doc, "unpin returns the tab to the main window");
        Check(failures, !Workspace.I.Windows.Contains(model) && App.Current.WindowFor(model) is null, "the pinned window is gone");
        w.PinOnTop(w.Model.ActiveGroup, doc);
        var again = Workspace.I.Windows.FirstOrDefault(m => m.Pinned && m.AllDocuments.Contains(doc));
        var againWindow = again is null ? null : App.Current.WindowFor(again);
        Check(failures, againWindow is not null && Near(againWindow.Width, 440) && Near(againWindow.Height, 560), "pinning again restores the per-file size");
        againWindow?.UnpinFromTop();
        await Idle(w);

        await w.CloseTabAsync(w.Model.ActiveGroup, doc);
        FileStateStore.I.Update(path, s => s with { PinBounds = null, PinOpacity = null });
        File.Delete(path);
    }

    private static bool CanRun(string id, MainWindow w) =>
        CommandRegistry.Get(id) is { } cmd && (cmd.CanExecute?.Invoke(w.PaletteContext()) ?? true);

    private static List<string> TextsOf(DependencyObject root)
    {
        var list = new List<string>();
        void Walk(DependencyObject d)
        {
            if (d is TextBlock t) list.Add(t.Text);
            foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) Walk(child);
        }
        Walk(root);
        return list;
    }
}
#endif
