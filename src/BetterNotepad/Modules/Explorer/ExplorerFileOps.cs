using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using BetterNotepad.Core;

namespace BetterNotepad.Modules.Explorer;

/// <summary>
/// Disk operations of the tree. Delete goes to the Recycle Bin (SHFileOperation + FOF_ALLOWUNDO, on its own STA thread so a
/// big folder never freezes the window). Test instances delete permanently (only their own fixtures), never open File
/// Explorer and never touch the real clipboard.
/// </summary>
internal static class ExplorerFileOps
{
    /// <summary>What the last "copy path" put on the clipboard (test instances only keep it here).</summary>
    public static string? LastCopied { get; private set; }

    public static Task<string?> DeleteAsync(string path, bool isDirectory, IntPtr owner)
    {
        var done = new TaskCompletionSource<string?>();
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(Delete(path, isDirectory, owner));
            }
            catch (Exception ex)
            {
                done.SetResult(ex.Message);
            }
        })
        {
            IsBackground = true,
            Name = "explorer-delete",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    /// <summary>Null on success, else the reason.</summary>
    private static string? Delete(string path, bool isDirectory, IntPtr owner)
    {
        if (AppEnv.TestMode)
        {
            if (isDirectory) Directory.Delete(path, recursive: true);
            else File.Delete(path);
            return null;
        }
        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };
        var result = SHFileOperation(ref op);
        if (op.fAnyOperationsAborted) return "cancelled";
        if (result != 0) return $"0x{result:X}";
        return File.Exists(path) || Directory.Exists(path) ? "still there" : null;
    }

    /// <summary>File Explorer with the entry selected in its folder, or (<paramref name="openInside"/>) the folder itself opened.</summary>
    public static void Reveal(string path, bool openInside)
    {
        if (AppEnv.TestMode)
        {
            Log.Info($"explorer: reveal skipped in test mode ({path})");
            return;
        }
        var args = openInside ? $"\"{path}\"" : $"/select,\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    public static void Copy(string text)
    {
        LastCopied = text;
        if (AppEnv.TestMode) return; // never the user's real clipboard
        Clipboard.SetText(text);
    }

    // ───────────── SHFileOperation ─────────────

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
