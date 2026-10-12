using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace BetterNotepad.Core;

/// <summary>
/// "Save as administrator": the app re-launches itself elevated with --elevated-save &lt;temp&gt; &lt;target&gt;,
/// that copy only moves the file into place and exits. Windows shows the UAC prompt.
/// </summary>
public static class ElevatedSave
{
    public const string Flag = "--elevated-save";

    /// <summary>Runs in the normal app: starts the elevated helper and waits for it.</summary>
    public static bool Run(string tempFile, string target)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.ArgumentList.Add(Flag);
            psi.ArgumentList.Add(tempFile);
            psi.ArgumentList.Add(target);
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(60_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            // the user declined the UAC prompt
            return false;
        }
    }

    /// <summary>Runs in the elevated helper process (no UI).</summary>
    public static int Helper(string[] args)
    {
        var i = Array.IndexOf(args, Flag);
        if (i < 0 || i + 2 >= args.Length) return 2;
        var temp = args[i + 1];
        var target = args[i + 2];
        try
        {
            if (!File.Exists(temp)) return 3;
            if (File.Exists(target))
            {
                var attrs = File.GetAttributes(target);
                if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(target, attrs & ~FileAttributes.ReadOnly);
                File.Copy(temp, target, overwrite: true);
                if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(target, attrs);
            }
            else
            {
                File.Copy(temp, target);
            }
            return 0;
        }
        catch
        {
            return 1;
        }
    }
}
