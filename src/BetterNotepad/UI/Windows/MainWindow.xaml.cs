using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterNotepad.Core;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Views;
using BetterNotepad.UI.Controls;
using BetterNotepad.UI.Themes;

namespace BetterNotepad.UI.Windows;

/// <summary>
/// A top-level window: title bar (menu, command palette, window buttons), one or two tab groups,
/// status bar and overlays. The main window and torn-off windows are the same class.
/// </summary>
public partial class MainWindow : Window
{
    private readonly Dictionary<TabGroup, TabGroupView> _groupViews = new();
    private TabGroupView? _activeGroupView;
    private bool _closingForShutdown;
    private HwndSource? _hwndSource;
    private bool _maxHot;

    public MainWindow(WindowModel model)
    {
        Model = model;
        InitializeComponent();
        if (Model.Pinned)
        {
            // per-pixel transparency (set before the window exists): ghost mode is real see-through. WPF strips a layered
            // style it did not create itself, so SetLayeredWindowAttributes on a normal window would not stick.
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
        }
        AppIcon.Source = AppIconSource;
        Title = AppInfo.ProductName;

        BuildMenu();
        BuildGroups();
        ApplyPresentation(); // before the placement: a pinned window has smaller minimum sizes
        ApplyPlacement();
        InitExplorer();

        Model.Groups.CollectionChanged += OnGroupsChanged;
        Model.PropertyChanged += OnModelChanged;
        SourceInitialized += OnSourceInitialized;
        Activated += OnWindowActivated;
        StateChanged += OnStateChanged;
        IsVisibleChanged += OnVisibleChanged;
        LocationChanged += (_, _) => CapturePlacement();
        SizeChanged += (_, _) => CapturePlacement();
        Closing += OnClosing;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        DragOver += OnFileDragOver;
        Drop += OnFileDrop;
        PreviewMouseWheel += OnPinnedWheel;
        PreviewMouseMove += OnPinnedMouseMove;
        MouseLeave += OnPinnedMouseLeave;
        Loc.LanguageChanged += OnLanguageChanged;
        ThemeManager.I.Changed += OnThemeChanged;

        if (AppEnv.TestMode)
        {
            // invisible for the user: never on screen, never activated, no taskbar button
            ShowActivated = false;
            ShowInTaskbar = false;
            Left = -32000;
            Top = -32000;
        }
        Topmost = Model.Topmost;
        UpdatePinButton();
    }

    public WindowModel Model { get; }

    public ToastHost Toasts => ToastLayer;

    public DialogHost DialogLayer => Dialogs;

    public static MainWindow? Of(DependencyObject d) => GetWindow(d) as MainWindow;

    public static ImageSource AppIconSource { get; } = LoadAppIcon();

    private static ImageSource LoadAppIcon()
    {
        try
        {
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/BetterNotepad;component/Assets/app.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - 32)).First();
        }
        catch
        {
            return new DrawingImage();
        }
    }

    // ───────────── groups ─────────────

    public TabGroupView? ActiveGroupView => _activeGroupView;

    public Document? ActiveDocument => Model.ActiveGroup.Active;

    public IDocumentView? ActiveView => _activeGroupView?.ActiveView;

    private TabGroupView ViewFor(TabGroup g)
    {
        if (_groupViews.TryGetValue(g, out var v)) return v;
        v = new TabGroupView(this, g) { Compact = Model.Pinned };
        v.Activated += OnGroupActivated;
        v.ActiveViewChanged += OnGroupActiveViewChanged;
        _groupViews[g] = v;
        return v;
    }

    private void BuildGroups()
    {
        GroupsHost.Children.Clear();
        GroupsHost.ColumnDefinitions.Clear();
        GroupsHost.RowDefinitions.Clear();
        foreach (var gone in _groupViews.Keys.Where(g => !Model.Groups.Contains(g)).ToList())
        {
            _groupViews[gone].DisposeViews();
            _groupViews.Remove(gone);
        }

        if (Model.Groups.Count == 1)
        {
            GroupsHost.Children.Add(ViewFor(Model.Groups[0]));
        }
        else
        {
            var horizontal = Model.SplitOrientation == Orientation.Horizontal;
            var a = ViewFor(Model.Groups[0]);
            var b = ViewFor(Model.Groups[1]);
            var splitter = new GridSplitter
            {
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Focusable = false,
            };
            splitter.SetResourceReference(BackgroundProperty, "Brush.Border");
            splitter.DragCompleted += (_, _) => CaptureSplitRatio();
            var ratio = Model.SplitRatio;
            if (horizontal)
            {
                GroupsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star), MinWidth = 160 });
                GroupsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
                GroupsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - ratio, GridUnitType.Star), MinWidth = 160 });
                Grid.SetColumn(a, 0);
                Grid.SetColumn(splitter, 1);
                Grid.SetColumn(b, 2);
                splitter.Cursor = Cursors.SizeWE;
            }
            else
            {
                GroupsHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ratio, GridUnitType.Star), MinHeight = 120 });
                GroupsHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
                GroupsHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - ratio, GridUnitType.Star), MinHeight = 120 });
                Grid.SetRow(a, 0);
                Grid.SetRow(splitter, 1);
                Grid.SetRow(b, 2);
                splitter.Cursor = Cursors.SizeNS;
            }
            GroupsHost.Children.Add(a);
            GroupsHost.Children.Add(splitter);
            GroupsHost.Children.Add(b);
        }
        SetActiveGroupView(ViewFor(Model.ActiveGroup));
    }

    private void CaptureSplitRatio()
    {
        if (Model.Groups.Count < 2) return;
        double first, second;
        if (Model.SplitOrientation == Orientation.Horizontal)
        {
            first = GroupsHost.ColumnDefinitions[0].ActualWidth;
            second = GroupsHost.ColumnDefinitions[2].ActualWidth;
        }
        else
        {
            first = GroupsHost.RowDefinitions[0].ActualHeight;
            second = GroupsHost.RowDefinitions[2].ActualHeight;
        }
        if (first + second > 0) Model.SplitRatio = first / (first + second);
    }

    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e) => BuildGroups();

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WindowModel.SplitOrientation):
                BuildGroups();
                break;
            case nameof(WindowModel.ActiveGroup):
                SetActiveGroupView(ViewFor(Model.ActiveGroup));
                break;
            case nameof(WindowModel.Topmost):
                Topmost = Model.Topmost;
                UpdatePinButton();
                break;
            case nameof(WindowModel.Pinned):
                ApplyPresentation();
                break;
            case nameof(WindowModel.Ghost) or nameof(WindowModel.GhostOpacity):
                ApplyGhost();
                break;
            case nameof(WindowModel.ExplorerWidth):
                FitExplorerWidth();
                break;
        }
    }

    private void OnGroupActivated(TabGroupView view)
    {
        if (Model.ActiveGroup != view.Group) Model.ActiveGroup = view.Group;
        else SetActiveGroupView(view);
    }

    private void SetActiveGroupView(TabGroupView view)
    {
        _activeGroupView = view;
        foreach (var v in _groupViews.Values) v.IsGroupActive = v == view || Model.Groups.Count == 1;
        OnGroupActiveViewChanged(view);
    }

    private void OnGroupActiveViewChanged(TabGroupView view)
    {
        if (view != _activeGroupView) return;
        Status.Bind(view.ActiveView);
        UpdateTitle();
        if (Model.Pinned)
        {
            _pinBar?.Bind(ActiveDocument);
            if (view.ActiveView is ICompactView compact) compact.EnterCompact();
        }
        if (ActiveDocument is { External: not ExternalChange.None } d && IsActive) QueueExternalPrompt(d);
        _explorer?.FollowActive(ActiveDocument);
    }

    private void UpdateTitle()
    {
        var doc = ActiveDocument;
        Title = doc is null ? AppInfo.ProductName : $"{(doc.IsDirty ? "● " : "")}{doc.Title} — {AppInfo.ProductName}";
    }

    // ───────────── window chrome ─────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowEffects.RemoveSystemCaptionButtons(this);
        WindowEffects.Apply(this);
        if (AppEnv.TestMode)
        {
            // belt and braces for test instances: never activated, no taskbar / Alt+Tab entry
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        }
        _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hwndSource?.AddHook(WndProc);
        if (Model.Maximized && !AppEnv.TestMode) WindowState = WindowState.Maximized;
        ApplyGhost();
    }

    private void OnThemeChanged()
    {
        WindowEffects.Apply(this);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        MaxGlyph.Glyph = WindowState == WindowState.Maximized ? Glyphs.ChromeRestore : Glyphs.ChromeMaximize;
        Model.Maximized = WindowState == WindowState.Maximized;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseButton(object sender, RoutedEventArgs e) => Close();

    private void OnSettingsButton(object sender, RoutedEventArgs e) => ShowSettings();

    private void OnPalettePill(object sender, RoutedEventArgs e) => ShowPalette(PaletteMode.Files);

    /// <summary>The pill shrinks gracefully: first the shortcut chip goes, then the label (icon only), then the pill itself.</summary>
    private void OnPillSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var w = e.NewSize.Width;
        if (PalettePill.Template.FindName("PillKeysChip", PalettePill) is FrameworkElement keys) keys.Visibility = w < 230 ? Visibility.Collapsed : Visibility.Visible;
        var iconOnly = w < 110;
        if (PalettePill.Template.FindName("PillLabel", PalettePill) is FrameworkElement label) label.Visibility = iconOnly ? Visibility.Collapsed : Visibility.Visible;
        if (PalettePill.Template.FindName("PillIcon", PalettePill) is FrameworkElement icon)
        {
            Grid.SetColumnSpan(icon, iconOnly ? 3 : 1);
            icon.HorizontalAlignment = iconOnly ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        }
        PalettePill.Opacity = w < 30 ? 0 : 1;
        PalettePill.IsHitTestVisible = w >= 30;
    }

    /// <summary>Title-bar pin of a torn-off window: always on top in the full presentation (the compact one is "Pin on top").</summary>
    private void OnPinToggle(object sender, RoutedEventArgs e) => Model.Topmost = PinButton.IsChecked == true;

    private void UpdatePinButton()
    {
        PinButton.Visibility = Model.IsMain && !Model.Topmost ? Visibility.Collapsed : Visibility.Visible;
        PinButton.IsChecked = Model.Topmost;
    }

    /// <summary>WM_NCHITTEST over the maximize button → Windows 11 snap layouts; work-area sized maximize.</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_GETMINMAXINFO:
                FitMaximizedToWorkArea(hwnd, lParam);
                break;
            case Native.WM_NCHITTEST:
                if (IsOverMaxButton(lParam))
                {
                    SetMaxHot(true);
                    handled = true;
                    return Native.HTMAXBUTTON;
                }
                SetMaxHot(false);
                break;
            case Native.WM_NCMOUSELEAVE:
            case Native.WM_MOUSEMOVE:
                SetMaxHot(false);
                break;
            case Native.WM_NCLBUTTONDOWN:
                if (wParam == Native.HTMAXBUTTON)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
                break;
            case Native.WM_NCLBUTTONUP:
                if (wParam == Native.HTMAXBUTTON)
                {
                    handled = true;
                    ToggleMaximize();
                    return IntPtr.Zero;
                }
                break;
            case Native.WM_EXITSIZEMOVE:
                SavePinState(); // a pinned window was moved or resized by its edges
                break;
            case Native.WM_SETTINGCHANGE:
                if (lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
                    ThemeManager.I.OnSystemThemeChanged();
                break;
        }
        return IntPtr.Zero;
    }

    private bool IsOverMaxButton(IntPtr lParam)
    {
        if (!MaxButton.IsVisible) return false;
        var x = (short)((long)lParam & 0xFFFF);
        var y = (short)(((long)lParam >> 16) & 0xFFFF);
        try
        {
            var p = MaxButton.PointFromScreen(new Point(x, y));
            return p.X >= 0 && p.Y >= 0 && p.X < MaxButton.ActualWidth && p.Y < MaxButton.ActualHeight;
        }
        catch
        {
            return false;
        }
    }

    private void SetMaxHot(bool hot)
    {
        if (_maxHot == hot) return;
        _maxHot = hot;
        MaxButton.Tag = hot ? "hot" : null;
    }

    private static void FitMaximizedToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        if (monitor == 0) return;
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) return;
        var mmi = Marshal.PtrToStructure<Native.MINMAXINFO>(lParam);
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    // ───────────── placement ─────────────

    private void ApplyPlacement()
    {
        var work = SystemParameters.WorkArea;
        if (Model.Bounds is { } b && b.Width >= MinWidth && b.Height >= MinHeight && IsOnAnyScreen(b))
        {
            Left = b.Left;
            Top = b.Top;
            Width = b.Width;
            Height = b.Height;
        }
        else
        {
            Width = Math.Min(1180, work.Width * 0.85);
            Height = Math.Min(760, work.Height * 0.85);
            Left = work.Left + (work.Width - Width) / 2;
            Top = work.Top + (work.Height - Height) / 2;
        }
    }

    /// <summary>A saved position on a monitor that is gone → back to a visible one.</summary>
    private static bool IsOnAnyScreen(Rect r)
    {
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var probe = new Rect(r.Left + 40, r.Top + 10, Math.Max(80, r.Width - 80), 30);
        return vs.IntersectsWith(probe) && vs.Contains(new Point(r.Left + r.Width / 2, r.Top + 20));
    }

    private void CapturePlacement()
    {
        if (!IsLoaded || AppEnv.TestMode || WindowState != WindowState.Normal) return;
        Model.Bounds = new Rect(Left, Top, Width, Height);
    }

    /// <summary>Cursor position in device pixels.</summary>
    public static Point CursorScreenPosition()
    {
        Native.GetCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    /// <summary>Device pixels → this window's DIPs.</summary>
    public Point DeviceToDip(Point devicePoint)
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget is { } t ? t.TransformFromDevice.Transform(devicePoint) : devicePoint;
    }

    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = Model.Maximized ? WindowState.Maximized : WindowState.Normal;
        if (AppEnv.TestMode) return; // test instances never take the focus away from the user
        Activate();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0) Native.SetForegroundWindow(hwnd);
        ActiveView?.FocusContent();
    }

    // ───────────── keyboard ─────────────

    public CommandContext Context()
    {
        var view = ActiveView;
        var editor = view?.Editor;
        var editorFocused = editor is not null && editor.TextArea.IsKeyboardFocusWithin;
        return new CommandContext
        {
            Window = this,
            Document = ActiveDocument,
            Group = Model.ActiveGroup,
            Editor = editorFocused ? editor : null,
        };
    }

    public bool Execute(string commandId) => CommandRegistry.TryExecute(commandId, Context());

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Dialogs.IsOpen) return;
        if (SettingsPage.InterceptKey(this, e)) return; // open settings own the keyboard (and record shortcuts)
        var chord = KeyChord.FromEvent(e);
        if (chord.IsModifierOnly) return;

        if (chord is { Modifiers: ModifierKeys.Alt, Key: Key.F4 })
        {
            e.Handled = true;
            Close();
            return;
        }
        if (_tabSwitcher is { IsOpen: true } && _tabSwitcher.HandleKey(e)) return;
        if (IsOverlayFocused() && chord.Modifiers == ModifierKeys.None) return;
        // the focused folder tree first: F2 / Delete / Enter / arrows mean its entries there, not the active tab
        if (_explorer is { IsKeyboardFocusWithin: true } explorer)
        {
            if (explorer.HandleKey(e))
            {
                e.Handled = true;
                return;
            }
            if (explorer.IsEditingName && chord.Modifiers == ModifierKeys.None) return;
        }

        var ctx = Context();
        var cmd = Keymap.Match(chord, ctx);
        if (cmd is null) return;
        e.Handled = true;
        try
        {
            cmd.Execute(ctx);
        }
        catch (Exception ex)
        {
            Log.Error($"command {cmd.Id} failed", ex);
            Toasts.Show(Loc.T("error.command", cmd.Title, ex.Message), ToastKind.Error);
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_tabSwitcher is { IsOpen: true }) _tabSwitcher.HandleKeyUp(e);
    }

    private bool IsOverlayFocused() => OverlayLayer.IsKeyboardFocusWithin;

    // ───────────── language / menu refresh ─────────────

    private void OnLanguageChanged()
    {
        // later: the change may come from a click inside the very menu being rebuilt
        Dispatcher.BeginInvoke(BuildMenu, System.Windows.Threading.DispatcherPriority.Background);
        UpdateTitle();
    }

    // ───────────── drag & drop files ─────────────

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            // a dropped folder opens in the Explorer panel, files open in tabs
            foreach (var f in files)
            {
                if (Directory.Exists(f)) OpenFolder(f);
                else OpenFile(f);
            }
            e.Handled = true;
            Activate();
        }
    }

    // ───────────── closing ─────────────

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingForShutdown) return;
        CaptureViewState();
        if (Model.IsMain)
        {
            var system = SettingsService.I.Get<SystemSettings>();
            if (Tray.TrayService.IsAvailable && system.TrayEnabled && system.CloseToTray && !AppEnv.TestMode)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            e.Cancel = true;
            App.Current.Quit();
            return;
        }
        // a torn-off window: its tabs go back to the main window, nothing is lost
        var main = Workspace.I.Main;
        foreach (var g in Model.Groups)
            foreach (var d in g.Tabs.ToList())
            {
                if (!main.Groups.Any(mg => mg.Tabs.Contains(d))) main.ActiveGroup.Add(d, activate: false);
                g.Remove(d);
            }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Loc.LanguageChanged -= OnLanguageChanged;
        ThemeManager.I.Changed -= OnThemeChanged;
        Model.Groups.CollectionChanged -= OnGroupsChanged;
        Model.PropertyChanged -= OnModelChanged;
        foreach (var v in _groupViews.Values) v.DisposeViews();
        _explorer?.Dispose(); // its watcher goes with the window
        Toasts.Clear(); // pending toast timers would fire later and animate in a closed window
        _hwndSource?.RemoveHook(WndProc);
        if (!Model.IsMain) Workspace.I.Windows.Remove(Model);
        // the last window on screen (e.g. a tab pinned on top) closed while the main window waits in the tray
        if (!Model.IsMain && !App.Current.IsShuttingDown && App.Current.MainAppWindow is { IsVisible: false }) Tray.TrayRest.Settle();
    }

    public void HideToTray()
    {
        Tray.TrayService.I?.OnMainHidden(this);
        Hide();
        var system = SettingsService.I.Get<SystemSettings>();
        if (!system.CloseToTrayHintShown)
        {
            system.CloseToTrayHintShown = true;
            Tray.TrayService.I?.ShowHint(Loc.T("tray.hintTitle"), Loc.T("tray.hintText"));
        }
        Tray.TrayRest.Enter(this);
    }

    /// <summary>Hidden in the tray: views drop what they rebuild by themselves (<see cref="TabGroupView.ReleaseRendering"/>), toasts go.</summary>
    internal void ReleaseForTray()
    {
        CaptureViewState();
        foreach (var v in _groupViews.Values) v.ReleaseRendering();
        Toasts.Clear();
        // no blinking caret in a hidden window; BringToFront focuses the active view again
        if (IsKeyboardFocusWithin) Keyboard.ClearFocus();
    }

    /// <summary>Shown again (tray icon, hotkey, a file from Explorer…): no more trims while the main window is up.</summary>
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && Model.IsMain) Tray.TrayRest.Leave();
    }

    public void CloseForShutdown()
    {
        _closingForShutdown = true;
        Close();
    }

    public void CaptureViewState()
    {
        foreach (var v in _groupViews.Values) v.CaptureState();
        SavePinState();
    }
}
