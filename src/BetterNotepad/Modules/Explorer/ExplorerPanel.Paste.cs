using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Documents.Files;
using BetterNotepad.UI;

namespace BetterNotepad.Modules.Explorer;

/// <summary>
/// Ctrl+V in the tree: files copied in Windows Explorer land in the selected folder (cut ones are moved), a bare picture on
/// the clipboard (a screenshot) is saved there as PNG. Existing names get " (2)", " (3)"… like Explorer.
/// </summary>
public sealed partial class ExplorerPanel
{
    private const int DropEffectMove = 2;

    /// <summary>Something Ctrl+V can drop into a folder (never reads the clipboard in test mode).</summary>
    internal static bool ClipboardHasPaste()
    {
        if (AppEnv.TestMode) return false;
        try
        {
            return Clipboard.ContainsFileDropList() || Clipboard.ContainsImage();
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    public async Task PasteAsync(ExplorerNode? where = null)
    {
        if (_tree is null || AppEnv.TestMode) return;
        var folder = where is { IsDirectory: true } ? where : where?.Parent ?? TargetFolder();
        List<string>? files = null;
        var move = false;
        BitmapSource? image = null;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                files = Clipboard.GetFileDropList().Cast<string>().ToList();
                move = IsCut(Clipboard.GetDataObject());
            }
            else if (Clipboard.ContainsImage())
            {
                image = ReadImage();
            }
        }
        catch (ExternalException ex)
        {
            Log.Warn("explorer: clipboard busy: " + ex.Message);
            _window.Toasts.Show(Loc.T("copy.clipboardBusy"), ToastKind.Error);
            return;
        }

        if (files is { Count: > 0 }) await PasteFilesAsync(folder, files, move);
        else if (image is not null) await PasteImageAsync(folder, image);
        else _window.Toasts.Show(Loc.T("explorer.pasteNothing"), ToastKind.Info);
    }

    /// <summary>Copies (or moves) files and folders into <paramref name="folder"/>; returns the new paths.</summary>
    internal async Task<List<string>> PasteFilesAsync(ExplorerNode folder, IReadOnlyList<string> sources, bool move)
    {
        var dest = folder.FullPath;
        var done = new List<string>();
        var moved = new List<(string From, string To)>();
        string? error = null;
        string? intoItself = null;
        if (move)
            foreach (var s in sources)
                if (Directory.Exists(s)) FileWatchService.I.ReleaseUnder(s); // our own watchers inside would block the move

        await Task.Run(() =>
        {
            foreach (var raw in sources)
            {
                var src = raw.TrimEnd('\\', '/');
                try
                {
                    var isDir = Directory.Exists(src);
                    if (!isDir && !File.Exists(src)) continue;
                    var name = Path.GetFileName(src);
                    if (isDir && ExplorerRules.IsSameOrUnder(src, dest))
                    {
                        intoItself = name;
                        continue;
                    }
                    var sameFolder = string.Equals(Path.GetDirectoryName(src)?.TrimEnd('\\', '/'), dest.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
                    if (move && sameFolder) continue; // cut and pasted where it already is: nothing to do
                    var target = UniqueTarget(dest, name, isDir);
                    if (move)
                    {
                        if (isDir) MoveDirectory(src, target);
                        else File.Move(src, target);
                        moved.Add((src, target));
                    }
                    else
                    {
                        if (isDir) CopyDirectory(src, target);
                        else File.Copy(src, target);
                    }
                    done.Add(target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"explorer: paste {src} failed: {ex.Message}");
                    error = ex.Message;
                }
            }
        });

        foreach (var (from, to) in moved) _window.OnExplorerRenamed(from, to); // open tabs follow
        if (move && done.Count > 0 && !AppEnv.TestMode)
        {
            try { Clipboard.Clear(); } // like Explorer: a cut is used up by its paste
            catch (ExternalException) { }
        }

        if (intoItself is not null) _window.Toasts.Show(Loc.T("explorer.pasteIntoItself", intoItself), ToastKind.Warning);
        if (error is not null) _window.Toasts.Show(Loc.T("explorer.pasteFailed", error), ToastKind.Error);
        if (done.Count > 0)
        {
            await ShowPasted(folder, done[^1]);
            _window.Toasts.Show(Loc.Plural(move ? "explorer.moved" : "explorer.pasted", done.Count), ToastKind.Success);
        }
        return done;
    }

    /// <summary>A picture without a file (screenshot): saved as "obraz 2026-10-16 14-05-33.png" in the folder.</summary>
    internal async Task<string?> PasteImageAsync(ExplorerNode folder, BitmapSource image)
    {
        var name = Loc.T("explorer.pastedImageName", DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss")) + ".png";
        var target = UniqueTarget(folder.FullPath, name, isDirectory: false);
        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(ms);
            bytes = ms.ToArray();
        }
        try
        {
            await File.WriteAllBytesAsync(target, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"explorer: saving the pasted picture failed: {ex.Message}");
            _window.Toasts.Show(Loc.T("explorer.pasteFailed", ex.Message), ToastKind.Error);
            return null;
        }
        await ShowPasted(folder, target);
        _window.Toasts.Show(Loc.T("explorer.pastedImage", Path.GetFileName(target)), ToastKind.Success);
        return target;
    }

    /// <summary>The folder opens (re-listed, the watcher may not have spoken yet) and the pasted entry is selected.</summary>
    private async Task ShowPasted(ExplorerNode folder, string path)
    {
        if (_tree is null) return;
        if (folder != _tree.Root && !folder.IsExpanded) await ExpandAsync(folder);
        await _tree.ListAsync(folder);
        await RevealAsync(path, center: false);
    }

    /// <summary>"name.ext", else "name (2).ext", "name (3).ext"…</summary>
    internal static string UniqueTarget(string folder, string name, bool isDirectory)
    {
        var candidate = Path.Combine(folder, name);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        var stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        var ext = isDirectory ? "" : Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            candidate = Path.Combine(folder, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    private static bool IsCut(IDataObject? data)
    {
        try
        {
            if (data?.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
            {
                var buf = new byte[4];
                ms.Position = 0;
                _ = ms.Read(buf, 0, 4);
                return (BitConverter.ToInt32(buf, 0) & DropEffectMove) != 0;
            }
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            Log.Warn("explorer: drop effect unreadable: " + ex.Message);
        }
        return false;
    }

    /// <summary>The PNG the source put on the clipboard (keeps transparency), else the bitmap.</summary>
    private static BitmapSource? ReadImage()
    {
        try
        {
            if (Clipboard.GetData("PNG") is MemoryStream png)
            {
                png.Position = 0;
                var frame = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                frame.Freeze();
                return frame;
            }
        }
        catch (Exception ex) when (ex is ExternalException or NotSupportedException or FileFormatException)
        {
            Log.Warn("explorer: clipboard PNG unreadable: " + ex.Message);
        }
        return Clipboard.GetImage();
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // links are not followed
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }

    private static void MoveDirectory(string source, string target)
    {
        try
        {
            Directory.Move(source, target);
        }
        catch (IOException) when (!string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
        {
            CopyDirectory(source, target); // another drive: copy, then remove the original
            Directory.Delete(source, recursive: true);
        }
    }
}
