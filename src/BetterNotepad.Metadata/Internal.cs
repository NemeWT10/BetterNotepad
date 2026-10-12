using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace BetterNotepad.Metadata;

internal static class Crc32
{
    private static readonly uint[] Table = Build();
    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
    public static uint Compute(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b = default)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var x in a) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}

/// <summary>Read-only memory-mapped file exposed as <see cref="ReadOnlyMemory{T}"/> (no heap copy for big files).</summary>
internal sealed unsafe class MappedFile : IDisposable
{
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private byte* _ptr;
    private PtrManager? _mgr;

    public ReadOnlyMemory<byte> Memory { get; private set; }
    public long Length { get; }

    private MappedFile(long length) { Length = length; }

    public static MappedFile Open(string path)
    {
        FileStream fs;
        try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None); }
        catch (FileNotFoundException ex) { throw new MetadataException(MetadataErrorCode.NotFound, ex.Message, ex); }
        catch (DirectoryNotFoundException ex) { throw new MetadataException(MetadataErrorCode.NotFound, ex.Message, ex); }
        catch (IOException ex) { throw new MetadataException(MetadataErrorCode.Io, ex.Message, ex); }
        catch (UnauthorizedAccessException ex) { throw new MetadataException(MetadataErrorCode.Io, ex.Message, ex); }

        long len = fs.Length;
        var mf = new MappedFile(len);
        if (len == 0) { fs.Dispose(); mf.Memory = ReadOnlyMemory<byte>.Empty; return mf; }
        if (len > int.MaxValue - 64) { fs.Dispose(); throw new MetadataException(MetadataErrorCode.TooLarge, "File is larger than 2 GB."); }
        try
        {
            mf._mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
            mf._view = mf._mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* p = null;
            mf._view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            mf._ptr = p + mf._view.PointerOffset;
            mf._mgr = new PtrManager(mf._ptr, (int)len);
            mf.Memory = mf._mgr.Memory;
            return mf;
        }
        catch { mf.Dispose(); fs.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_view != null)
        {
            if (_ptr != null) _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
        }
        _mmf?.Dispose();
        _view = null; _mmf = null; _ptr = null; _mgr = null;
        Memory = ReadOnlyMemory<byte>.Empty;
    }

    private sealed class PtrManager : MemoryManager<byte>
    {
        private readonly byte* _p; private readonly int _len;
        public PtrManager(byte* p, int len) { _p = p; _len = len; }
        public override Span<byte> GetSpan() => new(_p, _len);
        public override MemoryHandle Pin(int elementIndex = 0) => new(_p + elementIndex);
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}

internal static class Bin
{
    public static ushort U16(ReadOnlySpan<byte> d, int o, bool le) => le ? (ushort)(d[o] | d[o + 1] << 8) : (ushort)(d[o] << 8 | d[o + 1]);
    public static uint U32(ReadOnlySpan<byte> d, int o, bool le) =>
        le ? (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24)
           : (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);
    public static void PutU16(Span<byte> d, int o, ushort v, bool le)
    {
        if (le) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); } else { d[o] = (byte)(v >> 8); d[o + 1] = (byte)v; }
    }
    public static void PutU32(Span<byte> d, int o, uint v, bool le)
    {
        if (le) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); d[o + 2] = (byte)(v >> 16); d[o + 3] = (byte)(v >> 24); }
        else { d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v; }
    }
    public static bool StartsWith(ReadOnlySpan<byte> d, int offset, string ascii)
    {
        if (offset < 0 || offset + ascii.Length > d.Length) return false;
        for (int i = 0; i < ascii.Length; i++) if (d[offset + i] != (byte)ascii[i]) return false;
        return true;
    }
}

internal static class Txt
{
    public const int MaxValue = 2000;
    public static readonly Encoding Latin1 = Encoding.Latin1;
    public static string Clip(string s)
    {
        if (s.Length <= MaxValue) return s;
        return s.Substring(0, MaxValue) + "…";
    }
    public static string Iso(DateTime d) => d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    public static string Iso(DateTimeOffset d) => d.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    public static string Num(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);
    public static string TrimNul(string s) => s.TrimEnd('\0');

    public static bool TryParseIso(string s, out DateTimeOffset result)
    {
        s = s.Trim();
        // Without an explicit offset the value is interpreted as local time.
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out result);
    }

    public static bool TryBool(string? s, out bool v)
    {
        v = false;
        if (s == null) return false;
        s = s.Trim();
        if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1") { v = true; return true; }
        if (s.Equals("false", StringComparison.OrdinalIgnoreCase) || s == "0") { v = false; return true; }
        return false;
    }

    /// <summary>Inflates a zlib stream; on corrupt tails returns what was decoded so far.</summary>
    public static byte[] Inflate(ReadOnlySpan<byte> data, int maxBytes = 256 * 1024 * 1024)
    {
        var ms = new MemoryStream();
        try
        {
            using var src = new MemoryStream(data.ToArray());
            using var z = new ZLibStream(src, CompressionMode.Decompress);
            var buf = new byte[16384];
            int n;
            while ((n = z.Read(buf, 0, buf.Length)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > maxBytes) throw new MetadataException(MetadataErrorCode.TooLarge, "Decoded stream too large.");
            }
        }
        catch (InvalidDataException) { }
        catch (EndOfStreamException) { }
        return ms.ToArray();
    }
}

/// <summary>Read-only seekable stream over memory (lets ZipArchive read a mapped file / byte array without a copy).</summary>
internal sealed class ReadOnlyMemoryStream : Stream
{
    private readonly ReadOnlyMemory<byte> _m;
    private long _pos;
    public ReadOnlyMemoryStream(ReadOnlyMemory<byte> m) { _m = m; }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _m.Length;
    public override long Position { get => _pos; set => _pos = value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : value; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        if (_pos >= _m.Length) return 0;
        int n = (int)Math.Min(buffer.Length, _m.Length - _pos);
        _m.Span.Slice((int)_pos, n).CopyTo(buffer);
        _pos += n;
        return n;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        long np = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => _m.Length + offset };
        if (np < 0) throw new IOException("Seek before start.");
        _pos = np;
        return np;
    }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Format sniffing by content.</summary>
internal static class FormatDetector
{
    public static MetadataFormat Detect(string path)
    {
        Span<byte> head = stackalloc byte[1024];
        int n;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            n = fs.Read(head);
        }
        catch (FileNotFoundException ex) { throw new MetadataException(MetadataErrorCode.NotFound, ex.Message, ex); }
        catch (DirectoryNotFoundException ex) { throw new MetadataException(MetadataErrorCode.NotFound, ex.Message, ex); }
        catch (UnauthorizedAccessException ex) { throw new MetadataException(MetadataErrorCode.Io, ex.Message, ex); }
        catch (IOException ex) { throw new MetadataException(MetadataErrorCode.Io, ex.Message, ex); }
        return DetectHead(head.Slice(0, n), () => OfficeFormat.IsOfficePackage(path));
    }

    public static MetadataFormat Detect(ReadOnlyMemory<byte> data, string? extension = null) =>
        DetectHead(data.Span.Slice(0, Math.Min(1024, data.Length)), () => OfficeFormat.IsOfficePackage(data, extension));

    private static MetadataFormat DetectHead(ReadOnlySpan<byte> h, Func<bool> isOffice)
    {
        int n = h.Length;
        if (n >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return MetadataFormat.Jpeg;
        if (n >= 8 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47 && h[4] == 0x0D && h[5] == 0x0A && h[6] == 0x1A && h[7] == 0x0A) return MetadataFormat.Png;
        if (n >= 12 && Bin.StartsWith(h, 0, "RIFF") && Bin.StartsWith(h, 8, "WEBP")) return MetadataFormat.WebP;
        if (h.IndexOf("%PDF-"u8) >= 0) return MetadataFormat.Pdf;
        if (n >= 4 && h[0] == (byte)'P' && h[1] == (byte)'K' && (h[2] == 3 || h[2] == 5) && isOffice()) return MetadataFormat.Office;
        return MetadataFormat.Unknown;
    }
}

/// <summary>Atomic replace of a file by a finished temp file, restoring attributes, times and the Zone.Identifier stream.</summary>
internal static class AtomicFile
{
    public const string ZoneStream = ":Zone.Identifier";

    public sealed class Snapshot
    {
        public DateTime Created, Modified, Accessed;
        public FileAttributes Attributes;
        public byte[]? Zone;
    }

    public static string NewTemp(string target)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(target))!;
        return Path.Combine(dir, ".bnmeta-" + Guid.NewGuid().ToString("N") + ".tmp");
    }

    public static Snapshot Capture(string path)
    {
        var s = new Snapshot
        {
            Created = File.GetCreationTimeUtc(path),
            Modified = File.GetLastWriteTimeUtc(path),
            Accessed = File.GetLastAccessTimeUtc(path),
            Attributes = File.GetAttributes(path),
        };
        s.Zone = ReadZone(path);
        return s;
    }

    public static byte[]? ReadZone(string path)
    {
        try { return File.ReadAllBytes(path + ZoneStream); }
        catch { return null; }
    }

    public static void EnsureWritable(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            throw new MetadataException(MetadataErrorCode.ReadOnly, "The file is read-only.");
    }

    /// <param name="keepZone">Re-create the Zone.Identifier stream after the swap.</param>
    /// <param name="resetTimes">Use "now" instead of the original times.</param>
    public static void Commit(string temp, string target, Snapshot snap, bool keepBackup, bool keepZone, bool resetTimes)
    {
        string? bak = keepBackup ? target + ".bak" : null;
        try
        {
            try { File.Replace(temp, target, bak, true); }
            catch (Exception ex) when (ex is PlatformNotSupportedException || ex is IOException || ex is UnauthorizedAccessException)
            {
                // Replace can fail on exotic file systems; fall back to copy + move.
                if (bak != null) File.Copy(target, bak, true);
                File.Move(temp, target, true);
            }
        }
        catch (UnauthorizedAccessException ex) { Cleanup(temp); throw new MetadataException(MetadataErrorCode.ReadOnly, ex.Message, ex); }
        catch (IOException ex) { Cleanup(temp); throw new MetadataException(MetadataErrorCode.Io, ex.Message, ex); }

        try { File.SetAttributes(target, snap.Attributes); } catch { }
        try
        {
            if (keepZone && snap.Zone != null) File.WriteAllBytes(target + ZoneStream, snap.Zone);
            else if (!keepZone) RemoveZone(target);
        }
        catch { }
        ApplyTimes(target, snap, resetTimes);
    }

    public static void ApplyTimes(string path, Snapshot snap, bool reset)
    {
        try
        {
            if (reset)
            {
                var now = DateTime.UtcNow;
                File.SetCreationTimeUtc(path, now); File.SetLastWriteTimeUtc(path, now); File.SetLastAccessTimeUtc(path, now);
            }
            else
            {
                File.SetCreationTimeUtc(path, snap.Created); File.SetLastWriteTimeUtc(path, snap.Modified); File.SetLastAccessTimeUtc(path, snap.Accessed);
            }
        }
        catch { }
    }

    public static bool RemoveZone(string path)
    {
        try
        {
            if (!File.Exists(path + ZoneStream)) return false;
            File.Delete(path + ZoneStream);
            return true;
        }
        catch { return false; }
    }

    public static void Cleanup(string temp) { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
}
