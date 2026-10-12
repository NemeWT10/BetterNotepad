using System.Text;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Files;

namespace BetterNotepad.Tests;

public sealed class TextFileIOTests : IDisposable
{
    private const string Polish = "Zażółć gęślą jaźń";
    private const string PolishMultiline = "Zażółć gęślą jaźń\nSecond line: ĄĆĘŁŃÓŚŹŻ\nthird\n";

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly TempDir _dir = new();

    public TextFileIOTests() => TextFileIO.EnsureCodePages();

    public void Dispose() => _dir.Dispose();

    private static byte[] Concat(byte[] a, byte[] b) => [.. a, .. b];

    // ---------------------------------------------------------------- detection

    [Fact]
    public void Utf8_WithoutBom_IsDetected()
    {
        var r = TextFileIO.Decode(new UTF8Encoding(false).GetBytes(Polish));
        Assert.Equal(65001, r.Encoding.CodePage);
        Assert.False(r.HasBom);
        Assert.False(r.IsBinary);
        Assert.Equal(Polish, r.Text);
    }

    [Fact]
    public void Utf8_WithBom_IsDetected_AndBomIsNotPartOfText()
    {
        var r = TextFileIO.Decode(Concat(Utf8Bom, new UTF8Encoding(false).GetBytes(Polish)));
        Assert.Equal(65001, r.Encoding.CodePage);
        Assert.True(r.HasBom);
        Assert.Equal(Polish, r.Text);
        Assert.DoesNotContain('﻿', r.Text);
    }

    [Fact]
    public void Utf16LE_WithBom_IsDetected()
    {
        var r = TextFileIO.Decode(Concat([0xFF, 0xFE], new UnicodeEncoding(false, false).GetBytes(Polish)));
        Assert.Equal(1200, r.Encoding.CodePage);
        Assert.True(r.HasBom);
        Assert.False(r.IsBinary);
        Assert.Equal(Polish, r.Text);
    }

    [Fact]
    public void Utf16BE_WithBom_IsDetected()
    {
        var r = TextFileIO.Decode(Concat([0xFE, 0xFF], new UnicodeEncoding(true, false).GetBytes(Polish)));
        Assert.Equal(1201, r.Encoding.CodePage);
        Assert.True(r.HasBom);
        Assert.False(r.IsBinary);
        Assert.Equal(Polish, r.Text);
    }

    [Fact]
    public void Utf32_WithBom_IsDetected()
    {
        var le = TextFileIO.Decode(Concat([0xFF, 0xFE, 0, 0], new UTF32Encoding(false, false).GetBytes(Polish)));
        Assert.Equal(12000, le.Encoding.CodePage);
        Assert.True(le.HasBom);
        Assert.Equal(Polish, le.Text);

        var be = TextFileIO.Decode(Concat([0, 0, 0xFE, 0xFF], new UTF32Encoding(true, false).GetBytes(Polish)));
        Assert.Equal(12001, be.Encoding.CodePage);
        Assert.True(be.HasBom);
        Assert.Equal(Polish, be.Text);
    }

    [Fact]
    public void Utf16LE_WithoutBom_IsDetectedByHeuristic_NotTreatedAsBinary()
    {
        var r = TextFileIO.Decode(new UnicodeEncoding(false, false).GetBytes(PolishMultiline));
        Assert.Equal(1200, r.Encoding.CodePage);
        Assert.False(r.HasBom);
        Assert.False(r.IsBinary);
        Assert.Equal(PolishMultiline, r.Text);
    }

    [Fact]
    public void Utf16BE_WithoutBom_IsDetectedByHeuristic_NotTreatedAsBinary()
    {
        var r = TextFileIO.Decode(new UnicodeEncoding(true, false).GetBytes(PolishMultiline));
        Assert.Equal(1201, r.Encoding.CodePage);
        Assert.False(r.HasBom);
        Assert.False(r.IsBinary);
        Assert.Equal(PolishMultiline, r.Text);
    }

    [Fact]
    public void Utf16_WithoutBom_LongFile_IsDetected()
    {
        var text = string.Concat(Enumerable.Repeat("Hello world, zażółć gęślą jaźń.\r\n", 4000));
        var r = TextFileIO.Decode(new UnicodeEncoding(false, false).GetBytes(text));
        Assert.Equal(1200, r.Encoding.CodePage);
        Assert.False(r.IsBinary);
        Assert.Equal(text, r.Text);
    }

    [Fact]
    public void Windows1250_PolishText_IsNotDetectedAsUtf8()
    {
        var bytes = Encoding.GetEncoding(1250).GetBytes(Polish);
        Assert.False(TextFileIO.IsValidUtf8(bytes));

        var r = TextFileIO.Decode(bytes);
        Assert.NotEqual(65001, r.Encoding.CodePage);
        Assert.False(r.HasBom);
        Assert.False(r.IsBinary);
    }

    [Fact]
    public void Windows1250_PolishText_RoundTripsOnPolishSystem()
    {
        using var _ = new CultureScope("pl-PL");
        Assert.Equal(1250, TextFileIO.Ansi.CodePage);

        var r = TextFileIO.Decode(Encoding.GetEncoding(1250).GetBytes(Polish));
        Assert.Equal(1250, r.Encoding.CodePage);
        Assert.Equal(Polish, r.Text);
    }

    [Fact]
    public void PureAscii_IsUtf8_WithoutBom()
    {
        var r = TextFileIO.Decode(Encoding.ASCII.GetBytes("plain ascii\r\nsecond line\r\n"));
        Assert.Equal(65001, r.Encoding.CodePage);
        Assert.False(r.HasBom);
        Assert.False(r.IsBinary);
        Assert.Equal("plain ascii\r\nsecond line\r\n", r.Text);
    }

    [Fact]
    public void EmptyFile_IsEmptyText_NotBinary()
    {
        var r = TextFileIO.Decode([]);
        Assert.Equal("", r.Text);
        Assert.False(r.IsBinary);
        Assert.Equal(LineEnding.CRLF, r.Eol);
        Assert.Equal(0, r.Length);
    }

    [Fact]
    public void InvalidUtf8_IsRejected_ValidUtf8_Accepted()
    {
        Assert.True(TextFileIO.IsValidUtf8(new UTF8Encoding(false).GetBytes(Polish)));
        Assert.True(TextFileIO.IsValidUtf8(Encoding.ASCII.GetBytes("abc")));
        Assert.False(TextFileIO.IsValidUtf8([0xC4]));                 // truncated sequence
        Assert.False(TextFileIO.IsValidUtf8([0xFF, 0xFE, 0x41]));     // never valid in UTF-8
        Assert.False(TextFileIO.IsValidUtf8([0xC0, 0x80]));           // overlong NUL
    }

    // ---------------------------------------------------------------- binary

    [Fact]
    public void NulBytes_AreBinary()
    {
        var bytes = new byte[] { (byte)'M', (byte)'Z', 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00 };
        var r = TextFileIO.Decode(bytes);
        Assert.True(r.IsBinary);
        Assert.Equal("", r.Text);
        Assert.Equal(bytes.Length, r.Length);
    }

    [Fact]
    public void PngHeader_IsBinary()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];
        Assert.True(TextFileIO.Decode(png).IsBinary);
    }

    [Fact]
    public void ManyControlCharacters_WithoutNul_AreBinary()
    {
        var bytes = Enumerable.Range(0, 400).Select(i => (byte)(1 + i % 7)).ToArray();
        Assert.True(TextFileIO.LooksBinary(bytes));
        Assert.True(TextFileIO.Decode(bytes).IsBinary);
    }

    [Fact]
    public void NormalControlCharacters_AreNotBinary()
    {
        var text = "col1\tcol2\r\nrow\ttwo\r\n\u001b[0m escape\r\n";
        var r = TextFileIO.Decode(Encoding.ASCII.GetBytes(text));
        Assert.False(r.IsBinary);
        Assert.Equal(text, r.Text);
    }

    [Fact]
    public void LooksBinary_OnlyInspectsFirst8KB()
    {
        var bytes = new byte[10_000];
        Array.Fill(bytes, (byte)'a');
        bytes[9000] = 0;
        Assert.False(TextFileIO.LooksBinary(bytes));
        bytes[100] = 0;
        Assert.True(TextFileIO.LooksBinary(bytes));
    }

    [Fact]
    public void ForcedEncoding_SkipsBinaryDetection()
    {
        var r = TextFileIO.Decode([0x41, 0x00, 0x42, 0x00], Encoding.ASCII);
        Assert.False(r.IsBinary);
    }

    // ---------------------------------------------------------------- end of line

    [Theory]
    [InlineData("a\r\nb\r\nc", LineEnding.CRLF)]
    [InlineData("a\nb\nc", LineEnding.LF)]
    [InlineData("a\rb\rc", LineEnding.CR)]
    [InlineData("no line breaks at all", LineEnding.CRLF)]
    [InlineData("", LineEnding.CRLF)]
    [InlineData("a\r\nb\r\nc\nd", LineEnding.CRLF)]      // mixed: CRLF majority
    [InlineData("a\nb\nc\r\nd", LineEnding.LF)]          // mixed: LF majority
    [InlineData("a\rb\rc\r\nd", LineEnding.CR)]          // mixed: CR majority
    [InlineData("a\r\nb\nc", LineEnding.CRLF)]           // tie CRLF/LF: CRLF
    [InlineData("trailing newline\n", LineEnding.LF)]
    [InlineData("\r\n", LineEnding.CRLF)]
    public void DetectEol_ByMajority(string text, LineEnding expected)
        => Assert.Equal(expected, TextFileIO.DetectEol(text));

    [Fact]
    public void DetectEol_CrLfPairIsNotCountedAsTwoBreaks()
    {
        // 2 x CRLF must not be read as 2 CR + 2 LF (which would be a "tie" resolved to LF).
        Assert.Equal(LineEnding.CRLF, TextFileIO.DetectEol("x\r\ny\r\n"));
    }

    [Fact]
    public void Decode_ReportsEol()
    {
        Assert.Equal(LineEnding.LF, TextFileIO.Decode(Encoding.UTF8.GetBytes("a\nb\n")).Eol);
        Assert.Equal(LineEnding.CRLF, TextFileIO.Decode(Encoding.UTF8.GetBytes("a\r\nb\r\n")).Eol);
        Assert.Equal(LineEnding.CR, TextFileIO.Decode(Encoding.UTF8.GetBytes("a\rb\r")).Eol);
    }

    [Fact]
    public void LineEndings_Helpers()
    {
        Assert.Equal("\r\n", LineEnding.CRLF.Text());
        Assert.Equal("\n", LineEnding.LF.Text());
        Assert.Equal("\r", LineEnding.CR.Text());
        Assert.Equal("CRLF", LineEnding.CRLF.Label());
        Assert.Equal("LF", LineEnding.LF.Label());
        Assert.Equal("CR", LineEnding.CR.Label());
        Assert.Equal(LineEnding.LF, LineEndings.Parse("lf"));
        Assert.Equal(LineEnding.CR, LineEndings.Parse("Cr"));
        Assert.Equal(LineEnding.CRLF, LineEndings.Parse("whatever"));
        Assert.Equal(LineEnding.CRLF, LineEndings.Parse(null));
    }

    // ---------------------------------------------------------------- write / read round trip

    public static TheoryData<string, bool> UnicodeFormats => new()
    {
        { "utf-8", false },
        { "utf-8", true },
        { "utf-16le", true },
        { "utf-16be", true },
        { "utf-16le", false },
        { "utf-16be", false },
    };

    private static Encoding UnicodeEncodingFor(string id) => id switch
    {
        "utf-8" => new UTF8Encoding(false),
        "utf-16le" => new UnicodeEncoding(false, false),
        "utf-16be" => new UnicodeEncoding(true, false),
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    private static byte[] PreambleFor(int codePage) => codePage switch
    {
        65001 => [0xEF, 0xBB, 0xBF],
        1200 => [0xFF, 0xFE],
        1201 => [0xFE, 0xFF],
        _ => [],
    };

    [Theory]
    [MemberData(nameof(UnicodeFormats))]
    public void WriteThenRead_Unicode_PreservesTextEncodingAndBom(string id, bool bom)
    {
        var enc = UnicodeEncodingFor(id);
        var path = _dir.File($"rt-{id}-{bom}.txt");
        var text = "Hello zażółć gęślą jaźń 😀\r\nline two\r\n";

        TextFileIO.Write(path, text, enc, bom);

        var raw = File.ReadAllBytes(path);
        var pre = PreambleFor(enc.CodePage);
        Assert.Equal(bom, raw.AsSpan().StartsWith(pre));
        Assert.Equal(enc.GetByteCount(text) + (bom ? pre.Length : 0), raw.Length);

        var r = TextFileIO.Read(path);
        Assert.Equal(text, r.Text);
        Assert.Equal(enc.CodePage, r.Encoding.CodePage);
        Assert.Equal(bom, r.HasBom);
        Assert.False(r.IsBinary);
        Assert.Equal(LineEnding.CRLF, r.Eol);
        Assert.Equal(raw.Length, r.Length);
        Assert.Equal(File.GetLastWriteTimeUtc(path), r.LastWriteUtc);
    }

    [Fact]
    public void WriteThenRead_Utf32WithBom()
    {
        var enc = new UTF32Encoding(false, false);
        var path = _dir.File("utf32.txt");
        TextFileIO.Write(path, Polish, enc, bom: true);
        var raw = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xFF, 0xFE, 0, 0 }, raw[..4]);
        var r = TextFileIO.Read(path);
        Assert.Equal(12000, r.Encoding.CodePage);
        Assert.True(r.HasBom);
        Assert.Equal(Polish, r.Text);
    }

    public static TheoryData<int, string> LegacyCodePages => new()
    {
        { 1250, Polish },
        { 28592, Polish },
        { 852, Polish },
        { 1252, "Café déjà vu — naïve" },
        { 20127, "plain ASCII only" },
    };

    [Theory]
    [MemberData(nameof(LegacyCodePages))]
    public void WriteThenRead_LegacyCodePage_WithForcedEncoding(int codePage, string text)
    {
        var enc = Encoding.GetEncoding(codePage);
        var path = _dir.File($"legacy-{codePage}.txt");
        TextFileIO.Write(path, text, enc, bom: false);

        Assert.Equal(enc.GetBytes(text), File.ReadAllBytes(path));

        var r = TextFileIO.Read(path, enc);
        Assert.Equal(text, r.Text);
        Assert.Equal(codePage, r.Encoding.CodePage);
        Assert.False(r.HasBom);
    }

    [Fact]
    public void Write_BomRequestedForEncodingWithoutPreamble_WritesNoBom()
    {
        var enc = Encoding.GetEncoding(1250);
        var path = _dir.File("nobom-1250.txt");
        TextFileIO.Write(path, Polish, enc, bom: true);
        Assert.Equal(enc.GetBytes(Polish), File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("a\nb\nc\n")]
    [InlineData("a\r\nb\r\nc\r\n")]
    [InlineData("a\rb\rc\r")]
    public void WriteThenRead_PreservesLineEndingsVerbatim(string text)
    {
        var path = _dir.File("eol.txt");
        TextFileIO.Write(path, text, TextFileIO.Utf8NoBom, false);
        Assert.Equal(text, TextFileIO.Read(path).Text);
        Assert.Equal(TextFileIO.DetectEol(text), TextFileIO.Read(path).Eol);
    }

    [Fact]
    public void Read_ForcedUtf8_OnBomFile_SkipsBom()
    {
        var path = _dir.File("forced-bom.txt");
        File.WriteAllBytes(path, Concat(Utf8Bom, new UTF8Encoding(false).GetBytes(Polish)));
        var r = TextFileIO.Read(path, TextFileIO.Utf8NoBom);
        Assert.Equal(Polish, r.Text);
        Assert.True(r.HasBom);
    }

    [Fact]
    public void Read_ForcedUtf8_OnFileWithoutBom_ReportsNoBom()
    {
        var path = _dir.File("forced-nobom.txt");
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(Polish));
        var r = TextFileIO.Read(path, TextFileIO.Utf8NoBom);
        Assert.Equal(Polish, r.Text);
        Assert.False(r.HasBom);
    }

    [Fact]
    public void Read_ForcedWindows1250_OnUtf8File_DoesNotThrow()
    {
        var path = _dir.File("forced-wrong.txt");
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(Polish));
        var r = TextFileIO.Read(path, Encoding.GetEncoding(1250));
        Assert.NotEqual(Polish, r.Text);
        Assert.False(r.IsBinary);
    }

    [Fact]
    public void Read_EmptyFile()
    {
        var path = _dir.File("empty.txt");
        File.WriteAllBytes(path, []);
        var r = TextFileIO.Read(path);
        Assert.Equal("", r.Text);
        Assert.Equal(0, r.Length);
        Assert.False(r.IsBinary);
    }

    [Fact]
    public void Read_FileOpenForSharedWriting_StillWorks()
    {
        var path = _dir.File("shared.txt");
        File.WriteAllText(path, "shared content");
        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        Assert.Equal("shared content", TextFileIO.Read(path).Text);
    }

    // ---------------------------------------------------------------- atomic write

    [Fact]
    public void Write_OverExistingLongerFile_ReplacesContentCompletely()
    {
        var path = _dir.File("overwrite.txt");
        TextFileIO.Write(path, new string('x', 10_000) + "\r\n", TextFileIO.Utf8NoBom, false);
        TextFileIO.Write(path, "short", TextFileIO.Utf8NoBom, false);

        Assert.Equal("short", File.ReadAllText(path));
        Assert.Equal(5, new FileInfo(path).Length);
    }

    [Fact]
    public void Write_OverExistingFile_ChangesEncodingAndBom()
    {
        var path = _dir.File("reencode.txt");
        TextFileIO.Write(path, Polish, TextFileIO.Utf8NoBom, false);
        TextFileIO.Write(path, Polish, new UnicodeEncoding(false, false), true);

        var r = TextFileIO.Read(path);
        Assert.Equal(1200, r.Encoding.CodePage);
        Assert.True(r.HasBom);
        Assert.Equal(Polish, r.Text);
    }

    [Fact]
    public void Write_LeavesNoTempFilesBehind()
    {
        var path = _dir.File("clean.txt");
        TextFileIO.Write(path, "one", TextFileIO.Utf8NoBom, false);
        TextFileIO.Write(path, "two", TextFileIO.Utf8NoBom, false);
        TextFileIO.Write(path, "three", TextFileIO.Utf8NoBom, false);

        var files = Directory.GetFileSystemEntries(_dir.Path).Select(p => Path.GetFileName(p)!).ToArray();
        Assert.Equal(["clean.txt"], files);
    }

    [Fact]
    public void Write_CreatesMissingDirectories()
    {
        var path = Path.Combine(_dir.Path, "a", "b", "c", "deep.txt");
        TextFileIO.Write(path, "deep", TextFileIO.Utf8NoBom, false);
        Assert.Equal("deep", File.ReadAllText(path));
    }

    [Fact]
    public void Write_FileWithSpacesAndUnicodeInName()
    {
        var path = _dir.File("notatka żółć (1).txt");
        TextFileIO.Write(path, "ok", TextFileIO.Utf8NoBom, false);
        Assert.Equal("ok", File.ReadAllText(path));
    }

    [Fact]
    public void Write_EmptyText_CreatesEmptyFile()
    {
        var path = _dir.File("zero.txt");
        TextFileIO.Write(path, "", TextFileIO.Utf8NoBom, false);
        Assert.True(File.Exists(path));
        Assert.Equal(0, new FileInfo(path).Length);
    }

    [Fact]
    public void Write_Large_RoundTrips()
    {
        var path = _dir.File("large.txt");
        var text = string.Concat(Enumerable.Range(0, 50_000).Select(i => $"line {i} zażółć\r\n"));
        TextFileIO.Write(path, text, TextFileIO.Utf8NoBom, false);
        var r = TextFileIO.Read(path);
        Assert.Equal(text, r.Text);
        Assert.Equal(LineEnding.CRLF, r.Eol);
    }

    // ---------------------------------------------------------------- CanEncode / labels / choices

    [Fact]
    public void CanEncode_PolishInWindows1250_Yes()
        => Assert.True(TextFileIO.CanEncode(Polish + " ĄĆĘŁŃÓŚŹŻ", Encoding.GetEncoding(1250)));

    [Fact]
    public void CanEncode_EmojiInWindows1250_No()
        => Assert.False(TextFileIO.CanEncode("hello 😀", Encoding.GetEncoding(1250)));

    [Fact]
    public void CanEncode_PolishInAsciiAndWindows1252_No()
    {
        Assert.False(TextFileIO.CanEncode(Polish, Encoding.ASCII));
        Assert.False(TextFileIO.CanEncode("łódź", Encoding.GetEncoding(1252)));
    }

    [Fact]
    public void CanEncode_AsciiInEverything_Yes()
    {
        foreach (var (_, _, get, _) in TextFileIO.Choices)
            Assert.True(TextFileIO.CanEncode("plain ascii 123", get()));
    }

    [Fact]
    public void CanEncode_UnicodeEncodings_AlwaysYes()
    {
        Assert.True(TextFileIO.CanEncode("😀 Zażółć", new UTF8Encoding(false)));
        Assert.True(TextFileIO.CanEncode("😀 Zażółć", new UnicodeEncoding(false, false)));
        Assert.True(TextFileIO.CanEncode("😀 Zażółć", new UnicodeEncoding(true, false)));
        Assert.True(TextFileIO.CanEncode("😀 Zażółć", new UTF32Encoding(false, false)));
    }

    [Fact]
    public void CanEncode_DoesNotMutateTheSharedEncoding()
    {
        var enc = Encoding.GetEncoding(1250);
        TextFileIO.CanEncode("😀", enc);
        // Real writes still use the replacement fallback and must not throw.
        var bytes = enc.GetBytes("😀");
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public void CanEncode_EmptyText_Yes()
        => Assert.True(TextFileIO.CanEncode("", Encoding.ASCII));

    [Fact]
    public void Label_NamesCommonEncodings()
    {
        Assert.Equal("UTF-8", TextFileIO.Label(TextFileIO.Utf8NoBom, false));
        Assert.Equal("UTF-8 BOM", TextFileIO.Label(TextFileIO.Utf8NoBom, true));
        Assert.Equal("UTF-16 LE", TextFileIO.Label(new UnicodeEncoding(false, false), true));
        Assert.Equal("UTF-16 BE", TextFileIO.Label(new UnicodeEncoding(true, false), true));
        Assert.Equal("Windows-1250", TextFileIO.Label(Encoding.GetEncoding(1250), false));
        Assert.Equal("Windows-1252", TextFileIO.Label(Encoding.GetEncoding(1252), false));
        Assert.Equal("ISO-8859-2", TextFileIO.Label(Encoding.GetEncoding(28592), false));
        Assert.Equal("DOS 852", TextFileIO.Label(Encoding.GetEncoding(852), false));
        Assert.Equal("ASCII", TextFileIO.Label(Encoding.ASCII, false));
    }

    [Fact]
    public void Choices_HaveUniqueIdsAndWorkingFactories()
    {
        var ids = TextFileIO.Choices.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var c in TextFileIO.Choices)
        {
            var enc = c.Get();
            Assert.NotNull(enc);
            Assert.False(string.IsNullOrWhiteSpace(c.Label));
            // The label shown in the status bar must agree with the choice.
            Assert.Equal(c.Label, TextFileIO.Label(enc, c.Bom));
        }
    }

    [Fact]
    public void Ansi_IsTheSystemCodePage_NotTheUiCulture()
    {
        // a Polish file stays 1250 under an English UI: the system ANSI page (GetACP) decides, not the thread culture
        int pl, en;
        using (new CultureScope("pl-PL")) pl = TextFileIO.Ansi.CodePage;
        using (new CultureScope("en-US")) en = TextFileIO.Ansi.CodePage;
        Assert.Equal(pl, en);
        Assert.Contains(pl, new[] { 874, 932, 936, 949, 950, 1250, 1251, 1252, 1253, 1254, 1255, 1256, 1257, 1258 });
    }
}
