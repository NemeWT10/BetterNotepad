using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using BetterNotepad.Core;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents.Files;

namespace BetterNotepad.Documents.Session;

public sealed record SessionFile
{
    public int Version { get; init; } = 1;
    public int UntitledCounter { get; init; }
    public List<SessionDoc> Documents { get; init; } = [];
    public List<SessionWindow> Windows { get; init; } = [];
}

public sealed record SessionDoc
{
    public Guid Id { get; init; }
    public string? Path { get; init; }
    public string? UntitledName { get; init; }
    /// <summary>Automatic untitled number ("new 3"); the title follows the UI language. 0 = custom name in <see cref="UntitledName"/>.</summary>
    public int UntitledNumber { get; init; }
    public string? Language { get; init; }
    public bool LanguageExplicit { get; init; }
    public int CodePage { get; init; } = 65001;
    public bool Bom { get; init; }
    public string Eol { get; init; } = "CRLF";
    public bool HasBackup { get; init; }
    public bool Dirty { get; init; }
    public int Caret { get; init; }
    public int SelectionStart { get; init; }
    public int SelectionLength { get; init; }
    public double ScrollY { get; init; }
    public double ScrollX { get; init; }
    public string? View { get; init; }
    public bool Pinned { get; init; }
    public bool Tail { get; init; }
    public long DiskLength { get; init; }
    public DateTime DiskWriteUtc { get; init; }
    public List<int>? Bookmarks { get; init; }
}

public sealed record SessionWindow
{
    public Guid Id { get; init; }
    public bool Main { get; init; }
    public double? Left { get; init; }
    public double? Top { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
    public bool Maximized { get; init; }
    public bool Topmost { get; init; }
    /// <summary>A tab pinned on top (compact window). Restores topmost; ghost mode never restores.</summary>
    public bool Pinned { get; init; }
    public bool Ghost { get; init; }
    public double GhostOpacity { get; init; } = 0.55;
    public string Split { get; init; } = "horizontal";
    public double SplitRatio { get; init; } = 0.5;
    public int ActiveGroup { get; init; }
    public List<SessionGroup> Groups { get; init; } = [];
}

public sealed record SessionGroup
{
    public Guid Id { get; init; }
    public List<Guid> Tabs { get; init; } = [];
    public Guid? Active { get; init; }
    public List<Guid> Mru { get; init; } = [];
}

/// <summary>
/// Keeps the whole workspace across restarts like Notepad++: every tab, unsaved changes (also of
/// existing files) and untitled buffers. Closing the app never asks anything.
/// </summary>
public sealed class SessionService
{
    public static SessionService I { get; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private Debouncer? _sessionDebouncer;
    private Debouncer? _backupDebouncer;
    private bool _attached;
    private bool _suspended;

    public static string SessionPath => Path.Combine(AppEnv.SessionDir, "session.json");

    /// <summary>Subscribes to every model change that should persist.</summary>
    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _sessionDebouncer = new Debouncer(TimeSpan.FromMilliseconds(900), SaveSession);
        _backupDebouncer = new Debouncer(TimeSpan.FromMilliseconds(1500), () => WriteBackups(async: true));

        DocumentManager.I.Opened += OnDocumentOpened;
        DocumentManager.I.Closed += OnDocumentClosed;
        foreach (var d in DocumentManager.I.Documents) OnDocumentOpened(d);

        Workspace.I.Windows.CollectionChanged += OnWindowsChanged;
        foreach (var w in Workspace.I.Windows) HookWindow(w);
    }

    public void ScheduleSave()
    {
        if (!_suspended) _sessionDebouncer?.Trigger();
    }

    /// <summary>Synchronous write of everything (exit, Windows shutdown).</summary>
    public void Flush()
    {
        _backupDebouncer?.Cancel();
        _sessionDebouncer?.Cancel();
        WriteBackups(async: false);
        SaveSession();
    }

    // ───────────── change tracking ─────────────

    private void OnDocumentOpened(Document doc)
    {
        doc.PropertyChanged += OnDocumentPropertyChanged;
        if (doc is TextDocument t) t.BufferChanged += OnBufferChanged;
        ScheduleSave();
    }

    private void OnDocumentClosed(Document doc)
    {
        doc.PropertyChanged -= OnDocumentPropertyChanged;
        if (doc is TextDocument t) t.BufferChanged -= OnBufferChanged;
        BackupStore.Delete(doc.Id);
        ScheduleSave();
    }

    private void OnBufferChanged(object? sender, EventArgs e) => _backupDebouncer?.Trigger();

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Document.IsDirty))
        {
            if (sender is TextDocument { IsDirty: false } t)
            {
                BackupStore.Delete(t.Id);
                t.BackupStale = false;
            }
            else
            {
                _backupDebouncer?.Trigger();
            }
        }
        ScheduleSave();
    }

    private void OnWindowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (WindowModel w in e.NewItems) HookWindow(w);
        ScheduleSave();
    }

    private void HookWindow(WindowModel w)
    {
        w.PropertyChanged += (_, _) => ScheduleSave();
        w.Groups.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (TabGroup g in e.NewItems) HookGroup(g);
            ScheduleSave();
        };
        foreach (var g in w.Groups) HookGroup(g);
    }

    private void HookGroup(TabGroup g)
    {
        g.Tabs.CollectionChanged += (_, _) => ScheduleSave();
        g.PropertyChanged += (_, _) => ScheduleSave();
    }

    // ───────────── backups ─────────────

    private void WriteBackups(bool async)
    {
        var jobs = new List<(Guid Id, ICSharpCode.AvalonEdit.Document.ITextSource Snapshot)>();
        foreach (var doc in DocumentManager.I.Documents.OfType<TextDocument>())
        {
            if (!doc.HasBuffer)
            {
                if (doc.PendingBackupText is { } pending && !BackupStore.Exists(doc.Id))
                    BackupStore.Write(doc.Id, pending);
                continue;
            }
            if (!doc.NeedsBackup)
            {
                if (BackupStore.Exists(doc.Id)) BackupStore.Delete(doc.Id);
                continue;
            }
            if (!doc.BackupStale && BackupStore.Exists(doc.Id)) continue;
            doc.BackupStale = false;
            jobs.Add((doc.Id, doc.Buffer.CreateSnapshot()));
        }
        if (jobs.Count == 0) return;

        void Run()
        {
            foreach (var (id, snap) in jobs)
            {
                try { BackupStore.Write(id, snap.Text); }
                catch (Exception ex) { Log.Error($"backup: write failed for {id}", ex); }
            }
        }

        if (async) Task.Run(Run);
        else Run();
        ScheduleSave();
    }

    // ───────────── session file ─────────────

    private void SaveSession()
    {
        if (_suspended) return;
        try
        {
            var file = Capture();
            Directory.CreateDirectory(AppEnv.SessionDir);
            FileUtil.WriteAllTextAtomic(SessionPath, JsonSerializer.Serialize(file, Json));
        }
        catch (Exception ex)
        {
            Log.Error("session: save failed", ex);
        }
    }

    public SessionFile Capture()
    {
        var docs = new List<SessionDoc>();
        foreach (var d in DocumentManager.I.Documents)
        {
            if (!Workspace.I.IsOpenAnywhere(d)) continue;
            if (d is TextDocument t)
            {
                docs.Add(new SessionDoc
                {
                    Id = t.Id,
                    Path = t.FilePath,
                    UntitledName = t.IsUntitled ? t.UntitledName : null,
                    UntitledNumber = t.IsUntitled ? t.UntitledNumber : 0,
                    Language = t.LanguageId,
                    LanguageExplicit = t.LanguageExplicit,
                    CodePage = t.Encoding.CodePage,
                    Bom = t.HasBom,
                    Eol = t.Eol.Label(),
                    HasBackup = t.NeedsBackup && (BackupStore.Exists(t.Id) || t.PendingBackupText is not null),
                    Dirty = t.IsDirty,
                    Caret = t.CaretOffset,
                    SelectionStart = t.SelectionStart,
                    SelectionLength = t.SelectionLength,
                    ScrollY = t.ScrollY,
                    ScrollX = t.ScrollX,
                    View = t.ViewMode.ToString(),
                    Pinned = t.IsPinned,
                    Tail = t.TailMode,
                    DiskLength = t.DiskLength,
                    DiskWriteUtc = t.DiskWriteUtc,
                    Bookmarks = t.Bookmarks.Count > 0 ? [.. t.Bookmarks] : null,
                });
            }
            else if (d.FilePath is not null)
            {
                docs.Add(new SessionDoc { Id = d.Id, Path = d.FilePath, Pinned = d.IsPinned });
            }
        }

        var windows = Workspace.I.Windows.Select(w => new SessionWindow
        {
            Id = w.Id,
            Main = w.IsMain,
            Left = w.Bounds?.Left,
            Top = w.Bounds?.Top,
            Width = w.Bounds?.Width,
            Height = w.Bounds?.Height,
            Maximized = w.Maximized,
            Topmost = w.Topmost,
            Pinned = w.Pinned,
            Ghost = w.Ghost,
            GhostOpacity = w.GhostOpacity,
            Split = w.SplitOrientation == Orientation.Horizontal ? "horizontal" : "vertical",
            SplitRatio = w.SplitRatio,
            ActiveGroup = Math.Max(0, w.Groups.IndexOf(w.ActiveGroup)),
            Groups = w.Groups.Select(g => new SessionGroup
            {
                Id = g.Id,
                Tabs = g.Tabs.Select(t => t.Id).ToList(),
                Active = g.Active?.Id,
                Mru = g.Mru.Select(t => t.Id).ToList(),
            }).ToList(),
        }).ToList();

        return new SessionFile { UntitledCounter = DocumentManager.I.UntitledCounter, Documents = docs, Windows = windows };
    }

    public static SessionFile? Load()
    {
        try
        {
            if (!File.Exists(SessionPath)) return null;
            return JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(SessionPath), Json);
        }
        catch (Exception ex)
        {
            Log.Error("session: unreadable, starting fresh", ex);
            try { File.Copy(SessionPath, SessionPath + ".broken", overwrite: true); } catch { /* best effort */ }
            return null;
        }
    }

    /// <summary>Rebuilds documents and window models (no views yet). Content loads lazily.</summary>
    public void Restore(SessionFile file)
    {
        _suspended = true;
        try
        {
            DocumentManager.I.UntitledCounter = file.UntitledCounter;
            var byId = new Dictionary<Guid, Document>();
            foreach (var sd in file.Documents)
            {
                var doc = RestoreDocument(sd);
                if (doc is null) continue;
                byId[sd.Id] = doc;
                DocumentManager.I.Add(doc);
            }

            foreach (var sw in file.Windows)
            {
                var w = sw.Main ? Workspace.I.Main : new WindowModel(sw.Id);
                if (sw.Width is > 100 && sw.Height is > 100)
                    w.Bounds = new Rect(sw.Left ?? 0, sw.Top ?? 0, sw.Width.Value, sw.Height.Value);
                w.Maximized = sw.Maximized;
                w.Topmost = sw.Topmost;
                if (!sw.Main) w.Pinned = sw.Pinned;
                // ghost mode (click-through) never comes back after a restart: every window starts interactive
                w.GhostOpacity = sw.GhostOpacity;
                w.SplitOrientation = sw.Split == "vertical" ? Orientation.Vertical : Orientation.Horizontal;
                w.SplitRatio = sw.SplitRatio;

                for (var gi = 0; gi < sw.Groups.Count; gi++)
                {
                    var sg = sw.Groups[gi];
                    var g = gi == 0 ? w.Groups[0] : w.AddGroup();
                    foreach (var id in sg.Tabs)
                        if (byId.TryGetValue(id, out var d)) g.Add(d, activate: false);
                    g.RestoreMru(sg.Mru.Where(byId.ContainsKey).Select(id => byId[id]));
                    if (sg.Active is { } a && byId.TryGetValue(a, out var active)) g.Active = active;
                    else if (g.Tabs.Count > 0) g.Active = g.Tabs[0];
                }
                // drop empty extra groups
                foreach (var empty in w.Groups.Skip(1).Where(g => g.Tabs.Count == 0).ToList()) w.RemoveGroup(empty);
                if (sw.ActiveGroup < w.Groups.Count) w.ActiveGroup = w.Groups[sw.ActiveGroup];
                if (!sw.Main)
                {
                    if (w.AllDocuments.Any()) Workspace.I.Windows.Add(w);
                }
            }

            // documents not placed in any group (corrupt session) go to the main window
            foreach (var d in byId.Values.Where(d => !Workspace.I.IsOpenAnywhere(d)))
                Workspace.I.Main.ActiveGroup.Add(d, activate: false);

            BackupStore.DeleteOrphans(file.Documents.Where(d => d.HasBackup).Select(d => d.Id));
        }
        finally
        {
            _suspended = false;
        }
    }

    private static int? LegacyUntitled(string? name)
    {
        var m = name is null ? null : System.Text.RegularExpressions.Regex.Match(name, @"^(?:nowy|new) (\d{1,6})$");
        return m is { Success: true } && int.TryParse(m.Groups[1].Value, out var n) && n > 0 ? n : null;
    }

    private static Document? RestoreDocument(SessionDoc sd)
    {
        var hasBackup = sd.HasBackup && BackupStore.Exists(sd.Id);
        if (sd.Path is null)
        {
            var u = new TextDocument(null, sd.UntitledName ?? "", sd.Id) { RestoreFromBackup = hasBackup };
            // numbered names follow the UI language; older sessions stored "nowy 3" / "new 3" as text
            if (sd.UntitledNumber > 0) u.UntitledNumber = sd.UntitledNumber;
            else if (LegacyUntitled(sd.UntitledName) is { } n) u.UntitledNumber = n;
            ApplyText(u, sd);
            return u;
        }

        var exists = File.Exists(sd.Path);
        if (!hasBackup && exists)
        {
            // clean tab: same type detection as a fresh open (PDF, image, hex…)
            var doc = DocumentManager.I.CreateFor(sd.Path, sd.Id);
            doc.IsPinned = sd.Pinned;
            if (doc is TextDocument clean) ApplyText(clean, sd);
            else if (FileStateStore.I.IsLocked(sd.Path)) doc.IsLocked = true;
            return doc;
        }

        if (!FileTypes.IsTextual(FileTypes.KindFor(sd.Path)) && !exists) return null;

        var text = new TextDocument(sd.Path, id: sd.Id) { RestoreFromBackup = hasBackup };
        ApplyText(text, sd);
        if (!exists) text.External = ExternalChange.Deleted;
        return text;
    }

    private static void ApplyText(TextDocument doc, SessionDoc sd)
    {
        if (sd.Language is not null)
        {
            doc.LanguageId = sd.Language;
            doc.LanguageExplicit = sd.LanguageExplicit;
        }
        try
        {
            TextFileIO.EnsureCodePages();
            doc.Encoding = sd.CodePage == 65001 ? TextFileIO.Utf8NoBom : System.Text.Encoding.GetEncoding(sd.CodePage);
        }
        catch
        {
            doc.Encoding = TextFileIO.Utf8NoBom;
        }
        doc.HasBom = sd.Bom;
        doc.Eol = LineEndings.Parse(sd.Eol);
        doc.CaretOffset = sd.Caret;
        doc.SelectionStart = sd.SelectionStart;
        doc.SelectionLength = sd.SelectionLength;
        doc.ScrollY = sd.ScrollY;
        doc.ScrollX = sd.ScrollX;
        if (Enum.TryParse<ViewMode>(sd.View, out var v)) doc.ViewMode = v;
        doc.IsPinned = sd.Pinned;
        doc.TailMode = sd.Tail;
        doc.RestoreDiskStamp(sd.DiskLength, sd.DiskWriteUtc);
        if (sd.Bookmarks is not null) doc.Bookmarks.AddRange(sd.Bookmarks);
        if (doc.FilePath is not null && FileStateStore.I.IsLocked(doc.FilePath)) doc.IsLocked = true;
    }
}
