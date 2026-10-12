using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Documents;
using BetterNotepad.Modules.Sys;

namespace BetterNotepad.UI.Windows;

/// <summary>
/// All tabs pinned on top, and ghost mode across them. The global hotkey (Settings ▸ System, default Win+Shift+G)
/// and the palette command toggle like this: if any pinned window is a ghost, ghost mode goes off for all of them
/// (one press always gives every window back), otherwise it goes on for the pinned window used last.
/// </summary>
internal static class PinnedWindows
{
    private static WindowModel? _last;

    public static IEnumerable<MainWindow> All => App.Current?.OpenWindows.Where(w => w.Model.Pinned).ToList() ?? [];

    public static bool AnyGhost => All.Any(w => w.Model.Ghost);

    /// <summary>The pinned window used last (activated or created): the target of the hotkey.</summary>
    public static void Touch(WindowModel model)
    {
        if (model.Pinned) _last = model;
    }

    /// <summary>
    /// The configured ghost chord as the user sees it ("Win+Shift+G"), or null when it does not work
    /// (none set, refused by Windows, invalid). In test mode the validated chord counts.
    /// </summary>
    public static string? GhostChord()
    {
        var state = HotkeyService.StateOf(HotkeyService.Ghost);
        return state is HotkeyState.Registered or HotkeyState.SkippedTest ? HotkeyService.ChordOf(HotkeyService.Ghost)?.ToString() : null;
    }

    /// <summary>The way out of ghost mode, for the badge and the hint toast: the chord, else the tray menu, else the palette.</summary>
    public static string GhostExitText(string withChordKey, string trayKey, string paletteKey, bool noBreak = false)
    {
        // noBreak: a wrapping text never splits the chord ("Win" | "+Shift+G")
        if (GhostChord() is { } chord) return Loc.T(withChordKey, noBreak ? chord.Replace("+", "⁠+⁠") : chord);
        return Tray.TrayService.IsAvailable ? Loc.T(trayKey) : Loc.T(paletteKey);
    }

    /// <summary>Global hotkey / palette (outside a pinned window): see the class summary.</summary>
    public static void ToggleGhostGlobal()
    {
        var pinned = All.ToList();
        if (pinned.Any(w => w.Model.Ghost))
        {
            AllGhostOff();
            return;
        }
        var target = pinned.FirstOrDefault(w => w.Model == _last) ?? pinned.LastOrDefault();
        if (target is null)
        {
            if (App.Current.MainAppWindow is { IsVisible: true } main) main.Toasts.Show(Loc.T("pin.nonePinned"), ToastKind.Info, seconds: 4);
            return;
        }
        target.SetGhost(true);
    }

    /// <summary>Tray menu "Turn off ghost mode" (and the first half of the toggle).</summary>
    public static void AllGhostOff()
    {
        foreach (var w in All.Where(w => w.Model.Ghost)) w.SetGhost(false);
    }
}
