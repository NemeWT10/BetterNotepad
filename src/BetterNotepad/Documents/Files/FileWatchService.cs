using System.IO;
using System.Windows;
using System.Windows.Threading;
using BetterNotepad.Core;

namespace BetterNotepad.Documents.Files;

/// <summary>
/// Notices changes made by other programs to open files (no polling: one FileSystemWatcher per
/// folder of an open file). Events are coalesced and delivered on the UI thread. Changes to other files in those
/// folders (a busy Downloads or log folder) are dropped on the watcher thread, so they never wake the UI — also in the tray.
/// </summary>
public sealed class FileWatchService
{
    public static FileWatchService I { get; } = new();

    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private volatile HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase); // replaced, never mutated (read by watcher threads)
    private Debouncer? _flush;

    /// <summary>Paths that changed on disk (UI thread).</summary>
    public event Action<IReadOnlyCollection<string>>? Changed;

    public void Attach()
    {
        DocumentManager.I.Opened += _ => Sync();
        DocumentManager.I.Closed += _ => Sync();
        foreach (var d in DocumentManager.I.Documents) d.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Document.FilePath)) Sync(); };
        DocumentManager.I.Opened += d => d.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Document.FilePath)) Sync(); };
        Sync();
    }

    /// <summary>Keeps exactly one watcher per folder that holds an open file.</summary>
    public void Sync()
    {
        var files = DocumentManager.I.Documents
            .Where(d => d.FilePath is not null)
            .Select(d => FileUtil.Normalize(d.FilePath!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _files = files;
        var dirs = files
            .Select(Path.GetDirectoryName)
            .Where(d => d is not null && Directory.Exists(d))
            .Select(d => d!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var gone in _watchers.Keys.Where(k => !dirs.Contains(k)).ToList())
        {
            _watchers[gone].Dispose();
            _watchers.Remove(gone);
        }
        foreach (var dir in dirs.Where(d => !_watchers.ContainsKey(d)))
        {
            try
            {
                var w = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 16 * 1024,
                };
                w.Changed += OnEvent;
                w.Created += OnEvent;
                w.Deleted += OnEvent;
                w.Renamed += (s, e) =>
                {
                    if (!IsOpen(e.OldFullPath) && !IsOpen(e.FullPath)) return;
                    Queue(e.OldFullPath);
                    Queue(e.FullPath);
                };
                w.Error += (_, e) => Log.Warn($"watcher error in {dir}: {e.GetException().Message}");
                w.EnableRaisingEvents = true;
                _watchers[dir] = w;
            }
            catch (Exception ex)
            {
                Log.Warn($"watcher: cannot watch {dir}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Stops watching folders at or under <paramref name="folder"/>: their open handles would make Windows refuse to rename
    /// or delete it. <see cref="Sync"/> watches whatever is still open afterwards.
    /// </summary>
    public void ReleaseUnder(string folder)
    {
        var f = folder.TrimEnd('\\', '/');
        foreach (var dir in _watchers.Keys.ToList())
        {
            var d = dir.TrimEnd('\\', '/');
            if (!d.StartsWith(f, StringComparison.OrdinalIgnoreCase) || d.Length > f.Length && d[f.Length] is not ('\\' or '/')) continue;
            _watchers[dir].Dispose();
            _watchers.Remove(dir);
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        if (IsOpen(e.FullPath)) Queue(e.FullPath);
    }

    /// <summary>Watcher thread: is this one of the open files (the folder may hold many others)?</summary>
    private bool IsOpen(string path) => _files.Contains(FileUtil.Normalize(path));

    private void Queue(string path)
    {
        lock (_pending) _pending.Add(path);
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            (_flush ??= new Debouncer(TimeSpan.FromMilliseconds(250), Flush, DispatcherPriority.Background)).Trigger();
        });
    }

    private void Flush()
    {
        string[] paths;
        lock (_pending)
        {
            paths = [.. _pending];
            _pending.Clear();
        }
        if (paths.Length > 0) Changed?.Invoke(paths);
    }
}
