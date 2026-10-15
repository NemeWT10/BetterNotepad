using BetterNotepad.UI.Windows;

namespace BetterNotepad.UI.Tray;

/// <summary>
/// Notification-area icon, autostart and the global hotkey live here (system module).
/// Until it is started, the app quits on close instead of hiding. Memory while hidden: <see cref="TrayRest"/>.
/// </summary>
public class TrayService
{
    public static TrayService? I { get; protected set; }

    public static bool IsAvailable => I is not null;

    public virtual void OnMainHidden(MainWindow window) { }

    public virtual void ShowHint(string title, string text) { }
}
