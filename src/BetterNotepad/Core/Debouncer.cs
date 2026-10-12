using System.Windows.Threading;

namespace BetterNotepad.Core;

/// <summary>
/// Runs an action once after a quiet period. The timer exists only while something is pending,
/// so an idle app has no ticking timers.
/// </summary>
public sealed class Debouncer(TimeSpan delay, Action action, DispatcherPriority priority = DispatcherPriority.Background)
{
    private DispatcherTimer? _timer;

    public bool IsPending => _timer?.IsEnabled == true;

    public void Trigger()
    {
        if (_timer is null)
        {
            _timer = new DispatcherTimer(priority) { Interval = delay };
            _timer.Tick += OnTick;
        }
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Runs the pending action immediately (e.g. on shutdown).</summary>
    public void Flush()
    {
        if (!IsPending) return;
        _timer!.Stop();
        action();
    }

    public void Cancel() => _timer?.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        _timer!.Stop();
        action();
    }
}
