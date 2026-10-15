using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using BetterNotepad.Core;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.UI;
using BetterNotepad.UI.Controls;
using BetterNotepad.UI.Tray;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Modules.Sys;

/// <summary>How the main window appears at startup.</summary>
internal enum StartupMode
{
    Normal,
    /// <summary>Autostart with the tray on: never shown, lives in the tray.</summary>
    Hidden,
    /// <summary>Autostart without a tray: minimized to the taskbar.</summary>
    Minimized,
}

/// <summary>
/// System integration: tray, global hotkey, autostart, file types, installation and updates.
/// Hooks: <c>ModuleLoader</c> → <see cref="Register"/>, <c>App.OnStartup</c> → <see cref="Start"/>,
/// <c>App.ShowWindow</c> → <see cref="ModeFor"/>, <c>App.Quit</c> → <see cref="Shutdown"/>, <c>Program.Main</c> → <see cref="HandleStartupFlags"/>.
/// </summary>
internal static class SystemModule
{
    public static bool StartMinimizedRequested { get; } = Environment.GetCommandLineArgs().Skip(1).Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

    private static SystemSettings Prefs => SettingsService.I.Get<SystemSettings>();

    private static MainWindow? Main => App.Current?.MainAppWindow;

    // ───────────── program entry flags ─────────────

    /// <summary>
    /// --restarted-from &lt;pid&gt; (an update or install relaunch: wait until the old process is gone),
    /// --uninstall (Apps &amp; features) and --quit. Returns true when the program should exit with <paramref name="exitCode"/>.
    /// </summary>
    public static bool HandleStartupFlags(string[] args, out int exitCode)
    {
        exitCode = 0;
        var i = Array.FindIndex(args, a => a.Equals("--restarted-from", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid)) WaitForExit(pid);

        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            exitCode = Uninstaller.Run();
            return true;
        }
        if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            Uninstaller.AskRunningInstanceToQuit();
            return true;
        }
        return false;
    }

    private static void WaitForExit(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit(15000);
        }
        catch
        {
            // already gone
        }
    }

    // ───────────── startup ─────────────

    public static StartupMode ModeFor(WindowModel model)
    {
        if (model.IsMain && PerfProbe.StartHidden) return StartupMode.Hidden; // test probe: autostart into the tray
        if (!model.IsMain || !StartMinimizedRequested || AppEnv.TestMode) return StartupMode.Normal;
        return Prefs.TrayEnabled ? StartupMode.Hidden : StartupMode.Minimized;
    }

    /// <summary>Called once the first windows exist.</summary>
    public static void Start()
    {
        var s = Prefs;
        UpdateService.CleanupLeftovers(SysEnv.CurrentExe);

        Autostart.Sync(s.Autostart);
        if (FileAssociations.IsRegistered && !SysEnv.SamePath(FileAssociations.RegisteredExe, SysEnv.EffectiveExe)) FileAssociations.Refresh();

        ApplyTray(s);
        ApplyHotkey(s);
        ApplyGhostHotkey(s);
        SettingsService.I.Changed += OnSettingsChanged;
        Loc.LanguageChanged += FileAssociations.Refresh;

        UpdateFlow.Reschedule();
        if (Main is { } w)
        {
            w.IsVisibleChanged += (_, _) =>
            {
                if (w.IsVisible) UpdateFlow.OnMainShown();
            };
            // autostart straight into the tray: lean from the start (trimmed once the startup work is done)
            if (!w.IsVisible && (TrayService.IsAvailable || PerfProbe.StartHidden)) TrayRest.Settle();
            if (Installer.ShouldOfferInstall(s) && w.IsVisible) _ = OfferInstallLater(w);
        }
    }

    public static void Shutdown()
    {
        UpdateFlow.Stop();
        HotkeyService.ReleaseAll();
        SystemTray.Stop();
        MessageWindow.Dispose();
    }

    private static void OnSettingsChanged(SettingsSection section, string? property)
    {
        switch (section)
        {
            case SystemSettings s:
                if (property is null or nameof(SystemSettings.Autostart)) Autostart.Sync(s.Autostart);
                if (property is null or nameof(SystemSettings.TrayEnabled)) ApplyTray(s);
                if (property is null or nameof(SystemSettings.GlobalHotkeyEnabled) or nameof(SystemSettings.GlobalHotkey)) ApplyHotkey(s);
                if (property is null or nameof(SystemSettings.GhostHotkey)) ApplyGhostHotkey(s);
                break;
            case UpdateSettings:
                if (property is null or nameof(UpdateSettings.Mode)) UpdateFlow.Reschedule();
                break;
        }
    }

    private static void ApplyTray(SystemSettings s)
    {
        if (AppEnv.TestMode) return;
        if (s.TrayEnabled)
        {
            SystemTray.Start();
            return;
        }
        SystemTray.Stop();
        // a hidden window without a tray icon would be unreachable
        if (Main is { IsVisible: false } w) w.BringToFront();
    }

    private static void ApplyHotkey(SystemSettings s)
    {
        var state = HotkeyService.Set(HotkeyService.Summon, s.GlobalHotkeyEnabled ? s.GlobalHotkey : null, QuickNote);
        // another program owns the combination: say so (the settings page shows the same hint under the row)
        if (state == HotkeyState.Failed && Main is { IsVisible: true } w)
            w.Toasts.Show(Loc.T("hotkey.inUse", s.GlobalHotkey), ToastKind.Warning, seconds: 6);
    }

    /// <summary>
    /// Ghost mode of pinned windows (default Win+Shift+G). Independent of the summon switch: it is the way back from
    /// click-through windows. A test instance only validates it (<see cref="HotkeyState.SkippedTest"/>).
    /// </summary>
    private static void ApplyGhostHotkey(SystemSettings s) => HotkeyService.Set(HotkeyService.Ghost, s.GhostHotkey, PinnedWindows.ToggleGhostGlobal);

    /// <summary>The global hotkey (and the tray menu): bring the program up with an empty note ready to type into.</summary>
    public static void QuickNote()
    {
        var w = Main;
        if (w is null) return;
        w.BringToFront();
        var fresh = w.ActiveDocument is TextDocument { IsUntitled: true, IsDirty: false } t && (!t.HasBuffer || t.Buffer.TextLength == 0);
        if (!fresh) w.NewTab();
    }

    // ───────────── commands ─────────────

    /// <summary>Palette commands of the module (texts in Strings.System.cs).</summary>
    public static void Register()
    {
        Cmd("sys.checkUpdates", c => UpdateFlow.CheckNow(AppCommands.WindowOf(c)));
        Cmd("sys.registerFileTypes", c =>
        {
            var ok = FileAssociations.Register();
            AppCommands.WindowOf(c)?.Toasts.Show(Loc.T(ok ? "assoc.registered" : "assoc.failed"), ok ? ToastKind.Success : ToastKind.Error);
        });
        Cmd("sys.openDefaultApps", _ => FileAssociations.OpenDefaultApps());
        Cmd("sys.install", c =>
        {
            if (AppCommands.WindowOf(c) is { } w) ShowInstallDialog(w, remember: false);
        }, can: _ => Installer.CanInstall);
    }

    private static void Cmd(string id, Action<CommandContext> run, Func<CommandContext, bool>? can = null)
        => CommandRegistry.Register(new AppCommand
        {
            Id = id,
            TitleKey = "cmd." + id,
            CategoryKey = "cmd.cat.app",
            Execute = run,
            CanExecute = can,
        });

    // ───────────── install ─────────────

    private static async Task OfferInstallLater(MainWindow w)
    {
        await Task.Delay(1200);
        if (w.IsVisible && !Prefs.InstallPromptShown) ShowInstallDialog(w, remember: true);
    }

    /// <summary>"Install / Run without installing" (first start) or the explicit install command.</summary>
    public static async void ShowInstallDialog(MainWindow w, bool remember)
    {
        var details = BuildInstallDetails(out var associations);
        var answer = await w.DialogLayer.Show(
            Loc.T("install.title"), Loc.T("install.text"),
            [
                new DialogButton("skip", Loc.T("install.skip"), DialogButtonKind.Ghost, IsCancel: true),
                new DialogButton("install", Loc.T("install.install"), DialogButtonKind.Primary, IsDefault: true),
            ],
            extra: details, glyph: Glyphs.Download);
        if (remember) Prefs.InstallPromptShown = true;
        if (answer == "install") await InstallNow(w, associations.IsChecked == true);
    }

    private static async Task InstallNow(MainWindow w, bool registerTypes)
    {
        var exe = SysEnv.CurrentExe;
        if (exe is null) return;
        w.Toasts.Show(Loc.T("install.installing"), ToastKind.Info, seconds: 8);
        var result = await Task.Run(() => Installer.Install(exe));
        if (!result.Ok)
        {
            w.Toasts.Show(Loc.T("install.failed", result.Error ?? "?"), ToastKind.Error);
            return;
        }
        Prefs.InstallPromptShown = true;
        if (registerTypes) FileAssociations.Register();
        Autostart.Sync(Prefs.Autostart);
        if (Installer.RelaunchInstalled(Program.Args, minimized: false)) App.Current.Quit();
        else w.Toasts.Show(Loc.T("install.done"), ToastKind.Success);
    }

    private static StackPanel BuildInstallDetails(out CheckBox associations)
    {
        var panel = new StackPanel();
        void Line(string glyph, string text)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new Icon { Glyph = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
            var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 18 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            Grid.SetColumn(label, 1);
            row.Children.Add(icon);
            row.Children.Add(label);
            panel.Children.Add(row);
        }
        Line(Glyphs.Folder, SysEnv.InstallDir);
        Line(Glyphs.Window, Loc.T("install.detail.startMenu"));
        Line(Glyphs.Settings, Loc.T("install.detail.apps"));
        Line(Glyphs.Shield, Loc.T("install.detail.noAdmin"));

        // a switch has no label of its own: text on the left, switch on the right
        var option = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        option.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        option.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var optionLabel = new TextBlock { Text = Loc.T("install.associations"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(28, 0, 16, 0) };
        optionLabel.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        associations = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        associations.SetResourceReference(FrameworkElement.StyleProperty, "Switch");
        System.Windows.Automation.AutomationProperties.SetName(associations, Loc.T("install.associations"));
        Grid.SetColumn(associations, 1);
        option.Children.Add(optionLabel);
        option.Children.Add(associations);
        panel.Children.Add(option);
        return panel;
    }
}
