using System.IO;
using System.Text;
using BetterNotepad.Core;

namespace BetterNotepad.Modules.Explorer;

/// <summary>A small, realistic project folder for snapshots and the self-test, always inside the app's own (test) data folder.</summary>
internal static class ExplorerFixtures
{
    /// <summary>Recreates <paramref name="root"/> (which must lie inside <see cref="AppEnv.LocalDir"/>).</summary>
    public static void WriteProject(string root)
    {
        var full = Path.GetFullPath(root);
        if (!ExplorerRules.IsSameOrUnder(AppEnv.LocalDir, full) || string.Equals(full.TrimEnd('\\'), AppEnv.LocalDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("fixtures live only inside the app's data folder: " + full);
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);

        void Text(string rel, string content)
        {
            var p = Path.Combine(full, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, content, new UTF8Encoding(false));
        }
        void Bytes(string rel, int length)
        {
            var p = Path.Combine(full, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            var b = new byte[length];
            for (var i = 0; i < b.Length; i++) b[i] = (byte)(i * 31 % 251);
            File.WriteAllBytes(p, b);
        }

        Text(".git/HEAD", "ref: refs/heads/main\n");
        Text(".vscode/settings.json", "{ \"editor.tabSize\": 4 }\n");
        Bytes("assets/banner.jpg", 2048);
        Bytes("assets/logo.png", 1024);
        Text("assets/icon.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 16 16\"><rect width=\"16\" height=\"16\" rx=\"4\"/></svg>\n");
        Text("docs/changelog.md", "# Changelog\n\n## 1.2.0\n- Search across notes\n");
        Text("docs/guide.md", "# Guide\n\nOpen a notebook, write, press Ctrl+S.\n");
        Bytes("docs/manual.pdf", 4096);
        Text("src/App.cs", "namespace Notebook;\n\npublic static class App\n{\n    public static void Main() => new MainWindow().Run();\n}\n");
        Text("src/MainWindow.xaml", "<Window x:Class=\"Notebook.MainWindow\" Title=\"Notebook\"/>\n");
        Text("src/MainWindow.xaml.cs", "namespace Notebook;\n\npublic partial class MainWindow { }\n");
        Text("src/Notes/NoteParser.cs", "namespace Notebook.Notes;\n\npublic static class NoteParser { }\n");
        Text("src/Notes/NoteStore.cs",
            "namespace Notebook.Notes;\n\n/// <summary>Keeps notes on disk, one file per note.</summary>\npublic sealed class NoteStore\n{\n" +
            "    private readonly string _folder;\n\n    public NoteStore(string folder) => _folder = folder;\n\n" +
            "    public IEnumerable<string> List() => Directory.EnumerateFiles(_folder, \"*.md\");\n\n" +
            "    public string Read(string name) => File.ReadAllText(Path.Combine(_folder, name));\n}\n");
        Text("src/Search/Index.cs", "namespace Notebook.Search;\n\npublic sealed class Index { }\n");
        Text("src/styles/theme.css", ":root { --accent: #7c5cff; }\n");
        Text("tests/NoteStoreTests.cs", "public class NoteStoreTests { }\n");
        Text("tests/fixtures/sample1.txt", "one\n");
        Text("tests/fixtures/sample2.txt", "two\n");
        Text("tests/fixtures/sample10.txt", "ten\n");
        Text(".gitignore", "bin/\nobj/\n");
        Text("build.ps1", "dotnet build -c Release\n");
        Text("data.csv", "id;title\n1;First note\n");
        Text("LICENSE", "MIT License\n");
        Text("notes.txt", "remember the milk\n");
        Text("package.json", "{ \"name\": \"notebook-app\", \"version\": \"1.2.0\" }\n");
        Text("README.md", "# Notebook\n\nA tiny notes app.\n");
        Bytes("release.zip", 3000);
        Text("desktop.ini", "[.ShellClassInfo]\n");
        File.SetAttributes(Path.Combine(full, "desktop.ini"), FileAttributes.Hidden | FileAttributes.System);
    }
}
