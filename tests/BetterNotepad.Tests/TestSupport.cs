using System.Globalization;

namespace BetterNotepad.Tests;

/// <summary>Locates the repo root and hands out throw-away directories under tests\output (git-excluded).</summary>
internal static class TestPaths
{
    public static readonly string RepoRoot = FindRoot();

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

/// <summary>Unique scratch directory in tests\output\unit, removed on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(TestPaths.RepoRoot, "tests", "output", "unit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Temporarily switches the current thread's culture.</summary>
internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _old = CultureInfo.CurrentCulture;

    public CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

    public void Dispose() => CultureInfo.CurrentCulture = _old;
}
