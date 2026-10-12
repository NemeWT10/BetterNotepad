using System.IO;
using System.Text.Json;
using BetterNotepad.Core;

namespace BetterNotepad.Documents.Session;

/// <summary>Most recently used files (recent.json, roaming). Feeds Ctrl+P, File ▸ Recent and the jump list.</summary>
public sealed class RecentFiles
{
    public static RecentFiles I { get; } = new();

    private const int Max = 60;
    private List<string>? _items;
    private Debouncer? _save;

    private static string FilePath => Path.Combine(AppEnv.RoamingDir, "recent.json");

    public IReadOnlyList<string> Items => _items ??= Load();

    /// <summary>Raised after the list changed.</summary>
    public event Action? Changed;

    public void Add(string path)
    {
        var list = (List<string>)Items;
        var norm = FileUtil.Normalize(path);
        list.RemoveAll(p => string.Equals(p, norm, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, norm);
        if (list.Count > Max) list.RemoveRange(Max, list.Count - Max);
        Persist();
    }

    public void Remove(string path)
    {
        var list = (List<string>)Items;
        if (list.RemoveAll(p => string.Equals(p, FileUtil.Normalize(path), StringComparison.OrdinalIgnoreCase)) > 0) Persist();
    }

    public void Clear()
    {
        ((List<string>)Items).Clear();
        Persist();
    }

    private void Persist()
    {
        Changed?.Invoke();
        (_save ??= new Debouncer(TimeSpan.FromMilliseconds(700), Save)).Trigger();
    }

    public void Flush() => _save?.Flush();

    private static List<string> Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn($"recent: unreadable: {ex.Message}");
        }
        return new();
    }

    private void Save()
    {
        try
        {
            FileUtil.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(_items ?? new()));
        }
        catch (Exception ex)
        {
            Log.Warn($"recent: save failed: {ex.Message}");
        }
    }
}
