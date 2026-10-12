using System.Diagnostics;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.UI.Tray;

/// <summary>
/// Notification-area icon, autostart and the global hotkey live here (system module).
/// Until it is started, the app quits on close instead of hiding.
/// </summary>
public class TrayService
{
    public static TrayService? I { get; protected set; }

    public static bool IsAvailable => I is not null;

    public virtual void OnMainHidden(MainWindow window) { }

    public virtual void ShowHint(string title, string text) { }

    /// <summary>After hiding to the tray: give memory back to Windows (lean while idle).</summary>
    public virtual void TrimMemory()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        Native.EmptyWorkingSet(Process.GetCurrentProcess().Handle);
    }
}
