using System.IO;
using System.Text.Json;
using BetterNotepad.Core;

namespace BetterNotepad.UI.Palette;

/// <summary>Recently run palette commands (palette.json, roaming), newest first — they lead the command list.</summary>
public sealed class PaletteHistory
{
    public static PaletteHistory I { get; } = new();

    private const int Max = 24;
    private List<string>? _commands;

    private static string FilePath => Path.Combine(AppEnv.RoamingDir, "palette.json");

    public IReadOnlyList<string> Commands => _commands ??= Load();

    public void Used(string commandId)
    {
        var list = (List<string>)Commands;
        list.Remove(commandId);
        list.Insert(0, commandId);
        if (list.Count > Max) list.RemoveRange(Max, list.Count - Max);
        Save(); // a few hundred bytes, written at once so nothing is lost on quit
    }

    private sealed class Data
    {
        public List<string> Commands { get; set; } = new();
    }

    private static List<string> Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath))?.Commands ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn($"palette: history unreadable: {ex.Message}");
        }
        return new();
    }

    private void Save()
    {
        try
        {
            FileUtil.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(new Data { Commands = _commands ?? new() }));
        }
        catch (Exception ex)
        {
            Log.Warn($"palette: history save failed: {ex.Message}");
        }
    }
}
