using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.Documents.Files;
using BetterNotepad.UI;
using BetterNotepad.UI.Controls;

namespace BetterNotepad.Modules.Explorer;

/// <summary>File operations of the panel: inline new / rename with validation as you type, delete to the Recycle Bin, menus.</summary>
public sealed partial class ExplorerPanel
{
    /// <summary>The inline name box: a rename of an entry, or the row of a file / folder being created.</summary>
    private sealed class InlineEdit
    {
        public required ExplorerNode Node { get; init; }
        public required ExplorerNode Folder { get; init; }
        public required TextBox Box { get; init; }
        public bool IsNew => Node.Role == NodeRole.NewItem;
        public bool Done { get; set; }
    }

    private InlineEdit? _edit;
    private bool _menuOpen;

    /// <summary>The box a row shows in place of the name while it is edited.</summary>
    public TextBox? EditorFor(ExplorerNode node) => _edit is { Done: false } e && ReferenceEquals(e.Node, node) ? e.Box : null;

    /// <summary>Text of the open name box (tests).</summary>
    internal TextBox? EditorBox => _edit is { Done: false } e ? e.Box : null;

    /// <summary>The message shown under the name box, null when the name is fine (tests).</summary>
    internal string? NameProblem => _bubble.Visibility == Visibility.Visible ? _bubbleText.Text : null;

    /// <summary>Where "new file / folder" goes: the selected folder, the selected file's folder, else the open folder.</summary>
    private ExplorerNode TargetFolder()
    {
        if (_selected is { IsItem: true, Detached: false } s) return s.IsDirectory ? s : s.Parent ?? _tree!.Root;
        return _tree!.Root;
    }

    public async Task BeginNewItemAsync(bool folder, ExplorerNode? where = null)
    {
        if (_tree is null) return;
        CancelEdit();
        var dir = where ?? TargetFolder();
        var node = await _tree.AddNewItemRowAsync(dir, folder);
        if (node is null) return;
        StartEdit(node, dir, "");
    }

    public void BeginRename(ExplorerNode node)
    {
        if (_tree is null || !node.IsItem || node == _tree.Root || node.Detached) return;
        CancelEdit();
        Select(node);
        StartEdit(node, node.Parent ?? _tree.Root, node.Name);
    }

    private void StartEdit(ExplorerNode node, ExplorerNode folder, string text)
    {
        var box = new TextBox
        {
            Text = text,
            FontSize = 12.5,
            Height = 22,
            Margin = new Thickness(2, 0, 1, 0), // the typed text starts where the name was
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        box.SetResourceReference(StyleProperty, "Field");
        box.Padding = new Thickness(1, 0, 1, 1); // Field applies it twice (host margin + text view)
        AutomationProperties.SetAutomationId(box, "explorer.name");
        var edit = new InlineEdit { Node = node, Folder = folder, Box = box };
        _edit = edit;
        box.PreviewKeyDown += OnEditKey;
        box.TextChanged += (_, _) =>
        {
            if (_edit == edit) Validate();
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            if (_edit == edit && !edit.Done && !box.IsKeyboardFocusWithin) _ = CommitEditAsync(fromBlur: true);
        };
        node.IsEditing = true;
        ScrollIntoView(node, center: false);
        _list.UpdateLayout(); // realizes the row, which hosts the box
        UpdateTools();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_edit != edit || edit.Done) return;
            box.Focus();
            Keyboard.Focus(box);
            var (start, length) = ExplorerRules.RenameSelection(text, node.IsDirectory || node.IsNewFolder);
            box.Select(start, length);
        });
    }

    private void OnEditKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = CommitEditAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelEdit();
            FocusTree();
        }
    }

    /// <summary>Message for a typed name: null = fine, "" = nothing typed yet, else the problem.</summary>
    private string? ProblemWith(InlineEdit edit, string name)
    {
        switch (ExplorerRules.ValidateName(name))
        {
            case NameIssue.Empty: return "";
            case NameIssue.DotName: return Loc.T("explorer.name.dot");
            case NameIssue.InvalidCharacters: return Loc.T("explorer.name.invalid");
            case NameIssue.ReservedName: return Loc.T("explorer.name.reserved", name.Split('.')[0].Trim());
            case NameIssue.TrailingDotOrSpace: return Loc.T("explorer.name.trailing");
            case NameIssue.TooLong: return Loc.T("explorer.name.tooLong");
        }
        var target = Path.Combine(edit.Folder.FullPath, name);
        var sameEntry = !edit.IsNew && string.Equals(target, edit.Node.FullPath, StringComparison.OrdinalIgnoreCase); // a case-only rename
        if (!sameEntry && (File.Exists(target) || Directory.Exists(target))) return Loc.T("explorer.name.exists", name);
        return null;
    }

    private void Validate()
    {
        if (_edit is not { Done: false } edit) return;
        var name = edit.Box.Text.Trim();
        var problem = !edit.IsNew && name == edit.Node.Name ? null : ProblemWith(edit, name);
        edit.Box.Tag = string.IsNullOrEmpty(problem) ? null : "error";
        if (string.IsNullOrEmpty(problem)) HideBubble();
        else ShowBubble(problem);
    }

    /// <summary>Enter (or leaving the box): creates / renames, or keeps the box open with the problem shown. Leaving with a bad name cancels.</summary>
    public async Task<bool> CommitEditAsync(bool fromBlur = false)
    {
        if (_edit is not { Done: false } edit || _tree is null) return false;
        var name = edit.Box.Text.Trim();
        if (!edit.IsNew && name == edit.Node.Name)
        {
            EndEdit();
            if (!fromBlur) FocusTree();
            return false;
        }
        var problem = ProblemWith(edit, name);
        if (problem is not null)
        {
            if (fromBlur || problem.Length == 0)
            {
                CancelEdit();
                if (!fromBlur) FocusTree();
                return false;
            }
            ShowBubble(problem);
            edit.Box.Tag = "error";
            Motion.Shake(edit.Box);
            return false;
        }
        EndEdit();
        return edit.IsNew ? await CreateAsync(edit.Folder, name, edit.Node.IsNewFolder) : await RenameAsync(edit.Node, name);
    }

    public void CancelEdit()
    {
        if (_edit is not { Done: false }) return;
        EndEdit();
    }

    private void EndEdit()
    {
        if (_edit is not { } edit) return;
        _edit = null;
        edit.Done = true;
        var hadFocus = edit.Box.IsKeyboardFocusWithin;
        edit.Node.IsEditing = false; // the row puts the name back
        if (edit.IsNew) _tree?.RemoveRow(edit.Node);
        HideBubble();
        UpdateTools();
        if (hadFocus) _list.Focus();
    }

    private async Task<bool> CreateAsync(ExplorerNode folder, string name, bool isFolder)
    {
        var path = Path.Combine(folder.FullPath, name);
        try
        {
            if (isFolder) Directory.CreateDirectory(path);
            else using (new FileStream(path, FileMode.CreateNew, FileAccess.Write)) { }
        }
        catch (Exception ex)
        {
            _window.Toasts.Show(Loc.T("explorer.create.failed", name, ex.Message), ToastKind.Error);
            return false;
        }
        if (_tree is null) return true;
        await _tree.ListAsync(folder);
        if (_tree?.Find(path) is { } node) Select(node);
        PublishExpanded();
        if (isFolder) FocusTree();
        else _window.OpenFile(path); // a new file opens in a tab, ready for typing
        return true;
    }

    private async Task<bool> RenameAsync(ExplorerNode node, string name)
    {
        var source = node.FullPath;
        var target = Path.Combine(Path.GetDirectoryName(source)!, name);
        // the app's own watchers on folders of open files inside would block the move
        if (node.IsDirectory) FileWatchService.I.ReleaseUnder(source);
        try
        {
            if (node.IsDirectory)
            {
                if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                {
                    // a case-only change of a folder goes through a temporary name
                    var temp = source + ".~" + Guid.NewGuid().ToString("N")[..6];
                    Directory.Move(source, temp);
                    Directory.Move(temp, target);
                }
                else
                {
                    Directory.Move(source, target);
                }
            }
            else
            {
                File.Move(source, target);
            }
        }
        catch (Exception ex)
        {
            FileWatchService.I.Sync();
            Log.Warn($"explorer: rename {source} → {name} failed: {ex.Message}");
            _window.Toasts.Show(Loc.T("rename.failed", ex.Message), ToastKind.Error);
            FocusTree();
            return false;
        }
        _tree?.ApplyRename(node, name);
        _window.OnExplorerRenamed(source, target);
        Select(node);
        PublishExpanded();
        FocusTree();
        await Task.CompletedTask;
        return true;
    }

    /// <summary>Deletes after a confirmation (Recycle Bin; permanent only in test mode, where <paramref name="confirm"/> may be off).</summary>
    public async Task<bool> DeleteAsync(ExplorerNode node, bool confirm = true)
    {
        if (_tree is null || !node.IsItem || node == _tree.Root || node.Detached) return false;
        CancelEdit();
        if (confirm)
        {
            var choice = await _window.DialogLayer.Show(
                Loc.T(node.IsDirectory ? "explorer.delete.folderTitle" : "explorer.delete.fileTitle", node.Name),
                Loc.T(node.IsDirectory ? "explorer.delete.folderText" : "explorer.delete.fileText"),
            [
                new DialogButton("delete", Loc.T("explorer.delete.confirm"), DialogButtonKind.Danger, IsDefault: true),
                new DialogButton("cancel", Loc.T("common.cancel"), DialogButtonKind.Ghost, IsCancel: true),
            ], glyph: Glyphs.Delete);
            if (choice != "delete")
            {
                FocusTree();
                return false;
            }
        }
        var path = node.FullPath;
        var isDir = node.IsDirectory;
        var index = _tree?.Rows.IndexOf(node) ?? -1;
        var wasSelected = ReferenceEquals(node, _selected);
        if (isDir) FileWatchService.I.ReleaseUnder(path); // open handles inside would block it
        var error = await ExplorerFileOps.DeleteAsync(path, isDir, new WindowInteropHelper(_window).Handle);
        if (error is not null)
        {
            FileWatchService.I.Sync();
            Log.Warn($"explorer: delete {path} failed: {error}");
            _window.Toasts.Show(Loc.T("explorer.delete.failed", node.Name, error), ToastKind.Error);
            return false;
        }
        if (_tree is null) return true;
        if (!node.Detached) _tree.ApplyDelete(node);
        _window.OnExplorerDeleted(path);
        if (wasSelected && index >= 0 && _tree.Rows.Count > 0) SelectAt(Math.Min(index, _tree.Rows.Count - 1)); // the neighbor takes over
        PublishExpanded();
        return true;
    }

    public void CopyPath(ExplorerNode node, bool relative)
    {
        if (_tree is null) return;
        var text = relative ? ExplorerRules.RelativePath(_tree.RootPath, node.FullPath) : node.FullPath;
        try
        {
            ExplorerFileOps.Copy(text);
            _window.Toasts.Show(Loc.T("tab.copied", text), ToastKind.Success, seconds: 1.4);
        }
        catch (Exception ex)
        {
            _window.Toasts.Show(ex.Message, ToastKind.Error);
        }
    }

    public void Reveal(ExplorerNode node)
    {
        try
        {
            ExplorerFileOps.Reveal(node.FullPath, openInside: node == _tree?.Root);
        }
        catch (Exception ex)
        {
            _window.Toasts.Show(ex.Message, ToastKind.Error);
        }
    }

    // ───────────── error bubble ─────────────

    private void ShowBubble(string text)
    {
        _bubbleText.Text = text;
        _bubble.Visibility = Visibility.Visible;
        PositionBubble();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PositionBubble);
    }

    private void HideBubble() => _bubble.Visibility = Visibility.Collapsed;

    /// <summary>Just under the name box (above it near the bottom edge), as wide as the panel allows.</summary>
    private void PositionBubble()
    {
        if (_bubble.Visibility != Visibility.Visible) return;
        if (_edit is not { Done: false } edit || !edit.Box.IsLoaded || !edit.Box.IsVisible || _overlay.ActualWidth <= 0)
        {
            _bubble.Opacity = 0;
            return;
        }
        _bubble.Opacity = 1;
        var at = edit.Box.TranslatePoint(new Point(0, 0), _overlay);
        var left = Math.Max(8, at.X);
        var width = Math.Max(120, _overlay.ActualWidth - left - 8);
        _bubble.Width = width;
        _bubble.Measure(new Size(width, double.PositiveInfinity));
        var below = at.Y + edit.Box.ActualHeight + 3;
        var top = below + _bubble.DesiredSize.Height > _overlay.ActualHeight - 4 ? at.Y - _bubble.DesiredSize.Height - 3 : below;
        Canvas.SetLeft(_bubble, left);
        Canvas.SetTop(_bubble, top);
    }

    // ───────────── menus ─────────────

    private static MenuItem Item(string key, string glyph, Action click, string? gesture = null, bool enabled = true)
    {
        var item = new MenuItem
        {
            Header = Loc.T(key),
            InputGestureText = gesture ?? "",
            IsEnabled = enabled,
            Icon = new Icon { Glyph = glyph, FontSize = 13 },
        };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>Right-click menu of an entry (or of the open folder, for the empty area below the rows).</summary>
    public ContextMenu BuildItemMenu(ExplorerNode? node)
    {
        var menu = new ContextMenu();
        if (_tree is null) return menu;
        var target = node ?? _tree.Root;
        var isRoot = target == _tree.Root;
        if (!target.IsDirectory)
        {
            menu.Items.Add(Item("explorer.open", ExplorerLook.OpenFileGlyph, () => OpenNode(target, focus: true), "Enter"));
            menu.Items.Add(new Separator());
        }
        var folder = target.IsDirectory ? target : target.Parent ?? _tree.Root;
        menu.Items.Add(Item("explorer.newFile", ExplorerLook.NewFileGlyph, () => _ = BeginNewItemAsync(false, folder)));
        menu.Items.Add(Item("explorer.newFolder", ExplorerLook.NewFolderGlyph, () => _ = BeginNewItemAsync(true, folder)));
        menu.Items.Add(new Separator());
        if (isRoot)
        {
            menu.Items.Add(Item("explorer.refresh", Glyphs.Refresh, () => _ = RefreshAsync()));
            menu.Items.Add(Item("explorer.collapseAll", ExplorerLook.CollapseAllGlyph, CollapseAll));
        }
        else
        {
            menu.Items.Add(Item("explorer.rename", Glyphs.Rename, () => BeginRename(target), "F2"));
            menu.Items.Add(Item("explorer.delete", Glyphs.Delete, () => _ = DeleteAsync(target), "Delete"));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("explorer.copyPath", Glyphs.Copy, () => CopyPath(target, relative: false)));
        if (!isRoot) menu.Items.Add(Item("explorer.copyRelative", ExplorerLook.LinkGlyph, () => CopyPath(target, relative: true)));
        menu.Items.Add(Item("explorer.reveal", Glyphs.Folder, () => Reveal(target)));
        if (isRoot)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("explorer.close", ExplorerLook.CloseFolderGlyph, () => _window.CloseFolder()));
        }
        return menu;
    }

    /// <summary>The header's "…": folder-wide actions (and the ones a narrow header has no room for).</summary>
    public ContextMenu BuildMoreMenu()
    {
        var menu = new ContextMenu();
        if (_tree is null) return menu;
        var root = _tree.Root;
        menu.Items.Add(Item("explorer.refresh", Glyphs.Refresh, () => _ = RefreshAsync()));
        menu.Items.Add(Item("explorer.collapseAll", ExplorerLook.CollapseAllGlyph, CollapseAll));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("explorer.copyPath", Glyphs.Copy, () => CopyPath(root, relative: false)));
        menu.Items.Add(Item("explorer.reveal", Glyphs.Folder, () => Reveal(root)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("explorer.openOther", ExplorerLook.OpenFolderGlyph, () => _window.OpenFolderDialog()));
        menu.Items.Add(Item("explorer.close", ExplorerLook.CloseFolderGlyph, () => _window.CloseFolder()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("explorer.hide", ExplorerLook.PanelGlyph, () => _window.HideExplorer(), Keymap.GestureText("view.toggleExplorer")));
        return menu;
    }

    private void OpenContextMenu(ExplorerNode? node, MouseButtonEventArgs? mouse)
    {
        var menu = BuildItemMenu(node);
        if (menu.Items.Count == 0) return;
        if (mouse is not null)
        {
            menu.PlacementTarget = _list;
            menu.Placement = PlacementMode.MousePoint;
        }
        else
        {
            var row = node is null ? null : FindRow(node);
            menu.PlacementTarget = (UIElement?)row ?? _list;
            menu.Placement = row is null ? PlacementMode.Center : PlacementMode.Bottom;
            if (row is not null) menu.HorizontalOffset = 24;
        }
        Track(menu);
        menu.IsOpen = true;
    }

    private void ShowMoreMenu()
    {
        var menu = BuildMoreMenu();
        menu.PlacementTarget = _moreButton;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = -6;
        Track(menu);
        menu.IsOpen = true;
    }

    /// <summary>While a menu is open the header's actions stay visible.</summary>
    private void Track(ContextMenu menu)
    {
        _menuOpen = true;
        UpdateTools();
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            UpdateTools();
        };
    }

    /// <summary>The realized row showing <paramref name="node"/> (null when it is scrolled out of view).</summary>
    internal ExplorerRow? FindRow(ExplorerNode node)
    {
        if (_list.ItemContainerGenerator.ContainerFromItem(node) is not DependencyObject container) return null;
        return FindChild<ExplorerRow>(container);
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } deeper) return deeper;
        }
        return null;
    }
}
