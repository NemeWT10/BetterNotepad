using System.IO;
using System.Text.Json;
using BetterNotepad.Core;

namespace BetterNotepad.Documents.Session;

/// <summary>Per-file state that outlives the tab (read-only lock, pinned-window placement…).</summary>
public sealed record FileState
{
    public bool Locked { get; init; }
    public double[]? PinBounds { get; init; }
    public double? PinOpacity { get; init; }
    public DateTime Touched { get; init; } = DateTime.UtcNow;
}

/// <summary>filestate.json (roaming, per user). Bounded: oldest entries drop out beyond 2000.</summary>
public sealed class FileStateStore
{
    public static FileStateStore I { get; } = new();

    private const int MaxEntries = 2000;
    private Dictionary<string, FileState>? _map;
    private Debouncer? _save;

    private Dictionary<string, FileState> Map => _map ??= Load();

    public FileState Get(string path) => Map.TryGetValue(Key(path), out var s) ? s : new FileState();

    public bool IsLocked(string path) => Map.TryGetValue(Key(path), out var s) && s.Locked;

    public void Update(string path, Func<FileState, FileState> change)
    {
        var key = Key(path);
        var next = change(Get(path)) with { Touched = DateTime.UtcNow };
        if (next is { Locked: false, PinBounds: null, PinOpacity: null }) Map.Remove(key);
        else Map[key] = next;
        if (Map.Count > MaxEntries)
            foreach (var old in Map.OrderBy(kv => kv.Value.Touched).Take(Map.Count - MaxEntries).Select(kv => kv.Key).ToList())
                Map.Remove(old);
        (_save ??= new Debouncer(TimeSpan.FromMilliseconds(800), Save)).Trigger();
    }

    public void SetLocked(string path, bool locked) => Update(path, s => s with { Locked = locked });

    public void Flush() => _save?.Flush();

    private static string Key(string path) => FileUtil.Normalize(path).ToUpperInvariant();

    private static Dictionary<string, FileState> Load()
    {
        try
        {
            if (File.Exists(AppEnv.FileStateFile))
                return JsonSerializer.Deserialize<Dictionary<string, FileState>>(File.ReadAllText(AppEnv.FileStateFile)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("filestate: unreadable", ex);
        }
        return new();
    }

    private void Save()
    {
        try
        {
            FileUtil.WriteAllTextAtomic(AppEnv.FileStateFile, JsonSerializer.Serialize(Map));
        }
        catch (Exception ex)
        {
            Log.Error("filestate: save failed", ex);
        }
    }
}
