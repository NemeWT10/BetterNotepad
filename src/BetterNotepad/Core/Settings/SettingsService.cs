using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace BetterNotepad.Core.Settings;

/// <summary>A section of settings.json. Each module owns its own section class.</summary>
public abstract class SettingsSection : ObservableObject
{
}

/// <summary>Gives a section its JSON key without creating an instance.</summary>
public interface ISettingsSection
{
    static abstract string Key { get; }
}

/// <summary>
/// settings.json (per user, roaming): { "schemaVersion": n, "&lt;section&gt;": { ... } }.
/// Sections are loaded lazily, saved debounced and migrated by schema version, so an update
/// never breaks a user's settings.
/// </summary>
public sealed class SettingsService
{
    public const int CurrentSchemaVersion = 1;

    public static SettingsService I { get; private set; } = null!;

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Migrations: index i upgrades schema i+1 → i+2.</summary>
    private static readonly Action<JsonObject>[] Migrations = [];

    private readonly string _path;
    private JsonObject _root;
    private readonly Dictionary<string, SettingsSection> _sections = new(StringComparer.Ordinal);
    private readonly Dictionary<SettingsSection, string> _keys = new();
    private Debouncer? _saveDebouncer;

    /// <summary>Raised on the UI thread after a property of a section changed.</summary>
    public event Action<SettingsSection, string?>? Changed;

    private SettingsService(string path)
    {
        _path = path;
        _root = LoadRoot(path);
    }

    public static void Init(string path) => I = new SettingsService(path);

    public T Get<T>() where T : SettingsSection, ISettingsSection, new()
    {
        if (_sections.TryGetValue(T.Key, out var existing)) return (T)existing;

        T section;
        try
        {
            section = _root[T.Key] is JsonObject node ? node.Deserialize<T>(Json) ?? new T() : new T();
        }
        catch (Exception ex)
        {
            Log.Error($"settings: section '{T.Key}' unreadable, using defaults", ex);
            section = new T();
        }

        _sections[T.Key] = section;
        _keys[section] = T.Key;
        section.PropertyChanged += OnSectionChanged;
        return section;
    }

    /// <summary>Call after mutating a collection inside a section (no PropertyChanged for that).</summary>
    public void Touch(SettingsSection section, string? property = null)
    {
        Changed?.Invoke(section, property);
        ScheduleSave();
    }

    private void OnSectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SettingsSection s) return;
        Changed?.Invoke(s, e.PropertyName);
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        if (Dispatcher.FromThread(Thread.CurrentThread) is null)
        {
            SaveNow();
            return;
        }
        _saveDebouncer ??= new Debouncer(TimeSpan.FromMilliseconds(600), SaveNow);
        _saveDebouncer.Trigger();
    }

    public void Flush() => _saveDebouncer?.Flush();

    public void SaveNow()
    {
        try
        {
            foreach (var (key, section) in _sections)
                _root[key] = JsonSerializer.SerializeToNode(section, section.GetType(), Json);
            _root["schemaVersion"] = CurrentSchemaVersion;
            FileUtil.WriteAllTextAtomic(_path, _root.ToJsonString(Json));
        }
        catch (Exception ex)
        {
            Log.Error("settings: save failed", ex);
        }
    }

    public void ExportTo(string file)
    {
        SaveNow();
        File.Copy(_path, file, overwrite: true);
    }

    /// <summary>Replaces all settings with the given file and pushes the values into live sections.</summary>
    public void ImportFrom(string file)
    {
        var root = LoadRoot(file);
        _root = root;
        foreach (var (key, section) in _sections)
        {
            var type = section.GetType();
            object fresh;
            try
            {
                fresh = root[key] is JsonObject node ? node.Deserialize(type, Json) ?? Activator.CreateInstance(type)! : Activator.CreateInstance(type)!;
            }
            catch
            {
                fresh = Activator.CreateInstance(type)!;
            }
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (p.CanRead && p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
                    p.SetValue(section, p.GetValue(fresh));
            Changed?.Invoke(section, null);
        }
        SaveNow();
    }

    private static JsonObject LoadRoot(string path)
    {
        try
        {
            if (!File.Exists(path)) return new JsonObject { ["schemaVersion"] = CurrentSchemaVersion };
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];
            var version = root["schemaVersion"]?.GetValue<int>() ?? 1;
            for (var v = version; v < CurrentSchemaVersion; v++)
                if (v - 1 < Migrations.Length) Migrations[v - 1](root);
            root["schemaVersion"] = CurrentSchemaVersion;
            return root;
        }
        catch (Exception ex)
        {
            Log.Error("settings: file unreadable, starting with defaults", ex);
            try { File.Copy(path, path + ".broken", overwrite: true); } catch { /* best effort */ }
            return new JsonObject { ["schemaVersion"] = CurrentSchemaVersion };
        }
    }
}
