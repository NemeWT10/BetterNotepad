using System.Runtime;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Documents.Session;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.UI.Tray;

/// <summary>
/// What the app does while it waits in the tray (budget ≤ 50 MB, 0 % CPU). On the way in: the session and unsaved buffers
/// are written, hidden windows drop what their views rebuild by themselves (PDF pages, Markdown previews), toasts go.
/// Then the memory goes back to Windows: a compacting full GC and a working-set trim once the dispatcher is idle, and again
/// after the work started by hiding has settled (the render thread and the PDF engine freeing their buffers, ~4 s) and
/// after idle thread-pool threads have retired (~20 s, their exit touches memory). After that nothing runs — no timer,
/// no polling — until a window shows up again.
/// </summary>
internal static class TrayRest
{
    /// <summary>
    /// The later trims, counted from entering the tray: with a GC while the hiding work may still leave garbage behind; the last
    /// one only gives back the pages that retiring threads touched (a GC there would page the whole heap in again).
    /// </summary>
    private static readonly (TimeSpan After, bool Gc)[] LateTrims = [(TimeSpan.FromSeconds(4), true), (TimeSpan.FromSeconds(25), false)];

    private static DispatcherTimer? _timer;
    private static int _generation;

    /// <summary>Trims done so far (tests).</summary>
    internal static int Trims { get; private set; }

    /// <summary>A later trim is still scheduled (tests).</summary>
    internal static bool Pending => _timer is not null;

    /// <summary>The main window went to the tray.</summary>
    public static void Enter(MainWindow main)
    {
        try
        {
            SessionService.I.Flush(); // everything on disk while we sit in the tray
        }
        catch (Exception ex)
        {
            Log.Error("tray: session flush failed", ex);
        }
        // torn-off / pinned windows that stay on screen keep everything
        foreach (var w in App.Current.OpenWindows.ToList())
            if (!w.IsVisible) w.ReleaseForTray();
        Settle();
    }

    /// <summary>Schedules the trims (also used by an autostart straight into the tray). Cancelled when the main window shows.</summary>
    public static void Settle()
    {
        var generation = ++_generation;
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => TrimIfStill(generation, gc: true));

        _timer?.Stop();
        var timer = _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher) { Interval = LateTrims[0].After };
        var next = 0;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_timer != timer) return;
            TrimIfStill(generation, LateTrims[next].Gc);
            next++;
            if (next < LateTrims.Length && generation == _generation)
            {
                timer.Interval = LateTrims[next].After - LateTrims[next - 1].After;
                timer.Start();
            }
            else
            {
                _timer = null;
            }
        };
        timer.Start();
    }

    /// <summary>The main window is visible again: the trims still pending are dropped.</summary>
    public static void Leave()
    {
        _generation++;
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>Only while nothing is on screen: a window still in use (a tab pinned on top) would just page everything back in.</summary>
    private static void TrimIfStill(int generation, bool gc)
    {
        if (generation != _generation || App.Current is not { } app || app.OpenWindows.Any(w => w.IsVisible)) return;
        Trim(gc);
    }

    /// <summary>Compacting full GC (large objects too) with finalizers, then the working set goes back to Windows.</summary>
    private static void Trim(bool gc)
    {
        if (gc)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        Native.EmptyWorkingSet(Native.CurrentProcess);
        Trims++;
    }
}
