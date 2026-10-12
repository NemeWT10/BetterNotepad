using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BetterNotepad.Core;

/// <summary>
/// Process-wide environment: test mode, data folders and the key that separates instances
/// (a test instance never talks to the user's running instance).
/// </summary>
public static class AppEnv
{
    public static bool TestMode { get; private set; }

    /// <summary>Folder that holds settings (roaming, per user).</summary>
    public static string RoamingDir { get; private set; } = "";

    /// <summary>Folder that holds the session, backups, logs and caches (local, per machine).</summary>
    public static string LocalDir { get; private set; } = "";

    /// <summary>Stable per data-folder key used for the single-instance mutex and pipe.</summary>
    public static string InstanceKey { get; private set; } = "";

    /// <summary>Registry root (under HKCU) for associations/autostart; separate in test mode.</summary>
    public static string RegistryRoot => TestMode ? @"Software\BetterNotepad.Test" : @"Software\BetterNotepad";

    public static string SettingsFile => Path.Combine(RoamingDir, "settings.json");
    public static string FileStateFile => Path.Combine(RoamingDir, "filestate.json");
    public static string SessionDir => Path.Combine(LocalDir, "session");
    public static string BackupDir => Path.Combine(SessionDir, "backup");
    public static string LogDir => Path.Combine(LocalDir, "logs");
    public static string CacheDir => Path.Combine(LocalDir, "cache");

    public static void Init(bool testMode, string? homeOverride)
    {
        TestMode = testMode || Environment.GetEnvironmentVariable("BETTERNOTEPAD_TEST") == "1";
        homeOverride ??= Environment.GetEnvironmentVariable("BETTERNOTEPAD_HOME");

        if (!string.IsNullOrWhiteSpace(homeOverride))
        {
            var home = Path.GetFullPath(homeOverride);
            RoamingDir = Path.Combine(home, "roaming");
            LocalDir = Path.Combine(home, "local");
        }
        else
        {
            RoamingDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterNotepad");
            LocalDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterNotepad");
        }

        Directory.CreateDirectory(RoamingDir);
        Directory.CreateDirectory(LocalDir);
        Directory.CreateDirectory(BackupDir);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(RoamingDir.ToUpperInvariant() + (TestMode ? "|test" : "")));
        InstanceKey = "BetterNotepad-" + Convert.ToHexString(hash, 0, 8);
    }
}
