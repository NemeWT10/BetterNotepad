using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;

namespace BetterNotepad.Core.Localization;

/// <summary>
/// UI strings in Polish and English. Every entry is a (pl, en) pair, so a string without a
/// translation cannot even compile. Modules register their tables from a [ModuleInitializer].
/// XAML: {l:Tr key}; code: Loc.T("key", args…).
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new();

    private static readonly Dictionary<string, (string Pl, string En)> Table = new(StringComparer.Ordinal);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changed (code-built texts re-read here; subscribe in Loaded, unsubscribe in Unloaded).</summary>
    public static event Action? LanguageChanged;

    /// <summary>"pl" or "en".</summary>
    public string Language { get; private set; } = "en";

    public bool IsPolish => Language == "pl";

    public CultureInfo Culture => IsPolish ? CultureInfo.GetCultureInfo("pl-PL") : CultureInfo.GetCultureInfo("en-US");

    public static void Register(IReadOnlyDictionary<string, (string Pl, string En)> entries)
    {
        foreach (var (k, v) in entries)
        {
            if (!Table.TryAdd(k, v))
                throw new InvalidOperationException($"Duplicate localization key '{k}'.");
        }
    }

    public static IReadOnlyDictionary<string, (string Pl, string En)> AllEntries => Table;

    public static string ResolveLanguage(string? setting)
    {
        if (setting is "pl" or "en") return setting;
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pl" ? "pl" : "en";
    }

    public void SetLanguage(string? setting)
    {
        var lang = ResolveLanguage(setting);
        if (lang == Language) return;
        Language = lang;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke();
    }

    public string this[string key] => Get(key);

    public string Get(string key)
    {
        if (Table.TryGetValue(key, out var v)) return IsPolish ? v.Pl : v.En;
#if DEBUG
        return "⟦" + key + "⟧";
#else
        return key;
#endif
    }

    public static string T(string key) => I.Get(key);

    public static string T(string key, params object?[] args)
    {
        var format = I.Get(key);
        try { return string.Format(I.Culture, format, args); }
        catch (FormatException) { return format; }
    }

    /// <summary>Picks the singular/plural form: key + ".one", ".few" (pl 2–4), ".many".</summary>
    public static string Plural(string key, long n)
    {
        string suffix;
        if (I.IsPolish)
        {
            var mod10 = n % 10;
            var mod100 = n % 100;
            suffix = n == 1 ? "one" : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? "few" : "many";
        }
        else
        {
            suffix = n == 1 ? "one" : "many";
        }
        return T($"{key}.{suffix}", n);
    }
}
