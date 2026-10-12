using BetterNotepad.Core;
using BetterNotepad.Documents;

namespace BetterNotepad.Tests;

public sealed class FileUtilTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1L, "1 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1 MB")]
    [InlineData(52428800L, "50 MB")]
    [InlineData(1073741824L, "1 GB")]
    [InlineData(1099511627776L, "1 TB")]
    [InlineData(1125899906842624L, "1024 TB")]
    public void FormatSize(long bytes, string expected)
    {
        using var _ = new CultureScope("en-US");
        Assert.Equal(expected, FileUtil.FormatSize(bytes));
    }

    [Fact]
    public void PathEquals_IgnoresCaseSlashesAndTrailingSeparators()
    {
        Assert.True(FileUtil.PathEquals(@"C:\Dir\File.txt", @"c:\dir\file.TXT"));
        Assert.True(FileUtil.PathEquals(@"C:\Dir\Sub\", @"C:\Dir\Sub"));
        Assert.True(FileUtil.PathEquals(@"C:\Dir\Sub", @"C:/Dir/Sub"));
        Assert.True(FileUtil.PathEquals(@"C:\Dir\.\Sub\..\File.txt", @"C:\Dir\File.txt"));
        Assert.False(FileUtil.PathEquals(@"C:\Dir\a.txt", @"C:\Dir\b.txt"));
        Assert.False(FileUtil.PathEquals(null, @"C:\Dir"));
        Assert.False(FileUtil.PathEquals(@"C:\Dir", null));
        Assert.False(FileUtil.PathEquals(null, null));
    }

    [Fact]
    public void Normalize_ResolvesDotsAndTrimsTrailingSeparator()
    {
        Assert.Equal(@"C:\Dir\File.txt", FileUtil.Normalize(@"C:\Dir\.\Sub\..\File.txt"));
        Assert.Equal(@"C:\Dir", FileUtil.Normalize(@"C:\Dir\"));
    }

    [Fact]
    public void WriteAllBytesAtomic_CreatesThenReplaces()
    {
        var path = _dir.File("atomic.bin");
        FileUtil.WriteAllBytesAtomic(path, new byte[] { 1, 2, 3, 4, 5 });
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(path));
        FileUtil.WriteAllBytesAtomic(path, new byte[] { 9 });
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFileSystemEntries(_dir.Path));
    }

    [Fact]
    public void WriteAllBytesAtomic_KeepsHiddenAttribute()
    {
        var path = _dir.File("hidden.txt");
        File.WriteAllText(path, "old");
        File.SetAttributes(path, FileAttributes.Hidden);
        FileUtil.WriteAllBytesAtomic(path, "new"u8);
        Assert.Equal("new", File.ReadAllText(path));
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        File.SetAttributes(path, FileAttributes.Normal);
    }

    [Fact]
    public void WriteAllTextAtomic_WritesUtf8WithoutBom()
    {
        var path = _dir.File("t.txt");
        FileUtil.WriteAllTextAtomic(path, "zażółć");
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("zażółć"), File.ReadAllBytes(path));
    }
}

public sealed class FileTypesTests
{
    [Theory]
    [InlineData("a.md", DocumentKind.Markdown)]
    [InlineData("A.MARKDOWN", DocumentKind.Markdown)]
    [InlineData("a.html", DocumentKind.Html)]
    [InlineData("a.htm", DocumentKind.Html)]
    [InlineData("a.svg", DocumentKind.Svg)]
    [InlineData("a.csv", DocumentKind.Csv)]
    [InlineData("a.tsv", DocumentKind.Csv)]
    [InlineData("a.pdf", DocumentKind.Pdf)]
    [InlineData("a.png", DocumentKind.Image)]
    [InlineData("a.JPG", DocumentKind.Image)]
    [InlineData("a.webp", DocumentKind.Image)]
    [InlineData("a.docx", DocumentKind.Info)]
    [InlineData("a.zip", DocumentKind.Info)]
    [InlineData("a.exe", DocumentKind.Info)]
    [InlineData("a.cs", DocumentKind.Text)]
    [InlineData("a.txt", DocumentKind.Text)]
    [InlineData("Makefile", DocumentKind.Text)]
    [InlineData("", DocumentKind.Text)]
    [InlineData(null, DocumentKind.Text)]
    public void KindFor(string? name, DocumentKind expected)
        => Assert.Equal(expected, FileTypes.KindFor(name));

    [Fact]
    public void TextualAndPreviewKinds()
    {
        foreach (var k in new[] { DocumentKind.Text, DocumentKind.Markdown, DocumentKind.Html, DocumentKind.Svg, DocumentKind.Csv })
            Assert.True(FileTypes.IsTextual(k), k.ToString());
        foreach (var k in new[] { DocumentKind.Pdf, DocumentKind.Image, DocumentKind.Hex, DocumentKind.Info })
            Assert.False(FileTypes.IsTextual(k), k.ToString());
        foreach (var k in new[] { DocumentKind.Markdown, DocumentKind.Html, DocumentKind.Svg, DocumentKind.Csv })
            Assert.True(FileTypes.HasPreview(k), k.ToString());
        Assert.False(FileTypes.HasPreview(DocumentKind.Text));
        Assert.False(FileTypes.HasPreview(DocumentKind.Pdf));
    }
}
