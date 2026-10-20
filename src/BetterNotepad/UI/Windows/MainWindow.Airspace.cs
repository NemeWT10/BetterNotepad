using System.Windows;
using System.Windows.Threading;

namespace BetterNotepad.UI.Windows;

/// <summary>
/// Airspace: a WebView2 preview is a native child window drawn above every WPF element of the window, so an in-window
/// overlay (palette, settings, tab switcher, a dialog, toasts) would vanish behind it. The window reports when such overlays
/// come and go; a preview then swaps its live page for a still picture of itself while it is covered.
/// </summary>
public partial class MainWindow
{
    /// <summary>Room around the toast stack for the cards' soft shadow and their rise-in animation (DIPs).</summary>
    private const double ToastShadowRoom = 28;

    private bool _airspaceQueued;

    /// <summary>
    /// Before the window's shortcuts: content with a keyboard world of its own (a focused WebView2 page) may take a chord —
    /// e.g. the editor's find shortcut opens the page's find bar. True = handled.
    /// </summary>
    public static Func<MainWindow, Core.Commands.KeyChord, bool>? FocusedContentKey { get; set; }

    /// <summary>
    /// An overlay came or went, or the toasts changed. Raised once the layout is up to date (so <see cref="ToastArea"/> holds
    /// the final position), at most once per dispatcher turn.
    /// </summary>
    public event Action? AirspaceChanged;

    /// <summary>The palette, settings page, tab switcher or a dialog lies over the window content.</summary>
    public bool HasCoveringOverlay => OverlayLayer.Children.Count > 0 || Dialogs.IsOpen;

    /// <summary>Where the toasts are (window coordinates, shadow included); null without toasts.</summary>
    public Rect? ToastArea
    {
        get
        {
            if (ToastLayer.Children.Count == 0 || !ToastLayer.IsVisible || ToastLayer.ActualWidth <= 0) return null;
            try
            {
                var r = ToastLayer.TransformToAncestor(this).TransformBounds(new Rect(0, 0, ToastLayer.ActualWidth, ToastLayer.ActualHeight));
                r.Inflate(ToastShadowRoom, ToastShadowRoom);
                return r;
            }
            catch (InvalidOperationException)
            {
                return null; // not connected (closing)
            }
        }
    }

    private void InitAirspace()
    {
        OverlayLayer.ChildrenChanged += QueueAirspace;
        Dialogs.OpenChanged += QueueAirspace;
        ToastLayer.Changed += QueueAirspace;
    }

    private void QueueAirspace()
    {
        if (_airspaceQueued) return;
        _airspaceQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _airspaceQueued = false;
            AirspaceChanged?.Invoke();
        }, DispatcherPriority.Loaded);
    }
}
