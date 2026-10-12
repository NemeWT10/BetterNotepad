using BetterNotepad.Core;

namespace BetterNotepad.Modules.Sys;

/// <summary>Start with Windows: one value in HKCU\...\Run ("path" --minimized → starts hidden in the tray).</summary>
internal static class Autostart
{
    public const string ValueName = "BetterNotepad";

    public static string Command(string exe) => $"\"{exe}\" --minimized";

    public static string? CurrentValue => SysEnv.RegGet(SysEnv.RunKey, ValueName) as string;

    public static bool IsEnabled => !string.IsNullOrWhiteSpace(CurrentValue);

    /// <summary>Makes the Run value match the setting. Returns true when it changed something.</summary>
    public static bool Sync(bool enabled)
    {
        try
        {
            var exe = SysEnv.EffectiveExe;
            if (enabled && SysEnv.IsOwnExe(exe))
            {
                var want = Command(exe!);
                if (string.Equals(CurrentValue, want, StringComparison.Ordinal)) return false;
                SysEnv.RegSet(SysEnv.RunKey, ValueName, want);
                return true;
            }
            if (!enabled && CurrentValue is not null)
            {
                SysEnv.RegDeleteValue(SysEnv.RunKey, ValueName);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("autostart: " + ex.Message);
        }
        return false;
    }
}
