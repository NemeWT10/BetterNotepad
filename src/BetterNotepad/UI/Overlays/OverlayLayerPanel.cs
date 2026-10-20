using System.Windows;
using System.Windows.Controls;

namespace BetterNotepad.UI;

/// <summary>
/// The window's layer for in-window overlays (command palette, settings page, tab switcher). Tells when an overlay comes or
/// goes, so content drawn by a native child window (a WebView2 preview, which sits above every WPF element) can step aside.
/// </summary>
public sealed class OverlayLayerPanel : Grid
{
    /// <summary>An overlay was added or removed.</summary>
    public event Action? ChildrenChanged;

    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        ChildrenChanged?.Invoke();
    }
}
