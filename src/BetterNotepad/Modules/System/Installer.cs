using System.Diagnostics;
using System.IO;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;

namespace BetterNotepad.Modules.Sys;

internal sealed record InstallResult(bool Ok, string? Error = null);

/// <summary>
/// "Install" without admin rights: copy the exe to %LOCALAPPDATA%\Programs\BetterNotepad, add a Start menu
/// shortcut and an Apps &amp; features entry (HKCU Uninstall). The uninstaller is the exe itself (--uninstall).
/// In test mode everything goes to a sandbox (test data folder, HKCU\Software\BetterNotepad.Test).
/// </summary>
internal static class Installer
{
    public static bool IsInstalled => File.Exists(SysEnv.InstalledExe);

    /// <summary>Can this running copy be installed? (a real single-file exe that is not the installed one already)</summary>
    public static bool CanInstall => !SysEnv.IsRunningInstalled && SysEnv.IsOwnExe(SysEnv.CurrentExe) && SysEnv.IsSingleFile;

    /// <summary>First-start offer: only for a release build running from somewhere else, once.</summary>
    public static bool ShouldOfferInstall(SystemSettings s)
    {
        if (s.InstallPromptShown || !CanInstall || AppEnv.TestMode) return false;
#if DEBUG
        return Environment.GetEnvironmentVariable("BETTERNOTEPAD_INSTALL_PROMPT") == "1";
#else
        return true;
#endif
    }

    /// <summary>Copies <paramref name="sourceExe"/> into the install folder and registers it. Synchronous; call off the UI thread.</summary>
    public static InstallResult Install(string sourceExe)
    {
        try
        {
            if (!File.Exists(sourceExe)) return new(false, "source missing");
            var dir = SysEnv.InstallDir;
            var target = SysEnv.InstalledExe;
            Directory.CreateDirectory(dir);
            if (!SysEnv.SamePath(sourceExe, target))
            {
                var tmp = target + ".tmp";
                File.Copy(sourceExe, tmp, overwrite: true);
                try
                {
                    File.Move(tmp, target, overwrite: true);
                }
                catch (IOException)
                {
                    // the installed copy is running: keep it as .old like an update does
                    UpdateService.Swap(tmp, target);
                }
            }
            CreateStartMenuShortcut(target);
            WriteUninstallEntry(target);
            return new(true);
        }
        catch (Exception ex)
        {
            Log.Error("install failed", ex);
            return new(false, ex.Message);
        }
    }

    public static void CreateStartMenuShortcut(string exe)
    {
        Directory.CreateDirectory(SysEnv.StartMenuDir);
        SysNative.CreateShortcut(SysEnv.StartMenuLink, exe, null, Path.GetDirectoryName(exe)!, Loc.T("install.shortcutDescription"), exe);
    }

    /// <summary>The Apps &amp; features entry (HKCU, no admin).</summary>
    public static void WriteUninstallEntry(string exe)
    {
        var key = SysEnv.UninstallKey;
        long size = 0;
        try { size = new FileInfo(exe).Length / 1024; } catch { /* unknown */ }
        SysEnv.RegSet(key, "DisplayName", AppInfo.ProductName);
        SysEnv.RegSet(key, "DisplayVersion", AppInfo.Version);
        SysEnv.RegSet(key, "Publisher", "NemeWT10");
        SysEnv.RegSet(key, "InstallLocation", Path.GetDirectoryName(exe)!);
        SysEnv.RegSet(key, "DisplayIcon", exe + ",0");
        SysEnv.RegSet(key, "UninstallString", "\"" + exe + "\" --uninstall");
        SysEnv.RegSet(key, "URLInfoAbout", AppInfo.RepositoryUrl);
        SysEnv.RegSet(key, "InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        SysEnv.RegSet(key, "EstimatedSize", (int)Math.Min(size, int.MaxValue), Microsoft.Win32.RegistryValueKind.DWord);
        SysEnv.RegSet(key, "NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
        SysEnv.RegSet(key, "NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
    }

    /// <summary>Starts the installed copy (it waits for this process to end first) and returns whether it was launched.</summary>
    public static bool RelaunchInstalled(IEnumerable<string> files, bool minimized)
    {
        if (AppEnv.TestMode) return false; // never spawns programs from a test instance
        try
        {
            var psi = new ProcessStartInfo(SysEnv.InstalledExe) { UseShellExecute = false, WorkingDirectory = SysEnv.InstallDir };
            psi.ArgumentList.Add("--restarted-from");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            if (minimized) psi.ArgumentList.Add("--minimized");
            foreach (var f in files) psi.ArgumentList.Add(f);
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("relaunch failed: " + ex.Message);
            return false;
        }
    }

    // ───────────── uninstall ─────────────

    /// <summary>
    /// Removes autostart, file types, shortcut and the Apps &amp; features entry, then the install folder.
    /// The folder cannot be deleted while the uninstaller itself runs from it, so it is removed by a short-lived
    /// hidden cmd after this process exits (<paramref name="deleteNow"/> for sandboxed tests).
    /// </summary>
    public static void UninstallCore(bool deleteNow)
    {
        try { Autostart.Sync(false); } catch (Exception ex) { Log.Warn("uninstall: autostart: " + ex.Message); }
        try { FileAssociations.Unregister(); } catch (Exception ex) { Log.Warn("uninstall: types: " + ex.Message); }
        try { if (File.Exists(SysEnv.StartMenuLink)) File.Delete(SysEnv.StartMenuLink); } catch (Exception ex) { Log.Warn("uninstall: shortcut: " + ex.Message); }
        try { SysEnv.RegDeleteTree(SysEnv.UninstallKey); } catch (Exception ex) { Log.Warn("uninstall: entry: " + ex.Message); }
        try { SysEnv.RegDeleteIfEmpty(AppEnv.RegistryRoot); } catch { /* still has other values */ }

        var dir = SysEnv.InstallDir;
        if (!Directory.Exists(dir)) return;
        if (deleteNow)
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception ex) { Log.Warn("uninstall: folder: " + ex.Message); }
            return;
        }
        try
        {
            var psi = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add($"ping 127.0.0.1 -n 4 >nul & rmdir /s /q \"{dir}\"");
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("uninstall: could not schedule folder removal: " + ex.Message);
        }
    }
}

/// <summary>
/// Entry for "BetterNotepad.exe --uninstall" (Apps &amp; features): asks first, closes a running instance,
/// removes everything the installer added. The user's settings and notes are left alone.
/// </summary>
internal static class Uninstaller
{
    public static int Run()
    {
        try
        {
            SettingsService.Init(AppEnv.SettingsFile);
            Loc.I.SetLanguage(SettingsService.I.Get<AppearanceSettings>().Language);
        }
        catch { /* system language */ }

        var assumeYes = AppEnv.TestMode && Environment.GetEnvironmentVariable("BETTERNOTEPAD_UNINSTALL_YES") == "1";
        if (!assumeYes)
        {
            var answer = SysNative.MessageBox(0, Loc.T("install.uninstallText"), Loc.T("install.uninstallTitle"),
                SysNative.MB_YESNO | SysNative.MB_ICONQUESTION | SysNative.MB_TOPMOST);
            if (answer != SysNative.IDYES) return 1;
        }

        AskRunningInstanceToQuit();
        Installer.UninstallCore(deleteNow: AppEnv.TestMode);
        if (!AppEnv.TestMode)
            SysNative.MessageBox(0, Loc.T("install.uninstalledText", AppEnv.RoamingDir), Loc.T("install.uninstallTitle"), SysNative.MB_ICONINFORMATION | SysNative.MB_TOPMOST);
        return 0;
    }

    /// <summary>Sends --quit through the single-instance pipe and waits (up to 8 s) until the other instance is gone.</summary>
    public static void AskRunningInstanceToQuit()
    {
        // the probe must be disposed before polling: an open handle keeps the other instance's mutex alive
        using (var probe = new SingleInstance(AppEnv.InstanceKey))
        {
            if (probe.IsPrimary) return; // nobody running
            if (!probe.SendToPrimary(["--quit"])) return;
        }
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(250);
            using var again = new SingleInstance(AppEnv.InstanceKey);
            if (again.IsPrimary) return;
        }
    }
}
