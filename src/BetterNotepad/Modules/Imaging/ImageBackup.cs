using System.IO;
using System.Text;
using System.Text.Json;
using BetterNotepad.Core;
using BetterNotepad.Documents;
using BetterNotepad.Imaging;

namespace BetterNotepad.Modules.Imaging;

/// <summary>What survives a restart of an edited image: the edit list with undo, the file's stamp as edited, the base copy.</summary>
internal sealed record ImageJournal(string Path, bool HasBase, long Length, DateTime WriteUtc, bool Forced, EditJournal Edits);

/// <summary>
/// Unsaved image edits on disk, next to the text backups (session\backup): "&lt;id&gt;.img.json" — the edit list, undo / redo and
/// the file's stamp — and "&lt;id&gt;.img.base" — the file as it was when editing began (a byte copy, the edits apply to it).
/// Keyed by the document id, like the text backups, so the session's own entry (path + id) finds them again.
/// </summary>
internal static class ImageBackup
{
    public static string JournalFile(Guid id) => Path.Combine(AppEnv.BackupDir, id.ToString("N") + ".img.json");

    public static string BaseFile(Guid id) => Path.Combine(AppEnv.BackupDir, id.ToString("N") + ".img.base");

    public static void Write(Guid id, ImageJournal j)
    {
        Directory.CreateDirectory(AppEnv.BackupDir);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("path", j.Path);
            w.WriteBoolean("base", j.HasBase);
            w.WriteNumber("length", j.Length);
            w.WriteString("writeUtc", j.WriteUtc.ToUniversalTime().ToString("o"));
            w.WriteBoolean("forced", j.Forced);
            w.WritePropertyName("edits");
            w.WriteRawValue(j.Edits.ToJson());
            w.WriteEndObject();
        }
        FileUtil.WriteAllBytesAtomic(JournalFile(id), ms.ToArray());
    }

    /// <summary>The journal of <paramref name="id"/>, or null (none, or unreadable — then it is removed).</summary>
    public static ImageJournal? Read(Guid id)
    {
        var file = JournalFile(id);
        if (!File.Exists(file)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
            var r = doc.RootElement;
            var stamp = DateTime.Parse(r.GetProperty("writeUtc").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind);
            return new ImageJournal(r.GetProperty("path").GetString() ?? "", r.GetProperty("base").GetBoolean(), r.GetProperty("length").GetInt64(),
                stamp.ToUniversalTime(), r.GetProperty("forced").GetBoolean(), EditJournal.FromJson(r.GetProperty("edits").GetRawText()));
        }
        catch (Exception ex)
        {
            Log.Warn($"image backup: unreadable journal {id}: {ex.Message}");
            DeleteJournal(id);
            return null;
        }
    }

    /// <summary>Copies the file as it is into the base copy (temp + rename: a half-written copy never counts).</summary>
    public static bool CopyBase(string file, Guid id)
    {
        var target = BaseFile(id);
        var tmp = target + ".tmp";
        try
        {
            Directory.CreateDirectory(AppEnv.BackupDir);
            File.Copy(file, tmp, overwrite: true);
            File.Move(tmp, target, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"image backup: base copy of {file} failed: {ex.Message}");
            TryDelete(tmp);
            return false;
        }
    }

    public static void DeleteJournal(Guid id) => TryDelete(JournalFile(id));

    public static void DeleteBase(Guid id) => TryDelete(BaseFile(id));

    public static void Delete(Guid id)
    {
        DeleteJournal(id);
        DeleteBase(id);
    }

    /// <summary>After the session restored: image backups no open image refers to (a crash between close and cleanup).</summary>
    public static void DeleteOrphans()
    {
        try
        {
            if (!Directory.Exists(AppEnv.BackupDir)) return;
            var open = DocumentManager.I.Documents.OfType<ImageDocument>().Select(d => d.Id.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(AppEnv.BackupDir, "*.img.*"))
            {
                var name = Path.GetFileName(f);
                var id = name.Split('.')[0];
                if (!open.Contains(id)) TryDelete(f);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"image backup: orphan cleanup failed: {ex.Message}");
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex)
        {
            Log.Warn($"image backup: delete {Path.GetFileName(file)} failed: {ex.Message}");
        }
    }
}
