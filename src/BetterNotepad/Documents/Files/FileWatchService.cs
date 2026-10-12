using System.IO;
using System.Windows;
using System.Windows.Threading;
using BetterNotepad.Core;

namespace BetterNotepad.Documents.Files;

/// <summary>
/// Notices changes made by other programs to open files (no polling: one FileSystemWatcher per
/// folder of an open file). Events are coalesced and delivered on the UI thread.
/// </summary>
public sealed class FileWatchService
{
    public static FileWatchService I { get; } = new();

    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
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
        var dirs = DocumentManager.I.Documents
            .Where(d => d.FilePath is not null)
            .Select(d => Path.GetDirectoryName(d.FilePath!)!)
            .Where(d => d is not null && Directory.Exists(d))
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

    private void OnEvent(object sender, FileSystemEventArgs e) => Queue(e.FullPath);

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
