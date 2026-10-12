using BetterNotepad.Documents.Diff;

namespace BetterNotepad.Tests;

public sealed class LineDiffTests
{
    private static string[] Lines(params string[] l) => l;

    private static string Join(IEnumerable<string> lines) => string.Join("\n", lines);

    private static List<string> Rendered(string a, string b)
        => LineDiff.Compute(a, b).Select(d => (d.Op switch { DiffOp.Insert => "+", DiffOp.Delete => "-", _ => " " }) + d.Text).ToList();

    private static string[] UnifiedLines(string a, string b, string oldName = "old", string newName = "new", int context = 3)
        => LineDiff.Unified(a, b, oldName, newName, context).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    // ---------------------------------------------------------------- SplitLines

    [Theory]
    [InlineData("a\nb", new[] { "a", "b" })]
    [InlineData("a\r\nb", new[] { "a", "b" })]
    [InlineData("a\rb", new[] { "a", "b" })]
    [InlineData("a\r\nb\nc\rd", new[] { "a", "b", "c", "d" })]
    [InlineData("a\n", new[] { "a", "" })]
    public void SplitLines_NormalizesAllLineEndings(string text, string[] expected)
        => Assert.Equal(expected, LineDiff.SplitLines(text));

    [Fact]
    public void SplitLines_EmptyText_HasNoLines()
        => Assert.Empty(LineDiff.SplitLines(""));

    // ---------------------------------------------------------------- Compute

    [Fact]
    public void Identical_AllEqual()
    {
        var d = LineDiff.Compute("a\nb\nc", "a\nb\nc");
        Assert.All(d, l => Assert.Equal(DiffOp.Equal, l.Op));
        Assert.Equal(["a", "b", "c"], d.Select(l => l.Text));
        Assert.Equal([1, 2, 3], d.Select(l => l.OldLine));
        Assert.Equal([1, 2, 3], d.Select(l => l.NewLine));
    }

    [Fact]
    public void DifferentLineEndings_Only_AreEqual()
        => Assert.All(LineDiff.Compute("a\r\nb\r\nc", "a\nb\nc"), l => Assert.Equal(DiffOp.Equal, l.Op));

    [Fact]
    public void BothEmpty_NoLines()
        => Assert.Empty(LineDiff.Compute("", ""));

    [Fact]
    public void EmptyVsText_AllInserted()
    {
        var d = LineDiff.Compute("", "a\nb");
        Assert.Equal(["+a", "+b"], Rendered("", "a\nb"));
        Assert.All(d, l => Assert.Equal(DiffOp.Insert, l.Op));
        Assert.Equal([1, 2], d.Select(l => l.NewLine));
        Assert.All(d, l => Assert.Equal(-1, l.OldLine));
    }

    [Fact]
    public void TextVsEmpty_AllDeleted()
    {
        var d = LineDiff.Compute("a\nb", "");
        Assert.Equal(["-a", "-b"], Rendered("a\nb", ""));
        Assert.Equal([1, 2], d.Select(l => l.OldLine));
        Assert.All(d, l => Assert.Equal(-1, l.NewLine));
    }

    [Fact]
    public void InsertInTheMiddle()
    {
        Assert.Equal([" a", "+X", " b", " c"], Rendered("a\nb\nc", "a\nX\nb\nc"));
        var ins = LineDiff.Compute("a\nb\nc", "a\nX\nb\nc").Single(l => l.Op == DiffOp.Insert);
        Assert.Equal(-1, ins.OldLine);
        Assert.Equal(2, ins.NewLine);
    }

    [Fact]
    public void DeleteInTheMiddle()
    {
        Assert.Equal([" a", "-b", " c"], Rendered("a\nb\nc", "a\nc"));
        var del = LineDiff.Compute("a\nb\nc", "a\nc").Single(l => l.Op == DiffOp.Delete);
        Assert.Equal(2, del.OldLine);
        Assert.Equal(-1, del.NewLine);
    }

    [Fact]
    public void ReplaceInTheMiddle_IsDeleteThenInsert()
    {
        Assert.Equal([" a", "-b", "+B", " c"], Rendered("a\nb\nc", "a\nB\nc"));
        var d = LineDiff.Compute("a\nb\nc", "a\nB\nc");
        Assert.Equal(2, d.Single(l => l.Op == DiffOp.Delete).OldLine);
        Assert.Equal(2, d.Single(l => l.Op == DiffOp.Insert).NewLine);
    }

    [Fact]
    public void InsertAtStartAndEnd()
    {
        Assert.Equal(["+z", " a", " b"], Rendered("a\nb", "z\na\nb"));
        Assert.Equal([" a", " b", "+z"], Rendered("a\nb", "a\nb\nz"));
    }

    [Fact]
    public void DeleteAtStartAndEnd()
    {
        Assert.Equal(["-a", " b", " c"], Rendered("a\nb\nc", "b\nc"));
        Assert.Equal([" a", " b", "-c"], Rendered("a\nb\nc", "a\nb"));
    }

    [Fact]
    public void MultipleSeparatedChanges()
    {
        Assert.Equal(["-1", "+one", " 2", " 3", " 4", "-5", "+five", " 6"],
            Rendered("1\n2\n3\n4\n5\n6", "one\n2\n3\n4\nfive\n6"));
    }

    [Fact]
    public void ChangeWithRepeatedLines_StaysConsistent()
    {
        Assert.Equal(["a", "a", "a", "a"], LineDiff.Compute("a\na\na", "a\na\na\na").Select(l => l.Text));
        Assert.Single(LineDiff.Compute("a\na\na", "a\na\na\na"), l => l.Op == DiffOp.Insert);
    }

    [Fact]
    public void LineNumbers_AreConsistentWithBothTexts()
    {
        const string a = "h\nx1\nx2\ny\nz\nt1\nt2";
        const string b = "h\ny\nnew\nz\nt1\nt3\nt2\nend";
        var al = LineDiff.SplitLines(a);
        var bl = LineDiff.SplitLines(b);
        foreach (var l in LineDiff.Compute(a, b))
        {
            if (l.Op != DiffOp.Insert) Assert.Equal(al[l.OldLine - 1], l.Text);
            if (l.Op != DiffOp.Delete) Assert.Equal(bl[l.NewLine - 1], l.Text);
        }
    }

    [Fact]
    public void RandomInputs_ReconstructBothSides_AreMinimal_AndNumberedConsistently()
    {
        var rnd = new Random(20261012);
        for (var iter = 0; iter < 600; iter++)
        {
            var alphabet = 2 + rnd.Next(4);
            string Gen()
            {
                var n = rnd.Next(0, 16);
                return Join(Enumerable.Range(0, n).Select(_ => ((char)('a' + rnd.Next(alphabet))).ToString()));
            }
            var oldText = Gen();
            var newText = Gen();
            var a = LineDiff.SplitLines(oldText);
            var b = LineDiff.SplitLines(newText);
            var diff = LineDiff.Compute(oldText, newText);

            var oldSide = diff.Where(d => d.Op != DiffOp.Insert).Select(d => d.Text).ToArray();
            var newSide = diff.Where(d => d.Op != DiffOp.Delete).Select(d => d.Text).ToArray();
            Assert.True(a.SequenceEqual(oldSide), $"old side mismatch for '{oldText}' -> '{newText}'");
            Assert.True(b.SequenceEqual(newSide), $"new side mismatch for '{oldText}' -> '{newText}'");

            // 1-based numbers, strictly increasing per side, -1 on the other side
            var lastOld = 0;
            var lastNew = 0;
            foreach (var d in diff)
            {
                if (d.Op != DiffOp.Insert) { Assert.Equal(lastOld + 1, d.OldLine); lastOld = d.OldLine; }
                else Assert.Equal(-1, d.OldLine);
                if (d.Op != DiffOp.Delete) { Assert.Equal(lastNew + 1, d.NewLine); lastNew = d.NewLine; }
                else Assert.Equal(-1, d.NewLine);
            }

            // Myers must be minimal: edits = n + m - 2 * LCS
            var lcs = Lcs(a, b);
            var edits = diff.Count(d => d.Op != DiffOp.Equal);
            Assert.True(edits == a.Length + b.Length - 2 * lcs, $"not minimal for '{oldText}' -> '{newText}': {edits} edits, LCS {lcs}");
        }
    }

    private static int Lcs(string[] a, string[] b)
    {
        var t = new int[a.Length + 1, b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                t[i, j] = a[i - 1] == b[j - 1] ? t[i - 1, j - 1] + 1 : Math.Max(t[i - 1, j], t[i, j - 1]);
        return t[a.Length, b.Length];
    }

    [Fact]
    public void LargerRandomInputs_AreMinimalAndConsistent()
    {
        var rnd = new Random(7);
        for (var iter = 0; iter < 40; iter++)
        {
            var a = Enumerable.Range(0, rnd.Next(50, 300)).Select(_ => "L" + rnd.Next(12)).ToArray();
            var b = Enumerable.Range(0, rnd.Next(50, 300)).Select(_ => "L" + rnd.Next(12)).ToArray();
            var diff = LineDiff.Compute(Join(a), Join(b));
            Assert.True(a.SequenceEqual(diff.Where(d => d.Op != DiffOp.Insert).Select(d => d.Text)));
            Assert.True(b.SequenceEqual(diff.Where(d => d.Op != DiffOp.Delete).Select(d => d.Text)));
            Assert.Equal(a.Length + b.Length - 2 * Lcs(a, b), diff.Count(d => d.Op != DiffOp.Equal));
        }
    }

    [Fact]
    public void ManyScatteredEdits_ThousandsOfLines_AreMinimal()
    {
        // 2000 lines, every 4th changed: D = 1000 edits (well below the replace-all cut-off)
        var a = Enumerable.Range(0, 2000).Select(i => "line " + i).ToArray();
        var b = a.Select((l, i) => i % 4 == 2 ? "changed " + i : l).ToArray();
        var diff = LineDiff.Compute(Join(a), Join(b));
        Assert.Equal(1000, diff.Count(d => d.Op != DiffOp.Equal));
        Assert.Equal(500, diff.Count(d => d.Op == DiffOp.Delete));
        Assert.Equal(500, diff.Count(d => d.Op == DiffOp.Insert));
        Assert.True(a.SequenceEqual(diff.Where(d => d.Op != DiffOp.Insert).Select(d => d.Text)));
        Assert.True(b.SequenceEqual(diff.Where(d => d.Op != DiffOp.Delete).Select(d => d.Text)));
    }

    [Fact]
    public void HugeCompletelyDifferentInputs_FallBackToReplaceAll()
    {
        const int n = 6500; // n * n > 40M triggers the fallback
        var a = Join(Enumerable.Range(0, n).Select(i => "o" + i));
        var b = Join(Enumerable.Range(0, n).Select(i => "n" + i));
        var d = LineDiff.Compute(a, b);
        Assert.Equal(2 * n, d.Count);
        Assert.True(d.Take(n).All(l => l.Op == DiffOp.Delete));
        Assert.True(d.Skip(n).All(l => l.Op == DiffOp.Insert));
        Assert.Equal(1, d[0].OldLine);
        Assert.Equal(n, d[n - 1].OldLine);
        Assert.Equal(1, d[n].NewLine);
        Assert.Equal(n, d[2 * n - 1].NewLine);
    }

    [Fact]
    public void LargeFileWithFewEdits_IsFast()
    {
        var baseLines = Enumerable.Range(0, 50_000).Select(i => "line " + i).ToList();
        var edited = baseLines.ToList();
        edited[100] = "changed 100";
        edited.RemoveAt(25_000);
        edited.Insert(40_000, "inserted");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var d = LineDiff.Compute(Join(baseLines), Join(edited));
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(4, d.Count(l => l.Op != DiffOp.Equal)); // 1 replace (2) + 1 delete + 1 insert
    }

    // ---------------------------------------------------------------- Unified

    [Fact]
    public void Unified_Identical_HasOnlyHeaders()
    {
        Assert.Equal(["--- disk.txt", "+++ mine.txt"], UnifiedLines("a\nb", "a\nb", "disk.txt", "mine.txt"));
    }

    [Fact]
    public void Unified_SingleReplace_HeadersAndHunkNumbers()
    {
        var old = Join(Enumerable.Range(1, 10).Select(i => "line" + i));
        var @new = old.Replace("line5", "LINE5");
        var u = UnifiedLines(old, @new, "a/file.txt", "b/file.txt");
        Assert.Equal(
        [
            "--- a/file.txt",
            "+++ b/file.txt",
            "@@ -2,7 +2,7 @@",
            " line2",
            " line3",
            " line4",
            "-line5",
            "+LINE5",
            " line6",
            " line7",
            " line8",
        ], u);
    }

    [Fact]
    public void Unified_TwoDistantChanges_ProduceTwoHunks()
    {
        var old = Join(Enumerable.Range(1, 20).Select(i => "l" + i));
        var @new = old.Replace("l2\n", "L2\n").Replace("l18\n", "L18\n");
        var u = UnifiedLines(old, @new);
        var headers = u.Where(l => l.StartsWith("@@")).ToArray();
        Assert.Equal(["@@ -1,5 +1,5 @@", "@@ -15,6 +15,6 @@"], headers);
        Assert.Contains("-l2", u);
        Assert.Contains("+L2", u);
        Assert.Contains("-l18", u);
        Assert.Contains("+L18", u);
        // the middle of the file is not part of the output
        Assert.DoesNotContain(" l10", u);
    }

    [Fact]
    public void Unified_ChangesSeparatedByExactlyTwiceTheContext_MergeIntoOneHunk()
    {
        // 6 unchanged lines between the changes: the two 3-line contexts touch, so git emits one hunk.
        var old = Join(Enumerable.Range(1, 12).Select(i => "l" + i));
        var @new = old.Replace("l2\n", "L2\n").Replace("l9\n", "L9\n");
        var u = UnifiedLines(old, @new);
        Assert.Single(u, l => l.StartsWith("@@"));
        Assert.Equal("@@ -1,12 +1,12 @@", u[2]);
    }

    [Fact]
    public void Unified_ChangesSeparatedByMoreThanTwiceTheContext_AreSeparateHunks()
    {
        var old = Join(Enumerable.Range(1, 12).Select(i => "l" + i));
        var @new = old.Replace("l2\n", "L2\n").Replace("l10\n", "L10\n");
        var u = UnifiedLines(old, @new);
        Assert.Equal(["@@ -1,5 +1,5 @@", "@@ -7,6 +7,6 @@"], u.Where(l => l.StartsWith("@@")).ToArray());
    }

    [Fact]
    public void Unified_NearbyChanges_MergeIntoOneHunk()
    {
        var old = Join(Enumerable.Range(1, 12).Select(i => "l" + i));
        var @new = old.Replace("l3\n", "L3\n").Replace("l7\n", "L7\n");
        var u = UnifiedLines(old, @new);
        Assert.Single(u, l => l.StartsWith("@@"));
        Assert.Equal("@@ -1,10 +1,10 @@", u[2]);
    }

    [Fact]
    public void Unified_PureAppend_HunkCounts()
    {
        var u = UnifiedLines("a\nb", "a\nb\nc");
        Assert.Equal(["--- old", "+++ new", "@@ -1,2 +1,3 @@", " a", " b", "+c"], u);
    }

    [Fact]
    public void Unified_PureDeleteAtStart_HunkCounts()
    {
        var u = UnifiedLines("a\nb\nc", "b\nc");
        Assert.Equal(["--- old", "+++ new", "@@ -1,3 +1,2 @@", "-a", " b", " c"], u);
    }

    [Fact]
    public void Unified_EmptyVsText_GitStyleZeroRange()
    {
        var u = UnifiedLines("", "a\nb");
        Assert.Equal(["--- old", "+++ new", "@@ -0,0 +1,2 @@", "+a", "+b"], u);
    }

    [Fact]
    public void Unified_TextVsEmpty_GitStyleZeroRange()
    {
        var u = UnifiedLines("a\nb", "");
        Assert.Equal(["--- old", "+++ new", "@@ -1,2 +0,0 @@", "-a", "-b"], u);
    }

    [Fact]
    public void Unified_ContextParameter_IsRespected()
    {
        var old = Join(Enumerable.Range(1, 10).Select(i => "l" + i));
        var @new = old.Replace("l5", "X");
        Assert.Equal(["--- old", "+++ new", "@@ -5,1 +5,1 @@", "-l5", "+X"], UnifiedLines(old, @new, context: 0));
        Assert.Equal("@@ -4,3 +4,3 @@", UnifiedLines(old, @new, context: 1)[2]);
    }

    [Fact]
    public void Unified_ChangeAtFileStart_ContextIsClamped()
    {
        var old = Join(Enumerable.Range(1, 6).Select(i => "l" + i));
        var u = UnifiedLines(old, old.Replace("l1", "X"));
        Assert.Equal("@@ -1,4 +1,4 @@", u[2]);
    }

    [Fact]
    public void Unified_ChangeAtFileEnd_ContextIsClamped()
    {
        var old = Join(Enumerable.Range(1, 6).Select(i => "l" + i));
        var u = UnifiedLines(old, old.Replace("l6", "X"));
        Assert.Equal("@@ -3,4 +3,4 @@", u[2]);
    }

    [Fact]
    public void Unified_UsesGivenNames()
    {
        var text = LineDiff.Unified("a", "b", "left (disk)", "right (mine)");
        Assert.StartsWith("--- left (disk)", text);
        Assert.Contains("+++ right (mine)", text);
    }

    [Fact]
    public void Unified_MixedLineEndingsAreNormalized()
    {
        var u = UnifiedLines("a\r\nb\r\nc", "a\nB\nc");
        Assert.Equal(["--- old", "+++ new", "@@ -1,3 +1,3 @@", " a", "-b", "+B", " c"], u);
    }
}
