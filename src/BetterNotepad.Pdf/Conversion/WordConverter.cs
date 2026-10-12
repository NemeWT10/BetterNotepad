using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BetterNotepad.Pdf;

/// <summary>
/// PDF -> DOCX through Microsoft Word, if installed. Word is driven by late-bound COM (no interop assemblies, nothing to ship):
/// invisible instance, Documents.Open(ConfirmConversions:=False), SaveAs2(wdFormatDocumentDefault), Quit. COM objects are always released,
/// the work runs on an STA thread with a timeout, and the target file is only replaced when the conversion succeeded.
/// </summary>
public static class WordConverter
{
    private const string ProgId = "Word.Application";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>True when Word's COM class is registered (does not start Word).</summary>
    public static bool IsWordInstalled
    {
        get
        {
            try { return Type.GetTypeFromProgID(ProgId, false) != null; }
            catch { return false; }
        }
    }

    public static Task<PdfToWordResult> TryWithMsWordAsync(string pdfPath, string docxPath, TimeSpan? timeout = null, CancellationToken ct = default)
        => Task.Run(() => TryWithMsWord(pdfPath, docxPath, timeout, ct), CancellationToken.None);

    /// <summary>Blocking; call from a worker thread (or use <see cref="TryWithMsWordAsync"/>).</summary>
    public static PdfToWordResult TryWithMsWord(string pdfPath, string docxPath, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        Type? type;
        try { type = Type.GetTypeFromProgID(ProgId, false); }
        catch (Exception ex) { return Fail(PdfToWordStatus.WordNotInstalled, docxPath, ex.Message, ex); }
        if (type == null) return Fail(PdfToWordStatus.WordNotInstalled, docxPath, "Word.Application is not registered.");
        if (!File.Exists(pdfPath)) return Fail(PdfToWordStatus.Failed, docxPath, "PDF not found: " + pdfPath);
        if (ct.IsCancellationRequested) return Fail(PdfToWordStatus.Canceled, docxPath, null);

        string tmp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(docxPath)) ?? ".", "~bn_" + Guid.NewGuid().ToString("N")[..8] + ".docx");
        var start = DateTime.Now;
        var before = WordPids();
        PdfToWordResult? result = null;
        var th = new Thread(() => result = RunWord(type, Path.GetFullPath(pdfPath), tmp, docxPath)) { IsBackground = true, Name = "BetterNotepad.WordConvert" };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();

        var deadline = timeout ?? DefaultTimeout;
        var sw = Stopwatch.StartNew();
        while (!th.Join(150))
        {
            if (ct.IsCancellationRequested || sw.Elapsed > deadline)
            {
                // Word is stuck inside a COM call: the only way out is to end the invisible instance we started.
                KillNewWord(before, start);
                try { File.Delete(tmp); } catch { }
                return Fail(ct.IsCancellationRequested ? PdfToWordStatus.Canceled : PdfToWordStatus.WordTimedOut, docxPath, "Word did not finish in " + deadline.TotalSeconds.ToString("0") + " s.");
            }
        }

        var r = result ?? Fail(PdfToWordStatus.WordFailed, docxPath, "Word conversion thread ended without a result.");
        if (r.Success)
        {
            try { File.Move(tmp, docxPath, true); }
            catch (Exception ex)
            {
                try { File.Delete(tmp); } catch { }
                return Fail(PdfToWordStatus.Failed, docxPath, "Cannot write the output file: " + ex.Message, ex);
            }
        }
        else { try { File.Delete(tmp); } catch { } }
        return r;
    }

    private static PdfToWordResult RunWord(Type type, string pdf, string tmpDocx, string finalPath)
    {
        object? app = null, docs = null, doc = null;
        try
        {
            app = Activator.CreateInstance(type);
            if (app == null) return Fail(PdfToWordStatus.WordNotInstalled, finalPath, "Cannot create Word.Application.");
            Set(app, "Visible", false);
            Set(app, "DisplayAlerts", 0);              // wdAlertsNone
            TrySet(app, "AutomationSecurity", 3);      // msoAutomationSecurityForceDisable (no macros)
            docs = Get(app, "Documents");
            doc = Call(docs!, "Open",
                ["FileName", "ConfirmConversions", "ReadOnly", "AddToRecentFiles", "Visible", "NoEncodingDialog"],
                pdf, false, true, false, false, true);
            if (doc == null) return Fail(PdfToWordStatus.WordFailed, finalPath, "Word returned no document.");
            Call(doc, "SaveAs2", ["FileName", "FileFormat"], tmpDocx, 16);   // 16 = wdFormatDocumentDefault (.docx)
            Call(doc, "Close", ["SaveChanges"], 0);                          // wdDoNotSaveChanges
            Release(ref doc);
            if (!File.Exists(tmpDocx) || new FileInfo(tmpDocx).Length == 0)
                return Fail(PdfToWordStatus.WordFailed, finalPath, "Word did not produce the file.");
            return new PdfToWordResult { Status = PdfToWordStatus.Ok, Method = PdfToWordMethod.MsWord, OutputPath = finalPath };
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
            return Fail(PdfToWordStatus.WordFailed, finalPath, inner.GetType().Name + ": " + inner.Message, inner);
        }
        finally
        {
            if (doc != null)
            {
                try { Call(doc, "Close", ["SaveChanges"], 0); } catch { }
                Release(ref doc);
            }
            Release(ref docs);
            if (app != null)
            {
                try { Call(app, "Quit", ["SaveChanges"], 0); } catch { }
                Release(ref app);
            }
        }
    }

    // ---- late-bound COM helpers (IDispatch via reflection) ----

    private static object? Get(object o, string name) => o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null);
    private static void Set(object o, string name, object value) => o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, [value]);
    private static void TrySet(object o, string name, object value) { try { Set(o, name, value); } catch { } }
    private static object? Call(object o, string name, string[] names, params object[] args)
        => o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args, null, null, names);

    private static void Release<T>(ref T? o) where T : class
    {
        if (o != null && Marshal.IsComObject(o)) { try { Marshal.FinalReleaseComObject(o); } catch { } }
        o = null;
    }

    private static HashSet<int> WordPids()
    {
        var set = new HashSet<int>();
        try { foreach (var p in Process.GetProcessesByName("WINWORD")) { set.Add(p.Id); p.Dispose(); } } catch { }
        return set;
    }

    private static void KillNewWord(HashSet<int> before, DateTime startedAfter)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("WINWORD"))
            {
                try
                {
                    // Only the instance we started: new pid, started after us, no window (we run it invisible).
                    if (!before.Contains(p.Id) && p.StartTime >= startedAfter.AddSeconds(-2) && p.MainWindowHandle == 0) p.Kill();
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
                finally { p.Dispose(); }
            }
        }
        catch { }
    }

    private static PdfToWordResult Fail(PdfToWordStatus status, string output, string? detail, Exception? ex = null)
        => new() { Status = status, Method = PdfToWordMethod.None, OutputPath = output, Detail = detail, Error = ex };
}
