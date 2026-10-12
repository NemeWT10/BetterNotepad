using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Session;

namespace BetterNotepad.UI.Windows;

/// <summary>
/// Pinned on top: a tab in its own compact, always-on-top window (no menu, tab strip or status bar; content edge to edge;
/// a bar slides in at the top edge). Plain wheel scrolls and Ctrl+wheel zooms the content as usual, Alt+wheel resizes
/// the window around its center; the edges resize too. Position, size and ghost opacity are remembered per file.
/// Ghost mode: translucent and click-through, with an always-visible badge naming the way out (global hotkey).
/// </summary>
public partial class MainWindow
{
    /// <summary>Pointer this close to the top edge (DIPs, below the 6 px resize border) reveals the bar.</summary>
    private const double RevealZone = 14;

    /// <summary>Below the bar the pointer may wander this far (e.g. onto the PDF toolbar) before the bar slides away.</summary>
    private const double KeepZone = 64;

    /// <summary>Transparent margin around the pinned card for its shadow (DIPs).</summary>
    internal const double FrameRoom = 10;

    private const double FrameRadius = 10;

    private PinnedBar? _pinBar;
    private GhostBadge? _ghostBadge;
    private DispatcherTimer? _barTimer;
    private bool _barShown;
    private bool _chromePinned;
    private bool _pinDragging;
    private double? _opacityPreview;

    internal PinnedBar? PinBar => _pinBar;

    internal GhostBadge? Badge => _ghostBadge;

    /// <summary>True while the hover bar is (being) shown.</summary>
    internal bool PinBarShown => _barShown;

    // ───────────── presentation ─────────────

    /// <summary>Normal window ↔ compact pinned presentation (called on construction and when <see cref="WindowModel.Pinned"/> changes).</summary>
    private void ApplyPresentation()
    {
        var pinned = Model.Pinned;
        var chrome = pinned ? Visibility.Collapsed : Visibility.Visible;
        TitleBack.Visibility = chrome;
        TitleLine.Visibility = chrome;
        TitleBar.Visibility = chrome;
        Status.Visibility = chrome;
        TitleRow.Height = pinned ? GridLength.Auto : new GridLength(40);
        foreach (var v in _groupViews.Values) v.Compact = pinned;
        MinWidth = pinned ? PinGeometry.MinSize.Width : 560;
        MinHeight = pinned ? PinGeometry.MinSize.Height : 380;

        if (pinned != _chromePinned)
        {
            _chromePinned = pinned;
            // no caption in the compact window: it moves by the bar's drag area; the edges (shadow margin + 4 px) resize
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = pinned ? 0 : 40,
                ResizeBorderThickness = new Thickness(pinned ? FrameRoom + 4 : 6),
                GlassFrameThickness = new Thickness(pinned ? 0 : -1),
                UseAeroCaptionButtons = false,
                CornerRadius = new CornerRadius(0),
            });
            // slim rounded card with a soft shadow of its own (a transparent window gets no DWM frame)
            PinShadow.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
            PinShadow.Margin = PinFrame.Margin = new Thickness(pinned ? FrameRoom : 0);
            PinFrame.CornerRadius = new CornerRadius(pinned ? FrameRadius : 0);
            PinFrame.BorderThickness = new Thickness(pinned ? 1 : 0);
            if (pinned)
            {
                PinFrame.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
                PinFrame.SetResourceReference(Border.BackgroundProperty, "Brush.Bg");
                Root.SizeChanged += ClipRootToFrame;
            }
        }

        if (pinned && _pinBar is null)
        {
            _pinBar = new PinnedBar(this);
            Grid.SetRow(_pinBar, 0);
            Root.Children.Add(_pinBar);
            // outside the card: it keeps full opacity while the card fades in ghost mode
            _ghostBadge = new GhostBadge { Margin = new Thickness(0, FrameRoom + 10, FrameRoom + 12, 0) };
            WindowShell.Children.Insert(WindowShell.Children.IndexOf(PinFrame) + 1, _ghostBadge);
            ToastLayer.Margin = new Thickness(0, 0, FrameRoom + 12, FrameRoom + 14);
        }
        if (_pinBar is not null)
        {
            if (!pinned) HidePinBar(animate: false);
            _pinBar.Bind(pinned ? ActiveDocument : null);
        }
        Topmost = Model.Topmost;
        ApplyGhost();
    }

    /// <summary>The content follows the card's rounded corners (inside its 1 px border).</summary>
    private void ClipRootToFrame(object sender, SizeChangedEventArgs e)
    {
        var r = FrameRadius - 1;
        Root.Clip = new System.Windows.Media.RectangleGeometry(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight), r, r);
    }

    // ───────────── pin / unpin ─────────────

    /// <summary>
    /// Pins a tab on top: it moves into its own compact always-on-top window at the bounds remembered for the file,
    /// else near the cursor. A tab that is already pinned just comes to the front.
    /// </summary>
    public void PinOnTop(TabGroup source, Document doc)
    {
        if (Model.Pinned) return;
        if (Workspace.I.Windows.FirstOrDefault(w => w.Pinned && w.AllDocuments.Contains(doc)) is { } existing)
        {
            App.Current.WindowFor(existing)?.BringToFront();
            return;
        }
        var model = new WindowModel { Pinned = true, Bounds = PinBoundsFor(doc) };
        if (doc.FilePath is { } path && FileStateStore.I.Get(path).PinOpacity is { } opacity) model.GhostOpacity = opacity;
        model.Groups[0].Add(doc);
        source.Remove(doc);
        Workspace.I.Windows.Add(model); // App shows the window
        CleanupGroup(source);
        if (App.Current.WindowFor(model) is not { } pinned) return;
        PinnedWindows.Touch(model);
        pinned.SavePinState();
        pinned.RevealPinBarBriefly();
        pinned.Toasts.Show(Loc.T("pin.pinned"), ToastKind.Info, seconds: 4.5);
    }

    /// <summary>Unpin: the tab goes back to the main window's active group and the pinned window closes.</summary>
    public void UnpinFromTop()
    {
        if (!Model.Pinned) return;
        SavePinState();
        _pinBar?.ClosePopover();
        var main = Workspace.I.Main;
        var docs = Model.Groups.SelectMany(g => g.Tabs.Select(d => (Group: g, Doc: d))).ToList();
        if (docs.Count == 0)
        {
            Close();
            return;
        }
        // the active tab last, so it ends up active in the main window
        foreach (var (g, d) in docs.OrderBy(x => x.Doc == ActiveDocument ? 1 : 0)) MoveTab(g, main.ActiveGroup, d);
    }

    internal void ClosePinnedTab()
    {
        if (ActiveDocument is not { } doc) return;
        SavePinState();
        Fire(CloseTabAsync(Model.ActiveGroup, doc));
    }

    private Rect PinBoundsFor(Document doc)
    {
        var cursor = CursorScreenPosition();
        var size = PinGeometry.DefaultSize(page: doc.Kind is DocumentKind.Pdf or DocumentKind.Image);
        if (doc.FilePath is { } path && FileStateStore.I.Get(path).PinBounds is [var l, var t, var w, var h]
            && w >= PinGeometry.MinSize.Width && h >= PinGeometry.MinSize.Height)
        {
            var remembered = new Rect(l, t, w, h);
            if (IsOnAnyScreen(remembered)) return remembered;
            size = remembered.Size; // its monitor is gone: same size, next to the cursor
        }
        return PinGeometry.PlaceNear(DeviceToDip(cursor), size, WorkAreaDip(cursor));
    }

    /// <summary>Position, size and ghost opacity of the pinned window → per-file state (and the session).</summary>
    internal void SavePinState()
    {
        if (!Model.Pinned || WindowState != WindowState.Normal) return;
        var bounds = new Rect(Left, Top, double.IsNaN(Width) ? ActualWidth : Width, double.IsNaN(Height) ? ActualHeight : Height);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        if (!AppEnv.TestMode) Model.Bounds = bounds;
        double[] stored = [Math.Round(bounds.Left, 1), Math.Round(bounds.Top, 1), Math.Round(bounds.Width, 1), Math.Round(bounds.Height, 1)];
        var opacity = Math.Round(Model.GhostOpacity, 2);
        foreach (var d in Model.AllDocuments)
            if (d.FilePath is { } path) FileStateStore.I.Update(path, s => s with { PinBounds = stored, PinOpacity = opacity });
    }

    // ───────────── moving and resizing ─────────────

    internal void BeginPinDrag()
    {
        if (AppEnv.TestMode || Mouse.LeftButton != MouseButtonState.Pressed) return;
        _pinDragging = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // the button was released already
        }
        finally
        {
            _pinDragging = false;
        }
        SavePinState();
    }

    private void OnPinnedWheel(object sender, MouseWheelEventArgs e)
    {
        // plain wheel scrolls and Ctrl+wheel zooms the content (the views do that themselves); Alt+wheel is the window's
        if (!Model.Pinned || Keyboard.Modifiers != ModifierKeys.Alt) return;
        e.Handled = true;
        ResizePinned(e.Delta > 0 ? PinGeometry.WheelStep : 1 / PinGeometry.WheelStep);
    }

    /// <summary>Alt+wheel: scale around the center, aspect ratio kept, within the monitor's work area.</summary>
    internal void ResizePinned(double factor)
    {
        if (!Model.Pinned || WindowState != WindowState.Normal) return;
        var current = new Rect(Left, Top, double.IsNaN(Width) ? ActualWidth : Width, double.IsNaN(Height) ? ActualHeight : Height);
        // a test window lives off-screen: scale it, but never pull it onto the user's screen
        var next = PinGeometry.ScaleAroundCenter(current, factor, WorkAreaDip(null), new Size(MinWidth, MinHeight), keepInside: !AppEnv.TestMode);
        Left = next.Left;
        Top = next.Top;
        Width = next.Width;
        Height = next.Height;
        SavePinState();
    }

    /// <summary>Work area (DIPs) of the monitor under <paramref name="devicePoint"/>, or of this window's monitor.</summary>
    private Rect WorkAreaDip(Point? devicePoint)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        nint monitor = devicePoint is { } p
            ? Modules.Sys.SysNative.MonitorFromPoint(new Native.POINT { X = (int)p.X, Y = (int)p.Y }, Native.MONITOR_DEFAULTTONEAREST)
            : hwnd != 0 ? Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST) : 0;
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (monitor == 0 || !Native.GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;
        var r = info.rcWork;
        var tl = DeviceToDip(new Point(r.Left, r.Top));
        var br = DeviceToDip(new Point(r.Right, r.Bottom));
        return new Rect(tl, br);
    }

    // ───────────── the hover bar ─────────────

    private void OnPinnedMouseMove(object sender, MouseEventArgs e)
    {
        if (!Model.Pinned || Model.Ghost || _pinBar is null || _pinDragging) return;
        var y = e.GetPosition(Root).Y;
        if (!_barShown)
        {
            if (y <= RevealZone) ShowPinBar();
            return;
        }
        if (y > PinnedBar.BarHeight + KeepZone && !_pinBar.PopoverOpen) ScheduleBarHide(220);
        else _barTimer?.Stop();
    }

    private void OnPinnedMouseLeave(object sender, MouseEventArgs e)
    {
        if (!Model.Pinned || _pinBar is null || _pinBar.PopoverOpen || _pinDragging) return;
        ScheduleBarHide(380);
    }

    internal void OnPinPopoverClosed()
    {
        if (!IsMouseOver) ScheduleBarHide(380);
    }

    /// <summary>Right after pinning: the bar shows for a moment so its controls are discovered.</summary>
    internal void RevealPinBarBriefly()
    {
        ShowPinBar();
        ScheduleBarHide(2800);
    }

    internal void ShowPinBar()
    {
        if (_pinBar is null || Model.Ghost) return;
        _barTimer?.Stop();
        if (_barShown) return;
        _barShown = true;
        _pinBar.Visibility = Visibility.Visible;
        if (Motion.I.Enabled)
        {
            // the bar grows and pushes the content down (no overlap with a view's own toolbar), its content fades in
            _pinBar.BeginAnimation(HeightProperty, new DoubleAnimation(PinnedBar.BarHeight, TimeSpan.FromMilliseconds(190)) { EasingFunction = Motion.EaseOut });
            _pinBar.Content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = Motion.EaseOut });
        }
        else
        {
            _pinBar.BeginAnimation(HeightProperty, null);
            _pinBar.Content.BeginAnimation(OpacityProperty, null);
            _pinBar.Height = PinnedBar.BarHeight;
            _pinBar.Content.Opacity = 1;
        }
    }

    internal void HidePinBar(bool animate = true)
    {
        _barTimer?.Stop();
        if (_pinBar is null) return;
        _pinBar.ClosePopover();
        if (!_barShown && _pinBar.Visibility != Visibility.Visible) return;
        _barShown = false;
        var bar = _pinBar;
        if (animate && Motion.I.Enabled && bar.IsVisible)
        {
            var shrink = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170)) { EasingFunction = Motion.EaseOut };
            shrink.Completed += (_, _) =>
            {
                if (!_barShown) bar.Visibility = Visibility.Collapsed;
            };
            bar.BeginAnimation(HeightProperty, shrink);
            bar.Content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
        }
        else
        {
            bar.BeginAnimation(HeightProperty, null);
            bar.Content.BeginAnimation(OpacityProperty, null);
            bar.Height = 0;
            bar.Visibility = Visibility.Collapsed;
        }
    }

    private void ScheduleBarHide(int ms)
    {
        if (!_barShown) return;
        if (_barTimer is null)
        {
            _barTimer = new DispatcherTimer(DispatcherPriority.Input);
            _barTimer.Tick += (_, _) =>
            {
                _barTimer.Stop(); // one-shot
                if (_pinBar is { PopoverOpen: false } && !_pinDragging) HidePinBar();
            };
        }
        _barTimer.Stop();
        _barTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _barTimer.Start();
    }

    // ───────────── ghost mode (translucent, click-through) ─────────────

    /// <summary>Ghost mode on / off for this pinned window (bar button, palette, global hotkey, tray menu).</summary>
    public void SetGhost(bool on)
    {
        if (!Model.Pinned || Model.Ghost == on) return;
        if (on) HidePinBar(animate: false);
        Model.Ghost = on; // → ApplyGhost
        if (on)
        {
            PinnedWindows.Touch(Model);
            Toasts.Show(PinnedWindows.GhostExitText("pin.ghostOn", "pin.ghostOnTray", "pin.ghostOnPalette", noBreak: true), ToastKind.Info, seconds: 5);
            HandActivationBack();
        }
        else
        {
            Toasts.Show(Loc.T("pin.ghostOff"), ToastKind.Info, seconds: 1.6);
        }
    }

    /// <summary>Live preview of the ghost opacity while its popover is open (null = back to opaque).</summary>
    internal void SetOpacityPreview(double? opacity)
    {
        _opacityPreview = opacity;
        ApplyGhost();
    }

    /// <summary>
    /// A pinned window is per-pixel transparent (layered by WPF), so its Opacity is real see-through; ghost mode adds
    /// WS_EX_TRANSPARENT (clicks pass to whatever is below). The opacity preview is the same alpha without click-through.
    /// Only pinned windows ever become ghosts.
    /// </summary>
    private void ApplyGhost()
    {
        var ghost = Model.Ghost && Model.Pinned;
        if (_ghostBadge is not null)
        {
            _ghostBadge.Refresh();
            _ghostBadge.Visibility = ghost ? Visibility.Visible : Visibility.Collapsed;
        }
        // the card (content, bar, shadow) fades; the badge and the toasts sit outside it and stay fully readable
        var opacity = !Model.Pinned || !AllowsTransparency ? 1 : ghost ? Model.GhostOpacity : _opacityPreview ?? 1;
        PinFrame.Opacity = opacity;
        PinShadow.Opacity = opacity;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        var next = ghost ? ex | Native.WS_EX_TRANSPARENT : ex & ~(nint)Native.WS_EX_TRANSPARENT;
        if (next != ex) Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, next);
    }

    /// <summary>A ghost keeps no keyboard focus: the window under the cursor gets the activation back.</summary>
    private void HandActivationBack()
    {
        if (AppEnv.TestMode || !IsActive) return;
        var self = new WindowInteropHelper(this).Handle;
        Native.GetCursorPos(out var p);
        var under = Native.WindowFromPoint(p);
        var root = under == 0 ? 0 : Native.GetAncestor(under, Native.GA_ROOT);
        if (root != 0 && root != self) Native.SetForegroundWindow(root);
    }

    // ───────────── for tests ─────────────

    /// <summary>Current extended style and the card's opacity (SelfTest).</summary>
    internal (nint ExStyle, double Opacity) GhostState()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        return (hwnd == 0 ? 0 : Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE), PinFrame.Opacity);
    }
}
