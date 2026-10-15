using System.Windows.Media;
using BetterNotepad.Editor.Export;

namespace BetterNotepad.Tests;

/// <summary>Soft wrapping of code rows for "Copy as image".</summary>
public sealed class CodeWrapTests
{
    private static IReadOnlyList<CodeRun> Plain(string text) => [new CodeRun(text)];

    [Fact]
    public void ShortLineIsOneRowWithItsRuns()
    {
        IReadOnlyList<CodeRun> runs = [new CodeRun("var", Colors.Blue), new CodeRun(" x = 1;")];
        var rows = CodeWrap.Line(runs, 3, 40);
        var row = Assert.Single(rows);
        Assert.True(row.First);
        Assert.Equal(3, row.Source);
        Assert.Equal(0, row.Indent);
        Assert.Same(runs, row.Runs);
    }

    [Fact]
    public void LongLineBreaksAtTheLastSpaceAndHangsUnderItsIndent()
    {
        var text = "    return alpha + beta + gamma + delta + epsilon;"; // 50 chars
        var rows = CodeWrap.Line(Plain(text), 0, 30);
        Assert.Equal(2, rows.Count);
        Assert.Equal("    return alpha + beta +", rows[0].Text);
        Assert.Equal("gamma + delta + epsilon;", rows[1].Text);
        Assert.True(rows[0].First);
        Assert.False(rows[1].First);
        Assert.Equal(6, rows[1].Indent); // 4 spaces of indentation + 2
        Assert.All(rows, r => Assert.True(r.Indent + r.Text.Length <= 30));
    }

    [Fact]
    public void LongTokenBreaksWhereItMust()
    {
        var token = new string('x', 75);
        var rows = CodeWrap.Line(Plain(token), 0, 30);
        Assert.Equal(new[] { 30, 28, 17 }, rows.Select(r => r.Text.Length).ToArray());
        Assert.Equal(token, string.Concat(rows.Select(r => r.Text)));
    }

    [Fact]
    public void StylesSurviveTheBreak()
    {
        IReadOnlyList<CodeRun> runs = [new CodeRun("// " + new string('c', 20), Colors.Gray, Italic: true), new CodeRun(" tail", Colors.Red)];
        var rows = CodeWrap.Line(runs, 0, 24);
        Assert.True(rows.Count >= 2);
        Assert.All(rows.SelectMany(r => r.Runs), r => Assert.True(r.Text.Length > 0));
        Assert.Equal(runs.Sum(r => r.Text.Length) - 1, rows.Sum(r => r.Text.Length)); // only the space at the break goes
        Assert.Contains(rows.SelectMany(r => r.Runs), r => r is { Italic: true } && r.Color == Colors.Gray);
        Assert.Contains(rows.SelectMany(r => r.Runs), r => r.Color == Colors.Red && r.Text == "tail");
    }

    [Fact]
    public void RowsStopAtTheCap()
    {
        var block = new CodeBlock(Enumerable.Range(0, 10).Select(i => Plain("line " + i)));
        var rows = CodeWrap.Rows(block, 40, 4, out var truncated, out var lines);
        Assert.True(truncated);
        Assert.Equal(4, rows.Count);
        Assert.Equal(4, lines);

        rows = CodeWrap.Rows(block, 40, 10, out truncated, out lines);
        Assert.False(truncated);
        Assert.Equal(10, lines);
    }

    [Fact]
    public void AWrappedLineCutByTheCapCountsAsDrawn()
    {
        var block = new CodeBlock([Plain("short"), Plain(string.Join(" ", Enumerable.Repeat("word", 40)))]);
        var rows = CodeWrap.Rows(block, 20, 3, out var truncated, out var lines);
        Assert.True(truncated);
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, lines);
        Assert.Equal(new[] { true, true, false }, rows.Select(r => r.First).ToArray());
    }
}
