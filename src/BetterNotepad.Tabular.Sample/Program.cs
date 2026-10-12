using System.IO;
using System.Windows;

namespace BetterNotepad.Tabular.Sample;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = FindRoot();
        Fixtures.Dir = Path.Combine(root, "tests", "output", "tabular");
        HexSample.Dir = Path.Combine(root, "tests", "output", "hex");
        var command = args.Length > 0 ? args[0] : "tests";
        var filter = args.Length > 1 ? args[1] : null;
        var result = 0;

        if (command is "tests" or "all")
        {
            Console.WriteLine("Tabular tests");
            Fixtures.WriteAll();
            result |= Runner.Run([typeof(DialectTests), typeof(TableTests), typeof(ValueTests), typeof(ViewTests), typeof(FixtureTests), typeof(RandomTests)], filter);
        }

        if (command is "hextests" or "all")
        {
            Console.WriteLine("Hex tests");
            Directory.CreateDirectory(HexSample.Dir);
            result |= Runner.Run([typeof(HexLogicTests), typeof(HexSourceTests), typeof(HexControlTests)], filter);
        }

        if (command is "hex" or "all")
        {
            Console.WriteLine("Hex view");
            HexSample.RenderAll();
        }

        if (command is "bench" or "all")
        {
            Console.WriteLine("Tabular benchmark");
            Bench.Run();
        }

        return result;
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BetterNotepad.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Project root (BetterNotepad.sln) not found.");
    }
}
