using System.Diagnostics;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using Microsoft.Win32;

namespace BetterNotepad.Modules.Sys;

/// <summary>
/// File types without admin rights: ProgIDs + Capabilities + RegisteredApplications under HKCU make BetterNotepad
/// appear in "Open with" and in Settings ▸ Default apps (Windows 11 does not let a program set the default itself),
/// plus an "Open in BetterNotepad" entry in the classic Explorer context menu.
/// </summary>
internal static class FileAssociations
{
    public sealed record Group(string Id, string[] Extensions)
    {
        public string ProgId => "BetterNotepad." + Id;
    }

    public static readonly IReadOnlyList<Group> Groups =
    [
        new("Text", [".txt", ".log", ".ini", ".cfg", ".conf", ".toml", ".properties", ".json", ".jsonc", ".yaml", ".yml", ".xml", ".csv", ".tsv"]),
        new("Code", [".c", ".h", ".cpp", ".hpp", ".cc", ".cxx", ".cs", ".java", ".py", ".pyw", ".js", ".mjs", ".cjs", ".ts", ".jsx", ".tsx",
            ".css", ".scss", ".less", ".php", ".sql", ".sh", ".bash", ".bat", ".cmd", ".ps1", ".go", ".rs", ".kt", ".kts", ".lua", ".rb",
            ".asm", ".s", ".html", ".htm", ".vue", ".swift"]),
        new("Markdown", [".md", ".markdown", ".mdown", ".mkd", ".mkdn"]),
        new("Pdf", [".pdf"]),
        new("Image", [".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".ico", ".tif", ".tiff", ".webp", ".svg"]),
    ];

    public static IEnumerable<string> AllExtensions => Groups.SelectMany(g => g.Extensions);

    private static string Classes => SysEnv.ClassesRoot;

    private static string ContextKey => Classes + @"\*\shell\" + SysEnv.AppId;

    private static string Quote(string exe) => "\"" + exe + "\"";

    // ───────────── state ─────────────

    public static bool IsRegistered
    {
        get
        {
            var cmd = SysEnv.RegGet(Classes + @"\" + Groups[0].ProgId + @"\shell\open\command") as string;
            return !string.IsNullOrEmpty(cmd) && SysEnv.RegKeyExists(SysEnv.CapabilitiesKey);
        }
    }

    public static bool ContextMenuRegistered => SysEnv.RegKeyExists(ContextKey);

    /// <summary>The exe the registered command points at (null when not registered).</summary>
    public static string? RegisteredExe
    {
        get
        {
            var cmd = SysEnv.RegGet(Classes + @"\" + Groups[0].ProgId + @"\shell\open\command") as string;
            if (string.IsNullOrEmpty(cmd) || !cmd.StartsWith('"')) return null;
            var end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : null;
        }
    }

    // ───────────── register ─────────────

    /// <summary>File types (Open with / Default apps) and, optionally, the context menu entry.</summary>
    public static bool Register(bool contextMenu = true)
    {
        var exe = SysEnv.EffectiveExe;
        if (!SysEnv.IsOwnExe(exe)) return false;
        try
        {
            var open = Quote(exe!) + " \"%1\"";
            var icon = Quote(exe!) + ",0";
            var caps = SysEnv.CapabilitiesKey;

            SysEnv.RegSet(caps, "ApplicationName", AppInfo.ProductName);
            SysEnv.RegSet(caps, "ApplicationDescription", Loc.T("assoc.appDescription"));
            SysEnv.RegSet(caps, "ApplicationIcon", icon);

            foreach (var g in Groups)
            {
                var prog = Classes + @"\" + g.ProgId;
                SysEnv.RegSet(prog, null, Loc.T("assoc.type." + g.Id));
                SysEnv.RegSet(prog, "FriendlyTypeName", Loc.T("assoc.type." + g.Id));
                SysEnv.RegSet(prog + @"\DefaultIcon", null, icon);
                SysEnv.RegSet(prog + @"\shell\open\command", null, open);
                foreach (var ext in g.Extensions)
                {
                    SysEnv.RegSet(Classes + @"\" + ext + @"\OpenWithProgids", g.ProgId, Array.Empty<byte>(), RegistryValueKind.None);
                    SysEnv.RegSet(caps + @"\FileAssociations", ext, g.ProgId);
                    SysEnv.RegSet(Classes + @"\Applications\" + SysEnv.AppId + @".exe\SupportedTypes", ext, "");
                }
            }

            var app = Classes + @"\Applications\" + SysEnv.AppId + ".exe";
            SysEnv.RegSet(app, "FriendlyAppName", AppInfo.ProductName);
            SysEnv.RegSet(app + @"\DefaultIcon", null, icon);
            SysEnv.RegSet(app + @"\shell\open\command", null, open);
            SysEnv.RegSet(SysEnv.RegisteredAppsKey, SysEnv.AppId, caps);

            if (contextMenu) SetContextMenu(true, notify: false);
            SysEnv.NotifyAssociationsChanged();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("file associations: register failed", ex);
            return false;
        }
    }

    public static void Unregister()
    {
        try
        {
            foreach (var g in Groups)
            {
                foreach (var ext in g.Extensions)
                {
                    SysEnv.RegDeleteValue(Classes + @"\" + ext + @"\OpenWithProgids", g.ProgId);
                    SysEnv.RegDeleteIfEmpty(Classes + @"\" + ext + @"\OpenWithProgids");
                    SysEnv.RegDeleteIfEmpty(Classes + @"\" + ext);
                }
                SysEnv.RegDeleteTree(Classes + @"\" + g.ProgId);
            }
            SysEnv.RegDeleteTree(Classes + @"\Applications\" + SysEnv.AppId + ".exe");
            SysEnv.RegDeleteValue(SysEnv.RegisteredAppsKey, SysEnv.AppId);
            SysEnv.RegDeleteTree(SysEnv.CapabilitiesKey);
            SetContextMenu(false, notify: false);
            SysEnv.NotifyAssociationsChanged();
        }
        catch (Exception ex)
        {
            Log.Error("file associations: unregister failed", ex);
        }
    }

    /// <summary>"Open in BetterNotepad" in the Explorer context menu of every file.</summary>
    public static void SetContextMenu(bool on, bool notify = true)
    {
        try
        {
            if (on)
            {
                var exe = SysEnv.EffectiveExe;
                if (!SysEnv.IsOwnExe(exe)) return;
                SysEnv.RegSet(ContextKey, null, Loc.T("assoc.contextVerb"));
                SysEnv.RegSet(ContextKey, "Icon", Quote(exe!) + ",0");
                SysEnv.RegSet(ContextKey + @"\command", null, Quote(exe!) + " \"%1\"");
            }
            else
            {
                SysEnv.RegDeleteTree(ContextKey);
            }
            if (notify) SysEnv.NotifyAssociationsChanged();
        }
        catch (Exception ex)
        {
            Log.Error("context menu entry failed", ex);
        }
    }

    /// <summary>Re-points registered entries at the current effective exe and re-writes localized labels (after install, update or a language change).</summary>
    public static void Refresh()
    {
        if (IsRegistered) Register(contextMenu: ContextMenuRegistered);
        else if (ContextMenuRegistered) SetContextMenu(true, notify: false);
    }

    /// <summary>Opens Settings ▸ Apps ▸ Default apps on BetterNotepad's page.</summary>
    public static void OpenDefaultApps()
    {
        if (AppEnv.TestMode) return; // a test instance never opens windows of other programs
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=" + SysEnv.AppId) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("default apps page: " + ex.Message);
        }
    }
}
