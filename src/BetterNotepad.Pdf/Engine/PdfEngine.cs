namespace BetterNotepad.Pdf;

internal enum PdfPriority { Normal = 0, Low = 1 }

/// <summary>
/// PDFium is not thread-safe: every native call runs on this one dedicated thread. Work is queued (two priorities:
/// interactive renders first, bulk work such as search / thumbnails / conversion second) and exposed as Tasks, so the UI never blocks.
/// The engine is created on first use and can be shut down (FPDF_DestroyLibrary) once the last document is closed.
/// </summary>
internal sealed unsafe class PdfEngine
{
    private static readonly object s_gate = new();
    private static PdfEngine? s_current;
    private static int s_users;

    private readonly Thread _thread;
    private readonly object _q = new();
    private readonly Queue<IWorkItem> _normal = new();
    private readonly Queue<IWorkItem> _low = new();
    private bool _stop;
    private bool _nativeReady;

    private PdfEngine()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "BetterNotepad.Pdfium" };
        _thread.Start();
    }

    /// <summary>Gets (creating if needed) the shared engine and registers one user (document).</summary>
    public static PdfEngine Acquire()
    {
        lock (s_gate)
        {
            s_current ??= new PdfEngine();
            s_users++;
            return s_current;
        }
    }

    public void Release()
    {
        lock (s_gate) { if (s_users > 0) s_users--; }
    }

    /// <summary>Stops the engine thread and unloads PDFium state when no document is open. Returns false if documents are still open.</summary>
    public static bool Shutdown()
    {
        PdfEngine? e;
        lock (s_gate)
        {
            if (s_users > 0) return false;
            e = s_current;
            s_current = null;
        }
        e?.Stop();
        return true;
    }

    public static bool IsRunning { get { lock (s_gate) return s_current != null; } }
    public static int OpenDocuments { get { lock (s_gate) return s_users; } }

    private void Stop()
    {
        lock (_q) { _stop = true; Monitor.PulseAll(_q); }
    }

    public Task<T> Run<T>(Func<T> work, PdfPriority priority = PdfPriority.Normal, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<T>(ct);
        var item = new Item<T>(work, ct);
        lock (_q)
        {
            if (_stop) throw new ObjectDisposedException(nameof(PdfEngine));
            (priority == PdfPriority.Low ? _low : _normal).Enqueue(item);
            Monitor.Pulse(_q);
        }
        return item.Task;
    }

    public Task Run(Action work, PdfPriority priority = PdfPriority.Normal, CancellationToken ct = default)
        => Run<bool>(() => { work(); return true; }, priority, ct);

    private void Loop()
    {
        while (true)
        {
            IWorkItem item;
            lock (_q)
            {
                while (_normal.Count == 0 && _low.Count == 0)
                {
                    if (_stop) goto done;
                    Monitor.Wait(_q);
                }
                item = _normal.Count > 0 ? _normal.Dequeue() : _low.Dequeue();
            }
            try
            {
                EnsureNative();
                item.Run();
            }
            catch (Exception ex)
            {
                item.Fail(ex);
            }
        }
    done:
        if (_nativeReady)
        {
            try { Pdfium.FPDF_DestroyLibrary(); } catch { }
            _nativeReady = false;
        }
    }

    private void EnsureNative()
    {
        if (_nativeReady) return;
        Pdfium.LibraryConfig cfg = default;
        cfg.Version = 2;
        try
        {
            Pdfium.FPDF_InitLibraryWithConfig(&cfg);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            throw new PdfException("The native PDFium library (pdfium.dll) could not be loaded: " + ex.Message, ex);
        }
        _nativeReady = true;
    }

    private interface IWorkItem
    {
        void Run();
        void Fail(Exception ex);
    }

    private sealed class Item<T> : IWorkItem
    {
        private readonly Func<T> _work;
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _reg;

        public Item(Func<T> work, CancellationToken ct)
        {
            _work = work;
            if (ct.CanBeCanceled)
                _reg = ct.Register(static (s, token) => ((TaskCompletionSource<T>)s!).TrySetCanceled(token), _tcs);
        }

        public Task<T> Task => _tcs.Task;

        public void Run()
        {
            // Cancelled while queued: skip the (possibly expensive) work.
            if (_tcs.Task.IsCompleted) { _reg.Dispose(); return; }
            try { _tcs.TrySetResult(_work()); }
            catch (OperationCanceledException oce) { _tcs.TrySetCanceled(oce.CancellationToken); }
            catch (Exception ex) { _tcs.TrySetException(ex); }
            finally { _reg.Dispose(); }
        }

        public void Fail(Exception ex)
        {
            _tcs.TrySetException(ex);
            _reg.Dispose();
        }
    }
}
