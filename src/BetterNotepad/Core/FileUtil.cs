using System.IO;
using System.Text;

namespace BetterNotepad.Core;

public static class FileUtil
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>Writes through a temp file and swaps it in, so a crash never leaves half a file.</summary>
    public static void WriteAllTextAtomic(string path, string text) => WriteAllBytesAtomic(path, Utf8NoBom.GetBytes(text));

    public static void WriteAllBytesAtomic(string path, ReadOnlySpan<byte> bytes)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Environment.ProcessId + ".tmp");
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            fs.Write(bytes);

        if (File.Exists(path))
        {
            try
            {
                File.Replace(tmp, path, null, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException)
            {
                // e.g. a different volume or a filter driver refusing ReplaceFile — fall back to a plain move
            }
            catch (UnauthorizedAccessException)
            {
                File.Delete(tmp);
                throw;
            }
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static bool PathEquals(string? a, string? b)
        => a is not null && b is not null && string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch { return path; }
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }
}
