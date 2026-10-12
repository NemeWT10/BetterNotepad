using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.UI;
using BetterNotepad.UI.Controls;
using BetterNotepad.UI.Tray;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Modules.Sys;

/// <summary>
/// Update behaviour on top of <see cref="UpdateService"/>: the once-a-day schedule (one delayed continuation,
/// no polling), the ask dialog with release notes, the automatic download + swap + restart, "Check now".
/// </summary>
internal static class UpdateFlow
{
    /// <summary>The newest release found that is newer than this build (null when none / already installed).</summary>
    public static ReleaseInfo? Pending { get; private set; }

    public static event Action? PendingChanged;

    private static bool _busy;
    private static string? _offeredTag;
    private static CancellationTokenSource? _schedule;
    private static DateTime _notBeforeUtc;

    private static UpdateSettings Settings => SettingsService.I.Get<UpdateSettings>();

    private static MainWindow? Window => App.Current?.MainAppWindow;

    private static void SetPending(ReleaseInfo? r)
    {
        Pending = r;
        PendingChanged?.Invoke();
    }

    // ───────────── schedule ─────────────

    /// <summary>(Re)starts the daily check according to the update mode. Safe to call any time.</summary>
    public static void Reschedule()
    {
        _schedule?.Cancel();
        _schedule = null;
        if (Settings.Mode == "off" || UpdateService.ApiUrl is null) return;
        var cts = _schedule = new CancellationTokenSource();
        _ = ScheduleLoop(cts.Token);
    }

    public static void Stop()
    {
        _schedule?.Cancel();
        _schedule = null;
    }

    /// <summary>How long to wait before the next automatic check (at least 20 s after start, at most a day).</summary>
    internal static TimeSpan NextDelay(DateTime? lastCheckUtc, DateTime nowUtc, DateTime notBeforeUtc)
    {
        var due = (lastCheckUtc ?? DateTime.MinValue) + UpdateService.CheckInterval;
        if (notBeforeUtc > due) due = notBeforeUtc;
        var wait = due - nowUtc;
        if (wait < TimeSpan.FromSeconds(20)) wait = TimeSpan.FromSeconds(20);
        if (wait > UpdateService.CheckInterval) wait = UpdateService.CheckInterval;
        return wait;
    }

    private static async Task ScheduleLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && Settings.Mode != "off")
            {
                await Task.Delay(NextDelay(Settings.LastCheckUtc, DateTime.UtcNow, _notBeforeUtc), ct);
                await RunAutomaticCheck(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // rescheduled or shutting down
        }
    }

    private static async Task RunAutomaticCheck(CancellationToken ct)
    {
        var r = await UpdateService.CheckAsync(ct);
        switch (r.Status)
        {
            case UpdateStatus.UpToDate or UpdateStatus.Available:
                Settings.LastCheckUtc = DateTime.UtcNow;
                break;
            default:
                if (!ct.IsCancellationRequested) _notBeforeUtc = DateTime.UtcNow.AddHours(1); // offline or failed: quietly try again later
                return;
        }
        if (r.Status != UpdateStatus.Available || r.Release is not { } rel) return;
        SetPending(rel);
        if (rel.Tag == Settings.SkippedVersion) return;
        await HandleFound(rel);
    }

    private static async Task HandleFound(ReleaseInfo rel)
    {
        var w = Window;
        if (Settings.Mode == "auto")
        {
            await ApplyAsync(rel, w, interactive: false);
            return;
        }
        if (w is { IsVisible: true })
        {
            await OfferAsync(w, rel);
        }
        else
        {
            // the window is in the tray: a notification now, the dialog when the window comes back
            TrayService.I?.ShowHint(Loc.T("update.balloonTitle"), Loc.T("update.balloonText", rel.Version));
        }
    }

    /// <summary>The main window became visible: show the dialog for an update found while it was hidden.</summary>
    public static void OnMainShown()
    {
        if (Pending is not { } rel || Window is not { IsVisible: true } w) return;
        if (Settings.Mode != "ask" || rel.Tag == Settings.SkippedVersion || rel.Tag == _offeredTag) return;
        _ = OfferAsync(w, rel);
    }

    public static void OfferPending()
    {
        if (Pending is not { } rel || Window is not { } w) return;
        w.BringToFront();
        _ = OfferAsync(w, rel);
    }

    // ───────────── manual check ─────────────

    public static async void CheckNow(MainWindow? w)
    {
        w ??= Window;
        if (w is null || _busy) return;
        if (UpdateService.ApiUrl is null)
        {
            w.Toasts.Show(Loc.T("update.testMode"), ToastKind.Info);
            return;
        }
        _busy = true;
        UpdateCheckResult r;
        try
        {
            w.Toasts.Show(Loc.T("update.checking"), ToastKind.Info, seconds: 1.6);
            r = await UpdateService.CheckAsync();
        }
        finally
        {
            _busy = false;
        }

        switch (r.Status)
        {
            case UpdateStatus.UpToDate:
                Settings.LastCheckUtc = DateTime.UtcNow;
                SetPending(null);
                w.Toasts.Show(Loc.T("update.upToDate", AppInfo.Version), ToastKind.Success);
                break;
            case UpdateStatus.Available when r.Release is { } rel:
                Settings.LastCheckUtc = DateTime.UtcNow;
                SetPending(rel);
                await OfferAsync(w, rel);
                break;
            case UpdateStatus.Offline:
                w.Toasts.Show(Loc.T("update.offline"), ToastKind.Warning);
                break;
            default:
                w.Toasts.Show(Loc.T("update.failed", r.Error ?? "?"), ToastKind.Error);
                break;
        }
    }

    // ───────────── dialog ─────────────

    internal static async Task OfferAsync(MainWindow w, ReleaseInfo rel)
    {
        _offeredTag = rel.Tag;
        var answer = await w.DialogLayer.Show(
            Loc.T("update.available.title", rel.Version),
            Loc.T("update.available.text", AppInfo.Version, rel.Version),
            [
                new DialogButton("skip", Loc.T("update.skip"), DialogButtonKind.Ghost),
                new DialogButton("later", Loc.T("update.later"), DialogButtonKind.Secondary, IsCancel: true),
                new DialogButton("update", Loc.T("update.install"), DialogButtonKind.Primary, IsDefault: true),
            ],
            extra: BuildNotes(rel), glyph: Glyphs.Download);
        switch (answer)
        {
            case "update":
                await ApplyAsync(rel, w, interactive: true);
                break;
            case "skip":
                Settings.SkippedVersion = rel.Tag;
                SetPending(null);
                break;
        }
    }

    private static UIElement BuildNotes(ReleaseInfo rel)
    {
        var box = new StackPanel();
        var head = new TextBlock { Text = Loc.T("update.notes"), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 6) };
        head.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        box.Children.Add(head);

        var text = new TextBlock { Text = CleanNotes(rel.Notes) is { Length: > 0 } t ? t : Loc.T("update.noNotes"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 18 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        var scroll = new ScrollViewer { MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = text, Padding = new Thickness(0, 0, 6, 0) };
        var frame = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10, 8, 10), Child = scroll };
        frame.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceHover");
        frame.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        box.Children.Add(frame);
        return box;
    }

    /// <summary>Release notes as plain text: no Markdown markers, bullets as "•", capped.</summary>
    internal static string CleanNotes(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var lines = new List<string>();
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
                continue;
            }
            line = line.TrimStart('#').Trim().Replace("**", "").Replace("__", "").Replace("`", "");
            if (line.StartsWith("- ") || line.StartsWith("* ")) line = "• " + line[2..];
            lines.Add(line);
        }
        var result = string.Join("\n", lines).Trim();
        return result.Length > 3000 ? result[..3000].TrimEnd() + "…" : result;
    }

    // ───────────── apply ─────────────

    private static async Task ApplyAsync(ReleaseInfo rel, MainWindow? w, bool interactive)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var target = SysEnv.CurrentExe;
            if (!SysEnv.IsOwnExe(target) || !SysEnv.IsSingleFile)
            {
                if (interactive) w?.Toasts.Show(Loc.T("update.devBuild"), ToastKind.Warning);
                return;
            }
            if (interactive) w?.Toasts.Show(Loc.T("update.downloading", rel.Version), ToastKind.Info, seconds: 10);

            var staged = target + ".new";
            await UpdateService.DownloadAsync(rel, staged);
            UpdateService.Swap(staged, target!);
            Log.Info($"update: {AppInfo.Version} → {rel.Version} installed");
            SetPending(null);

            var hidden = w is not { IsVisible: true };
            if (interactive || hidden)
            {
                Restart();
                return;
            }
            // automatic mode while the user is working: the new exe is in place, restart whenever they like
            w!.Toasts.Show(Loc.T("update.ready", rel.Version), ToastKind.Success, Loc.T("update.restartNow"), Restart, seconds: 20);
        }
        catch (UpdateException ex)
        {
            Fail(w, interactive, "update.err." + ex.Reason);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException)
        {
            Log.Warn("update: " + ex.Message);
            Fail(w, interactive, "update.err.write");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            Log.Warn("update: " + ex.Message);
            Fail(w, interactive, "update.err.download");
        }
        finally
        {
            _busy = false;
        }
    }

    private static void Fail(MainWindow? w, bool interactive, string key)
    {
        Log.Warn("update failed: " + key);
        if (interactive) w?.Toasts.Show(Loc.T(key), ToastKind.Error);
    }

    /// <summary>Starts the (swapped) exe — it waits for this process to end — and quits. Tabs come back from the session.</summary>
    public static void Restart()
    {
        if (AppEnv.TestMode) return; // a test instance never spawns programs
        try
        {
            var psi = new ProcessStartInfo(SysEnv.CurrentExe!) { UseShellExecute = false };
            psi.ArgumentList.Add("--restarted-from");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            if (Window is not { IsVisible: true }) psi.ArgumentList.Add("--minimized");
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("restart failed: " + ex.Message);
            Window?.Toasts.Show(Loc.T("update.err.restart"), ToastKind.Error);
            return;
        }
        App.Current.Quit();
    }
}
