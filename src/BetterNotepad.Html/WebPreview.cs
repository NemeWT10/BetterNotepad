using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BetterNotepad.Html;

/// <summary>
/// Live preview of the editor text in a WebView2 (base of <see cref="HtmlPreview"/> and <see cref="SvgPreview"/>).
/// <para>The WebView is created on first show from the shared <see cref="WebViewRuntime"/>; the document is the editor text,
/// served from memory at https://bnpreview.local/&lt;path&gt; while the file's folder (or a project folder above it) is served
/// from the same host, so relative CSS / JS / images resolve as in a browser. Typing refreshes after a short debounce (the
/// scroll position is kept), external links go to the system browser, F12 opens DevTools, there is no address bar and the
/// context menu is trimmed.</para>
/// <para>Content from the internet is blocked unless <see cref="WebPreviewOptions.AllowRemoteContent"/>: http(s) requests to
/// other hosts are answered with 403 at the WebResourceRequested filter, remote frames are cancelled, and a Content Security
/// Policy (connect-src) stops web sockets; every block is counted (<see cref="BlockedCount"/>, <see cref="BlockedChanged"/>).</para>
/// <para>Hidden: suspended with a low memory target. <see cref="ReleaseWebView"/> frees the browser (the processes exit once the
/// last preview released it); the next show creates it again. UI thread only. Source lines are 1-based.</para>
/// </summary>
public abstract class WebPreview : UserControl, IDisposable
{
    internal const string Host = "bnpreview.local";

    /// <summary>The policy added while remote content is blocked: web sockets / fetch to other hosts never leave the page.</summary>
    internal const string BlockPolicy = "connect-src 'self' data: blob: http: https:";

    private const string Crlf = "\r\n";

    public static readonly DependencyProperty ThemeBackgroundProperty = DependencyProperty.Register(
        nameof(ThemeBackground), typeof(Color), typeof(WebPreview), new PropertyMetadata(Color.FromRgb(0x12, 0x14, 0x1C), OnThemeChanged));

    public static readonly DependencyProperty ThemeForegroundProperty = DependencyProperty.Register(
        nameof(ThemeForeground), typeof(Color), typeof(WebPreview), new PropertyMetadata(Color.FromRgb(0xE4, 0xE7, 0xF0), OnThemeChanged));

    private readonly Grid _root = new();
    private readonly Border _placeholder = new();
    private readonly StackPanel _message = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420, Visibility = Visibility.Collapsed };
    private FrameworkElement? _host;
    private IWebView2? _web;
    private bool _composited;
    private bool _pictureMode;
    private Canvas? _offstage;
    private Border? _pictureHost;
    private Image? _picture;
    private bool _capturing;
    private bool _captureAgain;
    private Point _pressPoint;
    private Image? _cover;
    private int _coverGeneration;
    private Task? _init;
    private bool _holdsRuntime;
    private DispatcherTimer? _timer;

    private string _text = "";
    private string? _filePath;
    private string? _rootRequest;
    private byte[] _doc = [];
    private string? _mappedFolder;
    private string? _navigatedPath;
    private string _docPath = "index.html";
    private bool _dirty = true;
    private bool _ready;
    private bool _disposed;
    private bool _themedPage;
    private System.Drawing.Color? _backgroundSet;
    private bool _navigating;
    private double _zoom = 1;

    private int _topLine = 1;
    private double _topFraction;
    private double _topScroll;
    private bool _haveTop;

    private readonly SortedSet<string> _blockedHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadedFiles = new(StringComparer.OrdinalIgnoreCase);
    private int _blocked;
    private bool _blockedQueued;

    protected WebPreview(WebPreviewOptions? options)
    {
        Options = options ?? new WebPreviewOptions();
        _placeholder.SetResourceReference(Border.BackgroundProperty, "Brush.Editor.Bg");
        _root.Children.Add(_placeholder);
        _root.Children.Add(_message);
        Content = _root;
        Focusable = false;
        SetResourceReference(ThemeBackgroundProperty, "Color.Editor.Bg");
        SetResourceReference(ThemeForegroundProperty, "Color.Text");
        IsVisibleChanged += OnVisibleChanged;
    }

    // ───────────── public surface ─────────────

    public WebPreviewOptions Options { get; }

    /// <summary>Editor background of the active theme (resource Color.Editor.Bg): the preview's color while the page loads.</summary>
    public Color ThemeBackground
    {
        get => (Color)GetValue(ThemeBackgroundProperty);
        set => SetValue(ThemeBackgroundProperty, value);
    }

    /// <summary>Text color of the active theme (resource Color.Text).</summary>
    public Color ThemeForeground
    {
        get => (Color)GetValue(ThemeForegroundProperty);
        set => SetValue(ThemeForegroundProperty, value);
    }

    /// <summary>True while this preview holds a WebView (and with it a share of the browser process).</summary>
    public bool IsBrowserCreated => _web is not null;

    /// <summary>The WebView is visually hosted (no native window, so nothing to cover for in-window overlays).</summary>
    public bool IsComposited => _web is not null && _composited;

    /// <summary>The page renders off-stage and WPF shows its live picture (<see cref="WebPreviewHosting.Picture"/>).</summary>
    public bool IsPicture => _web is not null && _pictureMode;

    /// <summary>A native WebView window is on screen: in-window overlays need <see cref="SetCoveredAsync"/>.</summary>
    public bool NeedsCover => _web is not null && !_composited && !_pictureMode;

    /// <summary>The live page is swapped for a still picture of itself right now.</summary>
    public bool IsCovered => _cover is not null;

    /// <summary>The page has the keyboard focus.</summary>
    public bool IsPageFocused => _host?.IsKeyboardFocusWithin == true;

    /// <summary>The missing-runtime or start-failure message is shown instead of a page.</summary>
    public bool IsShowingMessage => _message.Visibility == Visibility.Visible;

    /// <summary>For the render harness and the app's self-test.</summary>
    internal CoreWebView2? Core => _web?.CoreWebView2;

    /// <summary>Diagnostics hook (render harness).</summary>
    internal static Action<string>? Trace;

    /// <summary>The current document reached DOMContentLoaded.</summary>
    public bool IsReady => _ready;

    /// <summary>Requests to the internet blocked while loading the current document (resources, frames, web sockets).</summary>
    public int BlockedCount => _blocked;

    /// <summary>Hosts of the blocked requests, sorted.</summary>
    public IReadOnlyCollection<string> BlockedHosts => _blockedHosts;

    /// <summary>Local files the current document loaded (full paths): a change to one of them warrants a <see cref="Refresh"/>.</summary>
    public IReadOnlyCollection<string> LoadedFiles => _loadedFiles;

    /// <summary>The folder served as the root of the virtual host (null for an unsaved tab).</summary>
    public string? ServedRoot => _mappedFolder;

    /// <summary>Page zoom (1 = 100 %). Ctrl+wheel changes it in the HTML preview.</summary>
    public double ZoomFactor
    {
        get => _web?.ZoomFactor ?? _zoom;
        set
        {
            _zoom = Math.Clamp(value, 0.25, 5);
            if (_web is not null) _web.ZoomFactor = _zoom;
        }
    }

    /// <summary>Double-click in the preview: go to this source line in the editor.</summary>
    public event Action<int>? LineRequested;

    /// <summary>The user scrolled the preview: top source line (1-based) and progress through it.</summary>
    public event Action<int, double>? Scrolled;

    /// <summary>A link to a local file (next to this one, or a file dropped on the page) was clicked (absolute path).</summary>
    public event Action<string>? LocalLinkClicked;

    /// <summary>A (re)rendered document is ready (DOMContentLoaded).</summary>
    public event Action? DocumentReady;

    /// <summary>The document and everything it loads finished loading (window load).</summary>
    public event Action? DocumentLoaded;

    /// <summary>
    /// <see cref="BlockedCount"/> grew, or a load finished (then the count is final for that document; it is reset when the
    /// next render starts, without an event, so a notice does not flicker while typing).
    /// </summary>
    public event Action? BlockedChanged;

    /// <summary>Every request to another host: the URI and whether it was let through (tests, diagnostics).</summary>
    public event Action<Uri, bool>? RemoteRequested;

    /// <summary>
    /// Sets the text to show. <paramref name="filePath"/> gives the base folder for relative resources (null for an unsaved tab);
    /// <paramref name="rootFolder"/> (a folder above it, e.g. the opened project) is served too, so "../css/site.css" works.
    /// Debounced while visible; deferred while hidden; the first show renders at once.
    /// </summary>
    public void Update(string text, string? filePath, string? rootFolder = null)
    {
        if (_disposed) return;
        text ??= "";
        if (!_dirty && text == _text && filePath == _filePath && rootFolder == _rootRequest) return;
        _text = text;
        _filePath = filePath;
        _rootRequest = rootFolder;
        _dirty = true;
        if (!IsVisible) return;
        if (_web is null || _navigatedPath is null) _ = RenderAsync();
        else Schedule();
    }

    /// <summary>Loads the document again (a file it uses changed, the remote-content setting changed). Deferred while hidden.</summary>
    public void Refresh(bool now = false)
    {
        if (_disposed) return;
        _dirty = true;
        if (!IsVisible) return;
        if (now || _web is null || _navigatedPath is null) _ = RenderAsync();
        else Schedule();
    }

    /// <summary>Renders a pending update now (tests, before capturing).</summary>
    public Task FlushAsync() => _dirty ? RenderAsync() : Task.CompletedTask;

    /// <summary>Editor → preview sync (see IPreviewView.ScrollToLine).</summary>
    public void ScrollToLine(int line, double lineFraction, double scrollFraction)
    {
        _topLine = Math.Max(1, line);
        _topFraction = Math.Clamp(lineFraction, 0, 1);
        _topScroll = Math.Clamp(scrollFraction, 0, 1);
        _haveTop = true;
        if (_ready && _web?.CoreWebView2 is { } core)
            core.PostWebMessageAsJson(string.Create(CultureInfo.InvariantCulture,
                $"{{\"t\":\"to\",\"line\":{_topLine},\"frac\":{_topFraction:0.####},\"sf\":{_topScroll:0.####}}}"));
    }

    /// <summary>
    /// Frees the WebView (renderer and, if it was the last one, the browser process). The text is kept: the next show
    /// creates a new WebView and renders it again. Call when the preview has been hidden for a while.
    /// </summary>
    public void ReleaseWebView()
    {
        _timer?.Stop();
        _coverGeneration++;
        RemoveCover();
        if (_pictureHost is not null)
        {
            _root.Children.Remove(_pictureHost);
            _root.SizeChanged -= OnPictureAreaSize;
            _pictureHost = null;
            _picture = null;
        }
        if (_offstage is not null)
        {
            _root.Children.Remove(_offstage);
            _offstage.Children.Clear();
            _offstage = null;
        }
        _pictureMode = false;
        if (_host is { } host)
        {
            var web = _web;
            _host = null;
            _web = null;
            _root.Children.Remove(host);
            try
            {
                (web as IDisposable)?.Dispose();
            }
            catch
            {
                // already torn down by a crashed browser
            }
        }
        if (_holdsRuntime)
        {
            _holdsRuntime = false;
            WebViewRuntime.Release();
        }
        _mappedFolder = null;
        _navigatedPath = null;
        _backgroundSet = null;
        _navigating = false;
        _ready = false;
        _dirty = true;
        IsFinding = false;
    }

    /// <summary>
    /// A native WebView window is drawn above all WPF content (airspace). Before an in-window overlay (palette, dialog) covers
    /// the preview, call with true: the page is swapped for a still picture of itself; false brings the live page back.
    /// Overlapping calls are fine: the last one wins.
    /// </summary>
    public async Task SetCoveredAsync(bool covered)
    {
        var generation = ++_coverGeneration;
        if (!covered)
        {
            RemoveCover();
            if (_host is not null) _host.Visibility = Visibility.Visible;
            return;
        }
        if (_cover is not null || _web?.CoreWebView2 is null) return;
        var shot = await CaptureAsync();
        if (generation != _coverGeneration || shot is null || _host is null || _disposed) return;
        _cover = new Image { Source = shot, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_cover, BitmapScalingMode.HighQuality);
        _root.Children.Add(_cover);
        _host.Visibility = Visibility.Hidden;
    }

    /// <summary>The page as a picture (WebView2's own capture of its content; never the screen).</summary>
    public async Task<BitmapSource?> CaptureAsync()
    {
        if (_web?.CoreWebView2 is not { } core) return null;
        using var ms = new MemoryStream();
        try
        {
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
        }
        catch
        {
            return null;
        }
        if (ms.Length == 0) return null;
        ms.Position = 0;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Opens the DevTools window (also F12 inside the preview).</summary>
    public void OpenDevTools() => _web?.CoreWebView2?.OpenDevToolsWindow();

    /// <summary>A find in the page is running (its find bar is open): F3 / Shift+F3 move through its matches.</summary>
    public bool IsFinding { get; private set; }

    /// <summary>
    /// Find in the page with the browser's own find bar (empty <paramref name="term"/> = just open the bar). False when the
    /// installed runtime has no find API (the caller falls back to the editor's find).
    /// </summary>
    public async Task<bool> StartFindAsync(string term = "")
    {
        if (_web?.CoreWebView2 is not { } core) return false;
        try
        {
            var options = core.Environment.CreateFindOptions();
            options.FindTerm = term;
            options.ShouldHighlightAllMatches = true;
            IsFinding = true;
            await core.Find.StartAsync(options);
            return true;
        }
        catch (Exception ex) when (ex is NotImplementedException or InvalidCastException or NotSupportedException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            IsFinding = false;
            return false;
        }
    }

    /// <summary>Matches of the running find (tests): -1 while unknown.</summary>
    public int FindMatchCount => IsFinding && _web?.CoreWebView2 is { } core ? TryFind(() => core.Find.MatchCount, -1) : -1;

    public void FindNext(bool backwards = false)
    {
        if (!IsFinding || _web?.CoreWebView2 is not { } core) return;
        TryFind(() =>
        {
            if (backwards) core.Find.FindPrevious();
            else core.Find.FindNext();
            return 0;
        }, 0);
    }

    public void StopFind()
    {
        if (!IsFinding || _web?.CoreWebView2 is not { } core) return;
        IsFinding = false;
        TryFind(() =>
        {
            core.Find.Stop();
            return 0;
        }, 0);
    }

    private static T TryFind<T>(Func<T> f, T fallback)
    {
        try
        {
            return f();
        }
        catch (Exception ex) when (ex is NotImplementedException or InvalidCastException or NotSupportedException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return fallback;
        }
    }

    /// <summary>Moves the keyboard focus into the page.</summary>
    public void FocusPage() => _web?.Focus();

    public void Dispose()
    {
        if (_disposed) return;
        ReleaseWebView();
        _disposed = true;
        IsVisibleChanged -= OnVisibleChanged;
        _text = "";
        _doc = [];
    }

    // ───────────── for the subclasses ─────────────

    protected abstract bool IsSvg { get; }

    /// <summary>Whether Ctrl+wheel zooms the page (the SVG preview zooms the picture itself).</summary>
    protected virtual bool BrowserZoom => true;

    /// <summary>Color behind a page that paints no background of its own (HTML: white like a browser).</summary>
    protected abstract Color PageBackground();

    /// <summary>The document to serve: the source with line marks and <paramref name="configTag"/> in its head.</summary>
    protected abstract string Compose(string text, string configTag);

    // ───────────── rendering ─────────────

    private void Schedule()
    {
        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                _ = RenderAsync();
            };
        }
        // bigger documents refresh less often (re-parsing + layout cost grows with size)
        var extra = Math.Min(900, _text.Length / 1024 * 3);
        _timer.Stop();
        _timer.Interval = Options.RefreshDelay + TimeSpan.FromMilliseconds(extra);
        _timer.Start();
    }

    private async Task RenderAsync()
    {
        if (_disposed) return;
        _timer?.Stop();
        if (!await EnsureWebAsync() || _web?.CoreWebView2 is not { } core || _disposed) return;
        if (!_dirty) return; // a second caller that waited for the same initialization
        if (!IsVisible) return;
        _dirty = false;

        var folder = _filePath is null ? null : Path.GetDirectoryName(_filePath);
        var root = folder is not null && _rootRequest is not null && IsUnder(folder, _rootRequest) ? _rootRequest : folder;
        var path = _filePath is null || root is null
            ? (IsSvg ? "image.svg" : "index.html")
            : Path.GetRelativePath(root, _filePath).Replace('\\', '/');
        _themedPage = string.IsNullOrWhiteSpace(_text);
        _doc = Encoding.UTF8.GetBytes(_themedPage ? EmptyPage(ConfigTag()) : Compose(_text, ConfigTag()));

        var bg = ToDrawing(_themedPage ? ThemeBackground : PageBackground());
        if (_backgroundSet != bg)
        {
            _backgroundSet = bg;
            _web.DefaultBackgroundColor = bg;
        }
        if (!string.Equals(root, _mappedFolder, StringComparison.OrdinalIgnoreCase))
        {
            _mappedFolder = root;
            _navigatedPath = null;
        }
        _ready = false;
        _blocked = 0;
        _blockedHosts.Clear();
        _loadedFiles.Clear();
        var url = $"https://{Host}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";
        // Reload keeps no history and lets Chromium hold the old frame until the new one paints; but only once our document
        // is really there (a reload during the first navigation would reload about:blank)
        if (!_navigating && string.Equals(_navigatedPath, path, StringComparison.OrdinalIgnoreCase) && core.Source == url)
        {
            core.Reload();
        }
        else
        {
            _navigatedPath = path;
            _docPath = path;
            core.Navigate(url);
        }
        _navigating = true;
    }

    private string ConfigTag()
    {
        var lines = 1;
        foreach (var ch in _text.AsSpan())
            if (ch == '\n') lines++;
        var json = new StringBuilder(96);
        json.Append(CultureInfo.InvariantCulture, $"{{\"lines\":{lines},\"svg\":{(IsSvg ? "true" : "false")},\"picture\":{(_pictureMode ? "true" : "false")}");
        if (_haveTop)
            json.Append(CultureInfo.InvariantCulture, $",\"restore\":{{\"line\":{_topLine},\"frac\":{_topFraction:0.####},\"sf\":{_topScroll:0.####}}}");
        json.Append('}');
        var tag = $"<meta name=\"{PreviewBridge.MetaName}\" content=\"{WebUtility.HtmlEncode(json.ToString())}\">";
        // web sockets are not seen by WebResourceRequested: a policy keeps them (and fetch to odd schemes) inside the page
        if (!Options.AllowRemoteContent)
            tag = $"<meta http-equiv=\"Content-Security-Policy\" content=\"{BlockPolicy}\"><meta http-equiv=\"x-dns-prefetch-control\" content=\"off\">" + tag;
        return tag;
    }

    /// <summary>A document with no content yet: the theme's background and a faint word in the middle.</summary>
    private string EmptyPage(string configTag)
    {
        var bg = ThemeBackground;
        var faint = Css(Blend(bg, ThemeForeground, 0.38));
        return "<!doctype html><html><head><meta charset=\"utf-8\">" + configTag + "<style>" +
               "html,body{margin:0;height:100%;background:" + Css(bg) + ";}" +
               "body{display:flex;align-items:center;justify-content:center;font:13px 'Segoe UI Variable Text','Segoe UI',sans-serif;color:" + faint + ";user-select:none;}" +
               "</style></head><body>" + WebUtility.HtmlEncode(Options.Labels.EmptyPage) + "</body></html>";
    }

    private async Task<bool> EnsureWebAsync()
    {
        if (_web?.CoreWebView2 is not null) return true;
        if (_init is null)
        {
            if (WebViewRuntime.AvailableVersion() is null)
            {
                ShowRuntimeMissing();
                return false;
            }
            _init = CreateAsync();
        }
        try
        {
            await _init;
        }
        finally
        {
            _init = null;
        }
        return _web?.CoreWebView2 is not null;
    }

    private static bool? _compositionAvailable;

    /// <summary>WebView2CompositionControl can run: it needs the Windows SDK projection (Windows.Graphics.Capture).</summary>
    public static bool CompositionAvailable => _compositionAvailable ??= ProbeComposition();

    private static bool ProbeComposition()
    {
        try
        {
            return Type.GetType("Windows.Graphics.Capture.GraphicsCaptureItem, Microsoft.Windows.SDK.NET", throwOnError: false) is not null;
        }
        catch
        {
            return false;
        }
    }

    private WebPreviewHosting ResolveHosting()
    {
        var hosting = Options.Hosting;
        // a per-pixel transparent window (pinned on top) never draws child windows
        if (hosting == WebPreviewHosting.Auto)
            hosting = Window.GetWindow(this) is { AllowsTransparency: true } ? WebPreviewHosting.Composition : WebPreviewHosting.Window;
        if (hosting == WebPreviewHosting.Composition && !CompositionAvailable) hosting = WebPreviewHosting.Picture;
        return hosting;
    }

    private async Task CreateAsync()
    {
        HideMessage();
        FrameworkElement host;
        IWebView2 web;
        var hosting = ResolveHosting();
        _composited = hosting == WebPreviewHosting.Composition;
        _pictureMode = hosting == WebPreviewHosting.Picture;
        if (_composited)
        {
            var c = new WebView2CompositionControl();
            host = c;
            web = c;
        }
        else
        {
            var h = new WebView2();
            host = h;
            web = h;
        }
        web.DefaultBackgroundColor = ToDrawing(ThemeBackground);
        web.ZoomFactor = _zoom;
        host.PreviewKeyDown += OnWebKeyDown;
        _host = host;
        _web = web;
        if (_pictureMode) BuildPictureStage(host);
        else _root.Children.Add(host);
        _holdsRuntime = true;
        try
        {
            var env = await WebViewRuntime.Acquire(Options);
            if (_web != web) return;
            await web.EnsureCoreWebView2Async(env);
            if (_web != web) return;
            var core = web.CoreWebView2;
            Configure(core);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PreviewBridge.Script);
        }
        catch (Exception ex)
        {
            if (_web != web) return;
            ReleaseWebView();
            ShowMessage(Options.Labels.LoadFailed, ex.Message, null);
        }
    }

    private void Configure(CoreWebView2 core)
    {
        var s = core.Settings;
        s.AreDevToolsEnabled = true;
        s.AreDefaultContextMenusEnabled = true;
        s.AreBrowserAcceleratorKeysEnabled = false; // Ctrl+P, Ctrl+F, F5… go to the app instead
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = BrowserZoom;
        s.AreHostObjectsAllowed = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsReputationCheckingRequired = false; // no SmartScreen lookups for local files
        s.IsWebMessageEnabled = true;

        // every request passes the filter: the virtual host is served from memory / disk, other hosts are checked against
        // the setting at the time of the request (so switching it needs no new filter); workers and frames included
        try
        {
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        }
        catch (Exception ex) when (ex is NotImplementedException or InvalidCastException or NotSupportedException or ArgumentException)
        {
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All); // older runtime: documents only
        }
        core.WebResourceRequested += OnResourceRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.WebMessageReceived += OnWebMessage;
        core.ContextMenuRequested += OnContextMenu;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.ProcessFailed += OnProcessFailed;
        core.NavigationCompleted += (_, _) => _navigating = false;
        TryHook(() => core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true); // no protocol handlers from a page
        TryHook(() => core.ScreenCaptureStarting += (_, e) => e.Cancel = true); // a page never sees the screen
        TryHook(() => core.BasicAuthenticationRequested += (_, e) => e.Cancel = true);
        TryHook(() => core.SaveAsUIShowing += (_, e) => e.Cancel = true);
        if (Trace is { } trace)
        {
            core.NavigationCompleted += (_, e) => trace($"nav done ok={e.IsSuccess} status={e.WebErrorStatus} http={e.HttpStatusCode}");
            core.NavigationStarting += (_, e) => trace($"nav start {e.Uri} kind={e.NavigationKind} cancel={e.Cancel}");
            core.WebResourceRequested += (_, e) => trace($"req {e.Request.Uri} served={e.Response is not null}");
        }
    }

    /// <summary>Events newer than the installed runtime throw on subscription: the preview works without them.</summary>
    private static void TryHook(Action hook)
    {
        try
        {
            hook();
        }
        catch (Exception ex) when (ex is NotImplementedException or InvalidCastException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            // older runtime
        }
    }

    // ───────────── browser events ─────────────

    private bool IsDocument(Uri uri) =>
        uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')), _docPath, StringComparison.OrdinalIgnoreCase);

    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            ServeRequest(sender, e);
        }
        catch (Exception ex)
        {
            Trace?.Invoke("serve failed: " + ex);
        }
    }

    private void ServeRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || sender is not CoreWebView2 core) return;
        if (uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase))
        {
            e.Response = IsDocument(uri)
                ? core.Environment.CreateWebResourceResponse(new MemoryStream(_doc, writable: false), 200, "OK",
                    "Content-Type: text/html; charset=utf-8" + Crlf + "Cache-Control: no-store")
                : ServeFile(core.Environment, uri);
            return;
        }
        if (uri.Scheme is not ("http" or "https")) return; // data:, blob:… stay inside the page
        if (!Options.AllowRemoteContent)
        {
            e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            NoteBlocked(uri);
            return;
        }
        RemoteRequested?.Invoke(uri, true);
        if (Options.StubAllowedRemote) e.Response = core.Environment.CreateWebResourceResponse(null, 204, "No Content", "Cache-Control: no-store");
    }

    /// <summary>
    /// A file under the served folder, from the open (unsaved) tab when <see cref="WebPreviewOptions.ReadLocalText"/> has it,
    /// else from disk. (SetVirtualHostNameToFolderMapping cannot be used: a mapped host bypasses WebResourceRequested, so the
    /// document itself would come from disk instead of the unsaved editor buffer.)
    /// </summary>
    private CoreWebView2WebResourceResponse ServeFile(CoreWebView2Environment env, Uri uri)
    {
        if (_mappedFolder is not null)
        {
            try
            {
                var root = Path.GetFullPath(_mappedFolder);
                var path = Path.GetFullPath(Path.Combine(root, Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar)));
                if (IsUnder(path, root, allowSame: false))
                {
                    var type = ContentTypes.Of(path);
                    if (Options.ReadLocalText?.Invoke(path) is { } text)
                    {
                        _loadedFiles.Add(path);
                        var bytes = Encoding.UTF8.GetBytes(text);
                        return env.CreateWebResourceResponse(new MemoryStream(bytes, writable: false), 200, "OK",
                            $"Content-Type: {WithCharset(type)}{Crlf}Content-Length: {bytes.Length}{Crlf}Cache-Control: no-cache");
                    }
                    if (File.Exists(path))
                    {
                        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
                        _loadedFiles.Add(path);
                        return env.CreateWebResourceResponse(stream, 200, "OK",
                            $"Content-Type: {type}{Crlf}Content-Length: {stream.Length}{Crlf}Cache-Control: no-cache");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // locked / invalid name: 404 below
            }
        }
        return env.CreateWebResourceResponse(null, 404, "Not Found", "");
    }

    private static string WithCharset(string type) => type.StartsWith("text/", StringComparison.Ordinal) || type is "application/json" or "image/svg+xml" or "application/xml"
        ? (type.Contains("charset", StringComparison.Ordinal) ? type : type + "; charset=utf-8")
        : type;

    private void NoteBlocked(Uri uri)
    {
        _blocked++;
        var added = uri.Host.Length > 0 && _blockedHosts.Add(uri.Host);
        RemoteRequested?.Invoke(uri, false);
        if ((_blocked == 1 || added) && !_blockedQueued)
        {
            _blockedQueued = true;
            Dispatcher.BeginInvoke(() =>
            {
                _blockedQueued = false;
                if (!_disposed) BlockedChanged?.Invoke();
            }, DispatcherPriority.Background);
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.NavigationKind == CoreWebView2NavigationKind.BackOrForward)
        {
            e.Cancel = true; // mouse back button: there is no history in a preview
            return;
        }
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme == "https" && IsDocument(uri))
        {
            _ready = false;
            return;
        }
        e.Cancel = true;
        if (uri is not null && e.IsUserInitiated) Dispatcher.BeginInvoke(() => FollowLink(uri));
    }

    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (Options.AllowRemoteContent || !Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is "http" or "https" && !uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            NoteBlocked(uri);
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true; // never a second browser window
        if (e.IsUserInitiated && Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) Dispatcher.BeginInvoke(() => FollowLink(uri));
    }

    private void FollowLink(Uri uri)
    {
        if (_disposed) return;
        if (uri.IsFile)
        {
            // a file dropped on the page, or a file:/// link: the app opens it as a tab
            LocalLinkClicked?.Invoke(uri.LocalPath);
            return;
        }
        if (uri.Scheme == "https" && uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase))
        {
            if (_mappedFolder is null) return;
            try
            {
                var root = Path.GetFullPath(_mappedFolder);
                var path = Path.GetFullPath(Path.Combine(root, Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar)));
                if (IsUnder(path, root, allowSame: true)) LocalLinkClicked?.Invoke(path);
            }
            catch
            {
                // malformed path
            }
            return;
        }
        if (uri.Scheme is "http" or "https" or "mailto") Options.OpenExternal(uri);
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            var m = json.RootElement;
            switch (m.GetProperty("t").GetString())
            {
                case "p":
                    RequestPicture();
                    break;
                case "s":
                    _topLine = Math.Max(1, m.GetProperty("line").GetInt32());
                    _topFraction = m.GetProperty("frac").GetDouble();
                    _haveTop = true;
                    Scrolled?.Invoke(_topLine, _topFraction);
                    break;
                case "line":
                    LineRequested?.Invoke(Math.Max(1, m.GetProperty("line").GetInt32()));
                    break;
                case "ready":
                    _ready = true;
                    RequestPicture();
                    DocumentReady?.Invoke();
                    break;
                case "loaded":
                    RequestPicture();
                    DocumentLoaded?.Invoke();
                    BlockedChanged?.Invoke();
                    break;
                case "blocked":
                    // a web socket (or fetch to an odd scheme) stopped by our policy; the page's own policies do not count
                    if (!Options.AllowRemoteContent && m.TryGetProperty("p", out var policy) && policy.GetString() == BlockPolicy &&
                        Uri.TryCreate(m.GetProperty("uri").GetString(), UriKind.Absolute, out var blocked))
                        NoteBlocked(blocked);
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // a page posting its own messages
        }
    }

    private static readonly HashSet<string> KeptMenuItems = new(StringComparer.Ordinal)
    {
        "copy", "cut", "paste", "selectAll", "undo", "redo", "copyLinkLocation", "copyImage", "inspectElement",
    };

    private static void OnContextMenu(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var items = e.MenuItems;
        for (var i = items.Count - 1; i >= 0; i--)
            if (items[i].Kind != CoreWebView2ContextMenuItemKind.Separator && !KeptMenuItems.Contains(items[i].Name))
                items.RemoveAt(i);
        // no separator at either end or twice in a row
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var sep = items[i].Kind == CoreWebView2ContextMenuItemKind.Separator;
            if (sep && (i == 0 || i == items.Count - 1 || items[i - 1].Kind == CoreWebView2ContextMenuItemKind.Separator))
                items.RemoveAt(i);
        }
        if (items.Count == 0) e.Handled = true;
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                ReleaseWebView();
                if (IsVisible) _ = RenderAsync();
            }
            else if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited && _web?.CoreWebView2 is { } core)
            {
                core.Reload();
            }
        });
    }

    private void OnWebKeyDown(object sender, KeyEventArgs e)
    {
        if (Options.HandleDevToolsKey && e.Key == Key.F12 && Keyboard.Modifiers == ModifierKeys.None)
        {
            OpenDevTools();
            e.Handled = true;
        }
    }

    // ───────────── picture hosting (per-pixel transparent windows) ─────────────

    /// <summary>The native WebView sits outside the window's client area (never drawn, never hit), WPF shows its pictures.</summary>
    private const double OffstageX = -32000;

    private void BuildPictureStage(FrameworkElement host)
    {
        _offstage = new Canvas { IsHitTestVisible = false, Focusable = false };
        Canvas.SetLeft(host, OffstageX);
        Canvas.SetTop(host, 0);
        host.Width = Math.Max(1, _root.ActualWidth);
        host.Height = Math.Max(1, _root.ActualHeight);
        _offstage.Children.Add(host);
        _root.Children.Add(_offstage);

        _picture = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(_picture, BitmapScalingMode.HighQuality);
        _pictureHost = new Border { Background = Brushes.Transparent, Child = _picture, Cursor = Cursors.Arrow };
        _pictureHost.MouseWheel += OnPictureWheel;
        _pictureHost.MouseLeftButtonDown += OnPictureDown;
        _pictureHost.MouseLeftButtonUp += OnPictureUp;
        _root.Children.Add(_pictureHost);
        _root.SizeChanged += OnPictureAreaSize;
    }

    private void OnPictureAreaSize(object sender, SizeChangedEventArgs e)
    {
        if (_host is null || !_pictureMode) return;
        _host.Width = Math.Max(1, e.NewSize.Width);
        _host.Height = Math.Max(1, e.NewSize.Height); // the page's resize event asks for a new picture
    }

    /// <summary>A new picture of the page; while one is being taken, another follows right after it (no timer).</summary>
    private async void RequestPicture()
    {
        if (!_pictureMode || _picture is null || _disposed) return;
        if (_capturing)
        {
            _captureAgain = true;
            return;
        }
        _capturing = true;
        try
        {
            do
            {
                _captureAgain = false;
                if (!IsVisible) break;
                var shot = await CaptureAsync();
                if (shot is not null && _picture is not null && _pictureMode) _picture.Source = shot;
            }
            while (_captureAgain && _pictureMode && !_disposed);
        }
        finally
        {
            _capturing = false;
        }
    }

    /// <summary>The picture shown right now, and the element that takes the input over it (tests).</summary>
    internal ImageSource? PictureSource => _picture?.Source;

    internal FrameworkElement? PictureElement => _pictureHost;

    private void RunScript(string script)
    {
        if (_web?.CoreWebView2 is not { } core) return;
        _ = Run(core, script);

        static async Task Run(CoreWebView2 core, string script)
        {
            try
            {
                await core.ExecuteScriptAsync(script);
            }
            catch
            {
                // navigating meanwhile
            }
        }
    }

    /// <summary>A point on the picture in the page's CSS pixels.</summary>
    private string CssPoint(Point p)
    {
        var z = Math.Max(0.05, ZoomFactor);
        return string.Create(CultureInfo.InvariantCulture, $"{p.X / z:0.#},{p.Y / z:0.#}");
    }

    private void OnPictureWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (IsSvg) RunScript(string.Create(CultureInfo.InvariantCulture, $"window.dispatchEvent(new WheelEvent('wheel',{{deltaY:{-e.Delta},ctrlKey:true,cancelable:true}}))"));
            else if (BrowserZoom) ZoomFactor *= e.Delta > 0 ? 1.1 : 1 / 1.1;
            return;
        }
        RunScript(string.Create(CultureInfo.InvariantCulture, $"window.scrollBy(0,{-e.Delta / 120.0 * 100:0.#})"));
    }

    private void OnPictureDown(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(_pictureHost);
        if (e.ClickCount == 2)
            RunScript($"(function(x,y){{var t=document.elementFromPoint(x,y);if(t)t.dispatchEvent(new MouseEvent('dblclick',{{bubbles:true,clientX:x,clientY:y}}));}})({CssPoint(_pressPoint)})");
    }

    private void OnPictureUp(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_pictureHost);
        if (e.ClickCount > 1 || Math.Abs(p.X - _pressPoint.X) > 4 || Math.Abs(p.Y - _pressPoint.Y) > 4) return;
        // a click: a link is followed (ExecuteScript carries a user gesture), anything else gets the click event
        RunScript($"(function(x,y){{var t=document.elementFromPoint(x,y);if(!t)return;var a=t.closest&&t.closest('a[href]');(a||t).click();}})({CssPoint(p)})");
    }

    // ───────────── visibility, theme ─────────────

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (IsVisible)
        {
            if (_web?.CoreWebView2 is { } core) core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
            if (_dirty) _ = RenderAsync();
        }
        else
        {
            if (_timer?.IsEnabled == true)
            {
                _timer.Stop();
                _dirty = true; // refresh when shown again
            }
            if (_web?.CoreWebView2 is { } core) _ = SuspendAsync(core);
        }
    }

    private async Task SuspendAsync(CoreWebView2 core)
    {
        try
        {
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!IsVisible && _web?.CoreWebView2 == core) await core.TrySuspendAsync();
        }
        catch
        {
            // still visible to the browser, or closed meanwhile
        }
    }

    private static void OnThemeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var p = (WebPreview)d;
        if (p._web is { } web && p._backgroundSet is null) web.DefaultBackgroundColor = ToDrawing(p.ThemeBackground);
        // pages painted in the theme's colors (SVG stage, empty page) are composed again; real pages keep their own colors
        if (!(p.IsSvg || p._themedPage) || p._navigatedPath is null) return;
        p._dirty = true;
        if (p.IsVisible) p.Schedule();
    }

    private void ShowRuntimeMissing()
    {
        var link = new Button
        {
            Content = Options.Labels.RuntimeDownload,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
            Padding = new Thickness(14, 5, 14, 6),
        };
        link.SetResourceReference(StyleProperty, "SecondaryButton");
        link.Click += (_, _) => Options.OpenExternal(Options.RuntimeDownloadUri);
        ShowMessage(Options.Labels.RuntimeMissing, Options.Labels.RuntimeMissingHint, link);
    }

    private void ShowMessage(string title, string hint, UIElement? action)
    {
        _message.Children.Clear();
        // a soft badge with a line icon (Segoe Fluent Icons: Globe), like the app's dialogs
        var glyph = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
        var badge = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 0, 16), HorizontalAlignment = HorizontalAlignment.Center, Child = glyph };
        badge.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
        _message.Children.Add(badge);
        var t = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        var h = new TextBlock { Text = hint, FontSize = 12.5, LineHeight = 18, Margin = new Thickness(0, 6, 0, 0), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        h.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        _message.Children.Add(t);
        _message.Children.Add(h);
        if (action is not null) _message.Children.Add(action);
        _message.Visibility = Visibility.Visible;
    }

    private void HideMessage() => _message.Visibility = Visibility.Collapsed;

    private void RemoveCover()
    {
        if (_cover is null) return;
        _root.Children.Remove(_cover);
        _cover = null;
    }

    /// <summary><paramref name="path"/> is <paramref name="folder"/> itself (when allowed) or inside it.</summary>
    internal static bool IsUnder(string path, string folder, bool allowSame = true)
    {
        try
        {
            var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (p.Equals(f, StringComparison.OrdinalIgnoreCase)) return allowSame;
            return p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static System.Drawing.Color ToDrawing(Color c) => System.Drawing.Color.FromArgb(255, c.R, c.G, c.B);

    internal static string Css(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    internal static Color Blend(Color a, Color b, double t)
    {
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }
}
