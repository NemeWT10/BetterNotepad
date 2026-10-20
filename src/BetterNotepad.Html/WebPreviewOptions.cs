using System.Diagnostics;
using System.IO;
using Microsoft.Web.WebView2.Core;

namespace BetterNotepad.Html;

/// <summary>UI texts of the web previews. The app fills them from its PL / EN resources; the defaults are only a fallback.</summary>
public sealed class WebPreviewLabels
{
    public string RuntimeMissing { get; init; } = "The preview needs the Microsoft Edge WebView2 Runtime.";

    public string RuntimeMissingHint { get; init; } = "It ships with Windows 11. Install “WebView2 Runtime” from Microsoft and reopen the tab.";

    /// <summary>Button under the missing-runtime message (opens <see cref="WebPreviewOptions.RuntimeDownloadUri"/>).</summary>
    public string RuntimeDownload { get; init; } = "Get WebView2 Runtime";

    public string LoadFailed { get; init; } = "The preview could not be started.";

    /// <summary>Shown (faintly, in the theme's colors) for a document with no content yet.</summary>
    public string EmptyPage { get; init; } = "Empty page";
}

/// <summary>How the WebView is hosted in WPF.</summary>
public enum WebPreviewHosting
{
    /// <summary>A native child window, except in a per-pixel transparent window (there a child window is never drawn).</summary>
    Auto,

    /// <summary>A native child window (fastest; drawn above every WPF element — see <see cref="WebPreview.SetCoveredAsync"/>).</summary>
    Window,

    /// <summary>
    /// Visual hosting (WebView2CompositionControl): an ordinary WPF element, no airspace issues, a bit more costly. Needs the
    /// Windows SDK projection (an app targeting net*-windows10.0.17763 or later); without it Auto falls back to <see cref="Picture"/>.
    /// </summary>
    Composition,

    /// <summary>
    /// The page renders off-stage and WPF shows its live picture (refreshed on every change): works in a per-pixel transparent
    /// window. Wheel scrolls, Ctrl+wheel zooms, clicks follow links, double-click goes to the source line; no text selection.
    /// </summary>
    Picture,
}

/// <summary>Settings shared by all previews. The first preview that starts the browser fixes the environment-level ones.</summary>
public sealed class WebPreviewOptions
{
    /// <summary>WebView2 profile / cache folder. The app passes %LOCALAPPDATA%\BetterNotepad\WebView2.</summary>
    public string UserDataFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterNotepad", "WebView2");

    /// <summary>UI language of the browser's own parts (context menu, DevTools): "pl" or "en". Environment-level.</summary>
    public string? Language { get; set; }

    /// <summary>
    /// Load http(s) resources from other hosts (CDN styles, web fonts, remote images). Off by default: the preview then makes no
    /// network connection of its own; local CSS / JS / images next to the file always work. Read per request, so a change
    /// applies to the next load; call <see cref="WebPreview.Refresh"/> to load again at once.
    /// </summary>
    public bool AllowRemoteContent { get; set; }

    /// <summary>
    /// Tests: a remote request that <see cref="AllowRemoteContent"/> lets through is answered locally (204, empty) instead of
    /// going to the network. <see cref="WebPreview.RemoteRequested"/> still reports it as allowed.
    /// </summary>
    public bool StubAllowedRemote { get; set; }

    /// <summary>F12 inside the preview opens DevTools. The app turns it off when its own (rebindable) command does it.</summary>
    public bool HandleDevToolsKey { get; set; } = true;

    public WebPreviewHosting Hosting { get; set; } = WebPreviewHosting.Auto;

    /// <summary>Extra Chromium switches (environment-level; tests only).</summary>
    public string? AdditionalBrowserArguments { get; set; }

    /// <summary>Live refresh delay after the last change (grows for big documents).</summary>
    public TimeSpan RefreshDelay { get; set; } = TimeSpan.FromMilliseconds(260);

    public WebPreviewLabels Labels { get; set; } = new();

    /// <summary>Where the missing-runtime message sends the user (the Evergreen runtime download page).</summary>
    public Uri RuntimeDownloadUri { get; set; } = new("https://developer.microsoft.com/microsoft-edge/webview2/");

    /// <summary>Opens a link in the system browser / mail program. Default: ShellExecute (http, https, mailto only).</summary>
    public Action<Uri> OpenExternal { get; set; } = DefaultOpenExternal;

    /// <summary>
    /// Optional: the current text of a local file the page loads (an unsaved editor tab), else null and the file is read from
    /// disk. Lets a page show CSS / JS edits from other tabs before they are saved.
    /// </summary>
    public Func<string, string?>? ReadLocalText { get; set; }

    public static void DefaultOpenExternal(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https" or "mailto")) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch
        {
            // no handler registered: nothing to do
        }
    }
}

/// <summary>
/// One shared WebView2 environment (one browser process) for every preview, created on first use. When the last preview frees
/// its WebView the reference is dropped and the browser processes exit (WebView2's ~100 MB only while a preview needs it).
/// </summary>
public static class WebViewRuntime
{
    private static Task<CoreWebView2Environment>? _environment;
    private static int _views;
    private static bool _running;

    /// <summary>WebViews alive right now (0 = no browser process should be running).</summary>
    public static int ActiveViews => _views;

    /// <summary>A browser process of ours was started and has not reported its exit yet.</summary>
    public static bool IsBrowserRunning => _running;

    /// <summary>The browser process of the shared environment exited (after the last preview released it, or crashed).</summary>
    public static event Action<CoreWebView2BrowserProcessExitKind>? BrowserProcessExited;

    /// <summary>Tests: report the runtime as missing (the preview then shows its "install WebView2" message).</summary>
    public static bool SimulateMissingRuntime { get; set; }

    /// <summary>Installed Evergreen runtime version, or null when WebView2 is not available.</summary>
    public static string? AvailableVersion()
    {
        if (SimulateMissingRuntime) return null;
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch
        {
            return null;
        }
    }

    internal static Task<CoreWebView2Environment> Acquire(WebPreviewOptions o)
    {
        _views++;
        return _environment ??= Create(o);
    }

    internal static void Release()
    {
        if (_views > 0) _views--;
        if (_views == 0) _environment = null;
    }

    private static async Task<CoreWebView2Environment> Create(WebPreviewOptions o)
    {
        try
        {
            Directory.CreateDirectory(o.UserDataFolder);
            // background networking / component updates off: the preview should not phone anywhere by itself
            var args = "--disable-background-networking --disable-component-update --disable-sync --no-first-run --disable-domain-reliability";
            if (!string.IsNullOrWhiteSpace(o.AdditionalBrowserArguments)) args += " " + o.AdditionalBrowserArguments;
            var options = new CoreWebView2EnvironmentOptions(args, o.Language)
            {
                AreBrowserExtensionsEnabled = false,
                IsCustomCrashReportingEnabled = true, // keeps crash dumps local instead of sending them to Microsoft
            };
            var env = await CoreWebView2Environment.CreateAsync(null, o.UserDataFolder, options);
            _running = true;
            env.BrowserProcessExited += (_, e) =>
            {
                _running = false;
                BrowserProcessExited?.Invoke(e.BrowserProcessExitKind);
            };
            return env;
        }
        catch
        {
            _environment = null; // let the next preview try again
            throw;
        }
    }
}
