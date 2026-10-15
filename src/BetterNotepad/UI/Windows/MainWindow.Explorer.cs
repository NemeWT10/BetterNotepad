using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Files;
using BetterNotepad.Documents.Session;
using BetterNotepad.Modules.Explorer;
using Microsoft.Win32;

namespace BetterNotepad.UI.Windows;

/// <summary>
/// The Explorer side panel of a window: shown / hidden (Ctrl+Shift+E) with a short width + opacity slide, resized by
/// dragging its edge (170 DIP … half the window), the open folder and what renames / deletes in it mean for open tabs.
/// Pinned-on-top windows never show it. The panel is created on first use; nothing is listed until it is on screen.
/// </summary>
public partial class MainWindow
{
    private ExplorerPanel? _explorer;
    private Thumb? _explorerGrip;
    private Border? _explorerGripLine;
    private bool _explorerHiding;

    public ExplorerPanel? Explorer => _explorer;

    /// <summary>The panel is part of this window's layout.</summary>
    public bool ExplorerShown => Model.ExplorerVisible && !Model.Pinned;

    private void InitExplorer()
    {
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) _explorer?.OnShown();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _explorer?.OnShown();
        };
        SizeChanged += (_, _) => FitExplorerWidth();
        if (ExplorerShown) ShowExplorer(animate: false);
    }

    private ExplorerPanel EnsureExplorer()
    {
        if (_explorer is not null) return _explorer;
        _explorer = new ExplorerPanel(this) { HorizontalAlignment = HorizontalAlignment.Right };
        ExplorerSlot.Children.Add(_explorer);
        BuildExplorerGrip();
        _explorer.SetRoot(Model.ExplorerRoot, Model.ExplorerExpanded, quietIfMissing: true);
        return _explorer;
    }

    /// <summary>Shown width: the remembered one, at most half the window.</summary>
    private double ExplorerWidthNow()
    {
        var window = ActualWidth > 0 ? ActualWidth : double.IsNaN(Width) ? 1180 : Width;
        var max = Math.Max(ExplorerPanel.MinPanelWidth, window * 0.5);
        return Math.Clamp(Model.ExplorerWidth, ExplorerPanel.MinPanelWidth, max);
    }

    public void ToggleExplorer()
    {
        if (ExplorerShown) HideExplorer();
        else ShowExplorer(focus: true);
    }

    public void ShowExplorer(bool animate = true, bool focus = false)
    {
        if (Model.Pinned) return;
        var panel = EnsureExplorer();
        var wasShown = ExplorerSlot.Visibility == Visibility.Visible && !_explorerHiding;
        Model.ExplorerVisible = true;
        _explorerHiding = false;
        var width = ExplorerWidthNow();
        panel.Width = width;
        ExplorerSlot.Visibility = Visibility.Visible;
        _explorerGrip!.Visibility = Visibility.Visible;
        _explorerGripLine!.Visibility = Visibility.Visible;
        if (animate && !wasShown && Motion.I.Enabled && IsVisible)
        {
            // the panel keeps its width and slides out of the left edge while the slot opens
            var from = ExplorerSlot.ActualWidth;
            ExplorerSlot.Width = width;
            ExplorerSlot.BeginAnimation(WidthProperty, new DoubleAnimation(from, width, TimeSpan.FromMilliseconds(180)) { EasingFunction = Motion.EaseOut, FillBehavior = FillBehavior.Stop });
            panel.Opacity = 1;
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = Motion.EaseOut, FillBehavior = FillBehavior.Stop });
        }
        else
        {
            ExplorerSlot.BeginAnimation(WidthProperty, null);
            ExplorerSlot.Width = width;
            panel.BeginAnimation(OpacityProperty, null);
            panel.Opacity = 1;
        }
        panel.OnShown();
        if (focus) Dispatcher.BeginInvoke(panel.FocusTree, System.Windows.Threading.DispatcherPriority.Input);
    }

    public void HideExplorer(bool animate = true)
    {
        if (!Model.ExplorerVisible && ExplorerSlot.Visibility != Visibility.Visible) return;
        var hadFocus = _explorer?.IsKeyboardFocusWithin == true;
        _explorer?.CancelEdit();
        Model.ExplorerVisible = false;
        if (_explorerGrip is not null) _explorerGrip.Visibility = Visibility.Collapsed;
        if (_explorerGripLine is not null) _explorerGripLine.Visibility = Visibility.Collapsed;
        if (animate && Motion.I.Enabled && IsVisible && ExplorerSlot.Visibility == Visibility.Visible && _explorer is { } panel)
        {
            _explorerHiding = true;
            var shrink = new DoubleAnimation(ExplorerSlot.ActualWidth, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = Motion.EaseOut };
            shrink.Completed += (_, _) =>
            {
                if (!_explorerHiding) return;
                CollapseExplorerSlot();
            };
            ExplorerSlot.BeginAnimation(WidthProperty, shrink);
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, TimeSpan.FromMilliseconds(150)));
        }
        else
        {
            CollapseExplorerSlot();
        }
        if (hadFocus) ActiveView?.FocusContent();
    }

    private void CollapseExplorerSlot()
    {
        _explorerHiding = false;
        ExplorerSlot.BeginAnimation(WidthProperty, null);
        ExplorerSlot.Width = 0;
        ExplorerSlot.Visibility = Visibility.Collapsed;
        _explorer?.BeginAnimation(OpacityProperty, null);
    }

    /// <summary>Pinned on top: the panel goes away; unpinned again: back as it was.</summary>
    private void ApplyExplorerPresentation()
    {
        if (Model.Pinned)
        {
            ExplorerSlot.Visibility = Visibility.Collapsed;
            if (_explorerGrip is not null) _explorerGrip.Visibility = Visibility.Collapsed;
            if (_explorerGripLine is not null) _explorerGripLine.Visibility = Visibility.Collapsed;
        }
        else if (Model.ExplorerVisible && _explorer is not null)
        {
            ShowExplorer(animate: false);
        }
    }

    /// <summary>The window was resized or the remembered width changed: at most half the window, at least the minimum.</summary>
    private void FitExplorerWidth()
    {
        if (!ExplorerShown || _explorer is null || _explorerHiding) return;
        var width = ExplorerWidthNow();
        if (Math.Abs(ExplorerSlot.Width - width) < 0.5 && Math.Abs(_explorer.Width - width) < 0.5) return;
        ExplorerSlot.BeginAnimation(WidthProperty, null); // a running slide-in ends where it was going
        ExplorerSlot.Width = width;
        _explorer.Width = width;
    }

    // ───────────── resizing by the edge ─────────────

    private void BuildExplorerGrip()
    {
        // an 8 px handle over the panel's edge; after a short hover the edge lights up in the accent (VS Code's sash)
        var line = new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, -1, 0), Opacity = 0, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        line.SetResourceReference(Border.BackgroundProperty, "Brush.AccentGradient");
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        var grip = new Thumb
        {
            Width = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, -4, 0),
            Cursor = Cursors.SizeWE,
            Focusable = false,
            Visibility = Visibility.Collapsed,
            Template = new ControlTemplate(typeof(Thumb)) { VisualTree = surface },
        };
        void Light(bool on, bool delayed)
        {
            var a = new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(Motion.I.Enabled ? (on ? 140 : 200) : 0));
            if (on && delayed && Motion.I.Enabled) a.BeginTime = TimeSpan.FromMilliseconds(260);
            line.BeginAnimation(OpacityProperty, a);
        }
        grip.MouseEnter += (_, _) => Light(true, delayed: true);
        grip.MouseLeave += (_, _) =>
        {
            if (!grip.IsDragging) Light(false, delayed: false);
        };
        grip.DragStarted += (_, _) => Light(true, delayed: false);
        grip.DragDelta += (_, e) =>
        {
            if (_explorer is null) return;
            var max = Math.Max(ExplorerPanel.MinPanelWidth, ActualWidth * 0.5);
            var width = Math.Clamp(ExplorerSlot.ActualWidth + e.HorizontalChange, ExplorerPanel.MinPanelWidth, max);
            ExplorerSlot.BeginAnimation(WidthProperty, null);
            ExplorerSlot.Width = width;
            _explorer.Width = width;
        };
        grip.DragCompleted += (_, _) =>
        {
            Model.ExplorerWidth = ExplorerSlot.Width;
            if (!grip.IsMouseOver) Light(false, delayed: false);
        };
        grip.MouseDoubleClick += (_, e) =>
        {
            Model.ExplorerWidth = WindowModel.DefaultExplorerWidth;
            FitExplorerWidth();
            e.Handled = true;
        };
        Panel.SetZIndex(line, 5);
        Panel.SetZIndex(grip, 6);
        WorkArea.Children.Add(line);
        WorkArea.Children.Add(grip);
        _explorerGrip = grip;
        _explorerGripLine = line;
    }

    // ───────────── folder ─────────────

    /// <summary>Opens a folder in the panel (and shows it). A pinned window hands it to the main window.</summary>
    public void OpenFolder(string path)
    {
        if (Model.Pinned && App.Current.MainAppWindow is { } main && main != this)
        {
            main.BringToFront();
            main.OpenFolder(path);
            return;
        }
        string full;
        try
        {
            full = ExplorerRules.NormalizeDir(path);
        }
        catch
        {
            Toasts.Show(Loc.T("explorer.notFolder", path), ToastKind.Warning);
            return;
        }
        if (!Directory.Exists(full))
        {
            Toasts.Show(Loc.T("explorer.notFolder", path), ToastKind.Warning);
            return;
        }
        var panel = EnsureExplorer();
        if (!FileUtil.PathEquals(Model.ExplorerRoot, full) || panel.Tree is null)
        {
            Model.ExplorerRoot = full;
            Model.ExplorerExpanded = [];
            panel.SetRoot(full, null);
        }
        SettingsService.I.Get<FilesSettings>().LastFolder = full;
        ShowExplorer();
    }

    public void CloseFolder()
    {
        Model.ExplorerRoot = null;
        Model.ExplorerExpanded = [];
        _explorer?.SetRoot(null, null);
    }

    /// <summary>Folder picker (never in test mode: a real dialog would block the hidden test instance).</summary>
    public void OpenFolderDialog()
    {
        if (Model.Pinned && App.Current.MainAppWindow is { } main && main != this)
        {
            main.BringToFront();
            main.OpenFolderDialog();
            return;
        }
        if (AppEnv.TestMode)
        {
            Log.Info("explorer: folder dialog skipped in test mode");
            return;
        }
        var files = SettingsService.I.Get<FilesSettings>();
        var dlg = new OpenFolderDialog
        {
            Title = Loc.T("explorer.dialogTitle"),
            Multiselect = false,
            InitialDirectory = Model.ExplorerRoot ?? CurrentFolder() ?? files.LastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) == true) OpenFolder(dlg.FolderName);
    }

    /// <summary>The empty state's second button: the folder of the active (saved) file.</summary>
    public void OpenActiveFileFolder()
    {
        if (CurrentFolder() is { } dir) OpenFolder(dir);
    }

    // ───────────── what the panel's file operations mean for open tabs ─────────────

    /// <summary>Renamed in the tree: open tabs of the file (or of anything inside the folder) follow the new path.</summary>
    internal void OnExplorerRenamed(string oldPath, string newPath)
    {
        foreach (var d in DocumentManager.I.Documents.ToList())
        {
            if (d.FilePath is not { } p || ExplorerRules.Rebase(p, oldPath, newPath) is not { } moved || string.Equals(moved, p, StringComparison.Ordinal)) continue;
            d.ChangePath(moved);
            if (FileStateStore.I.IsLocked(p))
            {
                FileStateStore.I.SetLocked(p, false);
                FileStateStore.I.SetLocked(moved, true);
            }
            RecentFiles.I.Remove(p);
            RecentFiles.I.Add(moved);
        }
        FileWatchService.I.Sync();
        UpdateTitle();
    }

    /// <summary>
    /// Deleted in the tree: tabs of those files without unsaved changes close (like VS Code); a tab with changes keeps
    /// them and gets the usual "deleted on disk" question from the watcher.
    /// </summary>
    internal void OnExplorerDeleted(string path)
    {
        foreach (var d in DocumentManager.I.Documents.ToList())
        {
            if (d.FilePath is not { } p || !ExplorerRules.IsSameOrUnder(path, p) || d.IsDirty) continue;
            var places = Workspace.I.Windows.SelectMany(w => w.Groups.Where(g => g.Tabs.Contains(d)).Select(g => (Window: w, Group: g))).ToList();
            foreach (var (w, g) in places) g.Remove(d);
            BackupStore.Delete(d.Id);
            DocumentManager.I.Close(d);
            foreach (var (w, g) in places) App.Current.WindowFor(w)?.CleanupGroup(g);
        }
        FileWatchService.I.Sync();
    }
}
