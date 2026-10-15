using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using BetterNotepad.Core;

namespace BetterNotepad.Modules.Explorer;

/// <summary>What a row of the tree is.</summary>
public enum NodeRole
{
    /// <summary>A file or folder on disk.</summary>
    Item,
    /// <summary>A transient line under a folder: loading, access denied, gone.</summary>
    Placeholder,
    /// <summary>The inline name box of a file or folder being created.</summary>
    NewItem,
}

/// <summary>
/// One entry of the folder tree. Children are loaded lazily (null = never listed or dropped to save memory);
/// a folder keeps its expansion flag while collapsed under a collapsed parent.
/// </summary>
public sealed class ExplorerNode : ObservableObject
{
    private string _name;
    private string _fullPath;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _hasFocusRing;
    private bool _isOpen;
    private bool _isDirty;
    private bool _isEditing;
    private bool _isNewFolder;

    public ExplorerNode(ExplorerNode? parent, string name, string fullPath, bool isDirectory, NodeRole role = NodeRole.Item)
    {
        Parent = parent;
        _name = name;
        _fullPath = fullPath;
        IsDirectory = isDirectory;
        Role = role;
        Depth = parent is null ? -1 : parent.Depth + 1;
        Icon = ExplorerRules.IconFor(name, isDirectory);
    }

    public ExplorerNode? Parent { get; }

    /// <summary>Level below the open folder (its direct entries are 0; the folder itself is -1).</summary>
    public int Depth { get; }

    public NodeRole Role { get; }

    public bool IsDirectory { get; }

    public string Name
    {
        get => _name;
        private set => Set(ref _name, value);
    }

    public string FullPath
    {
        get => _fullPath;
        private set => Set(ref _fullPath, value);
    }

    public FileIconKind Icon { get; private set; }

    /// <summary>Localization key of a placeholder line.</summary>
    public string? TextKey { get; init; }

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>The selected row of a tree that has the keyboard focus (thin accent ring).</summary>
    public bool HasFocusRing { get => _hasFocusRing; set => Set(ref _hasFocusRing, value); }

    /// <summary>A file open in a tab (any window).</summary>
    public bool IsOpen { get => _isOpen; set => Set(ref _isOpen, value); }

    /// <summary>Open with unsaved changes.</summary>
    public bool IsDirty { get => _isDirty; set => Set(ref _isDirty, value); }

    /// <summary>The inline rename box is shown in this row.</summary>
    public bool IsEditing { get => _isEditing; set => Set(ref _isEditing, value); }

    /// <summary>For a <see cref="NodeRole.NewItem"/> row: a folder is being created (icon), else a file.</summary>
    public bool IsNewFolder { get => _isNewFolder; set => Set(ref _isNewFolder, value); }

    /// <summary>Loaded entries (sorted: folders first, natural order); null = not listed.</summary>
    internal List<ExplorerNode>? Children { get; set; }

    /// <summary>Bumped by every listing request: a slower, older result is dropped.</summary>
    internal int LoadVersion { get; set; }

    /// <summary>Loading / error line currently shown under this folder.</summary>
    internal ExplorerNode? Placeholder { get; set; }

    /// <summary>The listing in flight (one at a time; a request meanwhile sets <see cref="ListAgain"/>).</summary>
    internal Task? Listing { get; set; }

    internal bool ListAgain { get; set; }

    /// <summary>Removed from the tree (deleted, renamed away, its folder closed): late results for it are dropped.</summary>
    internal bool Detached { get; set; }

    public bool IsItem => Role == NodeRole.Item;

    /// <summary>Icon for the row: a new-item row follows what is being created / typed.</summary>
    public FileIconKind IconFor(string? typedName) => Role == NodeRole.NewItem
        ? (IsNewFolder ? FileIconKind.Folder : ExplorerRules.IconFor(string.IsNullOrEmpty(typedName) ? "x" : typedName, false))
        : Icon;

    /// <summary>After a rename on disk: new name and path, loaded descendants follow.</summary>
    internal void Rename(string name, string fullPath)
    {
        var old = _fullPath;
        Name = name;
        Icon = ExplorerRules.IconFor(name, IsDirectory);
        OnPropertyChanged(nameof(Icon));
        Move(old, fullPath);
    }

    private void Move(string oldBase, string newBase)
    {
        if (ExplorerRules.Rebase(_fullPath, oldBase, newBase) is { } moved) FullPath = moved;
        if (Children is null) return;
        foreach (var c in Children) c.Move(oldBase, newBase);
    }

    /// <summary>A case-only change seen by a refresh ("readme.md" → "README.md").</summary>
    internal void UpdateCase(string name)
    {
        if (name == _name) return;
        Rename(name, Path.Combine(Path.GetDirectoryName(_fullPath) ?? "", name));
    }

    public override string ToString() => Role == NodeRole.Item ? _fullPath : $"[{Role}] {TextKey}";
}

/// <summary>
/// The visible rows (a flattened tree). Bulk changes are one Reset instead of thousands of events: WPF's collection
/// views refuse range notifications, and a virtualized list regenerates only the rows in view after a Reset.
/// </summary>
public sealed class RowList : ObservableCollection<ExplorerNode>
{
    private const int BulkThreshold = 24;

    public void InsertRange(int index, IReadOnlyList<ExplorerNode> items)
    {
        if (items.Count == 0) return;
        if (items.Count <= BulkThreshold)
        {
            for (var i = 0; i < items.Count; i++) Insert(index + i, items[i]);
            return;
        }
        CheckReentrancy();
        ((List<ExplorerNode>)Items).InsertRange(index, items);
        RaiseReset();
    }

    public void RemoveRange(int index, int count)
    {
        if (count <= 0) return;
        if (count <= BulkThreshold)
        {
            for (var i = 0; i < count; i++) RemoveAt(index);
            return;
        }
        CheckReentrancy();
        ((List<ExplorerNode>)Items).RemoveRange(index, count);
        RaiseReset();
    }

    public void ReplaceAll(IReadOnlyList<ExplorerNode> items)
    {
        CheckReentrancy();
        var list = (List<ExplorerNode>)Items;
        list.Clear();
        list.AddRange(items);
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
