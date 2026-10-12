using System.IO;
using BetterNotepad.Core;

namespace BetterNotepad;

public static class Program
{
    public static SingleInstance? Instance { get; private set; }

    public static string[] Args { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        // elevated helper for "save as administrator": copy one file and exit, no UI, no single-instance
        if (args.Contains(ElevatedSave.Flag, StringComparer.Ordinal)) return ElevatedSave.Helper(args);

        var testMode = args.Contains("--test-mode", StringComparer.OrdinalIgnoreCase);
        string? home = null;
        var files = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--data-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) home = args[++i];
            else if (a.Equals("--restarted-from", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) i++; // value is a pid, not a file
            else if (a.StartsWith("--", StringComparison.Ordinal)) continue;
            else files.Add(a);
        }

        AppEnv.Init(testMode, home);
        Args = files.Select(f => Path.IsPathRooted(f) ? f : Path.GetFullPath(f)).ToArray();

        // system module: --restarted-from <pid> (wait for the old process), --uninstall, --quit
        if (Modules.Sys.SystemModule.HandleStartupFlags(args, out var exitCode)) return exitCode;

        Instance = new SingleInstance(AppEnv.InstanceKey);
        if (!Instance.IsPrimary)
        {
            // second launch: hand the files to the running app and exit before loading any UI
            if (Instance.SendToPrimary(Args)) return 0;
        }

        return RunApp();
    }

    private static int RunApp()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
