using System.Windows.Controls;
using BetterNotepad.UI.Palette;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.UI;

/// <summary>What the palette lists: commands (Ctrl+Shift+P), files (Ctrl+P), a line number, symbols, languages.</summary>
public enum PaletteMode
{
    Commands,
    Files,
    GoToLine,
    Symbols,
    Language,
}

/// <summary>
/// Command palette / quick open overlay of a window (see <see cref="PaletteView"/>). Typing a prefix
/// switches the mode: &gt; commands, : go to line, @ symbol; plain text searches open tabs and recent files.
/// </summary>
public static class CommandPalette
{
    /// <summary>
    /// Opens the palette in <paramref name="mode"/> (a leading prefix in <paramref name="initialText"/> wins).
    /// Already open: the same shortcut again walks the list, another one switches the mode.
    /// </summary>
    public static void Show(MainWindow window, PaletteMode mode, string? initialText = null)
    {
        if (Find(window) is { } open)
        {
            if (open.Mode == mode && initialText is null && mode is PaletteMode.Files or PaletteMode.Commands) open.MoveSelection(1);
            else open.SetMode(mode, initialText ?? "");
            return;
        }
        var view = new PaletteView(window);
        window.OverlayLayer.Children.Add(view);
        view.Open(mode, initialText);
    }

    public static bool IsOpen(MainWindow window) => Find(window) is not null;

    /// <summary>Closes the palette without running anything (e.g. the tab switcher takes over).</summary>
    public static void Close(MainWindow window, bool restoreFocus = false) => Find(window)?.Close(restoreFocus);

    internal static PaletteView? Find(MainWindow window)
        => window.OverlayLayer.Children.OfType<PaletteView>().LastOrDefault(v => !v.IsClosing);
}
