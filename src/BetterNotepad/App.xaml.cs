using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Files;
using BetterNotepad.Documents.Session;
using BetterNotepad.UI;
using BetterNotepad.UI.Themes;
using BetterNotepad.UI.Windows;

namespace BetterNotepad;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    private readonly Dictionary<WindowModel, MainWindow> _windows = new();
    private bool _shuttingDown;

    public IEnumerable<MainWindow> OpenWindows => _windows.Values;

    public MainWindow? MainAppWindow => _windows.Values.FirstOrDefault(w => w.Model.IsMain);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            Log.Error("fatal", ex.ExceptionObject as Exception);
            try { SessionService.I.Flush(); } catch { /* last resort */ }
        };

        Bootstrap();
        ShowWorkspace();
        Modules.Sys.SystemModule.Start(); // tray, hotkey, autostart, updates, install offer
#if DEBUG
        if (SelfTest.Requested) SelfTest.Start();
#endif
        // --test-mode + BETTERNOTEPAD_SNAPSHOT=<dir>: render UI scenarios to PNG (under SelfTest the same variable adds the palette / settings renders)
        if (SnapshotMode.Requested && Environment.GetEnvironmentVariable("BETTERNOTEPAD_SELFTEST") != "1") SnapshotMode.Start();
        // --test-mode + BETTERNOTEPAD_PERF=<report>: tray memory / CPU scenario, then quit
        else if (PerfProbe.Requested && Environment.GetEnvironmentVariable("BETTERNOTEPAD_SELFTEST") != "1") PerfProbe.Start();

        Program.Instance?.Listen(msg => Dispatcher.BeginInvoke(() => OnSecondInstance(msg)));
    }

    /// <summary>Settings, language, theme, modules, session — everything before the first window.</summary>
    public static void Bootstrap()
    {
        TextFileIO.EnsureCodePages();
        SettingsService.Init(AppEnv.SettingsFile);
        var appearance = SettingsService.I.Get<AppearanceSettings>();
        Loc.I.SetLanguage(appearance.Language);
        Motion.I.Update(appearance.Animations);
        ThemeManager.I.Apply(appearance.Theme, appearance.Mica);
        SettingsService.I.Changed += OnSettingsChanged;

        AppCommands.RegisterAll();
        Modules.ModuleLoader.RegisterAll();

        if (Workspace.I.Windows.Count == 0) Workspace.I.Windows.Add(new WindowModel(isMain: true));
        var files = SettingsService.I.Get<FilesSettings>();
        if (files.RestoreSession && SessionService.Load() is { } session) SessionService.I.Restore(session);
        // a fresh start gets an empty tab, unless files were passed (a folder alone still gets one)
        if (!Workspace.I.Main.Groups.SelectMany(g => g.Tabs).Any() && Program.Args.All(Directory.Exists))
            Workspace.I.Main.ActiveGroup.Add(DocumentManager.I.NewUntitled());
        SessionService.I.Attach();

        FileWatchService.I.Attach();
        FileWatchService.I.Changed += OnFilesChangedOnDisk;
    }

    /// <summary>Other programs changed files: every window re-checks its documents that live at those paths.</summary>
    private static void OnFilesChangedOnDisk(IReadOnlyCollection<string> paths)
    {
        var changed = paths.Select(FileUtil.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var w in Current.OpenWindows.ToList())
        {
            var docs = w.Model.AllDocuments.Where(d => d.FilePath is not null && changed.Contains(d.FilePath)).ToList();
            if (docs.Count > 0) w.CheckExternalChanges(docs);
        }
    }

    private static void OnSettingsChanged(SettingsSection section, string? property)
    {
        if (section is AppearanceSettings a)
        {
            switch (property)
            {
                case nameof(AppearanceSettings.Language) or null:
                    Loc.I.SetLanguage(a.Language);
                    break;
            }
            if (property is nameof(AppearanceSettings.Animations) or null) Motion.I.Update(a.Animations);
            if (property is nameof(AppearanceSettings.Theme) or nameof(AppearanceSettings.Mica) or null)
            {
                ThemeManager.I.Apply(a.Theme, a.Mica);
                foreach (var w in Current?.OpenWindows ?? []) WindowEffects.Apply(w);
            }
        }
    }

    private void ShowWorkspace()
    {
        foreach (var model in Workspace.I.Windows.ToList()) ShowWindow(model);
        Workspace.I.Windows.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (WindowModel m in e.NewItems) ShowWindow(m);
        };
        if (Program.Args.Length > 0) OpenFiles(Program.Args, MainAppWindow);
        // test instances never take the focus away from the user
        if (!AppEnv.TestMode && MainAppWindow is { IsVisible: true } main) main.Activate(); // not when it starts hidden in the tray
    }

    public MainWindow ShowWindow(WindowModel model)
    {
        if (_windows.TryGetValue(model, out var existing)) return existing;
        var w = new MainWindow(model);
        _windows[model] = w;
        w.Closed += (_, _) => _windows.Remove(model);
        switch (Modules.Sys.SystemModule.ModeFor(model))
        {
            case Modules.Sys.StartupMode.Hidden: break; // autostart: stays in the tray until summoned
            case Modules.Sys.StartupMode.Minimized: w.WindowState = WindowState.Minimized; w.Show(); break;
            default: w.Show(); break;
        }
        return w;
    }

    public MainWindow? WindowFor(WindowModel model) => _windows.GetValueOrDefault(model);

    /// <summary>Opens files in a window (default: the main one) and brings it to the front.</summary>
    public void OpenFiles(IEnumerable<string> paths, MainWindow? target = null)
    {
        target ??= MainAppWindow;
        if (target is null) return;
        foreach (var p in paths)
        {
            // a folder (command line, second instance, "Open with") becomes the window's Explorer folder
            if (Directory.Exists(p)) target.OpenFolder(p);
            else target.OpenFile(p);
        }
    }

    private void OnSecondInstance(InstanceMessage msg)
    {
        if (msg.Args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            Quit(); // asked by the uninstaller
            return;
        }
        var window = MainAppWindow;
        if (window is null) return;
        var files = msg.Args.Select(a => Path.IsPathRooted(a) ? a : Path.GetFullPath(Path.Combine(msg.WorkingDirectory, a))).ToList();
        OpenFiles(files, window);
        if (files.Count == 0 && window.IsVisible == false) window.NewTab();
        window.BringToFront();
    }

    /// <summary>Quit the app: write the session (incl. unsaved buffers) and close every window. Never asks.</summary>
    public void Quit()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        Modules.Sys.SystemModule.Shutdown(); // tray icon, hotkeys, update timer
        foreach (var w in _windows.Values) w.CaptureViewState();
        SessionService.I.Flush();
        SettingsService.I.Flush();
        FileStateStore.I.Flush();
        RecentFiles.I.Flush();
        foreach (var w in _windows.Values.ToList()) w.CloseForShutdown();
        Program.Instance?.Dispose();
        Shutdown();
    }

    public bool IsShuttingDown => _shuttingDown;

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows shutdown / logoff: keep everything, ask nothing
        Quit();
        base.OnSessionEnding(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("unhandled", e.Exception);
        try { SessionService.I.Flush(); } catch { /* keep going */ }
        e.Handled = true;
        MainAppWindow?.Toasts.Show(Loc.T("error.unexpected", e.Exception.Message), ToastKind.Error);
    }
}
