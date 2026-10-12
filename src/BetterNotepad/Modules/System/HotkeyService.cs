using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using BetterNotepad.Core;
using BetterNotepad.Core.Commands;

namespace BetterNotepad.Modules.Sys;

/// <summary>
/// Invisible top-level window shared by the tray icon and the global hotkeys (created on first use, never in test mode).
/// It is a real (never shown) window, not a message-only one: only top-level windows receive the "TaskbarCreated"
/// broadcast that tells the tray icon to re-add itself after Explorer restarted.
/// </summary>
internal static class MessageWindow
{
    private static HwndSource? _source;

    public static HwndSource Source => _source ??= new HwndSource(new HwndSourceParameters("BetterNotepad.Messages")
    {
        ParentWindow = 0,
        WindowStyle = unchecked((int)0x80000000), // WS_POPUP, not WS_VISIBLE: never shown
        ExtendedWindowStyle = 0x00000080, // WS_EX_TOOLWINDOW: no taskbar button
        Width = 0,
        Height = 0,
    });

    public static nint Handle => Source.Handle;

    /// <summary>False until something needs it (a test instance never creates it).</summary>
    public static bool Exists => _source is not null;

    public static void Dispose()
    {
        _source?.Dispose();
        _source = null;
    }
}

internal enum HotkeyState
{
    None,
    Registered,
    /// <summary>Windows refused it (another program owns the combination).</summary>
    Failed,
    /// <summary>Not a usable chord (no modifier, AltGr clash, reserved).</summary>
    Invalid,
    /// <summary>Test mode: parsed and validated, never registered with Windows.</summary>
    SkippedTest,
}

/// <summary>
/// Global hotkeys through RegisterHotKey on the message-only window. Named slots (summon, later ghost mode)
/// so other modules can add theirs; re-registered whenever the settings change.
/// </summary>
internal static class HotkeyService
{
    public const string Summon = "summon";

    /// <summary>Ghost mode of pinned windows on / off (the only way back when every pinned window is click-through).</summary>
    public const string Ghost = "ghost";

    private sealed class Slot
    {
        public required string Name;
        public int Id;
        public KeyChord? Chord;
        public Action? Action;
        public HotkeyState State;
        public bool Bound;
    }

    private static readonly Dictionary<string, Slot> Slots = new();
    private static int _nextId = 0x4E50;
    private static bool _hooked;

    /// <summary>Raised when a slot's state changed (the settings page and the tray tooltip re-read).</summary>
    public static event Action? StatusChanged;

    public static HotkeyState StateOf(string name) => Slots.TryGetValue(name, out var s) ? s.State : HotkeyState.None;

    public static KeyChord? ChordOf(string name) => Slots.TryGetValue(name, out var s) ? s.Chord : null;

    /// <summary>The combination Windows refused (null when everything registered).</summary>
    public static string? Failure => Slots.Values.FirstOrDefault(s => s.State == HotkeyState.Failed)?.Chord?.ToString();

    /// <summary>A global chord needs a modifier, must not be an AltGr combination (Ctrl+Alt+letter) and avoids the incognito/new-folder clash.</summary>
    public static bool IsUsable(KeyChord chord)
    {
        if (chord.Modifiers == ModifierKeys.None) return false;
        if (chord.IsAltGrConflict) return false;
        return !(chord.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && chord.Key == Key.N);
    }

    /// <summary>Binds <paramref name="name"/> to a chord (null/empty/unusable → unbound). Returns the outcome.</summary>
    public static HotkeyState Set(string name, string? chordText, Action action)
    {
        if (!Slots.TryGetValue(name, out var slot)) Slots[name] = slot = new Slot { Name = name, Id = _nextId++ };
        Release(slot);
        slot.Action = action;
        slot.Chord = null;

        HotkeyState state;
        if (string.IsNullOrWhiteSpace(chordText))
        {
            state = HotkeyState.None;
        }
        else if (KeyChord.Parse(chordText) is not { } chord || !IsUsable(chord))
        {
            state = HotkeyState.Invalid;
        }
        else
        {
            slot.Chord = chord;
            if (AppEnv.TestMode)
            {
                state = HotkeyState.SkippedTest; // a test instance never takes a key combination from the user
            }
            else
            {
                EnsureHook();
                var vk = (uint)KeyInterop.VirtualKeyFromKey(chord.Key);
                var mods = (uint)chord.Modifiers | SysNative.MOD_NOREPEAT; // WPF and Win32 share the modifier bit values
                slot.Bound = SysNative.RegisterHotKey(MessageWindow.Handle, slot.Id, mods, vk);
                state = slot.Bound ? HotkeyState.Registered : HotkeyState.Failed;
                if (!slot.Bound) Log.Warn($"hotkey {chord} could not be registered (in use by another program)");
            }
        }
        slot.State = state;
        StatusChanged?.Invoke();
        return state;
    }

    public static void ReleaseAll()
    {
        foreach (var s in Slots.Values) Release(s);
    }

    private static void Release(Slot slot)
    {
        if (slot.Bound) SysNative.UnregisterHotKey(MessageWindow.Handle, slot.Id);
        slot.Bound = false;
    }

    private static void EnsureHook()
    {
        if (_hooked) return;
        _hooked = true;
        MessageWindow.Source.AddHook(Hook);
    }

    private static nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != SysNative.WM_HOTKEY) return 0;
        var id = (int)wParam;
        foreach (var s in Slots.Values)
        {
            if (s.Id != id || s.Action is not { } action) continue;
            handled = true;
            Application.Current.Dispatcher.BeginInvoke(action);
            break;
        }
        return 0;
    }
}
