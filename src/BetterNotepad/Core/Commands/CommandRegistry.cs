using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;

namespace BetterNotepad.Core.Commands;

/// <summary>Where a shortcut is active.</summary>
public enum CommandScope
{
    /// <summary>Anywhere in a window.</summary>
    Window,
    /// <summary>When a text editor has focus.</summary>
    Editor,
}

/// <summary>What a command acts on (filled by the window when it dispatches).</summary>
public sealed class CommandContext
{
    public object? Window { get; init; }
    public Document? Document { get; init; }
    public TabGroup? Group { get; init; }

    /// <summary>The focused editor (BetterNotepad.Editor.CodeEditor), when any.</summary>
    public object? Editor { get; init; }

    public bool EditorFocused => Editor is not null;
}

/// <summary>
/// A user-visible command: palette entry, menu item, rebindable shortcut. TitleKey/CategoryKey are
/// localization keys. <see cref="Kinds"/> limits a command (and its shortcut) to some file types.
/// </summary>
public sealed class AppCommand
{
    public required string Id { get; init; }
    public required string TitleKey { get; init; }
    public string CategoryKey { get; init; } = "cmd.cat.general";
    public string? DefaultGesture { get; init; }
    public CommandScope Scope { get; init; } = CommandScope.Window;
    public DocumentKind[]? Kinds { get; init; }

    /// <summary>Extra words for palette search (both languages).</summary>
    public string? SearchTerms { get; init; }

    public bool ShowInPalette { get; init; } = true;
    public required Action<CommandContext> Execute { get; init; }
    public Func<CommandContext, bool>? CanExecute { get; init; }

    public string Title => Loc.T(TitleKey);
    public string Category => Loc.T(CategoryKey);

    public bool AppliesTo(CommandContext ctx)
    {
        if (Scope == CommandScope.Editor && !ctx.EditorFocused) return false;
        if (Kinds is not null && (ctx.Document is null || Array.IndexOf(Kinds, ctx.Document.Kind) < 0)) return false;
        return CanExecute?.Invoke(ctx) ?? true;
    }
}

public static class CommandRegistry
{
    private static readonly Dictionary<string, AppCommand> Map = new(StringComparer.Ordinal);
    private static readonly List<AppCommand> Ordered = new();

    public static void Register(AppCommand cmd)
    {
        if (Map.TryGetValue(cmd.Id, out var old)) Ordered.Remove(old);
        Map[cmd.Id] = cmd;
        Ordered.Add(cmd);
        Keymap.Invalidate();
    }

    public static IReadOnlyList<AppCommand> All => Ordered;

    public static AppCommand? Get(string id) => Map.GetValueOrDefault(id);

    public static bool TryExecute(string id, CommandContext ctx)
    {
        if (!Map.TryGetValue(id, out var cmd) || !(cmd.CanExecute?.Invoke(ctx) ?? true)) return false;
        cmd.Execute(ctx);
        return true;
    }
}

/// <summary>Resolves shortcuts: user overrides (settings) on top of command defaults.</summary>
public static class Keymap
{
    private static Dictionary<KeyChord, List<AppCommand>>? _byChord;

    public static void Invalidate() => _byChord = null;

    private static KeymapSettings Settings => SettingsService.I.Get<KeymapSettings>();

    public static KeyChord? GestureFor(string commandId)
    {
        if (Settings.Overrides.TryGetValue(commandId, out var custom))
            return string.IsNullOrEmpty(custom) ? null : KeyChord.Parse(custom);
        return KeyChord.Parse(CommandRegistry.Get(commandId)?.DefaultGesture);
    }

    public static string GestureText(string commandId) => GestureFor(commandId)?.ToString() ?? "";

    public static void SetGesture(string commandId, KeyChord? chord)
    {
        var def = KeyChord.Parse(CommandRegistry.Get(commandId)?.DefaultGesture);
        if (chord == def) Settings.Overrides.Remove(commandId);
        else Settings.Overrides[commandId] = chord?.ToString() ?? "";
        SettingsService.I.Touch(Settings, nameof(KeymapSettings.Overrides));
        Invalidate();
    }

    public static void ResetAll()
    {
        Settings.Overrides.Clear();
        SettingsService.I.Touch(Settings, nameof(KeymapSettings.Overrides));
        Invalidate();
    }

    private static Dictionary<KeyChord, List<AppCommand>> Build()
    {
        var map = new Dictionary<KeyChord, List<AppCommand>>();
        foreach (var cmd in CommandRegistry.All)
        {
            if (GestureFor(cmd.Id) is not { } chord) continue;
            if (!map.TryGetValue(chord, out var list)) map[chord] = list = new();
            list.Add(cmd);
        }
        return map;
    }

    /// <summary>The command a key press triggers here (kind-specific and editor commands win over general ones).</summary>
    public static AppCommand? Match(KeyChord chord, CommandContext ctx)
    {
        if (chord.IsAltGrConflict || chord.IsModifierOnly) return null;
        _byChord ??= Build();
        if (!_byChord.TryGetValue(chord, out var list)) return null;
        return list.Where(c => c.AppliesTo(ctx))
                   .OrderByDescending(c => c.Kinds is not null)
                   .ThenByDescending(c => c.Scope == CommandScope.Editor)
                   .FirstOrDefault();
    }

    /// <summary>Other commands sharing the chord in an overlapping context (shown as conflicts in settings).</summary>
    public static IEnumerable<AppCommand> Conflicts(string commandId, KeyChord chord)
    {
        var self = CommandRegistry.Get(commandId);
        _byChord ??= Build();
        if (!_byChord.TryGetValue(chord, out var list)) return [];
        return list.Where(c => c.Id != commandId && Overlaps(self, c));
    }

    private static bool Overlaps(AppCommand? a, AppCommand b)
    {
        if (a is null) return true;
        if (a.Kinds is null || b.Kinds is null) return true;
        return a.Kinds.Intersect(b.Kinds).Any();
    }
}
