#if DEBUG
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.UI;
using BetterNotepad.UI.Tray;
using BetterNotepad.UI.Windows;
using Microsoft.Win32;

namespace BetterNotepad.Modules.Sys;

/// <summary>
/// Debug-only checks of the system module, run from SelfTest in test mode: every registry write lands under
/// HKCU\Software\BetterNotepad.Test, hotkeys and the tray are skipped, SemVer, install paths, install/uninstall into
/// the sandbox, and the whole update path (check → download → SHA-256 → swap) against a local fake server.
/// Set BETTERNOTEPAD_SHOTS=&lt;dir&gt; to also render the tray menu and the install/update dialogs to PNG.
/// </summary>
internal static class SystemSelfTest
{
    private static void Check(List<string> failures, bool ok, string what)
    {
        if (!ok) failures.Add("system: " + what);
    }

    private const string RealRun = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run";

    public static async Task Run(MainWindow w, List<string> failures)
    {
        var realRun = Registry.GetValue(RealRun, "BetterNotepad", null) as string;
        var realTypes = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\BetterNotepad.Text\shell\open\command", "", null) as string;
        var realContext = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\*\shell\BetterNotepad\command", "", null) as string;
        var sys = SettingsService.I.Get<SystemSettings>();
        var (oldAuto, oldTray, oldKeyOn, oldKey) = (sys.Autostart, sys.TrayEnabled, sys.GlobalHotkeyEnabled, sys.GlobalHotkey);
        try
        {
            CleanSandbox();
            EnvironmentChecks(failures);
            Pure(failures);
            Hotkeys(failures, sys);
            Autostarts(failures, sys);
            Associations(failures, w);
            await InstallFlow(failures, sys);
            await Updates(failures, w);
            await QuickNotes(failures, w);
            await Visuals(w, failures);
        }
        finally
        {
            sys.Autostart = oldAuto;
            sys.TrayEnabled = oldTray;
            sys.GlobalHotkeyEnabled = oldKeyOn;
            sys.GlobalHotkey = oldKey;
            Environment_SetUpdateUrl(null);
            CleanSandbox();
        }

        // nothing leaked into the user's real registry locations (read-only comparison)
        Check(failures, Registry.GetValue(RealRun, "BetterNotepad", null) as string == realRun, "the real Run key changed during the test");
        Check(failures, Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\BetterNotepad.Text\shell\open\command", "", null) as string == realTypes, "the real file type registration changed during the test");
        Check(failures, Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\*\shell\BetterNotepad\command", "", null) as string == realContext, "the real context menu entry changed during the test");
        Log.Info("selftest: system module checked");
    }

    private static void Environment_SetUpdateUrl(string? url) => System.Environment.SetEnvironmentVariable("BETTERNOTEPAD_UPDATE_URL", url);

    private static void CleanSandbox()
    {
        if (AppEnv.TestMode && AppEnv.RegistryRoot.EndsWith(".Test", StringComparison.Ordinal))
            Registry.CurrentUser.DeleteSubKeyTree(AppEnv.RegistryRoot, throwOnMissingSubKey: false);
    }

    // ───────────── sandbox ─────────────

    private static void EnvironmentChecks(List<string> failures)
    {
        Check(failures, AppEnv.TestMode, "not in test mode");
        Check(failures, AppEnv.RegistryRoot == @"Software\BetterNotepad.Test", "registry root is " + AppEnv.RegistryRoot);
        foreach (var key in new[] { SysEnv.RunKey, SysEnv.ClassesRoot, SysEnv.UninstallKey, SysEnv.CapabilitiesKey, SysEnv.RegisteredAppsKey })
            Check(failures, key.StartsWith(AppEnv.RegistryRoot, StringComparison.Ordinal), "registry location outside the test root: " + key);
        Check(failures, SysEnv.InstallDir.StartsWith(AppEnv.LocalDir, StringComparison.OrdinalIgnoreCase) && SysEnv.StartMenuDir.StartsWith(AppEnv.LocalDir, StringComparison.OrdinalIgnoreCase),
            "install or Start menu folder outside the test data folder");
        var threw = false;
        try { SysEnv.RegSet(@"Software\BetterNotepad", "x", "y"); }
        catch (InvalidOperationException) { threw = true; }
        Check(failures, threw, "a registry write outside BetterNotepad.Test was not refused");

        Check(failures, !MessageWindow.Exists && SystemTray.Active is null && !TrayService.IsAvailable, "tray or message window created in test mode");
        Check(failures, SystemModule.ModeFor(new WindowModel(isMain: true)) == StartupMode.Normal, "test mode must always start normally");
        Check(failures, !Installer.ShouldOfferInstall(new SystemSettings()), "install offer in test mode");
        Check(failures, SysEnv.IsSingleFile || !Installer.CanInstall, "a dev build must not offer to install");
    }

    // ───────────── pure logic ─────────────

    private static void Pure(List<string> failures)
    {
        // install path logic
        Check(failures, SysEnv.InstallDirFor(@"C:\Users\x\AppData\Local") == @"C:\Users\x\AppData\Local\Programs\BetterNotepad", "install dir");
        Check(failures, SysEnv.SamePath(@"C:\A\B\App.exe", @"c:\a\b\..\b\app.EXE"), "same path, different case and ..");
        Check(failures, SysEnv.SamePath(@"C:\A\B\", @"C:\A\B"), "same folder with a trailing slash");
        Check(failures, !SysEnv.SamePath(@"C:\A\App.exe", @"C:\B\App.exe") && !SysEnv.SamePath(null, "x"), "different paths");
        Check(failures, SysEnv.IsOwnExe(@"C:\x\BetterNotepad.exe") && SysEnv.IsOwnExe(@"C:\x\betternotepad.EXE") && !SysEnv.IsOwnExe(@"C:\dotnet\dotnet.exe") && !SysEnv.IsOwnExe(null), "own exe name");
        Check(failures, Autostart.Command(@"C:\a b\BetterNotepad.exe") == "\"C:\\a b\\BetterNotepad.exe\" --minimized", "autostart command is quoted and starts minimized");

        // SemVer
        var v010 = new Version(0, 1, 0);
        Check(failures, UpdateService.IsNewer("v0.2.0", v010) && UpdateService.IsNewer("0.1.1", v010) && UpdateService.IsNewer("1.0.0", v010), "newer versions");
        Check(failures, !UpdateService.IsNewer("v0.1.0", v010) && !UpdateService.IsNewer("0.0.9", v010) && !UpdateService.IsNewer("v0.0.99", v010), "same or older versions");
        Check(failures, UpdateService.IsNewer("v0.10.0", new Version(0, 9, 0)), "0.10.0 is newer than 0.9.0 (numeric, not text)");
        Check(failures, UpdateService.IsNewer("v1.0.0-beta.1", v010) && !UpdateService.IsNewer("nonsense", v010) && !UpdateService.IsNewer(null, v010) && !UpdateService.IsNewer("", v010), "pre-release tag, junk and empty tags");

        // release JSON, checksum files, notes, schedule
        var json = """{"tag_name":"v1.2.3","body":"x","html_url":"https://github.com/o/r/releases/tag/v1.2.3","assets":[{"name":"BetterNotepad.exe.sha256","browser_download_url":"https://github.com/s"},{"name":"BetterNotepad.exe","browser_download_url":"https://github.com/e","size":42,"digest":"sha256:ABCD"}]}""";
        var rel = UpdateService.ParseRelease(json);
        Check(failures, rel is { Tag: "v1.2.3", AssetUrl: "https://github.com/e", ChecksumUrl: "https://github.com/s", AssetSize: 42, Digest: "sha256:ABCD" } && rel.Version == new Version(1, 2, 3), "release json parsed wrongly");
        Check(failures, UpdateService.ParseRelease("not json") is null && UpdateService.ParseRelease("{}") is null && UpdateService.ParseRelease("[]") is null, "bad release json");
        var hex = new string('a', 64);
        Check(failures, UpdateService.ParseChecksum(hex + "  BetterNotepad.exe\n") == hex, "checksum with file name");
        Check(failures, UpdateService.ParseChecksum(hex.ToUpperInvariant()) == hex, "checksum upper case");
        Check(failures, UpdateService.ParseChecksum(new string('b', 64) + "  other.zip\n" + hex + " *BetterNotepad.exe\n") == hex, "checksum list prefers the exe line");
        Check(failures, UpdateService.ParseChecksum("deadbeef") is null && UpdateService.ParseChecksum(null) is null, "short checksum");
        Check(failures, UpdateFlow.CleanNotes("## Nowe\r\n- **a** `b`\r\n\r\n\r\n* c") == "Nowe\n• a b\n\n• c", "release notes cleanup gave: " + UpdateFlow.CleanNotes("## Nowe\r\n- **a** `b`\r\n\r\n\r\n* c").Replace("\n", "|"));
        var now = DateTime.UtcNow;
        Check(failures, UpdateFlow.NextDelay(null, now, default) == TimeSpan.FromSeconds(20), "first check waits 20 s after start");
        Check(failures, UpdateFlow.NextDelay(now.AddHours(-1), now, default) is var d1 && d1 > TimeSpan.FromHours(22.9) && d1 < TimeSpan.FromHours(23.1), "recent check waits for the rest of the day");
        Check(failures, UpdateFlow.NextDelay(now.AddDays(5), now, default) == UpdateService.CheckInterval, "a clock jump never waits longer than a day");
        Check(failures, UpdateFlow.NextDelay(now.AddDays(-3), now.AddHours(0), now.AddHours(1)) is var d2 && d2 > TimeSpan.FromMinutes(59) && d2 <= TimeSpan.FromHours(1), "after a failure the retry waits an hour");

        // downloads only from GitHub in production, only loopback in test mode
        Check(failures, UpdateService.IsAllowedDownload("https://github.com/NemeWT10/BetterNotepad/releases/download/v1.0.0/BetterNotepad.exe", false), "github.com allowed");
        Check(failures, UpdateService.IsAllowedDownload("https://objects.githubusercontent.com/x", false), "githubusercontent allowed");
        Check(failures, !UpdateService.IsAllowedDownload("http://github.com/x", false) && !UpdateService.IsAllowedDownload("https://github.com.evil.example/x", false) && !UpdateService.IsAllowedDownload("https://evil.example/github.com", false) && !UpdateService.IsAllowedDownload("file:///c:/x.exe", false), "foreign hosts refused");
        Check(failures, UpdateService.IsAllowedDownload("http://127.0.0.1:5000/x", true) && !UpdateService.IsAllowedDownload("https://github.com/x", true), "test mode allows only loopback");
        Check(failures, UpdateService.ApiUrl is null, "test mode must not know an update URL by default");
        Environment_SetUpdateUrl(AppInfo.ReleasesApi);
        Check(failures, UpdateService.ApiUrl is null, "test mode must refuse the real GitHub URL");
        Environment_SetUpdateUrl("http://127.0.0.1:9/latest");
        Check(failures, UpdateService.ApiUrl == "http://127.0.0.1:9/latest", "test mode must accept an explicit local fake");
        Environment_SetUpdateUrl(null);
    }

    // ───────────── hotkeys ─────────────

    private static void Hotkeys(List<string> failures, SystemSettings sys)
    {
        Check(failures, HotkeyService.Set("t", "Win+Shift+N", () => { }) == HotkeyState.SkippedTest, "valid chord must be parsed but not registered in test mode");
        Check(failures, HotkeyService.Set("t", "Ctrl+Alt+N", () => { }) == HotkeyState.Invalid, "Ctrl+Alt+letter must be refused");
        Check(failures, HotkeyService.Set("t", "Ctrl+Shift+Alt+K", () => { }) == HotkeyState.Invalid, "Ctrl+Shift+Alt+letter must be refused");
        Check(failures, HotkeyService.Set("t", "Ctrl+Shift+N", () => { }) == HotkeyState.Invalid, "Ctrl+Shift+N must be refused");
        Check(failures, HotkeyService.Set("t", "N", () => { }) == HotkeyState.Invalid, "a hotkey without a modifier must be refused");
        Check(failures, HotkeyService.Set("t", "Win+Shift+Nope", () => { }) == HotkeyState.Invalid, "an unparsable chord must be refused");
        Check(failures, HotkeyService.Set("t", "", () => { }) == HotkeyState.None, "empty chord unbinds");
        Check(failures, HotkeyService.Failure is null && !MessageWindow.Exists, "no failure and no message window in test mode");

        sys.GlobalHotkeyEnabled = true;
        sys.GlobalHotkey = "Win+Shift+N";
        Check(failures, HotkeyService.StateOf(HotkeyService.Summon) == HotkeyState.SkippedTest && HotkeyService.ChordOf(HotkeyService.Summon)?.ToString() == "Win+Shift+N", "summon hotkey follows the settings");
        sys.GlobalHotkeyEnabled = false;
        Check(failures, HotkeyService.StateOf(HotkeyService.Summon) == HotkeyState.None, "turning the hotkey off unbinds it");
        sys.GlobalHotkeyEnabled = true;
        sys.GlobalHotkey = "Win+Shift+M";
        Check(failures, HotkeyService.ChordOf(HotkeyService.Summon)?.ToString() == "Win+Shift+M", "changing the chord re-binds");
        sys.GlobalHotkey = "Ctrl+Alt+K";
        Check(failures, HotkeyService.StateOf(HotkeyService.Summon) == HotkeyState.Invalid, "an AltGr chord in the settings file is refused");
        sys.GlobalHotkey = "Win+Shift+N";
        sys.TrayEnabled = false;
        sys.TrayEnabled = true;
        Check(failures, SystemTray.Active is null && !MessageWindow.Exists, "tray appeared in test mode after a settings change");

        // native plumbing that only runs for real outside test mode: struct layout and the hidden window (no icon, no hotkey)
        Check(failures, System.Runtime.InteropServices.Marshal.SizeOf<SysNative.NOTIFYICONDATA>() == 976, "NOTIFYICONDATA size is " + System.Runtime.InteropServices.Marshal.SizeOf<SysNative.NOTIFYICONDATA>());
        var hwnd = MessageWindow.Handle;
        var style = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE);
        Check(failures, hwnd != 0 && (style & 0x80000000L) != 0 && (style & 0x10000000L) == 0, "the tray window must be an invisible top-level popup (style " + style.ToString("X") + ")");
        MessageWindow.Dispose();
        Check(failures, !MessageWindow.Exists, "message window dispose");
    }

    // ───────────── autostart ─────────────

    private static void Autostarts(List<string> failures, SystemSettings sys)
    {
        sys.Autostart = false;
        Check(failures, !Autostart.IsEnabled, "autostart off");
        sys.Autostart = true;
        var exe = SysEnv.EffectiveExe!;
        Check(failures, Autostart.CurrentValue == Autostart.Command(exe), "autostart value is " + Autostart.CurrentValue);
        Check(failures, Registry.CurrentUser.OpenSubKey(AppEnv.RegistryRoot + @"\Run")?.GetValue("BetterNotepad") is string, "autostart not in the sandbox key");
        Check(failures, !Autostart.Sync(true), "second sync must be a no-op");
        sys.Autostart = false;
        Check(failures, !Autostart.IsEnabled, "autostart removed");
    }

    // ───────────── file types ─────────────

    private static void Associations(List<string> failures, MainWindow w)
    {
        Check(failures, !FileAssociations.IsRegistered && !FileAssociations.ContextMenuRegistered, "sandbox starts unregistered");
        Check(failures, FileAssociations.Register(), "register returned false");
        var exe = SysEnv.EffectiveExe!;
        Check(failures, FileAssociations.IsRegistered && FileAssociations.ContextMenuRegistered, "types and context menu registered");
        Check(failures, string.Equals(FileAssociations.RegisteredExe, exe, StringComparison.OrdinalIgnoreCase), "registered exe is " + FileAssociations.RegisteredExe);
        Check(failures, SysEnv.RegGet(SysEnv.ClassesRoot + @"\.txt\OpenWithProgids", "BetterNotepad.Text") is not null, ".txt OpenWithProgids");
        Check(failures, SysEnv.RegGet(SysEnv.ClassesRoot + @"\.cs\OpenWithProgids", "BetterNotepad.Code") is not null, ".cs OpenWithProgids");
        Check(failures, SysEnv.RegGet(SysEnv.ClassesRoot + @"\.pdf\OpenWithProgids", "BetterNotepad.Pdf") is not null, ".pdf OpenWithProgids");
        Check(failures, SysEnv.RegGet(SysEnv.ClassesRoot + @"\.png\OpenWithProgids", "BetterNotepad.Image") is not null, ".png OpenWithProgids");
        Check(failures, SysEnv.RegGet(SysEnv.CapabilitiesKey + @"\FileAssociations", ".md") as string == "BetterNotepad.Markdown", "capabilities .md");
        Check(failures, SysEnv.RegGet(SysEnv.RegisteredAppsKey, "BetterNotepad") as string == SysEnv.CapabilitiesKey, "RegisteredApplications value");
        Check(failures, SysEnv.RegGet(SysEnv.ClassesRoot + @"\*\shell\BetterNotepad\command") as string == $"\"{exe}\" \"%1\"", "context menu command");
        Check(failures, SysEnv.RegGet(SysEnv.ClassesRoot + @"\BetterNotepad.Code\shell\open\command") as string == $"\"{exe}\" \"%1\"", "ProgID open command");
        Check(failures, SysEnv.RegGet(SysEnv.ClassesRoot + @"\*\shell\BetterNotepad") as string is "Open in BetterNotepad" or "Otwórz w BetterNotepad", "context verb text");
        Check(failures, FileAssociations.AllExtensions.All(e => e.StartsWith('.') && e == e.ToLowerInvariant()), "extension list");
        Check(failures, FileAssociations.AllExtensions.Count() == FileAssociations.AllExtensions.Distinct().Count(), "an extension belongs to two groups");

        var appearance = SettingsService.I.Get<AppearanceSettings>();
        appearance.Language = "pl";
        Check(failures, SysEnv.RegGet(SysEnv.ClassesRoot + @"\*\shell\BetterNotepad") as string == "Otwórz w BetterNotepad", "context verb follows the language (pl)");
        appearance.Language = "en";
        Check(failures, SysEnv.RegGet(SysEnv.ClassesRoot + @"\*\shell\BetterNotepad") as string == "Open in BetterNotepad", "context verb follows the language (en)");
        appearance.Language = "system";

        FileAssociations.SetContextMenu(false);
        Check(failures, !FileAssociations.ContextMenuRegistered && FileAssociations.IsRegistered, "context menu off, types stay");
        FileAssociations.SetContextMenu(true);
        Check(failures, FileAssociations.ContextMenuRegistered, "context menu on again");

        FileAssociations.Unregister();
        Check(failures, !FileAssociations.IsRegistered && !FileAssociations.ContextMenuRegistered, "unregistered");
        Check(failures, !SysEnv.RegKeyExists(SysEnv.ClassesRoot + @"\.txt") && !SysEnv.RegKeyExists(SysEnv.ClassesRoot + @"\BetterNotepad.Text") && !SysEnv.RegKeyExists(SysEnv.CapabilitiesKey), "unregister left keys behind");
        Check(failures, SysEnv.RegGet(SysEnv.RegisteredAppsKey, "BetterNotepad") is null, "RegisteredApplications value left behind");
        FileAssociations.OpenDefaultApps(); // no-op in test mode (must not open Windows settings)
    }

    // ───────────── install / uninstall (sandbox) ─────────────

    private static async Task InstallFlow(List<string> failures, SystemSettings sys)
    {
        var srcDir = Path.Combine(AppEnv.LocalDir, "sys-install-src");
        Directory.CreateDirectory(srcDir);
        var src = Path.Combine(srcDir, "BetterNotepad.exe");
        File.WriteAllBytes(src, [(byte)'M', (byte)'Z', 1, 2, 3, 4]);

        var result = await Task.Run(() => Installer.Install(src));
        Check(failures, result.Ok, "install failed: " + result.Error);
        Check(failures, File.Exists(SysEnv.InstalledExe) && new FileInfo(SysEnv.InstalledExe).Length == 6, "installed exe missing");
        Check(failures, File.Exists(SysEnv.StartMenuLink) && new FileInfo(SysEnv.StartMenuLink).Length > 0, "Start menu shortcut missing");
        Check(failures, Installer.IsInstalled && !SysEnv.IsRunningInstalled, "install state");
        Check(failures, SysEnv.RegGet(SysEnv.UninstallKey, "DisplayName") as string == "BetterNotepad", "Apps & features name");
        Check(failures, SysEnv.RegGet(SysEnv.UninstallKey, "DisplayVersion") as string == AppInfo.Version, "Apps & features version");
        Check(failures, SysEnv.RegGet(SysEnv.UninstallKey, "UninstallString") as string == $"\"{SysEnv.InstalledExe}\" --uninstall", "uninstall command");
        Check(failures, SysEnv.RegGet(SysEnv.UninstallKey, "InstallLocation") as string == SysEnv.InstallDir, "install location");

        sys.Autostart = true;
        Check(failures, Autostart.CurrentValue == Autostart.Command(SysEnv.InstalledExe), "autostart must point at the installed copy");
        Check(failures, FileAssociations.Register() && string.Equals(FileAssociations.RegisteredExe, SysEnv.InstalledExe, StringComparison.OrdinalIgnoreCase), "file types must point at the installed copy");
        Check(failures, (await Task.Run(() => Installer.Install(src))).Ok, "installing over an existing install");

        Installer.UninstallCore(deleteNow: true);
        Check(failures, !Autostart.IsEnabled, "uninstall left the autostart value");
        Check(failures, !File.Exists(SysEnv.StartMenuLink), "uninstall left the shortcut");
        Check(failures, !SysEnv.RegKeyExists(SysEnv.UninstallKey), "uninstall left the Apps & features entry");
        Check(failures, !Directory.Exists(SysEnv.InstallDir), "uninstall left the install folder");
        Check(failures, !FileAssociations.IsRegistered && !FileAssociations.ContextMenuRegistered, "uninstall left the file types");
        sys.Autostart = false;
        Directory.Delete(srcDir, true);
    }

    // ───────────── updates against a local fake server ─────────────

    private sealed class FakeServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Dictionary<string, byte[]> _files = new();

        public string Base { get; }

        public FakeServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Base = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Base);
            _listener.Start();
            _ = Task.Run(Loop);
        }

        public void Add(string path, byte[] body) => _files[path] = body;

        public void Add(string path, string text) => _files[path] = Encoding.UTF8.GetBytes(text);

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }
                var path = ctx.Request.Url!.AbsolutePath.TrimStart('/');
                if (_files.TryGetValue(path, out var body))
                {
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                }
                ctx.Response.Close();
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }
    }

    private static ReleaseInfo FakeRelease(FakeServer fake, string tag, string? sumFile, string? digest = null, string notes = "## What’s new\n- **Tray** icon with a themed menu\n- Global hotkey Win+Shift+N\n- Updates from GitHub, SHA-256 verified")
    {
        var assets = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["name"] = "BetterNotepad.exe",
                ["browser_download_url"] = fake.Base + "BetterNotepad.exe",
                ["size"] = 300_000,
                ["digest"] = digest,
            },
        };
        if (sumFile is not null) assets.Add(new { name = "BetterNotepad.exe.sha256", browser_download_url = fake.Base + sumFile });
        var json = JsonSerializer.Serialize(new { tag_name = tag, body = notes, html_url = fake.Base + "page", assets });
        return UpdateService.ParseRelease(json)!;
    }

    private static async Task Updates(List<string> failures, MainWindow w)
    {
        var exeBytes = new byte[300_000];
        new Random(7).NextBytes(exeBytes);
        exeBytes[0] = (byte)'M';
        exeBytes[1] = (byte)'Z';
        var hex = Convert.ToHexString(SHA256.HashData(exeBytes)).ToLowerInvariant();

        FakeServer fake;
        try { fake = new FakeServer(); }
        catch (Exception ex)
        {
            failures.Add("system: local fake update server could not start: " + ex.Message);
            return;
        }
        using (fake)
        {
            fake.Add("BetterNotepad.exe", exeBytes);
            fake.Add("good.sha256", hex + " *BetterNotepad.exe\n");
            fake.Add("bad.sha256", new string('0', 64) + "  BetterNotepad.exe\n");
            var notes = "## What’s new\n- one";
            foreach (var (name, tag, sum) in new[] { ("latest", "v9.9.9", "good.sha256"), ("latest-bad", "v9.9.9", "bad.sha256"), ("old", "v0.0.1", "good.sha256") })
                fake.Add(name, JsonSerializer.Serialize(new
                {
                    tag_name = tag,
                    body = notes,
                    html_url = fake.Base + "page",
                    assets = new object[]
                    {
                        new { name = "BetterNotepad.exe", browser_download_url = fake.Base + "BetterNotepad.exe", size = exeBytes.Length },
                        new { name = "BetterNotepad.exe.sha256", browser_download_url = fake.Base + sum },
                    },
                }));

            // check
            Environment_SetUpdateUrl(fake.Base + "latest");
            var r = await UpdateService.CheckAsync();
            Check(failures, r.Status == UpdateStatus.Available && r.Release is { } rel0 && rel0.Version == new Version(9, 9, 9) && rel0.AssetUrl == fake.Base + "BetterNotepad.exe" && rel0.ChecksumUrl == fake.Base + "good.sha256", "check against the fake: " + r.Status + " " + r.Error);
            Environment_SetUpdateUrl(fake.Base + "old");
            Check(failures, (await UpdateService.CheckAsync()).Status == UpdateStatus.UpToDate, "an older release means up to date");
            Environment_SetUpdateUrl(fake.Base + "missing");
            Check(failures, (await UpdateService.CheckAsync()).Status == UpdateStatus.UpToDate, "no release published (404) means up to date");
            Environment_SetUpdateUrl("http://127.0.0.1:1/latest");
            Check(failures, (await UpdateService.CheckAsync()).Status == UpdateStatus.Offline, "a refused connection means offline");
            Environment_SetUpdateUrl(null);
            Check(failures, (await UpdateService.CheckAsync()).Status == UpdateStatus.Disabled, "test mode without a fake makes no network call");

            // download + verify + swap
            var dir = Path.Combine(AppEnv.LocalDir, "sys-update");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "BetterNotepad.exe");
            var staged = target + ".new";
            File.WriteAllText(target, "MZ-old");

            var good = FakeRelease(fake, "v9.9.9", "good.sha256");
            await UpdateService.DownloadAsync(good, staged);
            Check(failures, File.Exists(staged) && UpdateService.VerifyFile(staged, hex), "downloaded exe does not match");

            UpdateService.Swap(staged, target);
            Check(failures, File.ReadAllBytes(target).AsSpan().SequenceEqual(exeBytes) && File.ReadAllText(target + ".old") == "MZ-old" && !File.Exists(staged), "swap result");
            File.WriteAllText(target + ".new", "MZ-next");
            UpdateService.Swap(target + ".new", target); // an .old from before is in the way
            Check(failures, File.ReadAllText(target) == "MZ-next", "second swap");
            var threw = false;
            try { UpdateService.Swap(Path.Combine(dir, "missing.new"), target); }
            catch (IOException) { threw = true; }
            Check(failures, threw && File.ReadAllText(target) == "MZ-next", "a failed swap must roll back");
            UpdateService.CleanupLeftovers(target);
            Check(failures, Directory.GetFiles(dir).Length == 1, "leftovers (.old) not cleaned: " + string.Join(",", Directory.GetFiles(dir).Select(Path.GetFileName)));

            // a wrong checksum is refused and nothing stays behind
            var bad = FakeRelease(fake, "v9.9.9", "bad.sha256");
            UpdateException? ex1 = null;
            try { await UpdateService.DownloadAsync(bad, staged); }
            catch (UpdateException e) { ex1 = e; }
            Check(failures, ex1?.Reason == "hash" && !File.Exists(staged), "a SHA-256 mismatch must be refused and cleaned up");

            // no checksum at all is refused; the API digest field works as a fallback
            UpdateException? ex2 = null;
            try { await UpdateService.DownloadAsync(FakeRelease(fake, "v9.9.9", null), staged); }
            catch (UpdateException e) { ex2 = e; }
            Check(failures, ex2?.Reason == "noChecksum" && !File.Exists(staged), "a release without a checksum must be refused");
            await UpdateService.DownloadAsync(FakeRelease(fake, "v9.9.9", null, digest: "sha256:" + hex.ToUpperInvariant()), staged);
            Check(failures, UpdateService.VerifyFile(staged, hex), "digest fallback");
            File.Delete(staged);

            // a foreign download host is refused before any request
            UpdateException? ex3 = null;
            try { await UpdateService.DownloadAsync(good with { AssetUrl = "https://evil.example/BetterNotepad.exe" }, staged); }
            catch (UpdateException e) { ex3 = e; }
            Check(failures, ex3?.Reason == "noAsset", "a foreign host must be refused");
            Directory.Delete(dir, true);
        }
        Environment_SetUpdateUrl(null);
    }

    // ───────────── quick note ─────────────

    private static async Task QuickNotes(List<string> failures, MainWindow w)
    {
        var group = w.Model.ActiveGroup;
        var before = group.Tabs.Count;
        SystemModule.QuickNote();
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        var after = group.Tabs.Count;
        Check(failures, w.ActiveDocument is TextDocument { IsUntitled: true, IsDirty: false } && after <= before + 1, "quick note must leave an empty untitled tab active");
        SystemModule.QuickNote();
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        Check(failures, group.Tabs.Count == after, "a second quick note must reuse the empty tab, not pile up tabs");
        if (after > before && w.ActiveDocument is { } d)
        {
            group.Remove(d);
            DocumentManager.I.Close(d);
        }
    }

    // ───────────── renders for review ─────────────

    private static async Task Settle(MainWindow w, int ms = 120)
    {
        await w.Dispatcher.InvokeAsync(w.UpdateLayout, DispatcherPriority.Background);
        await Task.Delay(ms);
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        w.UpdateLayout();
    }

    private static async Task Visuals(MainWindow w, List<string> failures)
    {
        var shots = System.Environment.GetEnvironmentVariable("BETTERNOTEPAD_SHOTS");
        if (string.IsNullOrWhiteSpace(shots)) return;
        Directory.CreateDirectory(shots);
        SettingsPage.For(w)?.Close();
        await Settle(w, 200);
        var appearance = SettingsService.I.Get<AppearanceSettings>();
        var notes = "## Co nowego\n- Ikona w zasobniku z menu w motywie programu\n- Skrót globalny Win+Shift+N otwiera nową notatkę\n- Aktualizacje z GitHub Releases, sprawdzane sumą SHA-256\n- Instalacja bez uprawnień administratora";
        var release = UpdateService.ParseRelease(JsonSerializer.Serialize(new { tag_name = "v0.2.0", body = notes, html_url = "https://example.invalid", assets = Array.Empty<object>() }))!;

        foreach (var (theme, lang) in new[] { ("aurora", "pl"), ("daylight", "en") })
        {
            appearance.Language = lang;
            appearance.Theme = theme;
            await Settle(w, 150);
            var tag = $"{theme}-{lang}";

            // tray menu (the card is rendered over the theme background; the popup itself needs a real tray click)
            var menu = new TrayMenu();
            var card = menu.Card;
            menu.Content = null;
            var host = new Border { Child = card, Padding = new Thickness(10) };
            host.SetResourceReference(Border.BackgroundProperty, "Brush.Bg");
            host.Measure(new Size(400, 1000));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();
            Render(host, Path.Combine(shots, $"sys-tray-menu-{tag}.png"));

            // install dialog and update dialog inside the window
            SystemModule.ShowInstallDialog(w, remember: false);
            await Settle(w, 350);
            Render(w, Path.Combine(shots, $"sys-install-dialog-{tag}.png"));
            ClickDialogButton(w, Loc.T("install.skip"));
            await Settle(w, 120);

            var offer = UpdateFlow.OfferAsync(w, release);
            await Settle(w, 350);
            Render(w, Path.Combine(shots, $"sys-update-dialog-{tag}.png"));
            ClickDialogButton(w, Loc.T("update.later"));
            await offer;
            await Settle(w, 120);
        }
        appearance.Language = "system";
        appearance.Theme = "system";
    }

    private static void ClickDialogButton(MainWindow w, string text)
    {
        var button = Descendants<Button>(w.DialogLayer).FirstOrDefault(b => b.Content as string == text)
            ?? throw new InvalidOperationException("dialog button not found: " + text);
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    private static void Render(FrameworkElement el, string path)
    {
        el.UpdateLayout();
        var scale = 1.25;
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(el.ActualWidth * scale), (int)Math.Ceiling(el.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(el);
        Write(bmp, path);
    }

    private static void Write(BitmapSource bmp, string path)
    {
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        png.Save(fs);
    }
}
#endif
