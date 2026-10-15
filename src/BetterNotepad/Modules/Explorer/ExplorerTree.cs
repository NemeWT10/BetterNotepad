using System.IO;
using System.IO.Enumeration;
using System.Windows.Threading;
using BetterNotepad.Core;

namespace BetterNotepad.Modules.Explorer;

/// <summary>
/// The model of one open folder: lazily listed nodes (off the UI thread), the flattened visible rows and one
/// FileSystemWatcher on the folder (subfolders included). Watcher events are coalesced (150 ms, at most ~1 s while
/// they keep coming) and re-list only folders that are loaded and visible; while the panel is hidden or the window is
/// minimized they only queue up. Everything except the listing itself runs on the UI thread.
/// </summary>
public sealed class ExplorerTree : IDisposable
{
    /// <summary>Folders remembered as expanded in the session (bounded).</summary>
    public const int MaxRemembered = 200;

    /// <summary>Collapsing a folder with more loaded entries than this releases them (memory); re-expanding lists again.</summary>
    private const int DropChildrenAbove = 400;

    private const int PendingCap = 4096;
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(900);

    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, ExplorerNode> _dirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _remember = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private bool _refreshAll;
    private bool _posted;
    private bool _deferred;
    private DispatcherTimer? _flushTimer;
    private DateTime _firstEvent;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    private readonly record struct Entry(string Name, bool IsDirectory);

    private sealed record Listing(List<Entry>? Entries, string? ErrorKey, bool Missing);

    public ExplorerTree(string rootPath, IEnumerable<string>? expandedRelative = null)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        RootPath = ExplorerRules.NormalizeDir(rootPath);
        Root = new ExplorerNode(null, ExplorerRules.DisplayName(RootPath), RootPath, true) { IsExpanded = true };
        foreach (var rel in expandedRelative ?? [])
        {
            try { _remember.Add(Path.GetFullPath(Path.Combine(RootPath, rel))); }
            catch { /* a broken entry of an old session */ }
        }
    }

    public string RootPath { get; }

    public ExplorerNode Root { get; }

    public RowList Rows { get; } = new();

    /// <summary>False while the panel is hidden or the window minimized: watcher batches wait.</summary>
    public Func<bool> CanApply { get; set; } = () => true;

    /// <summary>(open in a tab, unsaved) for a file path; applied to every node created.</summary>
    public Func<string, (bool Open, bool Dirty)>? FileState { get; set; }

    /// <summary>The open folder itself is gone (deleted or renamed by another program).</summary>
    public event Action? RootLost;

    /// <summary>Rows were removed (the panel re-checks its selection).</summary>
    public event Action? RowsRemoved;

    public bool IsListed => Root.Children is not null;

    public bool IsWatching => _watcher is not null;

    /// <summary>Watcher batches waiting for the panel to show again (tests).</summary>
    public bool HasDeferredChanges => _deferred;

    // ───────────── listing ─────────────

    /// <summary>First listing; then the watcher starts (only for a folder that exists) and remembered folders expand.</summary>
    public async Task LoadAsync()
    {
        await ListAsync(Root);
        if (_disposed || Root.Children is null) return;
        StartWatching();
        await ExpandRememberedAsync(Root);
    }

    /// <summary>Lists (or re-lists) a folder off the UI thread and merges the result into the tree and the rows.</summary>
    public Task ListAsync(ExplorerNode dir)
    {
        if (dir.Listing is { IsCompleted: false } running)
        {
            dir.ListAgain = true; // a change arrived while listing: list once more afterwards
            return running;
        }
        var task = ListCoreAsync(dir);
        dir.Listing = task;
        return task;
    }

    private async Task ListCoreAsync(ExplorerNode dir)
    {
        do
        {
            dir.ListAgain = false;
            var version = ++dir.LoadVersion;
            var path = dir.FullPath;
            var work = Task.Run(() => Enumerate(path));
            if (dir.Children is null && await Task.WhenAny(work, Task.Delay(140)) != work && !_disposed && version == dir.LoadVersion)
                ShowPlaceholder(dir, "explorer.loading");
            var result = await work;
            if (_disposed || dir.Detached || version != dir.LoadVersion) return;
            RemovePlaceholder(dir);
            if (result.Entries is null)
            {
                if (dir == Root && result.Missing)
                {
                    RootLost?.Invoke();
                    return;
                }
                dir.Children ??= [];
                _dirs[dir.FullPath] = dir;
                if (!result.Missing) ShowPlaceholder(dir, result.ErrorKey!);
                else if (dir.Parent is { } parent) _ = ListAsync(parent); // it vanished: its folder drops it
                return;
            }
            Merge(dir, result.Entries);
        }
        while (dir.ListAgain && !_disposed && !dir.Detached);
    }

    private static Listing Enumerate(string path)
    {
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
            };
            var items = new FileSystemEnumerable<Entry>(path, (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory), options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => ExplorerRules.ShouldShow(e.FileName, e.Attributes, e.IsDirectory),
            };
            var list = items.ToList();
            list.Sort((a, b) => ExplorerRules.CompareEntries(a.Name, a.IsDirectory, b.Name, b.IsDirectory));
            return new Listing(list, null, false);
        }
        catch (DirectoryNotFoundException)
        {
            return new Listing(null, "explorer.gone", true);
        }
        catch (UnauthorizedAccessException)
        {
            return new Listing(null, "explorer.denied", false);
        }
        catch (Exception ex)
        {
            Log.Warn($"explorer: cannot list {path}: {ex.Message}");
            return new Listing(null, "explorer.unreadable", !Directory.Exists(path));
        }
    }

    private ExplorerNode CreateNode(ExplorerNode parent, Entry e)
    {
        var node = new ExplorerNode(parent, e.Name, Path.Combine(parent.FullPath, e.Name), e.IsDirectory);
        if (!e.IsDirectory && FileState?.Invoke(node.FullPath) is { } state)
        {
            node.IsOpen = state.Open;
            node.IsDirty = state.Dirty;
        }
        return node;
    }

    /// <summary>New listing of a folder: kept nodes keep their state (expansion, selection), the rows change minimally.</summary>
    private void Merge(ExplorerNode dir, List<Entry> entries)
    {
        var old = dir.Children;
        Dictionary<string, ExplorerNode>? byName = null;
        if (old is not null)
        {
            byName = new Dictionary<string, ExplorerNode>(old.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var c in old) byName.TryAdd(c.Name, c);
        }
        var next = new List<ExplorerNode>(entries.Count);
        var caseChanged = false;
        foreach (var e in entries)
        {
            if (byName is not null && byName.Remove(e.Name, out var keep) && keep.IsDirectory == e.IsDirectory)
            {
                if (keep.Name != e.Name)
                {
                    RekeyAfter(keep, () => keep.UpdateCase(e.Name));
                    caseChanged = true;
                }
                next.Add(keep);
            }
            else
            {
                next.Add(CreateNode(dir, e));
            }
        }
        var removed = byName?.Values.ToList() ?? [];
        foreach (var r in removed) Detach(r);
        dir.Children = next;
        _dirs[dir.FullPath] = dir;

        if (!IsShown(dir)) return;
        if (old is null)
        {
            InsertChildRows(dir);
        }
        else if (caseChanged || removed.Count + Math.Max(0, next.Count - (old.Count - removed.Count)) > 64)
        {
            RebuildRows(dir);
        }
        else
        {
            DiffRows(dir, old, next);
        }
        if (removed.Count > 0) RowsRemoved?.Invoke();
    }

    /// <summary>Old and new child lists share their order (same names, same comparer): a merge walk inserts and removes rows in place.</summary>
    private void DiffRows(ExplorerNode dir, List<ExplorerNode> old, List<ExplorerNode> next)
    {
        var row = FirstChildRow(dir);
        if (row < 0) return;
        var nextSet = new HashSet<ExplorerNode>(next, ReferenceEqualityComparer.Instance);
        var oldSet = new HashSet<ExplorerNode>(old, ReferenceEqualityComparer.Instance);
        int i = 0, j = 0;
        bool At(ExplorerNode n) => row < Rows.Count && ReferenceEquals(Rows[row], n);
        while (i < old.Count || j < next.Count)
        {
            if (i < old.Count && j < next.Count && ReferenceEquals(old[i], next[j]))
            {
                if (!At(old[i])) break;
                row += 1 + SubtreeRows(row);
                i++;
                j++;
            }
            else if (i < old.Count && !nextSet.Contains(old[i]))
            {
                if (!At(old[i])) break;
                Rows.RemoveRange(row, 1 + SubtreeRows(row));
                i++;
            }
            else if (j < next.Count && !oldSet.Contains(next[j]))
            {
                Rows.Insert(row, next[j]);
                row++;
                j++;
            }
            else
            {
                break; // the order changed
            }
        }
        if (i < old.Count || j < next.Count) RebuildRows(dir); // out of step: rebuild this folder's rows
    }

    // ───────────── rows ─────────────

    /// <summary>The folder's own row is in view (every ancestor expanded) and its children are shown.</summary>
    private bool IsShown(ExplorerNode dir)
    {
        if (dir.Detached || !dir.IsExpanded) return false;
        for (var p = dir.Parent; p is not null; p = p.Parent)
            if (!p.IsExpanded || p.Detached) return false;
        return true;
    }

    /// <summary>Row index of the first entry under a folder (after its placeholder / new-item rows); -1 when the folder is not in view.</summary>
    private int FirstChildRow(ExplorerNode dir)
    {
        int row;
        if (dir == Root) row = 0;
        else
        {
            var at = Rows.IndexOf(dir);
            if (at < 0) return -1;
            row = at + 1;
        }
        while (row < Rows.Count && Rows[row].Depth == dir.Depth + 1 && !Rows[row].IsItem) row++;
        return row;
    }

    /// <summary>Number of rows below <paramref name="row"/> that belong to its subtree.</summary>
    public int SubtreeRows(int row)
    {
        var depth = Rows[row].Depth;
        var k = row + 1;
        while (k < Rows.Count && Rows[k].Depth > depth) k++;
        return k - row - 1;
    }

    private void VisibleSubtree(ExplorerNode dir, List<ExplorerNode> into)
    {
        if (dir.Placeholder is { } ph) into.Add(ph);
        if (dir.Children is null) return;
        foreach (var c in dir.Children)
        {
            into.Add(c);
            if (c.IsDirectory && c.IsExpanded) VisibleSubtree(c, into);
        }
    }

    private void InsertChildRows(ExplorerNode dir)
    {
        var row = FirstChildRow(dir);
        if (row < 0) return;
        var items = new List<ExplorerNode>();
        if (dir.Children is not null)
            foreach (var c in dir.Children)
            {
                items.Add(c);
                if (c.IsDirectory && c.IsExpanded) VisibleSubtree(c, items);
            }
        Rows.InsertRange(row, items);
    }

    /// <summary>Replaces every row under a folder (order changes, big batches): one Reset for large folders.</summary>
    private void RebuildRows(ExplorerNode dir)
    {
        if (dir == Root)
        {
            var all = new List<ExplorerNode>();
            // a new-item row at the top of the folder survives
            all.AddRange(Rows.TakeWhile(r => r.Depth == 0 && r.Role == NodeRole.NewItem));
            VisibleSubtree(Root, all);
            Rows.ReplaceAll(all);
            return;
        }
        var at = Rows.IndexOf(dir);
        if (at < 0) return;
        var start = FirstChildRow(dir);
        var end = at + 1 + SubtreeRows(at);
        Rows.RemoveRange(start, end - start);
        InsertChildRows(dir);
    }

    private void ShowPlaceholder(ExplorerNode dir, string key)
    {
        RemovePlaceholder(dir);
        var ph = new ExplorerNode(dir, "", dir.FullPath, false, NodeRole.Placeholder) { TextKey = key };
        dir.Placeholder = ph;
        if (!IsShown(dir)) return;
        var row = dir == Root ? 0 : Rows.IndexOf(dir) + 1;
        if (dir != Root && row == 0) return;
        Rows.Insert(row, ph);
    }

    private void RemovePlaceholder(ExplorerNode dir)
    {
        if (dir.Placeholder is not { } ph) return;
        dir.Placeholder = null;
        var i = Rows.IndexOf(ph);
        if (i >= 0) Rows.RemoveAt(i);
    }

    /// <summary>Inserts the inline name row of a file / folder being created at the top of <paramref name="dir"/> (expanded first).</summary>
    public async Task<ExplorerNode?> AddNewItemRowAsync(ExplorerNode dir, bool folder)
    {
        if (!dir.IsDirectory) return null;
        if (dir != Root && (!dir.IsExpanded || dir.Children is null)) await ExpandAsync(dir);
        if (!IsShown(dir)) return null;
        var node = new ExplorerNode(dir, "", dir.FullPath, folder, NodeRole.NewItem) { IsNewFolder = folder, IsEditing = true };
        var row = dir == Root ? 0 : Rows.IndexOf(dir) + 1;
        if (dir != Root && row == 0) return null;
        Rows.Insert(row, node);
        return node;
    }

    public void RemoveRow(ExplorerNode node)
    {
        var i = Rows.IndexOf(node);
        if (i >= 0) Rows.RemoveAt(i);
    }

    // ───────────── expand / collapse ─────────────

    public async Task ExpandAsync(ExplorerNode dir)
    {
        if (!dir.IsDirectory || !dir.IsItem || dir.Detached) return;
        _remember.Remove(dir.FullPath);
        if (!dir.IsExpanded)
        {
            dir.IsExpanded = true;
            if (dir.Children is not null && IsShown(dir))
            {
                InsertChildRows(dir);       // what we had, at once…
                _ = ListAsync(dir);         // …brought up to date in the background
            }
        }
        if (dir.Children is null) await ListAsync(dir);
        else if (dir.Listing is { IsCompleted: false } running) await running;
        await ExpandRememberedAsync(dir);
    }

    /// <summary>Children that the session (or a released subtree) remembers as expanded open again.</summary>
    private async Task ExpandRememberedAsync(ExplorerNode dir)
    {
        if (_remember.Count == 0 || dir.Children is null) return;
        var again = dir.Children.Where(c => c.IsDirectory && _remember.Contains(c.FullPath)).ToList();
        if (again.Count > 0) await Task.WhenAll(again.Select(ExpandAsync));
    }

    public void Collapse(ExplorerNode dir)
    {
        if (!dir.IsDirectory || dir == Root || !dir.IsExpanded) return;
        var at = Rows.IndexOf(dir);
        dir.IsExpanded = false;
        if (at >= 0) Rows.RemoveRange(at + 1, SubtreeRows(at));
        if (dir.Placeholder is not null) dir.Placeholder = null;
        if (CountLoaded(dir, DropChildrenAbove + 1) > DropChildrenAbove) Release(dir);
        RowsRemoved?.Invoke();
    }

    /// <summary>Every folder closed; only the top level stays (VS Code's "Collapse Folders in Explorer").</summary>
    public void CollapseAll()
    {
        _remember.Clear();
        if (Root.Children is null) return;
        foreach (var c in Root.Children) CollapseDeep(c);
        var rows = new List<ExplorerNode>(Rows.TakeWhile(r => r.Depth == 0 && r.Role == NodeRole.NewItem));
        VisibleSubtree(Root, rows);
        Rows.ReplaceAll(rows);
        RowsRemoved?.Invoke();
    }

    private void CollapseDeep(ExplorerNode n)
    {
        if (!n.IsDirectory) return;
        n.IsExpanded = false;
        n.Placeholder = null;
        if (n.Children is null) return;
        if (CountLoaded(n, DropChildrenAbove + 1) > DropChildrenAbove)
        {
            foreach (var c in n.Children) Detach(c);
            n.Children = null;
            return;
        }
        foreach (var c in n.Children) CollapseDeep(c);
    }

    private static int CountLoaded(ExplorerNode n, int stopAt)
    {
        if (n.Children is null) return 0;
        var count = n.Children.Count;
        foreach (var c in n.Children)
        {
            if (count >= stopAt) break;
            if (c.Children is not null) count += CountLoaded(c, stopAt - count);
        }
        return count;
    }

    /// <summary>Drops a collapsed folder's entries; its expanded subfolders are remembered and reopen with it.</summary>
    private void Release(ExplorerNode dir)
    {
        if (dir.Children is null) return;
        foreach (var c in dir.Children)
        {
            RememberExpanded(c);
            Detach(c);
        }
        dir.Children = null;
        _dirs.Remove(dir.FullPath);
    }

    private void RememberExpanded(ExplorerNode n)
    {
        if (!n.IsDirectory || !n.IsExpanded || _remember.Count >= MaxRemembered) return;
        _remember.Add(n.FullPath);
        if (n.Children is null) return;
        foreach (var c in n.Children) RememberExpanded(c);
    }

    private void Detach(ExplorerNode n)
    {
        n.Detached = true;
        n.LoadVersion++;
        n.IsSelected = false;
        if (!n.IsDirectory) return;
        if (_dirs.TryGetValue(n.FullPath, out var known) && known == n) _dirs.Remove(n.FullPath);
        if (n.Children is null) return;
        foreach (var c in n.Children) Detach(c);
    }

    // ───────────── lookups ─────────────

    /// <summary>A loaded node by path (null when its folder is not listed).</summary>
    public ExplorerNode? Find(string path)
    {
        var full = ExplorerRules.NormalizeDir(path);
        if (string.Equals(full, RootPath, StringComparison.OrdinalIgnoreCase)) return Root;
        if (_dirs.TryGetValue(full, out var dir) && !dir.Detached) return dir;
        var parent = Path.GetDirectoryName(full);
        if (parent is null || !_dirs.TryGetValue(parent, out var p) || p.Children is null) return null;
        var name = Path.GetFileName(full);
        return p.Children.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Expands the folders down to <paramref name="path"/> (listing them as needed) and returns its node.</summary>
    public async Task<ExplorerNode?> RevealAsync(string path)
    {
        if (!ExplorerRules.IsSameOrUnder(RootPath, path)) return null;
        if (Root.Children is null) await ListAsync(Root);
        var rel = Path.GetRelativePath(RootPath, path);
        if (rel == ".") return Root;
        var parts = rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var node = Root;
        for (var i = 0; i < parts.Length; i++)
        {
            if (_disposed || node.Detached) return null;
            var child = node.Children?.FirstOrDefault(c => c.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
            if (child is null)
            {
                await ListAsync(node); // created a moment ago
                child = node.Children?.FirstOrDefault(c => c.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
                if (child is null) return null;
            }
            if (i == parts.Length - 1) return child;
            if (!child.IsDirectory) return null;
            if (!child.IsExpanded || child.Children is null) await ExpandAsync(child);
            node = child;
        }
        return node;
    }

    /// <summary>Expanded folders in view, relative to the open folder (plus remembered ones not listed yet), for the session.</summary>
    public List<string> ExpandedRelative()
    {
        var list = new List<string>();
        void Walk(ExplorerNode n)
        {
            if (n.Children is null || list.Count >= MaxRemembered) return;
            foreach (var c in n.Children)
                if (c.IsDirectory && c.IsExpanded)
                {
                    list.Add(Path.GetRelativePath(RootPath, c.FullPath));
                    Walk(c);
                }
        }
        Walk(Root);
        list.AddRange(_remember.Where(p => ExplorerRules.IsSameOrUnder(RootPath, p)).Select(p => Path.GetRelativePath(RootPath, p)));
        return ExplorerRules.Cap(list, MaxRemembered);
    }

    // ───────────── changes made here ─────────────

    /// <summary>A file or folder was renamed by the panel: the node (and its loaded subtree) moves without a re-list.</summary>
    public void ApplyRename(ExplorerNode node, string newName)
    {
        var newPath = Path.Combine(Path.GetDirectoryName(node.FullPath)!, newName);
        RekeyAfter(node, () => node.Rename(newName, newPath));
        if (node.Parent is { Children: { } siblings } parent)
        {
            siblings.Sort((a, b) => ExplorerRules.CompareEntries(a.Name, a.IsDirectory, b.Name, b.IsDirectory));
            if (IsShown(parent)) RebuildRows(parent);
        }
    }

    /// <summary>Deleted by the panel: gone at once (the watcher would only confirm it).</summary>
    public void ApplyDelete(ExplorerNode node)
    {
        var at = Rows.IndexOf(node);
        if (at >= 0) Rows.RemoveRange(at, 1 + SubtreeRows(at));
        node.Parent?.Children?.Remove(node);
        Detach(node);
        RowsRemoved?.Invoke();
    }

    private void RekeyAfter(ExplorerNode node, Action change)
    {
        var loaded = new List<ExplorerNode>();
        void Collect(ExplorerNode n)
        {
            if (!n.IsDirectory || n.Children is null) return;
            loaded.Add(n);
            foreach (var c in n.Children) Collect(c);
        }
        Collect(node);
        foreach (var d in loaded) _dirs.Remove(d.FullPath);
        change();
        foreach (var d in loaded) _dirs[d.FullPath] = d;
    }

    // ───────────── watcher ─────────────

    public void StartWatching()
    {
        if (_watcher is not null || _disposed) return;
        try
        {
            var w = new FileSystemWatcher(RootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 32 * 1024,
            };
            w.Created += OnFsEvent;
            w.Deleted += OnFsEvent;
            w.Renamed += (_, e) =>
            {
                Queue(e.OldFullPath);
                Queue(e.FullPath);
            };
            w.Error += (_, e) =>
            {
                Log.Warn($"explorer: watcher error in {RootPath}: {e.GetException().Message}");
                lock (_lock) _refreshAll = true;
                Post();
            };
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex)
        {
            Log.Warn($"explorer: cannot watch {RootPath}: {ex.Message}");
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => Queue(e.FullPath);

    private void Queue(string path)
    {
        if (ExplorerRules.IsInsideVcsFolder(path)) return;
        lock (_lock)
        {
            if (_refreshAll) return;
            _pending.Add(path);
            if (_pending.Count > PendingCap)
            {
                _refreshAll = true; // a flood (checkout, npm install): re-list what is loaded once it settles
                _pending.Clear();
            }
        }
        Post();
    }

    /// <summary>One dispatcher hop per burst (not one per event); none at all while a deferred batch waits for <see cref="Resume"/> (tray, minimized).</summary>
    private void Post()
    {
        lock (_lock)
        {
            if (_posted || _deferred) return;
            _posted = true;
        }
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            lock (_lock) _posted = false;
            if (_disposed) return;
            if (_flushTimer is null)
            {
                _flushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Quiet };
                _flushTimer.Tick += (_, _) =>
                {
                    _flushTimer!.Stop(); // one-shot: no timer runs while nothing changes
                    Flush();
                };
            }
            var now = DateTime.UtcNow;
            if (!_flushTimer.IsEnabled)
            {
                _firstEvent = now;
                _flushTimer.Start();
            }
            else if (now - _firstEvent < MaxWait - Quiet)
            {
                _flushTimer.Stop();
                _flushTimer.Start();
            }
        });
    }

    /// <summary>Applies the queued watcher batch now (or keeps it for later while the panel cannot show it).</summary>
    public void Flush()
    {
        if (_disposed) return;
        if (!CanApply())
        {
            lock (_lock) _deferred = _refreshAll || _pending.Count > 0;
            return;
        }
        HashSet<string> paths;
        bool all;
        lock (_lock)
        {
            paths = _pending;
            _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            all = _refreshAll;
            _refreshAll = false;
            _deferred = false;
        }
        if (all)
        {
            RefreshLoaded();
            return;
        }
        foreach (var folder in ExplorerRules.FoldersToRefresh(paths, IsListedAndShown))
            if (_dirs.TryGetValue(folder, out var dir)) _ = ListAsync(dir);
    }

    /// <summary>The panel shows again: a batch that waited is applied now.</summary>
    public void Resume()
    {
        if (_deferred) Flush();
    }

    /// <summary>Re-lists every loaded folder in view (watcher overflow, explicit refresh).</summary>
    public void RefreshLoaded()
    {
        _ = ListAsync(Root);
        foreach (var dir in _dirs.Values.Where(d => d != Root && d.Children is not null && IsShown(d)).ToList()) _ = ListAsync(dir);
    }

    /// <summary>Re-lists everything in view and waits for it (the Refresh button, tests).</summary>
    public async Task RefreshLoadedAsync()
    {
        var dirs = _dirs.Values.Where(d => d.Children is not null && (d == Root || IsShown(d))).Prepend(Root).Distinct().ToList();
        await Task.WhenAll(dirs.Select(ListAsync));
    }

    private bool IsListedAndShown(string dir)
        => _dirs.TryGetValue(dir, out var n) && n.Children is not null && (n == Root || IsShown(n));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushTimer?.Stop();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        Detach(Root);
        _dirs.Clear();
    }
}
