using System.IO;
using Microsoft.Win32.SafeHandles;

namespace BetterNotepad.Hex;

/// <summary>Random access, read only bytes behind a hex view. UI thread use; implementations are also safe for occasional use from one extra thread.</summary>
public interface IHexSource : IDisposable
{
    long Length { get; }

    /// <summary>Reads up to <paramref name="buffer"/>.Length bytes starting at <paramref name="offset"/>; returns how many were read (fewer at the end of the data).</summary>
    int Read(long offset, Span<byte> buffer);
}

/// <summary>Bytes held in memory (tests, small buffers, clipboard data).</summary>
public sealed class MemoryHexSource(ReadOnlyMemory<byte> data) : IHexSource
{
    public long Length => data.Length;

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= data.Length) return 0;
        var n = (int)Math.Min(buffer.Length, data.Length - offset);
        data.Span.Slice((int)offset, n).CopyTo(buffer);
        return n;
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// A file read through a handle with a small page cache (64 KB pages, 32 of them = 2 MB at most), so a multi-gigabyte file
/// costs only what is on screen. Opened with sharing, so other programs keep working on the file; a file that is
/// truncated meanwhile simply reads as shorter (use <see cref="Refresh"/> to pick up the new length).
/// </summary>
public sealed class FileHexSource : IHexSource
{
    private const int PageSize = 64 * 1024;
    private const int MaxPages = 32;

    private readonly SafeFileHandle _handle;
    private readonly Dictionary<long, byte[]> _pages = new();
    private readonly LinkedList<long> _lru = new();
    private readonly object _gate = new();
    private long _length;

    private FileHexSource(string path, SafeFileHandle handle)
    {
        Path = path;
        _handle = handle;
        _length = RandomAccess.GetLength(handle);
    }

    public string Path { get; }

    public long Length
    {
        get
        {
            lock (_gate) return _length;
        }
    }

    public static FileHexSource Open(string path)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        return new FileHexSource(path, handle);
    }

    /// <summary>Re-reads the file length and drops cached pages (call after the file changed on disk).</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            _pages.Clear();
            _lru.Clear();
            _length = RandomAccess.GetLength(_handle);
        }
    }

    public int Read(long offset, Span<byte> buffer)
    {
        lock (_gate)
        {
            if (offset < 0 || offset >= _length || buffer.Length == 0) return 0;
            var total = 0;
            var want = (int)Math.Min(buffer.Length, _length - offset);
            while (total < want)
            {
                var pos = offset + total;
                var pageIndex = pos / PageSize;
                var page = GetPage(pageIndex, out var valid);
                var inPage = (int)(pos - pageIndex * PageSize);
                if (inPage >= valid) break; // the file shrank under us
                var n = Math.Min(want - total, valid - inPage);
                page.AsSpan(inPage, n).CopyTo(buffer[total..]);
                total += n;
            }

            return total;
        }
    }

    private byte[] GetPage(long index, out int valid)
    {
        if (_pages.TryGetValue(index, out var page))
        {
            _lru.Remove(index);
            _lru.AddFirst(index);
            valid = (int)Math.Min(PageSize, Math.Max(0, _length - index * PageSize));
            return page;
        }

        page = new byte[PageSize];
        var read = RandomAccess.Read(_handle, page, index * PageSize);
        valid = read;
        if (_pages.Count >= MaxPages)
        {
            var oldest = _lru.Last!.Value;
            _lru.RemoveLast();
            _pages.Remove(oldest);
        }

        _pages[index] = page;
        _lru.AddFirst(index);
        return page;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _pages.Clear();
            _lru.Clear();
        }

        _handle.Dispose();
    }
}
