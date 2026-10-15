using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using BetterNotepad.Core;

namespace BetterNotepad.Documents;

/// <summary>A row of tabs with one active document. A window has one or two groups (split).</summary>
public sealed class TabGroup : ObservableObject
{
    private Document? _active;
    private readonly List<Document> _mru = new();

    public TabGroup(Guid? id = null) => Id = id ?? Guid.NewGuid();

    public Guid Id { get; }

    public ObservableCollection<Document> Tabs { get; } = new();

    /// <summary>Most recently used first (Ctrl+Tab order).</summary>
    public IReadOnlyList<Document> Mru => _mru;

    public Document? Active
    {
        get => _active;
        set
        {
            if (value is not null && !Tabs.Contains(value)) return;
            if (!Set(ref _active, value) || value is null) return;
            _mru.Remove(value);
            _mru.Insert(0, value);
        }
    }

    public int PinnedCount => Tabs.Count(t => t.IsPinned);

    /// <summary>Adds a tab (after the active one by default); pinned tabs always stay in front.</summary>
    public void Add(Document doc, int index = -1, bool activate = true)
    {
        if (!Tabs.Contains(doc))
        {
            var pinned = PinnedCount;
            if (index < 0) index = _active is null ? Tabs.Count : Tabs.IndexOf(_active) + 1;
            index = doc.IsPinned ? Math.Clamp(index, 0, pinned) : Math.Clamp(index, pinned, Tabs.Count);
            Tabs.Insert(index, doc);
            _mru.Add(doc);
        }
        if (activate) Active = doc;
    }

    /// <summary>Removes a tab; the previously used tab becomes active.</summary>
    public void Remove(Document doc)
    {
        var index = Tabs.IndexOf(doc);
        if (index < 0) return;
        Tabs.RemoveAt(index);
        _mru.Remove(doc);
        if (_active == doc)
        {
            _active = null;
            Active = _mru.FirstOrDefault() ?? (Tabs.Count > 0 ? Tabs[Math.Min(index, Tabs.Count - 1)] : null);
            if (Active is null) OnPropertyChanged(nameof(Active));
        }
    }

    public void Move(Document doc, int newIndex)
    {
        var old = Tabs.IndexOf(doc);
        if (old < 0) return;
        var pinned = PinnedCount;
        newIndex = doc.IsPinned ? Math.Clamp(newIndex, 0, pinned - 1) : Math.Clamp(newIndex, pinned, Tabs.Count - 1);
        if (old != newIndex) Tabs.Move(old, newIndex);
    }

    /// <summary>Re-sorts after a pin/unpin so pinned tabs sit in front.</summary>
    public void Normalize(Document changed)
    {
        var index = Tabs.IndexOf(changed);
        if (index < 0) return;
        var pinnedBefore = Tabs.Take(index).Count(t => t.IsPinned);
        if (changed.IsPinned) Tabs.Move(index, pinnedBefore);
        else
        {
            var lastPinned = Tabs.Count(t => t.IsPinned);
            Tabs.Move(index, Math.Min(lastPinned, Tabs.Count - 1));
        }
    }

    public void RestoreMru(IEnumerable<Document> order)
    {
        _mru.Clear();
        foreach (var d in order) if (Tabs.Contains(d) && !_mru.Contains(d)) _mru.Add(d);
        foreach (var d in Tabs) if (!_mru.Contains(d)) _mru.Add(d);
    }
}

/// <summary>Model of one top-level window: its tab groups, split and placement.</summary>
public sealed class WindowModel : ObservableObject
{
    private TabGroup _activeGroup;
    private Orientation _splitOrientation = Orientation.Horizontal;
    private double _splitRatio = 0.5;
    private bool _topmost;
    private bool _ghost;
    private bool _pinned;
    private double _ghostOpacity = 0.55;

    public WindowModel(Guid? id = null, bool isMain = false)
    {
        Id = id ?? Guid.NewGuid();
        IsMain = isMain;
        _activeGroup = new TabGroup();
        Groups.Add(_activeGroup);
    }

    public Guid Id { get; }

    public bool IsMain { get; }

    public ObservableCollection<TabGroup> Groups { get; } = new();

    public TabGroup ActiveGroup
    {
        get => _activeGroup;
        set
        {
            if (Groups.Contains(value)) Set(ref _activeGroup, value);
        }
    }

    public bool IsSplit => Groups.Count > 1;

    /// <summary>Horizontal = side by side, Vertical = one above the other.</summary>
    public Orientation SplitOrientation { get => _splitOrientation; set => Set(ref _splitOrientation, value); }

    public double SplitRatio { get => _splitRatio; set => Set(ref _splitRatio, Math.Clamp(value, 0.1, 0.9)); }

    public bool Topmost { get => _topmost; set => Set(ref _topmost, value); }

    /// <summary>
    /// A tab pinned on top: a secondary window in the compact presentation (no menu, tab strip or status bar,
    /// a bar on hover), always topmost. Never the main window.
    /// </summary>
    public bool Pinned
    {
        get => _pinned;
        set
        {
            if (IsMain || !Set(ref _pinned, value)) return;
            if (value) Topmost = true;
            else Ghost = false;
        }
    }

    /// <summary>Click-through and translucent (only for pinned windows; never restored from the session).</summary>
    public bool Ghost { get => _ghost; set => Set(ref _ghost, value); }

    /// <summary>Opacity while in ghost mode (the hover bar's slider; remembered per file).</summary>
    public double GhostOpacity { get => _ghostOpacity; set => Set(ref _ghostOpacity, Math.Clamp(value, 0.15, 1)); }

    public Rect? Bounds { get; set; }

    public bool Maximized { get; set; }

    // ───────────── Explorer (folder tree) of this window ─────────────

    private string? _explorerRoot;
    private bool _explorerVisible;
    private double _explorerWidth = DefaultExplorerWidth;
    private IReadOnlyList<string> _explorerExpanded = [];

    public const double DefaultExplorerWidth = 260;

    /// <summary>The folder shown in the Explorer panel (null = empty state).</summary>
    public string? ExplorerRoot { get => _explorerRoot; set => Set(ref _explorerRoot, value); }

    public bool ExplorerVisible { get => _explorerVisible; set => Set(ref _explorerVisible, value); }

    /// <summary>Width chosen by dragging the panel's edge (DIPs; the window caps what is shown at half its width).</summary>
    public double ExplorerWidth { get => _explorerWidth; set => Set(ref _explorerWidth, Math.Clamp(double.IsFinite(value) ? value : DefaultExplorerWidth, 170, 1600)); }

    /// <summary>Expanded folders, relative to <see cref="ExplorerRoot"/> (bounded list).</summary>
    public IReadOnlyList<string> ExplorerExpanded { get => _explorerExpanded; set => Set(ref _explorerExpanded, value ?? []); }

    public TabGroup AddGroup()
    {
        var g = new TabGroup();
        Groups.Add(g);
        OnPropertyChanged(nameof(IsSplit));
        return g;
    }

    public void RemoveGroup(TabGroup g)
    {
        if (Groups.Count <= 1 || !Groups.Remove(g)) return;
        if (_activeGroup == g) ActiveGroup = Groups[0];
        OnPropertyChanged(nameof(IsSplit));
    }

    public IEnumerable<Document> AllDocuments => Groups.SelectMany(g => g.Tabs).Distinct();
}

/// <summary>All windows. The main window always exists; secondary windows come from torn-off tabs.</summary>
public sealed class Workspace
{
    public static Workspace I { get; } = new();

    public ObservableCollection<WindowModel> Windows { get; } = new();

    public WindowModel Main => Windows.First(w => w.IsMain);

    public bool IsOpenAnywhere(Document doc) => Windows.Any(w => w.Groups.Any(g => g.Tabs.Contains(doc)));

    public IEnumerable<TabGroup> AllGroups => Windows.SelectMany(w => w.Groups);

    public (WindowModel Window, TabGroup Group)? Locate(Document doc)
    {
        foreach (var w in Windows)
            foreach (var g in w.Groups)
                if (g.Tabs.Contains(doc)) return (w, g);
        return null;
    }
}
