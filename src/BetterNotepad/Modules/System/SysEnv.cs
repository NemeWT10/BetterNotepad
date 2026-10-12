using System.IO;
using BetterNotepad.Core;
using Microsoft.Win32;

namespace BetterNotepad.Modules.Sys;

/// <summary>
/// Where the system module reads and writes. In test mode every registry key lives under
/// HKCU\Software\BetterNotepad.Test and every file under the test data folder, so a test run can never
/// touch the user's real autostart, file associations, Start menu or installed copy.
/// </summary>
internal static class SysEnv
{
    public const string AppId = "BetterNotepad";

    public static bool Sandboxed => AppEnv.TestMode;

    // ───────────── registry locations (all under HKCU) ─────────────

    public static string RunKey => Sandboxed ? AppEnv.RegistryRoot + @"\Run" : @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string ClassesRoot => Sandboxed ? AppEnv.RegistryRoot + @"\Classes" : @"Software\Classes";

    public static string UninstallKey => (Sandboxed ? AppEnv.RegistryRoot + @"\Uninstall" : @"Software\Microsoft\Windows\CurrentVersion\Uninstall") + @"\" + AppId;

    /// <summary>Capabilities key (listed in Default apps through RegisteredApplications).</summary>
    public static string CapabilitiesKey => AppEnv.RegistryRoot + @"\Capabilities";

    public static string RegisteredAppsKey => Sandboxed ? AppEnv.RegistryRoot + @"\RegisteredApplications" : @"Software\RegisteredApplications";

    // ───────────── files ─────────────

    /// <summary>This process's exe, captured at startup (an update renames it while it runs).</summary>
    public static string? CurrentExe { get; } = Environment.ProcessPath;

    /// <summary>Per-user install folder under the given %LOCALAPPDATA% (no admin rights needed).</summary>
    public static string InstallDirFor(string localAppData) => Path.Combine(localAppData, "Programs", AppId);

    public static string InstallDir => InstallDirFor(Sandboxed
        ? Path.Combine(AppEnv.LocalDir, "install-test")
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static string InstalledExe => Path.Combine(InstallDir, AppId + ".exe");

    public static string StartMenuDir => Sandboxed
        ? Path.Combine(AppEnv.LocalDir, "install-test", "StartMenu")
        : Environment.GetFolderPath(Environment.SpecialFolder.Programs);

    public static string StartMenuLink => Path.Combine(StartMenuDir, AppId + ".lnk");

    /// <summary>The exe that autostart and file associations should point at: the installed copy when there is one.</summary>
    public static string? EffectiveExe => File.Exists(InstalledExe) ? InstalledExe : CurrentExe;

    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsRunningInstalled => SamePath(CurrentExe, InstalledExe);

    /// <summary>True for the real BetterNotepad.exe (not dotnet.exe or a test host).</summary>
    public static bool IsOwnExe(string? exe) => exe is not null && string.Equals(Path.GetFileName(exe), AppId + ".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>The release build is one self-contained file; a dev build is an apphost next to its dlls and cannot be copied alone.</summary>
#pragma warning disable IL3000 // intended: an empty Location is exactly how a single-file app is recognized
    public static bool IsSingleFile => string.IsNullOrEmpty(typeof(SysEnv).Assembly.Location);
#pragma warning restore IL3000

    // ───────────── registry helpers (guarded in test mode) ─────────────

    private static void Guard(string subKey)
    {
        if (Sandboxed && !subKey.StartsWith(AppEnv.RegistryRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test mode must not write outside " + AppEnv.RegistryRoot + ": " + subKey);
    }

    public static void RegSet(string subKey, string? name, object value, RegistryValueKind kind = RegistryValueKind.String)
    {
        Guard(subKey);
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        key.SetValue(name ?? "", value, kind);
    }

    public static object? RegGet(string subKey, string? name = null)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        return key?.GetValue(name ?? "");
    }

    public static bool RegKeyExists(string subKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        return key is not null;
    }

    public static void RegDeleteTree(string subKey)
    {
        Guard(subKey);
        Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
    }

    /// <summary>Removes a key that has no values and no subkeys left (the empty shells our registration leaves behind).</summary>
    public static void RegDeleteIfEmpty(string subKey)
    {
        Guard(subKey);
        bool empty;
        using (var key = Registry.CurrentUser.OpenSubKey(subKey))
            empty = key is not null && key.SubKeyCount == 0 && key.ValueCount == 0;
        if (empty) Registry.CurrentUser.DeleteSubKey(subKey, throwOnMissingSubKey: false);
    }

    public static void RegDeleteValue(string subKey, string name)
    {
        Guard(subKey);
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    /// <summary>Tells Explorer that associations changed (skipped in test mode).</summary>
    public static void NotifyAssociationsChanged()
    {
        if (Sandboxed) return;
        try { SysNative.SHChangeNotify(SysNative.SHCNE_ASSOCCHANGED, SysNative.SHCNF_IDLIST, 0, 0); }
        catch (Exception ex) { Log.Warn("shell notify failed: " + ex.Message); }
    }
}
