using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using BetterNotepad.Core;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Files;
using BetterNotepad.UI.Controls;

namespace BetterNotepad.UI;

/// <summary>General, Files, System and Updates pages.</summary>
public sealed partial class SettingsPage
{
    // ───────────── General ─────────────

    private void BuildGeneral(StackPanel panel)
    {
        var appearance = Section<AppearanceSettings>();
        var files = Section<FilesSettings>();

        var lang = Group(panel, Loc.T("settings.general.languageGroup"));
        var langRow = lang.Add(new SettingRow(Glyphs.Globe, Loc.T("settings.general.language"), null,
            Segment("general.language",
                [("system", Loc.T("settings.general.languageSystem")), ("pl", Loc.T("settings.lang.pl")), ("en", Loc.T("settings.lang.en"))],
                () => appearance.Language, v => appearance.Language = v)));
        Bind(() => langRow.SetDescription(appearance.Language is "pl" or "en"
            ? Loc.T("settings.general.languageDesc")
            : Loc.T("settings.general.languageSystemNow", Loc.T("settings.lang." + Loc.ResolveLanguage("system")))));

        var motion = Group(panel, Loc.T("settings.general.motionGroup"));
        var anim = motion.Add(new SettingRow("\uE945", Loc.T("settings.general.animations"), Loc.T("settings.general.animationsDesc"),
            Switch("general.animations", () => appearance.Animations, v => appearance.Animations = v)));
        Bind(() => anim.SetNotice(appearance.Animations && !SystemParameters.ClientAreaAnimation ? "systemOff" : null,
            () => SettingsUi.Notice("warning", Loc.T("settings.general.animationsSystemOff"))));

        var session = Group(panel, Loc.T("settings.general.sessionGroup"));
        session.Add(new SettingRow(Glyphs.History, Loc.T("settings.general.restoreSession"), Loc.T("settings.general.restoreSessionDesc"),
            Switch("general.restoreSession", () => files.RestoreSession, v => files.RestoreSession = v)));

        var backup = Group(panel, Loc.T("settings.general.backupGroup"));
        var export = SettingsUi.Button(Loc.T("settings.general.exportButton"), (_, _) => ExportSettings(), id: "general.export");
        var import = SettingsUi.Button(Loc.T("settings.general.importButton"), (_, _) => ImportSettings(), id: "general.import");
        export.MinWidth = import.MinWidth = 112;
        backup.Add(new SettingRow("\uE898", Loc.T("settings.general.export"), Loc.T("settings.general.exportDesc"), export));
        backup.Add(new SettingRow(Glyphs.Download, Loc.T("settings.general.import"), Loc.T("settings.general.importDesc"), import));
    }

    private string JsonFilter => $"{Loc.T("settings.general.jsonFilter")} (*.json)|*.json|{Loc.T("dialog.filterAll")} (*.*)|*.*";

    private void ExportSettings()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("settings.general.exportTitle"),
            FileName = $"BetterNotepad-settings-{DateTime.Now:yyyy-MM-dd}.json",
            DefaultExt = ".json",
            Filter = JsonFilter,
            AddExtension = true,
        };
        if (dlg.ShowDialog(_window) == true) ExportTo(dlg.FileName);
    }

    internal bool ExportTo(string file)
    {
        try
        {
            SettingsService.I.ExportTo(file);
            _window.Toasts.Show(Loc.T("settings.general.exported", Path.GetFileName(file)), ToastKind.Success);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("settings: export failed", ex);
            _window.Toasts.Show(Loc.T("settings.general.exportFailed", ex.Message), ToastKind.Error);
            return false;
        }
    }

    private async void ImportSettings()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("settings.general.importTitle"), Filter = JsonFilter, CheckFileExists = true };
        if (dlg.ShowDialog(_window) != true) return;
        var answer = await _window.DialogLayer.Show(
            Loc.T("settings.general.importConfirmTitle"), Loc.T("settings.general.importConfirmText", Path.GetFileName(dlg.FileName)),
            [
                new DialogButton("cancel", Loc.T("common.cancel"), DialogButtonKind.Ghost, IsCancel: true),
                new DialogButton("import", Loc.T("settings.general.importButtonConfirm"), DialogButtonKind.Primary, IsDefault: true),
            ], glyph: "\uE8B5");
        if (answer == "import") ImportFrom(dlg.FileName);
    }

    /// <summary>Replaces all settings with a file exported earlier (a file that is not one is refused, nothing changes).</summary>
    internal bool ImportFrom(string file)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject root || !root.ContainsKey("schemaVersion"))
            {
                _window.Toasts.Show(Loc.T("settings.general.importInvalid"), ToastKind.Error);
                return false;
            }
            SettingsService.I.ImportFrom(file);
            Keymap.Invalidate();
            RefreshAllChrome();
            _window.Toasts.Show(Loc.T("settings.general.imported"), ToastKind.Success);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("settings: import failed", ex);
            _window.Toasts.Show(Loc.T("settings.general.importInvalid"), ToastKind.Error);
            return false;
        }
    }

    /// <summary>Menus and the palette pill show shortcuts: rebuild them in every window.</summary>
    private static void RefreshAllChrome()
    {
        foreach (var w in App.Current?.OpenWindows.ToList() ?? []) w.RefreshChrome();
    }

    // ───────────── Files ─────────────

    private void BuildFiles(StackPanel panel)
    {
        var f = Section<FilesSettings>();

        var fresh = Group(panel, Loc.T("settings.files.newGroup"));
        fresh.Add(new SettingRow(Glyphs.Text, Loc.T("settings.files.encoding"), Loc.T("settings.files.encodingDesc"),
            Choice("files.encoding", TextFileIO.Choices.Select(c => (c.Id, c.Label)).ToList(),
                () => f.DefaultEncoding == "utf-8" && f.DefaultBom ? "utf-8-bom" : f.DefaultEncoding,
                v =>
                {
                    var choice = TextFileIO.Choices.FirstOrDefault(c => c.Id == v);
                    f.DefaultBom = choice.Bom;
                    f.DefaultEncoding = v == "utf-8-bom" ? "utf-8" : v;
                })));
        fresh.Add(new SettingRow("\uE751", Loc.T("settings.files.eol"), Loc.T("settings.files.eolDesc"),
            Segment("files.eol", [("CRLF", Loc.T("eol.crlf")), ("LF", Loc.T("eol.lf"))],
                () => f.DefaultEol.Equals("LF", StringComparison.OrdinalIgnoreCase) ? "LF" : "CRLF", v => f.DefaultEol = v)));

        var large = Group(panel, Loc.T("settings.files.largeGroup"));
        large.Add(new SettingRow(Glyphs.Page, Loc.T("settings.files.largeFile"), Loc.T("settings.files.largeFileDesc"),
            Number("files.largeFileMb", 5, 2048,
                (v, d) => d > 0 ? v + (v < 100 ? 5 : v < 500 ? 50 : 100) : v - (v <= 100 ? 5 : v <= 500 ? 50 : 100),
                () => f.LargeFileMb, v => f.LargeFileMb = (int)v, v => Loc.T("settings.unit.mb", v), 78)));

        var auto = Group(panel, Loc.T("settings.files.autosaveGroup"));
        auto.Add(new SettingRow(Glyphs.Save, Loc.T("settings.files.autosave"), Loc.T("settings.files.autosaveDesc"),
            Switch("files.autosave", () => f.Autosave, v => f.Autosave = v)));
        var seconds = auto.Add(new SettingRow(null, Loc.T("settings.files.autosaveDelay"), Loc.T("settings.files.autosaveDelayDesc"),
            Number("files.autosaveSeconds", 2, 3600, Stepper.Presets(2, 5, 10, 15, 30, 45, 60, 120, 300, 600, 1800, 3600),
                () => f.AutosaveSeconds, v => f.AutosaveSeconds = (int)v, Duration, 78), indent: true));
        Bind(() => seconds.SetActive(f.Autosave));

        var preview = Group(panel, Loc.T("settings.files.previewGroup"));
        preview.Add(new SettingRow(Glyphs.Split, Loc.T("settings.files.previewLeft"), Loc.T("settings.files.previewLeftDesc"),
            Switch("files.previewLeft", () => f.PreviewOnLeft, v => f.PreviewOnLeft = v)));
        var views = Group(panel, Loc.T("settings.files.viewsGroup"));
        foreach (var (kind, glyph) in new[] { (DocumentKind.Markdown, Glyphs.Markdown), (DocumentKind.Html, Glyphs.Globe), (DocumentKind.Svg, Glyphs.Image), (DocumentKind.Csv, Glyphs.Table) })
        {
            var key = kind.ToString().ToLowerInvariant();
            IReadOnlyList<(string, string)> options = kind == DocumentKind.Csv
                ? [("preview", Loc.T("view.table")), ("split", Loc.T("view.split")), ("edit", Loc.T("view.text"))]
                : [("preview", Loc.T("view.preview")), ("split", Loc.T("view.split")), ("edit", Loc.T("view.edit"))];
            var k = kind;
            views.Add(new SettingRow(glyph, Loc.T("settings.kind." + key), Loc.T("settings.files.viewDesc." + key),
                Segment("files.view." + key, options,
                    () => TextDocument.DefaultViewMode(k).ToString().ToLowerInvariant(),
                    v => f.DefaultViews = new Dictionary<string, string>(f.DefaultViews, StringComparer.OrdinalIgnoreCase) { [key] = v })));
        }
        BuildHtmlPreview(panel);

        // images: the quality "Save as JPEG" starts from (the dialog asks each time and remembers the answer)
        var img = Section<BetterNotepad.Modules.Imaging.ImagingSettings>();
        var images = Group(panel, Loc.T("settings.images.group"));
        images.Add(new SettingRow(Glyphs.Image, Loc.T("settings.images.jpegQuality"), Loc.T("settings.images.jpegQualityDesc"),
            Number("images.jpegQuality", 1, 100, Stepper.Presets(50, 60, 70, 75, 80, 85, 90, 92, 95, 98, 100),
                () => img.JpegQuality, v => img.JpegQuality = (int)v, v => v.ToString("0") + " %", 70)));
    }

    private static string Duration(double seconds)
        => seconds < 60 ? Loc.T("settings.unit.seconds", seconds) : Loc.T("settings.unit.minutes", Math.Round(seconds / 60, 1));

    // ───────────── System ─────────────

    private void BuildSystem(StackPanel panel)
    {
        var s = Section<SystemSettings>();
        var start = Group(panel, Loc.T("settings.system.startGroup"));
        start.Add(new SettingRow("\uE7E8", Loc.T("settings.system.autostart"), Loc.T("settings.system.autostartDesc"),
            Switch("system.autostart", () => s.Autostart, v => s.Autostart = v)));

        var tray = Group(panel, Loc.T("settings.system.trayGroup"));
        tray.Add(new SettingRow(Glyphs.Window, Loc.T("settings.system.tray"), Loc.T("settings.system.trayDesc"),
            Switch("system.tray", () => s.TrayEnabled, v => s.TrayEnabled = v)));
        var close = tray.Add(new SettingRow(null, Loc.T("settings.system.closeToTray"), Loc.T("settings.system.closeToTrayDesc"),
            Switch("system.closeToTray", () => s.CloseToTray, v => s.CloseToTray = v), indent: true));
        Bind(() => close.SetActive(s.TrayEnabled));

        var keys = Group(panel, Loc.T("settings.system.hotkeyGroup"));
        keys.Add(new SettingRow(Glyphs.Keyboard, Loc.T("settings.system.hotkey"), Loc.T("settings.system.hotkeyDesc"),
            Switch("system.hotkeyEnabled", () => s.GlobalHotkeyEnabled, v => s.GlobalHotkeyEnabled = v)));
        var summon = HotkeyRow(keys, "system.globalHotkey", null, Loc.T("settings.system.hotkeyChord"), Loc.T("settings.system.hotkeyChordDesc"),
            () => s.GlobalHotkey, v => s.GlobalHotkey = v, "Win+Shift+N", () => s.GhostHotkey);
        Bind(() => summon.SetActive(s.GlobalHotkeyEnabled));
        HotkeyRow(keys, "system.ghostHotkey", Glyphs.Ghost, Loc.T("settings.system.ghostHotkey"), Loc.T("settings.system.ghostHotkeyDesc"),
            () => s.GhostHotkey, v => s.GhostHotkey = v, "Win+Shift+G", () => s.GlobalHotkeyEnabled ? s.GlobalHotkey : null);
        BuildSystemExtras(panel, summon);
    }

    // ───────────── Updates ─────────────

    private void BuildUpdates(StackPanel panel)
    {
        var u = Section<UpdateSettings>();
        var mode = Group(panel, Loc.T("settings.updates.modeGroup"));
        var modeRow = mode.Add(new SettingRow("\uE895", Loc.T("settings.updates.mode"), null,
            Segment("updates.mode",
                [("auto", Loc.T("settings.updates.mode.auto")), ("ask", Loc.T("settings.updates.mode.ask")), ("off", Loc.T("settings.updates.mode.off"))],
                () => u.Mode, v => u.Mode = v)));
        Bind(() => modeRow.SetDescription(Loc.T("settings.updates.mode." + (u.Mode is "auto" or "off" ? u.Mode : "ask") + ".desc")));
        var check = mode.Add(new SettingRow(Glyphs.Refresh, Loc.T("settings.updates.checkNow"), null,
            SettingsUi.Button(Loc.T("settings.updates.checkButton"), (_, _) => CheckForUpdates(), id: "updates.check")));
        Bind(() => check.SetDescription(u.LastCheckUtc is { } t
            ? Loc.T("settings.updates.lastCheck", t.ToLocalTime().ToString("g", Loc.I.Culture))
            : Loc.T("settings.updates.neverChecked")));
        BuildUpdatesExtras(check);

        var privacy = Group(panel, Loc.T("settings.updates.privacyGroup"));
        privacy.Add(new SettingRow(Glyphs.Shield, Loc.T("settings.updates.privacy"), Loc.T("settings.updates.privacyDesc"), null));
    }

    private void CheckForUpdates() => Modules.Sys.UpdateFlow.CheckNow(_window);
}
