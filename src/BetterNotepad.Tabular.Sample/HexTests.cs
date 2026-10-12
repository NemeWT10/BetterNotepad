using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BetterNotepad.Hex;

namespace BetterNotepad.Tabular.Sample;

internal static class HexLogicTests
{
    [Case]
    public static void OffsetDigitsGrowWithTheFile()
    {
        Check.Equal(8, HexFormat.OffsetDigits(0));
        Check.Equal(8, HexFormat.OffsetDigits(0x1_0000_0000L), "highest offset 0xFFFFFFFF still fits");
        Check.Equal(10, HexFormat.OffsetDigits(0x1_0000_0001L), "needs a 9th digit, rounded to even");
        Check.Equal(12, HexFormat.OffsetDigits(1L << 40 | 1));
        Check.Equal("0000001F", HexFormat.FormatOffset(0x1F, 8));
        Check.Equal("0001234567", HexFormat.FormatOffset(0x1234567, 10));
    }

    [Case]
    public static void HexCopyFormats()
    {
        byte[] b = [0xDE, 0xAD, 0x00, 0xBE, 0xEF];
        Check.Equal("DE AD 00 BE EF", HexFormat.ToHex(b));
        Check.Equal("DEAD00BEEF", HexFormat.ToHex(b, HexCopyFormat.Compact));
        Check.Equal("0xDE, 0xAD, 0x00, 0xBE, 0xEF", HexFormat.ToHex(b, HexCopyFormat.CArray));
        Check.Equal("", HexFormat.ToHex([]));
        var table = HexTextTable.For(HexTextEncoding.Windows1250);
        Check.Equal("", HexFormat.ToDump([], 0, 16, table, 6), "empty dump");
        var sample = new byte[20];
        for (var i = 0; i < sample.Length; i++) sample[i] = (byte)(0x41 + i);
        var lines = HexFormat.ToDump(sample.AsSpan(3, 13), 3, 16, table, 20).Split("\r\n");
        Check.Equal(1, lines.Length, "a 13 byte range from offset 3 fits one row of the grid");
        Check.True(lines[0].StartsWith("00000000  "), "starts at the row of the grid");
        Check.True(lines[0].Contains("44 45 46 47 48  49 4A 4B 4C 4D 4E 4F 50"), "hex bytes of the range, grouped by 8: " + lines[0]);
        Check.True(lines[0].TrimEnd().EndsWith("DEFGHIJKLMNOP"), "text column: " + lines[0]);
        var two = HexFormat.ToDump(sample, 0, 16, table, 20).Split("\r\n");
        Check.Equal(2, two.Length, "20 bytes = two rows");
        Check.True(two[1].StartsWith("00000010  "), "second row offset");
    }

    [Case]
    public static void ParsesGoToOffsets()
    {
        void Ok(string text, bool defaultHex, long expected)
        {
            Check.True(HexFormat.TryParseOffset(text, defaultHex, out var v), "should parse: " + text);
            Check.Equal(expected, v, text);
        }

        Ok("0x1F4", false, 0x1F4);
        Ok("0X1f4", true, 0x1F4);
        Ok("1F4h", false, 0x1F4);
        Ok("$1F4", false, 0x1F4);
        Ok("500", false, 500);
        Ok("500", true, 0x500);
        Ok("DEAD BEEF", true, 0xDEADBEEF);
        Ok("1_000_000", false, 1_000_000);
        Ok("  42  ", false, 42);
        Ok("0", false, 0);
        Ok("7FFFFFFFFFFFFFFF", true, long.MaxValue);
        foreach (var bad in new[] { "", "  ", "-5", "+5", "abc", "12x", "0x", "99999999999999999999", "FFFFFFFFFFFFFFFF" })
            Check.True(!HexFormat.TryParseOffset(bad, false, out _), "should not parse: " + bad);
        Check.True(!HexFormat.TryParseOffset("FFFFFFFFFFFFFFFF", true, out _), "overflows a long");
    }

    [Case]
    public static void TextColumnTables()
    {
        var cp = HexTextTable.For(HexTextEncoding.Windows1250);
        (byte B, char C)[] polish = [(0xB9, 'ą'), (0xE6, 'ć'), (0xEA, 'ę'), (0xB3, 'ł'), (0xF1, 'ń'), (0xF3, 'ó'), (0x9C, 'ś'), (0x9F, 'ź'), (0xBF, 'ż'), (0xA5, 'Ą'), (0x8C, 'Ś'), (0xAF, 'Ż')];
        foreach (var (b, c) in polish) Check.Equal(c, cp.Map(b), $"cp1250 0x{b:X2}");
        Check.Equal('A', cp.Map(0x41));
        Check.Equal('.', cp.Map(0x00), "NUL");
        Check.Equal('.', cp.Map(0x7F), "DEL");
        Check.Equal('.', cp.Map(0x0A), "LF");
        Check.Equal('.', cp.Map(0xA0), "no-break space");
        Check.Equal('.', cp.Map(0xAD), "soft hyphen");
        Check.Equal('.', cp.Map(0x81), "undefined in cp1250");
        Check.True(cp.IsPrintable(0xB9) && !cp.IsPrintable(0x81));

        var latin = HexTextTable.For(HexTextEncoding.Latin1);
        Check.Equal('é', latin.Map(0xE9));
        Check.Equal('.', latin.Map(0x85), "C1 control");
        var ascii = HexTextTable.For(HexTextEncoding.Ascii);
        Check.Equal('.', ascii.Map(0xE9));
        Check.Equal('~', ascii.Map(0x7E));
        Check.Equal("A.ą", HexFormat.ToText([0x41, 0x00, 0xB9], cp));
    }

    [Case]
    public static void SniffsBinaryFromText()
    {
        Check.True(!HexSniffer.LooksBinary("plain text, with ąęś in UTF-8\r\nline two\r\n"u8), "utf-8 text");
        Check.True(!HexSniffer.LooksBinary(new byte[] { 0xEF, 0xBB, 0xBF, 0x68, 0x69 }), "utf-8 with BOM");
        Check.True(!HexSniffer.LooksBinary(new byte[] { 0xFF, 0xFE, 0x68, 0x00, 0x69, 0x00 }), "utf-16 with BOM");
        Check.True(!HexSniffer.LooksBinary(System.Text.Encoding.Unicode.GetBytes("hello world, this is utf-16 text without a BOM")), "utf-16 without BOM");
        Check.True(HexSniffer.LooksBinary(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D }), "png header");
        Check.True(HexSniffer.LooksBinary(new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00 }), "exe header");
        var rnd = new Random(1);
        var noise = new byte[512];
        rnd.NextBytes(noise);
        Check.True(HexSniffer.LooksBinary(noise), "random bytes");
        Check.True(!HexSniffer.LooksBinary(ReadOnlySpan<byte>.Empty), "empty");
    }

    [Case]
    public static void GeometryHitTestsAgreeWithDrawing()
    {
        foreach (var bpr in new[] { 8, 16, 24, 32 })
        {
            var g = new HexGeometry(8, 21, bpr, 1 << 20);
            Check.True(g.XOffset < g.XHex && g.XHex < g.XText && g.XText + bpr * 8 < g.ContentWidth, $"column order for {bpr}");
            for (var i = 0; i < bpr; i++)
            {
                var x = g.HexX(i);
                Check.Equal(i, g.HitHex(x + 0.5), $"first digit of byte {i}");
                Check.Equal(i, g.HitHex(x + 12), $"second digit of byte {i}");
                Check.Equal(i, g.HitHex(x + 2 * 8 + 3), $"gap after byte {i} belongs to it");
                Check.Equal(i, g.HitText(g.TextX(i) + 1), $"text byte {i}");
                var (l, r) = g.HexSpan(i, i);
                Check.True(l < x && r > x + 16 && r - l < 3 * 8 + 0.01, $"span of byte {i} covers its digits only");
                if (i + 1 < bpr) Check.True(g.HexSpan(i, i).Right <= g.HexSpan(i + 1, i + 1).Left + 0.01 + (i % 8 == 7 ? 8 : 0), "spans of neighbours do not overlap");
            }

            Check.Equal(0, g.HitHex(g.XHex - 40), "left of the hex column");
            Check.Equal(bpr - 1, g.HitHex(g.XText), "right of the hex column");
            Check.Equal(bpr - 1, g.HitText(g.XText + 9999), "right of the text column");
            Check.True(g.PaneAt(g.XHex + 10) == HexPane.Hex && g.PaneAt(g.XText + 10) == HexPane.Text && g.PaneAt(g.XOffset) == HexPane.Hex, "pane by x");
        }
    }
}

internal static class HexSourceTests
{
    [Case]
    public static void MemorySource()
    {
        var s = new MemoryHexSource(new byte[] { 1, 2, 3, 4, 5 });
        var buf = new byte[3];
        Check.Equal(3, s.Read(1, buf));
        Check.SeqEqual(new byte[] { 2, 3, 4 }, buf);
        Check.Equal(2, s.Read(3, buf), "short read at the end");
        Check.Equal(0, s.Read(5, buf), "at the end");
        Check.Equal(0, s.Read(-1, buf), "negative");
        Check.Equal(0, s.Read(99, buf), "far past");
    }

    private static string TempFile(string name, byte[] data)
    {
        Directory.CreateDirectory(HexSample.Dir);
        var path = Path.Combine(HexSample.Dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Case]
    public static void FileSourceMatchesTheFileAcrossPages()
    {
        var data = new byte[3 * 1024 * 1024 + 123];
        new Random(5).NextBytes(data);
        var path = TempFile("pages.bin", data);
        using var s = FileHexSource.Open(path);
        Check.Equal(data.Length, s.Length, "length");
        var rnd = new Random(9);
        var buf = new byte[300_000];
        for (var i = 0; i < 400; i++)
        {
            var offset = rnd.Next(0, data.Length + 10);
            var count = rnd.Next(1, i % 7 == 0 ? buf.Length : 700);
            var n = s.Read(offset, buf.AsSpan(0, count));
            var expected = Math.Max(0, Math.Min(count, data.Length - offset));
            Check.Equal(expected, n, $"count at {offset}+{count}");
            if (!buf.AsSpan(0, n).SequenceEqual(data.AsSpan(offset, n))) throw new CheckFailed($"bytes differ at {offset}+{count}");
        }

        // the 64 KB page boundary
        var edge = new byte[16];
        Check.Equal(16, s.Read(65536 - 8, edge));
        Check.True(edge.AsSpan().SequenceEqual(data.AsSpan(65536 - 8, 16)), "across a page boundary");
    }

    [Case]
    public static void FileSourceSeesGrowthAfterRefreshAndReleasesTheFile()
    {
        var path = TempFile("grow.bin", [1, 2, 3, 4]);
        using (var s = FileHexSource.Open(path))
        {
            Check.Equal(4, s.Length);
            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) fs.Write([5, 6, 7, 8]);
            Check.Equal(4, s.Length, "length is a snapshot until Refresh");
            s.Refresh();
            Check.Equal(8, s.Length, "after refresh");
            var b = new byte[8];
            Check.Equal(8, s.Read(0, b));
            Check.SeqEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, b);
        }

        // closed: nobody holds the file any more
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Check.True(exclusive.Length == 8, "file free after dispose");
    }

    [Case]
    public static void FileSourceSurvivesTruncation()
    {
        var path = TempFile("shrink.bin", new byte[200_000]);
        using var s = FileHexSource.Open(path);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) fs.SetLength(1000);
        var buf = new byte[100];
        Check.True(s.Read(150_000, buf) <= 100, "no exception");
        s.Refresh();
        Check.Equal(1000, s.Length);
    }
}

/// <summary>Source of 8 GB generated on demand: proves nothing but the visible rows is ever read.</summary>
internal sealed class PatternSource : IHexSource
{
    public long Length => 8L << 30;

    public long BytesRead { get; private set; }

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= Length) return 0;
        var n = (int)Math.Min(buffer.Length, Length - offset);
        for (var i = 0; i < n; i++) buffer[i] = (byte)((offset + i) * 31 >> 3);
        BytesRead += n;
        return n;
    }

    public void Dispose()
    {
    }
}

internal static class HexControlTests
{
    private static void Key(HexView v, Key key)
        => v.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(v)!, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });

    private static byte[] Ramp(int n) => Enumerable.Range(0, n).Select(i => (byte)i).ToArray();

    [Case]
    public static void SelectionApiAndCopyFormats()
    {
        using var host = new HexHost(1000, 400);
        var v = host.View;
        v.SetBytes(Ramp(256));
        host.Layout();
        Check.Equal(256L, v.Length);
        Check.Equal(1L, v.SelectionLength, "the caret byte is the selection");
        v.Select(10, 5);
        Check.Equal(10L, v.SelectionStart);
        Check.Equal(5L, v.SelectionLength);
        Check.Equal(14L, v.CaretOffset);
        Check.Equal("0A 0B 0C 0D 0E", v.GetSelectionAsHex());
        Check.Equal("0A0B0C0D0E", v.GetSelectionAsHex(HexCopyFormat.Compact));
        Check.Equal("0x0A, 0x0B, 0x0C, 0x0D, 0x0E", v.GetSelectionAsHex(HexCopyFormat.CArray));
        Check.Equal(".....", v.GetSelectionAsText(), "control bytes show as dots");
        v.Select(0x41, 3);
        Check.Equal("ABC", v.GetSelectionAsText());
        Check.True(v.GetSelectionAsHex(HexCopyFormat.Dump)!.StartsWith("00000040  "), "dump keeps the file's row grid");
        v.SelectAll();
        Check.Equal(256L, v.SelectionLength);
        v.ClearSelection();
        Check.Equal(1L, v.SelectionLength);
        v.Select(1000, 5);
        Check.Equal(255L, v.CaretOffset, "clamped to the file");
        v.MaxCopyBytes = 4;
        v.Select(0, 10);
        Check.True(v.GetSelectedBytes() is null && v.GetSelectionAsHex() is null, "selection above the copy limit");
        var events = 0;
        v.SelectionChanged += (_, _) => events++;
        v.Select(3, 1);
        Check.Equal(1, events, "SelectionChanged raised once per change");
    }

    [Case]
    public static void KeyboardNavigation()
    {
        using var host = new HexHost(1000, 400);
        var v = host.View;
        v.SetBytes(Ramp(250)); // 15 full rows + a partial one of 10
        host.Layout();
        Key(v, System.Windows.Input.Key.Down);
        Check.Equal(16L, v.CaretOffset, "down");
        Key(v, System.Windows.Input.Key.Right);
        Check.Equal(17L, v.CaretOffset, "right");
        Key(v, System.Windows.Input.Key.End);
        Check.Equal(31L, v.CaretOffset, "end of row");
        Key(v, System.Windows.Input.Key.Home);
        Check.Equal(16L, v.CaretOffset, "start of row");
        Key(v, System.Windows.Input.Key.Left);
        Key(v, System.Windows.Input.Key.Up);
        Check.Equal(15L, v.CaretOffset, "left from a row start goes to the end of the previous row");
        Key(v, System.Windows.Input.Key.Up);
        Check.Equal(15L, v.CaretOffset, "up on the first row does nothing");
        v.Select(235, 1);
        Key(v, System.Windows.Input.Key.Down);
        Check.Equal(249L, v.CaretOffset, "down into a short last row lands on its last byte");
        Key(v, System.Windows.Input.Key.Down);
        Check.Equal(249L, v.CaretOffset, "down on the last row does nothing");
        Key(v, System.Windows.Input.Key.End);
        Check.Equal(249L, v.CaretOffset);
        v.Select(0, 1);
        Key(v, System.Windows.Input.Key.PageDown);
        Check.True(v.CaretOffset >= 16 * 10, "page down moves about a screen: " + v.CaretOffset);
        Key(v, System.Windows.Input.Key.PageUp);
        Check.Equal(0L, v.CaretOffset, "page up returns");
        Key(v, System.Windows.Input.Key.Escape);
        Check.Equal(1L, v.SelectionLength);
    }

    [Case]
    public static void LargeFileOnlyReadsTheVisibleRows()
    {
        using var host = new HexHost(1000, 400);
        var v = host.View;
        var source = new PatternSource();
        v.SmoothScrolling = false;
        v.SetSource(source, ownsSource: false);
        host.Layout();
        host.SavePng(Path.Combine(HexSample.Dir, "big-source.png"), 1);
        Check.Equal(10, v.Geometry.OffsetDigits, "8 GB needs 10 offset digits");
        Check.True(source.BytesRead < 16 * 100, $"first screen read {source.BytesRead} bytes");

        v.GoToOffset(5_000_000_000, 20);
        host.SavePng(Path.Combine(HexSample.Dir, "big-source.png"), 1);
        Check.Equal(5_000_000_019L, v.CaretOffset, "caret at the end of the selection");
        Check.True(Math.Abs(v.TopOffset - 5_000_000_000) < 16 * 20, "scrolled near the offset: " + v.TopOffset);
        Check.True(source.BytesRead < 16 * 200, $"after a jump of 5 GB only {source.BytesRead} bytes were read");

        v.ScrollToRow(double.MaxValue);
        host.SavePng(Path.Combine(HexSample.Dir, "big-source.png"), 1);
        var lastRow = (source.Length + 15) / 16;
        Check.True(Math.Abs(v.TopRow + v.ViewportRows - lastRow) < 0.01, "scrolled to the very end");
        v.Select(source.Length - 1, 1);
        Check.Equal(source.Length - 1, v.CaretOffset);
        v.ScrollToRow(-5);
        Check.Equal(0.0, v.TopRow, "clamped at the top");
    }

    [Case]
    public static void WheelScrollsWholeNotchesByTheSystemSetting()
    {
        using var host = new HexHost(1000, 400);
        var v = host.View;
        v.SmoothScrolling = false;
        v.SetBytes(new byte[16 * 1000]);
        host.Layout();
        var lines = SystemParameters.WheelScrollLines;
        v.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.MouseWheelEvent });
        Check.Equal(lines > 0 ? (double)lines : Math.Round(v.ViewportRows - 1), Math.Round(v.TopRow, 3), "one notch down");
        v.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.MouseWheelEvent });
        Check.Equal(0.0, v.TopRow, "and back");
        v.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 12) { RoutedEvent = Mouse.MouseWheelEvent });
        Check.Equal(0.0, v.TopRow, "a precision touchpad delta above the top stays clamped");
        v.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -12) { RoutedEvent = Mouse.MouseWheelEvent });
        Check.True(v.TopRow > 0 && v.TopRow < lines, "a small delta scrolls a fraction of a notch: " + v.TopRow);
    }

    [Case]
    public static void HitTestMapsPointsToBytes()
    {
        using var host = new HexHost(1000, 400);
        var v = host.View;
        v.SmoothScrolling = false;
        v.SetBytes(Ramp(256));
        host.Layout();
        var g = v.Geometry;
        double RowY(int r) => v.HeaderHeight + (r + 0.5) * g.RowHeight;
        Check.True(v.TryGetOffsetAt(new Point(g.HexX(5) + 3, RowY(2)), out var o) && o == 2 * 16 + 5, "hex pane");
        Check.True(v.TryGetOffsetAt(new Point(g.TextX(9) + 2, RowY(3)), out o) && o == 3 * 16 + 9, "text pane");
        Check.True(!v.TryGetOffsetAt(new Point(g.HexX(5), 3), out _), "over the header");
        Check.True(v.TryGetOffsetAt(new Point(g.HexX(15), RowY(30)), out o) && o == 255, "below the data clamps to the last byte");
        v.SetBytes(new byte[16 * 100]);
        host.Layout();
        v.ScrollToRow(4);
        Check.True(v.TryGetOffsetAt(new Point(g.HexX(0) + 1, RowY(0)), out o) && o == 4 * 16, "after scrolling: " + o);
        v.ScrollToRow(4.5);
        Check.True(v.TryGetOffsetAt(new Point(g.HexX(0) + 1, RowY(0)), out o) && o == 5 * 16, "half a row further, the point is on the next row: " + o);
    }

    [Case]
    public static void MouseSelectionAcrossPanes()
    {
        using var host = new HexHost(1000, 400);
        var v = host.View;
        v.SetBytes(Ramp(256));
        host.Layout();
        var g = v.Geometry;
        double RowY(int r) => v.HeaderHeight + (r + 0.5) * g.RowHeight;
        Check.True(!v.PointerDown(new Point(g.HexX(3), 4), false), "a click on the header selects nothing");
        Check.True(v.PointerDown(new Point(g.HexX(3) + 2, RowY(1)), false), "click in the hex pane");
        Check.Equal(19L, v.CaretOffset, "caret");
        Check.True(v.ActivePane == HexPane.Hex && v.SelectionLength == 1, "active pane and single byte");
        v.PointerMove(new Point(g.TextX(9) + 2, RowY(3)));
        Check.Equal(57L, v.CaretOffset, "dragging into the text column picks the byte under the pointer");
        Check.Equal(19L, v.SelectionStart, "the anchor stays");
        Check.Equal(39L, v.SelectionLength, "length");
        v.PointerMove(new Point(g.HexX(0), RowY(0)));
        Check.Equal(0L, v.SelectionStart, "dragging back above the anchor");
        Check.Equal(20L, v.SelectionLength);
        v.PointerUp();
        v.PointerMove(new Point(g.HexX(8), RowY(5)));
        Check.Equal(20L, v.SelectionLength, "after the button is released moves change nothing");
        Check.True(v.PointerDown(new Point(g.TextX(2) + 1, RowY(6)), false), "click in the text pane");
        Check.True(v.ActivePane == HexPane.Text && v.CaretOffset == 98, "text pane active, caret " + v.CaretOffset);
        Check.True(v.PointerDown(new Point(g.HexX(10), RowY(7)), true), "shift + click extends");
        Check.Equal(98L, v.SelectionStart, "from the old caret");
        Check.Equal(25L, v.SelectionLength, "to the new one (98..122)");
        v.PointerUp();
    }

    [Case]
    public static void FitsTheWidth()
    {
        using (var wide = new HexHost(1000, 300))
        {
            wide.View.SetBytes(Ramp(256));
            wide.Layout();
            Check.Equal(16, wide.View.EffectiveBytesPerRow, "wide");
        }

        using (var narrow = new HexHost(520, 300))
        {
            narrow.View.SetBytes(Ramp(256));
            narrow.Layout();
            Check.Equal(8, narrow.View.EffectiveBytesPerRow, "narrow falls back to 8");
            narrow.View.Select(100, 1);
            Check.Equal(100L, narrow.View.CaretOffset, "the selection survives a different row width");
        }

        using (var huge = new HexHost(1600, 300))
        {
            huge.View.BytesPerRow = 32;
            huge.View.SetBytes(Ramp(256));
            huge.Layout();
            Check.Equal(32, huge.View.EffectiveBytesPerRow, "32 per row when asked and wide enough");
            huge.View.AutoFit = false;
            huge.Layout();
        }

        using (var fixedWidth = new HexHost(520, 300))
        {
            fixedWidth.View.AutoFit = false;
            fixedWidth.View.SetBytes(Ramp(256));
            fixedWidth.Layout();
            Check.Equal(16, fixedWidth.View.EffectiveBytesPerRow, "AutoFit off keeps 16 (clipped)");
        }
    }

    [Case]
    public static void EmptyFileAndTinySizesDoNotThrow()
    {
        using (var host = new HexHost(700, 240))
        {
            var v = host.View;
            v.SetBytes(Array.Empty<byte>());
            host.Layout();
            Key(v, System.Windows.Input.Key.Down);
            v.SelectAll();
            v.Select(5, 5);
            Check.Equal(0L, v.SelectionLength, "empty file has no selection");
            Check.True(v.GetSelectedBytes() is null, "nothing to copy");
            host.SavePng(Path.Combine(HexSample.Dir, "tiny-empty.png"), 1);
        }

        foreach (var (w, h) in new[] { (10.0, 10.0), (50, 20), (300, 30), (1, 500) })
        {
            using var host = new HexHost(w, h);
            host.View.SetBytes(Ramp(100));
            host.Layout();
            host.SavePng(Path.Combine(HexSample.Dir, "tiny-empty.png"), 1);
            host.View.Select(50, 10);
        }
    }

    [Case]
    public static void FontIsMonospaceAndResolves()
    {
        using var host = new HexHost(1000, 300);
        host.View.CodeFontFamily = "No Such Font Anywhere, Consolas";
        host.View.SetBytes(Ramp(100));
        host.Layout();
        var g = host.View.Geometry;
        Check.True(g.CharWidth >= 6 && g.CharWidth <= 12, "char width " + g.CharWidth);
        Check.True(g.RowHeight > g.CharWidth * 2, "row height " + g.RowHeight);
        host.View.CodeFontSize = 20;
        host.Layout();
        Check.True(host.View.Geometry.CharWidth > g.CharWidth * 1.4, "bigger font gives wider columns");
    }

    [Case]
    public static void ThemeSwitchRepaintsLive()
    {
        // a Window that is never shown: it is what makes WPF push app-level resource changes into the tree
        HexHost.SetTheme(FakeTheme.Aurora);
        var v = new HexView();
        var window = new Window { Content = v, Width = 800, Height = 300, ShowActivated = false, ShowInTaskbar = false };
        v.SetBytes(Ramp(100));
        Check.Equal(Color.FromRgb(0x12, 0x14, 0x1C), ((SolidColorBrush)v.Background!).Color, "Aurora editor background");
        Check.Equal(Color.FromRgb(0x6A, 0x71, 0x85), ((SolidColorBrush)v.FaintForeground!).Color, "Aurora faint");
        HexHost.SetTheme(FakeTheme.Daylight);
        Check.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), ((SolidColorBrush)v.Background!).Color, "Daylight editor background after the swap");
        Check.Equal(Color.FromRgb(0x7E, 0x86, 0x99), ((SolidColorBrush)v.FaintForeground!).Color, "faint brush follows too");
        Check.Equal(Color.FromArgb(0x33, 0x4F, 0x46, 0xE5), ((SolidColorBrush)v.SelectionBrush!).Color, "selection brush follows too");
        HexHost.SetTheme(FakeTheme.Aurora);
        window.Content = null;
        window.Close();
    }

    [Case]
    public static void FileOpenAndClose()
    {
        var path = HexSample.WriteSampleFile();
        using var host = new HexHost(900, 300);
        host.View.OpenFile(path);
        host.Layout();
        Check.Equal(2L * 1024 * 1024, host.View.Length);
        Check.Equal("42 65 74 74", HexFormat.ToHex(host.View.ReadBytes(0, 4)), "ReadBytes");
        host.View.Close();
        Check.Equal(0L, host.View.Length);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Check.True(exclusive.Length > 0, "file released by Close");
    }
}
