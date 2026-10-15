using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BetterNotepad.Documents;
using BetterNotepad.Modules.Pdf;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Core;

/// <summary>
/// Test-mode memory / CPU probe for the tray budget (≤ 50 MB, 0 % CPU while idle). <c>BETTERNOTEPAD_PERF=&lt;report file&gt;</c>
/// with <c>--test-mode</c> runs one scenario (<c>BETTERNOTEPAD_PERF_SCENARIO</c>), appends the numbers to the report and quits:
/// <c>fresh</c> = start → hide to the tray → idle → restore; <c>files</c> = 10 code files + Markdown + PDF used first; <c>code</c> = 10 code files only;
/// <c>hidden</c> = autostart straight into the tray, shown later. Timeline in the tray: +5 s, +30 s (all trims done), then an idle
/// window of <c>BETTERNOTEPAD_PERF_IDLE</c> seconds (30) with CPU time and dispatcher operations, then restore.
/// </summary>
internal static partial class PerfProbe
{
    public static bool Requested => AppEnv.TestMode && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BETTERNOTEPAD_PERF"));

    private static string Scenario => Environment.GetEnvironmentVariable("BETTERNOTEPAD_PERF_SCENARIO") ?? "fresh";

    /// <summary>The "hidden" scenario starts the main window in the tray, as autostart does.</summary>
    public static bool StartHidden => Requested && Scenario == "hidden";

    private static int IdleSeconds => int.TryParse(Environment.GetEnvironmentVariable("BETTERNOTEPAD_PERF_IDLE"), out var s) ? s : 30;

    private static readonly StringBuilder Report = new();

    public static void Start()
    {
        var app = App.Current;
        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () =>
        {
            try
            {
                await Run();
            }
            catch (Exception ex)
            {
                Line("PERF FAIL: " + ex);
            }
            Flush();
            app.Quit();
        });
    }

    private static async Task Run()
    {
        var w = App.Current.MainAppWindow ?? throw new InvalidOperationException("no main window");
        var startup = DateTime.Now - Process.GetCurrentProcess().StartTime;
        var flavor = $"{(Debugger.IsAttached ? "debugger " : "")}{BuildFlavor()}";
        Line($"## {Scenario} — {flavor} — {DateTime.Now:yyyy-MM-dd HH:mm:ss} — {AppInfo.Version}");
        Line($"startup to first idle: {startup.TotalMilliseconds:0} ms (window visible: {w.IsVisible})");
        Line(Mem.Header);

        await Settle(w, 2000);
        Record(Scenario == "hidden" ? "start (hidden)" : "start (visible)");
        if (Scenario is "files" or "code")
        {
            await UseFiles(w, extras: Scenario == "files");
            Record(Scenario == "files" ? "files used" : "10 code tabs used");
        }
        var views = WeakViews(w);
        var before = ViewState(w);
        EnterTrayLikeTheApp(w, startedHidden: Scenario == "hidden");

        await Wait(5000);
        Record("tray +5 s");
        var kept = LiveViews(w);
        var leaked = views.Where(v => v.Ref.Target is { } t && !kept.Any(k => ReferenceEquals(k, t))).Select(v => v.Name).ToList();
        Line($"views: {views.Count} before the tray, {kept.Count} kept; closed but still alive after the trim GC: {leaked.Count} {string.Join(", ", leaked)}");
        Line(Breakdown(top: 6));
        await Wait(25000); // the last trim runs 25 s after hiding
        Record("tray +30 s");
        await Idle(w);
        Record($"tray +{30 + IdleSeconds} s");
        Line(Breakdown());

        await Restore(w);
        await Settle(w, 1500);
        Record("restored +1.5 s");
        var after = ViewState(w);
        Line($"view state before/after: {before} / {after} → {(before == after ? "same" : "DIFFERENT")}");
    }

    private static string BuildFlavor()
    {
#if DEBUG
        const string config = "Debug";
#else
        const string config = "Release";
#endif
        var single = File.Exists(Path.Combine(AppContext.BaseDirectory, "BetterNotepad.dll")) ? "" : " single-file";
        return config + single;
    }

    /// <summary>What a click on X / the tray icon does in the real app (the test instance has no tray icon).</summary>
    private static void EnterTrayLikeTheApp(MainWindow w, bool startedHidden)
    {
        var sw = Stopwatch.StartNew();
        if (!startedHidden) w.HideToTray(); // a start into the tray settles by itself (SystemModule.Start → TrayRest.Settle)
        // builds before TrayRest trimmed only through the tray icon's service, which a test instance does not have
        if (typeof(PerfProbe).Assembly.GetType("BetterNotepad.UI.Tray.TrayRest") is null && typeof(UI.Tray.TrayService).GetMethod("TrimMemory") is { } trim)
            trim.Invoke(Activator.CreateInstance(typeof(UI.Tray.TrayService)), null);
        Line($"hide to tray: {sw.Elapsed.TotalMilliseconds:0} ms");
    }

    private static async Task Restore(MainWindow w)
    {
        var rendered = new TaskCompletionSource();
        void OnRendering(object? s, EventArgs e) => rendered.TrySetResult();
        var sw = Stopwatch.StartNew();
        w.BringToFront();
        var sync = sw.Elapsed;
        CompositionTarget.Rendering += OnRendering;
        await Task.WhenAny(rendered.Task, Wait(5000));
        CompositionTarget.Rendering -= OnRendering;
        var frame = sw.Elapsed;
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Line($"restore: show {sync.TotalMilliseconds:0} ms, first frame {frame.TotalMilliseconds:0} ms, idle {sw.Elapsed.TotalMilliseconds:0} ms, active view {(w.ActiveView is null ? "MISSING" : w.ActiveView.GetType().Name)}");
    }

    /// <summary>Idle window in the tray: CPU time and every dispatcher operation that ran (should be none).</summary>
    private static async Task Idle(MainWindow w)
    {
        // per-thread times first: the snapshot itself touches memory, so the window's own numbers are read after it
        var threads0 = ThreadTimes();
        var posted = new List<string>(64);
        var count = 0;
        void OnPosted(object? s, DispatcherHookEventArgs e)
        {
            count++;
            if (posted.Count < 40) posted.Add(Describe(e.Operation));
        }
        w.Dispatcher.Hooks.OperationPosted += OnPosted;
        var cpu0 = CpuMs();
        var start = Mem.Counters();
        var sw = Stopwatch.StartNew();
        await Wait(IdleSeconds * 1000);
        var end = Mem.Counters();
        var cpu = CpuMs() - cpu0;
        w.Dispatcher.Hooks.OperationPosted -= OnPosted;
        Line($"idle {sw.Elapsed.TotalSeconds:0} s: CPU {cpu:0.0} ms ({cpu / sw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100:0.000} % of the machine), "
            + $"private WS {start.PrivateWorkingSetSize / 1048576.0:0.0} → {end.PrivateWorkingSetSize / 1048576.0:0.0} MB, page faults {end.PageFaultCount - start.PageFaultCount}, "
            + $"dispatcher operations {count} (the probe's own wake-up: 1–2)");
        foreach (var op in posted.GroupBy(o => o)) Line($"  op ×{op.Count()}: {op.Key}");
        var now = ThreadTimes();
        foreach (var (id, ms) in now.Select(t => (t.Key, t.Value - threads0.GetValueOrDefault(t.Key))).Where(t => t.Item2 > 0).OrderByDescending(t => t.Item2))
            Line($"  thread {id} {ThreadName(id)}: {ms:0.0} ms{(threads0.ContainsKey(id) ? "" : " (new)")}");
        foreach (var gone in threads0.Keys.Where(k => !now.ContainsKey(k))) Line($"  thread {gone} exited");
    }

    /// <summary>Thread description (.NET names its threads) and the module of its start address.</summary>
    private static string ThreadName(int id)
    {
        var h = OpenThread(0x0040 | 0x0800 /* QUERY_INFORMATION | QUERY_LIMITED_INFORMATION */, false, (uint)id);
        if (h == 0) return "?";
        try
        {
            var name = "";
            if (GetThreadDescription(h, out var p) >= 0 && p != 0)
            {
                name = Marshal.PtrToStringUni(p) ?? "";
                LocalFree(p);
            }
            var module = NtQueryInformationThread(h, 9 /* ThreadQuerySetWin32StartAddress */, out var start, nint.Size, out _) == 0 ? MappedName(GetCurrentProcess(), start) : "?";
            return $"[{(name.Length > 0 ? name + " " : "")}{module}]";
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static double CpuMs()
    {
        GetProcessTimes(GetCurrentProcess(), out _, out _, out var kernel, out var user);
        return (kernel + user) / 10000.0;
    }

    /// <summary>CPU time per thread of this process (native snapshot: no managed allocations that would touch the heap).</summary>
    private static Dictionary<int, double> ThreadTimes()
    {
        var d = new Dictionary<int, double>();
        var pid = Environment.ProcessId;
        var snap = CreateToolhelp32Snapshot(4 /* TH32CS_SNAPTHREAD */, 0);
        if (snap == -1) return d;
        try
        {
            var e = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
            for (var ok = Thread32First(snap, ref e); ok; ok = Thread32Next(snap, ref e))
            {
                if (e.th32OwnerProcessID != pid) continue;
                var h = OpenThread(0x0800 /* THREAD_QUERY_LIMITED_INFORMATION */, false, e.th32ThreadID);
                if (h == 0) continue;
                if (GetThreadTimes(h, out _, out _, out var kernel, out var user)) d[(int)e.th32ThreadID] = (kernel + user) / 10000.0;
                CloseHandle(h);
            }
        }
        finally
        {
            CloseHandle(snap);
        }
        return d;
    }

    /// <summary>What a dispatcher operation will run (a timer: its Tick handlers) — for finding work that should not be there.</summary>
    internal static string Describe(DispatcherOperation op)
    {
        try
        {
            var m = typeof(DispatcherOperation).GetField("_method", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(op) as Delegate;
            if (m?.Target is DispatcherTimer t
                && typeof(DispatcherTimer).GetField("Tick", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(t) is EventHandler tick)
                return $"{op.Priority} timer {t.Interval.TotalMilliseconds} ms → " + string.Join(", ", tick.GetInvocationList().Select(x => $"{x.Method.DeclaringType?.Name}.{x.Method.Name}"));
            return $"{op.Priority} {m?.Method.DeclaringType?.Name}.{m?.Method.Name}";
        }
        catch (Exception ex)
        {
            return op.Priority + " ? " + ex.Message;
        }
    }

    /// <summary>A wait on the UI thread's own timer: no thread-pool thread wakes up (and later retires) because of the probe.</summary>
    private static Task Wait(int ms)
    {
        var done = new TaskCompletionSource();
        var t = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            done.TrySetResult();
        };
        t.Start();
        return done.Task;
    }

    private static async Task Settle(MainWindow w, int ms)
    {
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Wait(ms);
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static List<(string Name, WeakReference Ref)> WeakViews(MainWindow w) =>
        LiveViews(w).Select(v => ($"{v.GetType().Name}({v.Document.Title})", new WeakReference(v))).ToList();

    /// <summary>Views alive in the window's groups (private fields: the probe also runs against older builds for before/after numbers).</summary>
    private static List<Documents.Views.IDocumentView> LiveViews(MainWindow w)
    {
        var groups = typeof(MainWindow).GetField("_groupViews", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(w) as System.Collections.IDictionary;
        var field = typeof(UI.Controls.TabGroupView).GetField("_views", BindingFlags.NonPublic | BindingFlags.Instance);
        return groups?.Values.Cast<object>().SelectMany(g => (field?.GetValue(g) as System.Collections.IDictionary)?.Values.Cast<Documents.Views.IDocumentView>() ?? []).ToList() ?? [];
    }

    private static string ViewState(MainWindow w)
    {
        var doc = w.ActiveDocument;
        w.CaptureViewState();
        return doc is TextDocument t
            ? $"{t.Title} caret {t.CaretOffset} scroll {t.ScrollY:0} dirty {t.IsDirty}"
            : doc?.Title ?? "none";
    }

    // ───────────── files scenario ─────────────

    private static async Task UseFiles(MainWindow w, bool extras)
    {
        var dir = Path.Combine(AppEnv.LocalDir, "perf-fixtures");
        Directory.CreateDirectory(dir);
        var cs = Fixture("NoteBook.cs");
        var py = Fixture("tools.py");
        var files = new List<string>();
        string Write(string name, string text, int times)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, string.Join("\n", Enumerable.Repeat(text, times)), Encoding.UTF8);
            files.Add(path);
            return path;
        }
        Write("f01.cs", cs, 14);
        Write("f02.py", py, 22);
        Write("f03.js", cs, 14);
        Write("f04.ts", cs, 14);
        Write("f05.cpp", cs, 14);
        Write("f06.java", cs, 14);
        Write("f07.go", cs, 14);
        Write("f08.rs", cs, 14);
        Write("f09.json", Fixture("config.json"), 40);
        Write("f10.sql", py, 22);
        if (extras)
        {
            Write("notes.md", Fixture("README.md"), 20);
            var pdf = Path.Combine(dir, "manual.pdf");
            PdfFixtures.WriteManual(pdf, 12);
            files.Add(pdf);
        }

        foreach (var f in files)
        {
            w.OpenFile(f);
            await Settle(w, 150);
            if (w.ActiveView is PdfDocumentView pv)
            {
                for (var i = 0; i < 100 && pv.Handle is null; i++) await Wait(50);
                await Wait(600);
                pv.ZoomStep(+1);
                await Settle(w, 400);
            }
            else if (w.ActiveView?.Editor is { } ed)
            {
                ed.ScrollToLine(ed.Document.LineCount / 2);
                ed.CaretOffset = ed.Document.GetLineByNumber(ed.Document.LineCount / 2).Offset;
                await Settle(w, Path.GetExtension(f) == ".md" ? 600 : 150);
            }
        }
        // back to the first file and type into it: an unsaved change that must survive everything
        w.OpenFile(files[0]);
        await Settle(w, 200);
        if (w.ActiveView?.Editor is { } first) first.Document.Insert(first.CaretOffset, "// perf probe edit\n");
        await Settle(w, 2000);
    }

    private static string Fixture(string name) => SnapshotFixtures.All.First(f => f.Name == name).Text;

    // ───────────── numbers ─────────────

    private static void Record(string label) => Line(Mem.Take(label).ToString());

    private static void Line(string text)
    {
        Report.AppendLine(text);
        Log.Info("perf: " + text);
    }

    private static void Flush()
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("BETTERNOTEPAD_PERF")!;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.AppendAllText(path, Report + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Error("perf: report not written", ex);
        }
    }

    private readonly record struct Mem(string Label, double PrivateWs, double Ws, double PrivateBytes, double GcHeap, double GcCommitted, int Threads, int Handles, int Gdi, int User, double CpuMs, uint Faults)
    {
        public const string Header = "| state | private WS | WS | private bytes | GC heap | GC committed | threads | handles | GDI | USER | CPU ms | faults |\n|---|---|---|---|---|---|---|---|---|---|---|---|";

        /// <summary>Memory first (nothing allocated before it is read), then the cheap native counters.</summary>
        public static Mem Take(string label)
        {
            var c = Counters();
            var gc = GC.GetGCMemoryInfo(GCKind.Any);
            GetProcessHandleCount(GetCurrentProcess(), out var handles);
            return new Mem(label, Mb(c.PrivateWorkingSetSize), Mb(c.WorkingSetSize), Mb(c.PrivateUsage), Mb((nuint)gc.HeapSizeBytes), Mb((nuint)gc.TotalCommittedBytes),
                ThreadTimes().Count, (int)handles, (int)GetGuiResources(GetCurrentProcess(), 0), (int)GetGuiResources(GetCurrentProcess(), 1), PerfProbe.CpuMs(), c.PageFaultCount);
        }

        public static PROCESS_MEMORY_COUNTERS_EX2 Counters()
        {
            var c = new PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>() };
            GetProcessMemoryInfo(GetCurrentProcess(), ref c, c.cb);
            return c;
        }

        private static double Mb(nuint bytes) => bytes / 1048576.0;

        public override string ToString() =>
            $"| {Label} | **{PrivateWs:0.0}** | {Ws:0.0} | {PrivateBytes:0.0} | {GcHeap:0.0} | {GcCommitted:0.0} | {Threads} | {Handles} | {Gdi} | {User} | {CpuMs:0} | {Faults} |";
    }

    /// <summary>Where the private working set lives: pages in the working set that are not shared, grouped by region kind.</summary>
    private static string Breakdown(int top = 28)
    {
        var count = 1 << 17;
        var native = Marshal.AllocHGlobal(count * nint.Size); // native: reading the working set must not touch the GC heap
        try
        {
            var process = GetCurrentProcess();
            while (!QueryWorkingSet(process, native, count * nint.Size))
            {
                var needed = (int)Marshal.ReadIntPtr(native) + 4096;
                if (needed <= count) return "breakdown: QueryWorkingSet failed " + Marshal.GetLastPInvokeError();
                Marshal.FreeHGlobal(native);
                count = needed + 1;
                native = Marshal.AllocHGlobal(count * nint.Size);
            }
            var entries = (int)Marshal.ReadIntPtr(native);
            var heaps = new nint[64];
            var heapCount = (int)GetProcessHeaps((uint)heaps.Length, heaps);
            var defaultHeap = GetProcessHeap();
            var gcProbe = GCHandle.Alloc(new byte[64], GCHandleType.Pinned);
            nint gcBase;
            try
            {
                gcBase = Query(gcProbe.AddrOfPinnedObject()).AllocationBase;
            }
            finally
            {
                gcProbe.Free();
            }

            var byKind = new Dictionary<string, long>();
            var byBase = new Dictionary<nint, (long Bytes, string Kind)>();
            var names = new Dictionary<nint, string>();
            nint regionStart = 0, regionEnd = 0;
            MEMORY_BASIC_INFORMATION region = default;
            for (var i = 1; i <= entries; i++)
            {
                var block = (nuint)Marshal.ReadIntPtr(native, i * nint.Size);
                if ((block & 0x100) != 0) continue; // shared page
                var addr = (nint)(block & ~(nuint)0xFFF);
                if (addr < regionStart || addr >= regionEnd)
                {
                    region = Query(addr);
                    regionStart = region.BaseAddress;
                    regionEnd = region.BaseAddress + (nint)region.RegionSize;
                }
                string kind;
                switch (region.Type)
                {
                    case 0x1000000: // MEM_IMAGE
                        if (!names.TryGetValue(region.AllocationBase, out var mod)) names[region.AllocationBase] = mod = "image " + MappedName(process, region.AllocationBase);
                        kind = mod;
                        break;
                    case 0x40000: // MEM_MAPPED
                        if (!names.TryGetValue(region.AllocationBase, out var map)) names[region.AllocationBase] = map = "mapped " + MappedName(process, region.AllocationBase);
                        kind = map;
                        break;
                    default:
                        kind = region.AllocationBase == gcBase ? "private: GC heap"
                            : (region.Protect & 0xF0) != 0 ? "private: executable (JIT code, stubs)"
                            : "private: other (native heaps, stacks, runtime)";
                        break;
                }
                byKind[kind] = byKind.GetValueOrDefault(kind) + 4096;
                if (kind.StartsWith("private: other", StringComparison.Ordinal))
                {
                    var (bytes, k) = byBase.GetValueOrDefault(region.AllocationBase);
                    if (k is null)
                    {
                        var heap = Array.IndexOf(heaps, region.AllocationBase, 0, heapCount);
                        k = DescribeReservation(region.AllocationBase) + (region.AllocationBase == defaultHeap ? " process heap" : heap >= 0 ? $" heap #{heap}" : "");
                    }
                    byBase[region.AllocationBase] = (bytes + 4096, k);
                }
            }

            var sb = new StringBuilder();
            var total = byKind.Values.Sum();
            sb.AppendLine($"private WS breakdown ({total / 1048576.0:0.0} MB in {entries} WS pages, {heapCount} heaps):");
            if (top > 10) sb.AppendLine(HeapStats(heaps, heapCount, defaultHeap));
            foreach (var (k, v) in byKind.OrderByDescending(x => x.Value).Take(top)) sb.AppendLine($"  {v / 1048576.0,7:0.00} MB  {k}");
            sb.AppendLine("  largest 'private: other' reservations:");
            foreach (var (b, (bytes, k)) in byBase.OrderByDescending(x => x.Value.Bytes).Take(Math.Min(12, top))) sb.AppendLine($"  {bytes / 1048576.0,7:0.00} MB  0x{b:X} {k}");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return "breakdown failed: " + ex.Message;
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    /// <summary>Busy / free bytes and regions of every native heap (HeapWalk under HeapLock; probe only).</summary>
    private static string HeapStats(nint[] heaps, int count, nint defaultHeap)
    {
        var sb = new StringBuilder("  native heaps (busy / free committed, regions):");
        for (var i = 0; i < count; i++)
        {
            long busy = 0, free = 0;
            var regions = new List<string>();
            if (!HeapLock(heaps[i])) continue;
            try
            {
                var e = new PROCESS_HEAP_ENTRY();
                while (HeapWalk(heaps[i], ref e))
                {
                    if ((e.wFlags & 0x0001) != 0) regions.Add($"0x{e.lpData:X}"); // PROCESS_HEAP_REGION
                    else if ((e.wFlags & 0x0004) != 0) busy += e.cbData; // PROCESS_HEAP_ENTRY_BUSY
                    else if ((e.wFlags & 0x0002) == 0) free += e.cbData; // not uncommitted range
                }
            }
            finally
            {
                HeapUnlock(heaps[i]);
            }
            if (busy + free < 512 * 1024) continue;
            sb.Append($"\n    heap #{i}{(heaps[i] == defaultHeap ? " (process)" : "")} 0x{heaps[i]:X}: busy {busy / 1048576.0:0.0} MB, free {free / 1048576.0:0.0} MB, regions {string.Join(" ", regions.Take(6))}");
        }
        return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_HEAP_ENTRY
    {
        public nint lpData;
        public uint cbData;
        public byte cbOverhead;
        public byte iRegionIndex;
        public ushort wFlags;
        public nint u1; // union Block / Region: 24 bytes
        public nint u2;
        public nint u3;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HeapWalk(nint heap, ref PROCESS_HEAP_ENTRY entry);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HeapLock(nint heap);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HeapUnlock(nint heap);

    /// <summary>Total reserved / committed size of the allocation that starts at <paramref name="allocationBase"/>.</summary>
    private static string DescribeReservation(nint allocationBase)
    {
        long reserved = 0, committed = 0;
        var addr = allocationBase;
        for (var guard = 0; guard < 100000; guard++)
        {
            var r = Query(addr);
            if (r.AllocationBase != allocationBase || r.RegionSize == 0) break;
            reserved += (long)r.RegionSize;
            if (r.State == 0x1000) committed += (long)r.RegionSize;
            addr = r.BaseAddress + (nint)r.RegionSize;
        }
        return $"(reserved {reserved / 1048576.0:0.0} MB, committed {committed / 1048576.0:0.0} MB)";
    }

    private static MEMORY_BASIC_INFORMATION Query(nint addr)
    {
        VirtualQuery(addr, out var mbi, (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());
        return mbi;
    }

    private static string MappedName(nint process, nint addr)
    {
        var buf = new char[520];
        var n = GetMappedFileNameW(process, addr, buf, (uint)buf.Length);
        return n == 0 ? "(pagefile section)" : Path.GetFileName(new string(buf, 0, (int)n));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(nint process, ref PROCESS_MEMORY_COUNTERS_EX2 counters, uint cb);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryWorkingSet(nint process, nint buffer, int cb);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetProcessHeaps(uint count, [Out] nint[] heaps);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetProcessHeap();

    [LibraryImport("psapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetMappedFileNameW(nint process, nint address, [Out] char[] name, uint size);

    [LibraryImport("kernel32.dll")]
    private static partial nuint VirtualQuery(nint address, out MEMORY_BASIC_INFORMATION info, nuint length);

    [LibraryImport("user32.dll")]
    private static partial uint GetGuiResources(nint process, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct THREADENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ThreadID;
        public uint th32OwnerProcessID;
        public int tpBasePri;
        public int tpDeltaPri;
        public uint dwFlags;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Thread32First(nint snapshot, ref THREADENTRY32 entry);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Thread32Next(nint snapshot, ref THREADENTRY32 entry);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint threadId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessHandleCount(nint process, out uint count);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial int GetThreadDescription(nint thread, out nint description);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint mem);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationThread(nint thread, int infoClass, out nint info, int length, out int returned);
}
