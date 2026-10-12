using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterNotepad.Ui.Sample;

/// <summary>
/// BetterNotepad.Ui.Sample [html|svg|image|csv|all]. Must run on a hidden desktop:
/// tools\HiddenDesktopRunner\bin\Release\net10.0-windows\HiddenDesktopRunner.exe --timeout 240 -- BetterNotepad.Ui.Sample.exe all
/// Writes PNGs + report.txt to tests\output\ui; exit code = number of failed checks.
/// </summary>
internal static class Program
{
    public static string Root = "";
    public static string Out = "";
    public static readonly List<string> Report = [];
    public static int Failures;

    [STAThread]
    private static int Main(string[] args)
    {
        Root = FindRoot();
        Out = Path.Combine(Root, "tests", "output", "ui");
        Directory.CreateDirectory(Out);
        var only = args.Length > 0 ? args[0] : "all";
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (only is "all" or "html" or "svg") await HtmlScenarios.Run(only);
                foreach (var extra in Extra)
                    await extra(only);
            }
            catch (Exception ex)
            {
                Fail("harness crashed: " + ex);
            }
            finally
            {
                File.WriteAllLines(Path.Combine(Out, "report-" + only + ".txt"), Report);
                app.Shutdown(Failures);
            }
        });
        return app.Run();
    }

    /// <summary>Further scenario sets (imaging, table) register here.</summary>
    public static readonly List<Func<string, Task>> Extra = [];

    public static void Check(bool ok, string what)
    {
        Report.Add((ok ? "ok   " : "FAIL ") + what);
        Console.WriteLine(Report[^1]);
        if (!ok) Failures++;
    }

    public static void Fail(string what) => Check(false, what);

    public static void Note(string what)
    {
        Report.Add("     " + what);
        Console.WriteLine(Report[^1]);
    }

    public static void UseTheme(string name)
    {
        var res = Application.Current.Resources.MergedDictionaries;
        res.Clear();
        res.Add(name == "daylight" ? FakeTheme.Daylight() : FakeTheme.Aurora());
    }

    /// <summary>A plain window on the hidden desktop (never activated, no taskbar button).</summary>
    public static Window Host(FrameworkElement content, double width, double height)
    {
        var w = new Window
        {
            Width = width,
            Height = height,
            Left = 0,
            Top = 0,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = content,
        };
        w.SetResourceReference(Window.BackgroundProperty, "Brush.Bg");
        w.Show();
        return w;
    }

    public static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 10000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > until) return false;
            await Task.Delay(40);
        }
        return true;
    }

    public static void SavePng(BitmapSource bmp, string name)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(Out, name));
        enc.Save(fs);
        Note("png " + name);
    }

    /// <summary>The element as a picture of its own visual tree at 1.25x (the user's screen is never touched).</summary>
    public static void Snapshot(FrameworkElement el, string name, double scale = 1.25)
    {
        el.UpdateLayout();
        var w = (int)Math.Ceiling(el.ActualWidth * scale);
        var h = (int)Math.Ceiling(el.ActualHeight * scale);
        // rendered in the element's own coordinates (a VisualBrush would squeeze in the shadows that spill past its bounds)
        var rtb = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(el);
        SavePng(rtb, name);
    }

    private static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "BetterNotepad.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }
}
