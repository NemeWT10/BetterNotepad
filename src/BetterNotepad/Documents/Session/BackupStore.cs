using System.IO;
using System.Text;
using BetterNotepad.Core;

namespace BetterNotepad.Documents.Session;

/// <summary>Unsaved buffers on disk (session\backup\&lt;id&gt;.txt, UTF-8). Survives a restart / crash.</summary>
public static class BackupStore
{
    public static string FileFor(Guid id) => Path.Combine(AppEnv.BackupDir, id.ToString("N") + ".txt");

    public static void Write(Guid id, string text)
    {
        Directory.CreateDirectory(AppEnv.BackupDir);
        FileUtil.WriteAllBytesAtomic(FileFor(id), new UTF8Encoding(false).GetBytes(text));
    }

    public static void Delete(Guid id)
    {
        try
        {
            var f = FileFor(id);
            if (File.Exists(f)) File.Delete(f);
        }
        catch (Exception ex)
        {
            Log.Warn($"backup: delete failed for {id}: {ex.Message}");
        }
    }

    public static bool Exists(Guid id) => File.Exists(FileFor(id));

    /// <summary>Removes backups no session entry refers to.</summary>
    public static void DeleteOrphans(IEnumerable<Guid> keep)
    {
        try
        {
            var keepNames = keep.Select(k => k.ToString("N") + ".txt").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(AppEnv.BackupDir, "*.txt"))
                if (!keepNames.Contains(Path.GetFileName(f))) File.Delete(f);
        }
        catch (Exception ex)
        {
            Log.Warn($"backup: orphan cleanup failed: {ex.Message}");
        }
    }
}
