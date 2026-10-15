using BetterNotepad.Modules.Explorer;

namespace BetterNotepad.Tests;

public sealed class ExplorerRulesTests
{
    private static List<string> Sorted(params string[] names)
    {
        var list = names.ToList();
        list.Sort(ExplorerRules.NaturalCompare);
        return list;
    }

    // ───────────── natural order ─────────────

    [Fact]
    public void Numbers_sort_by_value()
        => Assert.Equal(["file1", "file2", "file10", "file20", "file100"], Sorted("file10", "file2", "file100", "file1", "file20"));

    [Fact]
    public void Case_does_not_split_the_order()
        => Assert.Equal(["apple", "Banana", "cherry"], Sorted("cherry", "Banana", "apple"));

    [Fact]
    public void Polish_letters_sort_next_to_their_base_letter()
        => Assert.Equal(["a.txt", "ą.txt", "b.txt", "z.txt"], Sorted("z.txt", "b.txt", "ą.txt", "a.txt"));

    [Fact]
    public void Punctuation_before_a_number_sorts_like_explorer()
    {
        Assert.True(ExplorerRules.NaturalCompare("file.txt", "file1.txt") < 0);
        Assert.True(ExplorerRules.NaturalCompare("file1.txt", "filea.txt") < 0); // digits before letters
        Assert.True(ExplorerRules.NaturalCompare("a b", "a1") < 0);
    }

    [Fact]
    public void Leading_zeros_and_huge_numbers_are_ordered_without_overflow()
    {
        Assert.True(ExplorerRules.NaturalCompare("v7", "v007") < 0);
        Assert.True(ExplorerRules.NaturalCompare("v007", "v8") < 0);
        Assert.True(ExplorerRules.NaturalCompare("x99999999999999999999999", "x100000000000000000000000") < 0);
    }

    [Fact]
    public void The_order_is_total_and_antisymmetric()
    {
        string[] names = ["File1.txt", "file1.txt", "file01.txt", "file1", "file1 copy", "File10", "a", "A", "ą", "_x", "-y", "1", "01", ""];
        foreach (var a in names)
        {
            Assert.Equal(0, ExplorerRules.NaturalCompare(a, a));
            foreach (var b in names.Where(b => b != a))
            {
                var ab = ExplorerRules.NaturalCompare(a, b);
                Assert.NotEqual(0, ab);
                Assert.Equal(-ab, ExplorerRules.NaturalCompare(b, a));
            }
        }
    }

    [Fact]
    public void Folders_come_first()
    {
        Assert.True(ExplorerRules.CompareEntries("zeta", true, "alpha.txt", false) < 0);
        Assert.True(ExplorerRules.CompareEntries("b.txt", false, "a", true) > 0);
        Assert.True(ExplorerRules.CompareEntries("dir2", true, "dir10", true) < 0);
    }

    // ───────────── filtering ─────────────

    [Theory]
    [InlineData(".git", true, false)]
    [InlineData(".GIT", true, false)]
    [InlineData(".svn", true, false)]
    [InlineData(".hg", true, false)]
    [InlineData(".github", true, true)]
    [InlineData(".gitignore", false, true)]
    [InlineData(".git", false, true)] // a file named .git (submodule pointer) is shown
    [InlineData("src", true, true)]
    public void Vcs_folders_are_hidden(string name, bool isDirectory, bool shown)
        => Assert.Equal(shown, ExplorerRules.ShouldShow(name, FileAttributes.Normal, isDirectory));

    [Fact]
    public void Hidden_and_system_entries_are_hidden()
    {
        Assert.False(ExplorerRules.ShouldShow("thumbs.db", FileAttributes.Hidden | FileAttributes.System, false));
        Assert.False(ExplorerRules.ShouldShow("secret", FileAttributes.Hidden | FileAttributes.Directory, true));
        Assert.False(ExplorerRules.ShouldShow("pagefile.sys", FileAttributes.System, false));
        Assert.True(ExplorerRules.ShouldShow("readme.md", FileAttributes.Archive | FileAttributes.ReadOnly, false));
    }

    [Theory]
    [InlineData(@"C:\repo\.git\objects\ab\cd", true)]
    [InlineData(@"C:\repo\.git", true)]
    [InlineData(@"C:\repo\sub\.hg\store", true)]
    [InlineData(@"C:\repo\.github\workflows\ci.yml", false)]
    [InlineData(@"C:\repo\.gitignore", false)]
    [InlineData(@"C:\repo\.gitx\.git\HEAD", true)]
    public void Watcher_noise_inside_vcs_folders_is_ignored(string path, bool inside)
        => Assert.Equal(inside, ExplorerRules.IsInsideVcsFolder(path));

    // ───────────── paths ─────────────

    [Fact]
    public void Relative_paths_use_the_open_folder()
    {
        Assert.Equal(@"src\app.cs", ExplorerRules.RelativePath(@"C:\repo", @"C:\repo\src\app.cs"));
        Assert.Equal("app.cs", ExplorerRules.RelativePath(@"C:\repo\", @"C:\repo\app.cs"));
        Assert.Equal(".", ExplorerRules.RelativePath(@"C:\repo", @"C:\repo"));
    }

    [Fact]
    public void Same_or_under_compares_whole_segments_ignoring_case()
    {
        Assert.True(ExplorerRules.IsSameOrUnder(@"C:\Repo", @"c:\repo\src\a.cs"));
        Assert.True(ExplorerRules.IsSameOrUnder(@"C:\repo\", @"C:\repo"));
        Assert.True(ExplorerRules.IsSameOrUnder(@"C:\", @"C:\repo\x"));
        Assert.False(ExplorerRules.IsSameOrUnder(@"C:\repo", @"C:\repository\a.cs"));
        Assert.False(ExplorerRules.IsSameOrUnder(@"C:\repo\src", @"C:\repo"));
        Assert.False(ExplorerRules.IsSameOrUnder("", @"C:\repo"));
    }

    [Fact]
    public void Rebase_moves_paths_with_a_renamed_folder_or_file()
    {
        Assert.Equal(@"C:\r\gamma\nested\x.cs", ExplorerRules.Rebase(@"C:\r\alpha\nested\x.cs", @"C:\r\alpha", @"C:\r\gamma"));
        Assert.Equal(@"C:\r\new.md", ExplorerRules.Rebase(@"C:\r\old.md", @"C:\r\old.md", @"C:\r\new.md"));
        Assert.Equal(@"C:\r\README.md", ExplorerRules.Rebase(@"C:\r\readme.md", @"C:\r\readme.md", @"C:\r\README.md")); // case-only
        Assert.Null(ExplorerRules.Rebase(@"C:\r\alphabet\x.cs", @"C:\r\alpha", @"C:\r\gamma"));
    }

    [Fact]
    public void Normalized_folders_keep_drive_roots()
    {
        Assert.Equal(@"C:\", ExplorerRules.NormalizeDir(@"C:\"));
        Assert.Equal(@"C:\repo", ExplorerRules.NormalizeDir(@"C:\repo\"));
        Assert.Equal(@"C:\repo", ExplorerRules.NormalizeDir(@"C:\repo\src\.."));
    }

    [Fact]
    public void Display_name_is_the_folder_or_the_drive()
    {
        Assert.Equal("repo", ExplorerRules.DisplayName(@"C:\work\repo"));
        Assert.Equal(@"D:\", ExplorerRules.DisplayName(@"D:\"));
    }

    // ───────────── names ─────────────

    [Theory]
    [InlineData("notes.txt", NameIssue.None)]
    [InlineData(".gitignore", NameIssue.None)]
    [InlineData("zażółć gęślą.md", NameIssue.None)]
    [InlineData("", NameIssue.Empty)]
    [InlineData("   ", NameIssue.Empty)]
    [InlineData(".", NameIssue.DotName)]
    [InlineData("..", NameIssue.DotName)]
    [InlineData("a:b", NameIssue.InvalidCharacters)]
    [InlineData("a/b", NameIssue.InvalidCharacters)]
    [InlineData(@"a\b", NameIssue.InvalidCharacters)]
    [InlineData("what?", NameIssue.InvalidCharacters)]
    [InlineData("a*b", NameIssue.InvalidCharacters)]
    [InlineData("say \"hi\"", NameIssue.InvalidCharacters)]
    [InlineData("a<b>", NameIssue.InvalidCharacters)]
    [InlineData("pipe|x", NameIssue.InvalidCharacters)]
    [InlineData("tab\tname", NameIssue.InvalidCharacters)]
    [InlineData("name.", NameIssue.TrailingDotOrSpace)]
    [InlineData("name ", NameIssue.TrailingDotOrSpace)]
    [InlineData("CON", NameIssue.ReservedName)]
    [InlineData("con.txt", NameIssue.ReservedName)]
    [InlineData("Nul.tar.gz", NameIssue.ReservedName)]
    [InlineData("COM1", NameIssue.ReservedName)]
    [InlineData("lpt9.log", NameIssue.ReservedName)]
    [InlineData("console.txt", NameIssue.None)]
    [InlineData("com10", NameIssue.None)]
    public void Names_follow_windows_rules(string name, NameIssue expected)
        => Assert.Equal(expected, ExplorerRules.ValidateName(name));

    [Fact]
    public void Overlong_names_are_refused()
    {
        Assert.Equal(NameIssue.TooLong, ExplorerRules.ValidateName(new string('a', 256)));
        Assert.Equal(NameIssue.None, ExplorerRules.ValidateName(new string('a', 255)));
    }

    [Fact]
    public void Rename_selects_the_name_without_its_extension()
    {
        Assert.Equal((0, 4), ExplorerRules.RenameSelection("main.cs", false));
        Assert.Equal((0, 11), ExplorerRules.RenameSelection("archive.tar.gz", false));
        Assert.Equal((0, 10), ExplorerRules.RenameSelection(".gitignore", false));
        Assert.Equal((0, 8), ExplorerRules.RenameSelection("Makefile", false));
        Assert.Equal((0, 7), ExplorerRules.RenameSelection("src.old", true)); // folders: everything
    }

    // ───────────── watcher batches, type-ahead, session ─────────────

    [Fact]
    public void Watcher_batches_refresh_only_loaded_parent_folders()
    {
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\r", @"C:\r\src" };
        var dirs = ExplorerRules.FoldersToRefresh(
            [@"C:\r\new.txt", @"C:\R\SRC\a.cs", @"C:\r\src\a.cs", @"C:\r\docs\guide.md", @"C:\r\src\deep\x.cs", @"C:\r\gone"],
            loaded.Contains);
        Assert.Equal(2, dirs.Count);
        Assert.Contains(@"C:\r", dirs);
        Assert.Contains(@"C:\r\src", dirs);
    }

    [Fact]
    public void Type_ahead_finds_the_next_match_and_wraps()
    {
        string?[] names = ["alpha", "beta", null, "bravo", "Charlie", "billing"];
        Assert.Equal(1, ExplorerRules.FindByPrefix(names, 0, "b"));
        Assert.Equal(3, ExplorerRules.FindByPrefix(names, 2, "b"));
        Assert.Equal(5, ExplorerRules.FindByPrefix(names, 4, "B"));
        Assert.Equal(1, ExplorerRules.FindByPrefix(names, 6, "b")); // wraps
        Assert.Equal(3, ExplorerRules.FindByPrefix(names, 0, "br"));
        Assert.Equal(4, ExplorerRules.FindByPrefix(names, 0, "ch"));
        Assert.Equal(-1, ExplorerRules.FindByPrefix(names, 0, "zz"));
        Assert.Equal(-1, ExplorerRules.FindByPrefix([], 0, "a"));
    }

    [Fact]
    public void The_session_list_is_distinct_and_bounded()
    {
        var capped = ExplorerRules.Cap(["src", "SRC", "docs", "", "tests", "a", "b"], 4);
        Assert.Equal(["src", "docs", "tests", "a"], capped);
    }

    // ───────────── icons ─────────────

    [Theory]
    [InlineData("App.cs", false, FileIconKind.Code)]
    [InlineData("index.TSX", false, FileIconKind.Code)]
    [InlineData("MainWindow.xaml", false, FileIconKind.Markup)]
    [InlineData("theme.scss", false, FileIconKind.Style)]
    [InlineData("package.json", false, FileIconKind.Data)]
    [InlineData(".gitignore", false, FileIconKind.Config)]
    [InlineData("data.csv", false, FileIconKind.Table)]
    [InlineData("build.ps1", false, FileIconKind.Script)]
    [InlineData("Dockerfile", false, FileIconKind.Script)]
    [InlineData("README.md", false, FileIconKind.Markdown)]
    [InlineData("LICENSE", false, FileIconKind.Text)]
    [InlineData("notes.txt", false, FileIconKind.Text)]
    [InlineData("logo.png", false, FileIconKind.Image)]
    [InlineData("manual.pdf", false, FileIconKind.Pdf)]
    [InlineData("report.docx", false, FileIconKind.Document)]
    [InlineData("release.zip", false, FileIconKind.Archive)]
    [InlineData("app.exe", false, FileIconKind.Binary)]
    [InlineData("song.mp3", false, FileIconKind.Audio)]
    [InlineData("clip.mp4", false, FileIconKind.Video)]
    [InlineData("font.woff2", false, FileIconKind.Font)]
    [InlineData("thing.unknownext", false, FileIconKind.File)]
    [InlineData("src", true, FileIconKind.Folder)]
    [InlineData("folder.zip", true, FileIconKind.Folder)]
    public void Icons_follow_the_type(string name, bool isDirectory, FileIconKind kind)
        => Assert.Equal(kind, ExplorerRules.IconFor(name, isDirectory));
}
