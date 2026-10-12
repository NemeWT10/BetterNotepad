using System.Windows;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Modules.Sys;
using BetterNotepad.UI.Controls;

namespace BetterNotepad.UI;

/// <summary>Rows contributed by the system module to the System and Updates pages (hotkey status, file types, installation, pending update).</summary>
public sealed partial class SettingsPage
{
    /// <summary>Called at the end of <c>BuildSystem</c>; <paramref name="summon"/> is the summon-hotkey row.</summary>
    private void BuildSystemExtras(System.Windows.Controls.StackPanel panel, SettingRow summon)
    {
        var s = Section<SystemSettings>();

        // the combination is taken by another program: say so right under the row
        void UpdateHotkeyNotice()
        {
            var failed = s.GlobalHotkeyEnabled ? HotkeyService.Failure : null;
            summon.SetNotice(failed is null ? null : "inUse:" + failed,
                () => SettingsUi.Notice("warning", Loc.T("settings.system.hotkeyInUse", failed)));
        }
        Bind(UpdateHotkeyNotice);
        void OnStatus()
        {
            if (!summon.IsLoaded)
            {
                HotkeyService.StatusChanged -= OnStatus; // the page was rebuilt or closed
                return;
            }
            UpdateHotkeyNotice();
        }
        HotkeyService.StatusChanged += OnStatus;

        var types = Group(panel, Loc.T("settings.system.typesGroup"));
        types.Add(new SettingRow(Glyphs.Document, Loc.T("settings.system.fileTypes"), Loc.T("settings.system.fileTypesDesc"),
            Switch("system.fileTypes", () => FileAssociations.IsRegistered, v =>
            {
                if (v) FileAssociations.Register(contextMenu: FileAssociations.ContextMenuRegistered);
                else FileAssociations.Unregister();
                OnSettingsChanged(s, null);
            })));
        types.Add(new SettingRow(Glyphs.More, Loc.T("settings.system.contextMenu"), Loc.T("settings.system.contextMenuDesc"),
            Switch("system.contextMenu", () => FileAssociations.ContextMenuRegistered, v => FileAssociations.SetContextMenu(v))));
        types.Add(new SettingRow(Glyphs.Settings, Loc.T("settings.system.defaultApps"), Loc.T("settings.system.defaultAppsDesc"),
            SettingsUi.Button(Loc.T("settings.system.defaultAppsButton"), (_, _) =>
            {
                if (!FileAssociations.IsRegistered) FileAssociations.Register();
                FileAssociations.OpenDefaultApps();
                OnSettingsChanged(s, null);
            }, id: "system.defaultApps")));

        var install = Group(panel, Loc.T("settings.system.installGroup"));
        var button = SettingsUi.Button(Loc.T("settings.system.installButton"), (_, _) => SystemModule.ShowInstallDialog(_window, remember: false),
            "SecondaryButton", Glyphs.Download, "system.install");
        var row = install.Add(new SettingRow(Glyphs.Download, Loc.T("settings.system.install"), null, button));
        Bind(() =>
        {
            row.SetDescription(Installer.IsInstalled
                ? Loc.T("settings.system.installedAt", SysEnv.InstallDir)
                : Loc.T("settings.system.notInstalled", System.IO.Path.GetDirectoryName(SysEnv.CurrentExe) ?? ""));
            button.Visibility = Installer.CanInstall ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    /// <summary>Called at the end of <c>BuildUpdates</c>: a notice with an "Update" button when a newer version is known.</summary>
    private void BuildUpdatesExtras(SettingRow check)
    {
        void Update()
        {
            var pending = UpdateFlow.Pending;
            check.SetNotice(pending is null ? null : "pending:" + pending.Tag, () => SettingsUi.Notice("accent",
                Loc.T("settings.updates.pending", pending!.Version),
                SettingsUi.SmallButton(Loc.T("update.install"), (_, _) => UpdateFlow.OfferPending(), "PrimaryButton", "updates.pending")));
        }
        Bind(Update);
        void OnPending()
        {
            if (!check.IsLoaded)
            {
                UpdateFlow.PendingChanged -= OnPending;
                return;
            }
            Update();
        }
        UpdateFlow.PendingChanged += OnPending;
    }
}
