using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterNotepad.UiSnapshots;

/// <summary>
/// Renders every UI scenario once (the app drives itself, see src\BetterNotepad\Core\SnapshotMode.cs) and checks the result.
/// Nothing here ever looks at the screen: the app runs on a private hidden desktop and writes its own visual tree to PNG.
/// Output: tests\output\snap (git-excluded). Review the PNGs by eye against the style guide after a change.
/// </summary>
public sealed class SnapshotTests : IClassFixture<SnapshotRun>
{
    private static readonly string[] Themes = ["aurora", "graphite", "daylight", "neon", "sunset"];
    private static readonly string[] ScenariosPerTheme = ["01_empty", "02_code", "03_split", "04_find", "05_dialog", "06_toasts", "07_syntax"];

    private readonly SnapshotRun _run;

    public SnapshotTests(SnapshotRun run) => _run = run;

    [Fact]
    public void App_finished_without_failures()
    {
        Assert.True(_run.ExitCode == 0, $"app exit code {_run.ExitCode}\n{_run.Output}");
        Assert.False(File.Exists(Path.Combine(_run.PngDir, "FAILED.txt")), "FAILED.txt exists:\n" + SafeRead(Path.Combine(_run.PngDir, "FAILED.txt")));
        Assert.True(File.Exists(Path.Combine(_run.PngDir, "index.txt")), "index.txt missing: the app did not finish\n" + _run.Output);
        var log = Path.Combine(_run.DataDir, "local", "logs", "app.log");
        var errors = File.Exists(log) ? File.ReadAllLines(log).Where(l => l.Contains("[ERROR]")).ToList() : [];
        Assert.True(errors.Count == 0, "app.log has errors:\n" + string.Join("\n", errors));
    }

    [Fact]
    public void Every_scenario_is_rendered_for_every_theme()
    {
        var missing = from t in Themes
                      from s in ScenariosPerTheme
                      let file = Path.Combine(_run.PngDir, $"{s}_{t}_en.png")
                      where !File.Exists(file)
                      select Path.GetFileName(file);
        Assert.Empty(missing);
    }

    [Fact]
    public void Pngs_are_real_pictures()
    {
        var bad = new List<string>();
        foreach (var file in Directory.GetFiles(_run.PngDir, "*.png"))
        {
            var info = Inspect(file);
            // strips (a notice bar, a toolbar) are cropped on purpose: wide and short is fine, tiny is not
            if (info.Width < 150 || info.Height < 60 || info.Width * info.Height < 40_000) bad.Add($"{Path.GetFileName(file)}: only {info.Width}x{info.Height}");
            else if (info.DistinctColors < 12) bad.Add($"{Path.GetFileName(file)}: blank ({info.DistinctColors} colors)");
        }
        Assert.Empty(bad);
    }

    [Fact]
    public void Full_window_shots_have_the_expected_size()
    {
        // 1100 x 700 DIP rendered at 1.25x
        foreach (var file in Directory.GetFiles(_run.PngDir, "0[1-6]_*_en.png"))
        {
            var info = Inspect(file);
            Assert.True(info.Width == 1375 && info.Height == 875, $"{Path.GetFileName(file)} is {info.Width}x{info.Height}");
        }
    }

    [Fact]
    public void Core_text_colors_have_enough_contrast_in_every_theme()
    {
        var report = Path.Combine(_run.PngDir, "contrast.txt");
        Assert.True(File.Exists(report), "contrast.txt missing");
        var strict = new[] { "Text on Bg", "TextDim on Bg", "OnAccent on Accent", "PanelText on Panel", "PanelTextDim on Panel", "EditorText on EditorBg" };
        var low = File.ReadAllLines(report).Where(l => l.StartsWith("LOW ") && strict.Any(l.Contains)).ToList();
        Assert.True(low.Count == 0, "low contrast:\n" + string.Join("\n", low));
    }

    private static string SafeRead(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    private static (int Width, int Height, int DistinctColors) Inspect(string path)
    {
        using var fs = File.OpenRead(path);
        var frame = BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        var colors = new HashSet<int>();
        for (var i = 0; i < pixels.Length; i += 4 * 7) // sample every 7th pixel
        {
            colors.Add(pixels[i] | pixels[i + 1] << 8 | pixels[i + 2] << 16);
            if (colors.Count > 64) break;
        }
        return (bgra.PixelWidth, bgra.PixelHeight, colors.Count);
    }
}

/// <summary>Runs the app once for all tests of the class: hidden desktop, test mode, data inside tests\output\snap.</summary>
public sealed class SnapshotRun
{
    public string PngDir { get; }

    public string DataDir { get; }

    public int ExitCode { get; }

    public string Output { get; } = "";

    public SnapshotRun()
    {
        var root = FindRoot();
        var snap = Path.Combine(root, "tests", "output", "snap-test");
        PngDir = Path.Combine(snap, "png");
        DataDir = Path.Combine(snap, "data");
        if (Directory.Exists(snap)) Directory.Delete(snap, recursive: true);
        Directory.CreateDirectory(PngDir);
        Directory.CreateDirectory(DataDir);

        var runner = Newest(Path.Combine(root, "tools", "HiddenDesktopRunner", "bin"), "HiddenDesktopRunner.exe");
        var exe = Newest(Path.Combine(root, "src", "BetterNotepad", "bin"), "BetterNotepad.exe");

        var psi = new ProcessStartInfo(runner)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "--timeout", "300", "--", exe, "--test-mode", "--data-dir", DataDir }) psi.ArgumentList.Add(a);
        psi.Environment["BETTERNOTEPAD_SNAPSHOT"] = PngDir;
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEndAsync();
        var err = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        ExitCode = p.ExitCode;
        Output = text.Result + err.Result;
    }

    private static string Newest(string binDir, string name)
    {
        var found = Directory.Exists(binDir)
            ? Directory.GetFiles(binDir, name, SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        return found ?? throw new FileNotFoundException($"{name} not found under {binDir}; build the solution first.");
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BetterNotepad.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("BetterNotepad.sln not found above " + AppContext.BaseDirectory);
    }
}
