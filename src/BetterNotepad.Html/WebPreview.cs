using System.Globalization;
using System.IO;
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
/// served from memory at https://bnpreview.local/&lt;file name&gt; while the file's folder is mapped to the same host, so relative
/// CSS / JS / images resolve as in a browser. Typing refreshes after a short debounce (the scroll position is kept), external
/// links go to the system browser, F12 opens DevTools, there is no address bar and the context menu is trimmed.</para>
/// <para>Hidden: suspended with a low memory target. <see cref="ReleaseWebView"/> frees the browser (the processes exit once the
/// last preview released it); the next show creates it again. UI thread only. Source lines are 1-based.</para>
/// </summary>
public abstract class WebPreview : UserControl, IDisposable
{
    internal const string Host = "bnpreview.local";

    private const string Crlf = "\r\n";

    public static readonly DependencyProperty ThemeBackgroundProperty = DependencyProperty.Register(
        nameof(ThemeBackground), typeof(Color), typeof(WebPreview), new PropertyMetadata(Color.FromRgb(0x12, 0x14, 0x1C), OnThemeChanged));

    public static readonly DependencyProperty ThemeForegroundProperty = DependencyProperty.Register(
        nameof(ThemeForeground), typeof(Color), typeof(WebPreview), new PropertyMetadata(Color.FromRgb(0xE4, 0xE7, 0xF0), OnThemeChanged));

    private readonly Grid _root = new();
    private readonly Border _placeholder = new();
    private readonly StackPanel _message = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420, Visibility = Visibility.Collapsed };
    private WebView2? _web;
    private Image? _cover;
    private Task? _init;
    private bool _holdsRuntime;
    private DispatcherTimer? _timer;

    private string _text = "";
    private string? _filePath;
    private byte[] _doc = [];
    private string? _mappedFolder;
    private string? _navigatedName;
    private string _docName = "index.html";
    private bool _dirty = true;
    private bool _ready;
    private bool _disposed;
    private bool _pageBackgroundSet;
    private bool _navigating;
    private double _zoom = 1;

    private int _topLine = 1;
    private double _topFraction;
    private double _topScroll;
    private bool _haveTop;

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

    /// <summary>For the render harness.</summary>
    internal CoreWebView2? Core => _web?.CoreWebView2;

    /// <summary>Diagnostics hook (render harness).</summary>
    internal static Action<string>? Trace;

    /// <summary>The current document reached DOMContentLoaded.</summary>
    public bool IsReady => _ready;

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

    /// <summary>A link to another local file next to this one was clicked (absolute path; the app opens it as a tab).</summary>
    public event Action<string>? LocalLinkClicked;

    /// <summary>A (re)rendered document is ready (DOMContentLoaded).</summary>
    public event Action? DocumentReady;

    /// <summary>
    /// Sets the text to show. <paramref name="filePath"/> gives the base folder for relative resources (null for an unsaved tab).
    /// Debounced while visible; deferred while hidden; the first show renders at once.
    /// </summary>
    public void Update(string text, string? filePath)
    {
        if (_disposed) return;
        text ??= "";
        if (!_dirty && text == _text && filePath == _filePath) return;
        _text = text;
        _filePath = filePath;
        _dirty = true;
        if (!IsVisible) return;
        if (_web is null || _navigatedName is null) _ = RenderAsync();
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
        RemoveCover();
        if (_web is { } web)
        {
            _web = null;
            _root.Children.Remove(web);
            try
            {
                web.Dispose();
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
        _navigatedName = null;
        _pageBackgroundSet = false;
        _navigating = false;
        _ready = false;
        _dirty = true;
    }

    /// <summary>
    /// WebView2 is a native window drawn above all WPF content (airspace). Before an in-window overlay (palette, dialog) covers
    /// the preview, call with true: the page is swapped for a still picture of itself; false brings the live page back.
    /// </summary>
    public async Task SetCoveredAsync(bool covered)
    {
        if (!covered)
        {
            RemoveCover();
            if (_web is not null) _web.Visibility = Visibility.Visible;
            return;
        }
        if (_cover is not null || _web?.CoreWebView2 is null) return;
        var shot = await CaptureAsync();
        if (shot is null || _web is null) return;
        _cover = new Image { Source = shot, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_cover, BitmapScalingMode.HighQuality);
        _root.Children.Add(_cover);
        _web.Visibility = Visibility.Hidden;
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
        ms.Position = 0;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Opens the DevTools window (also F12 inside the preview).</summary>
    public void OpenDevTools() => _web?.CoreWebView2?.OpenDevToolsWindow();

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
        var name = _filePath is null ? (IsSvg ? "image.svg" : "index.html") : Path.GetFileName(_filePath);
        _doc = Encoding.UTF8.GetBytes(Compose(_text, ConfigTag()));

        if (!_pageBackgroundSet)
        {
            _pageBackgroundSet = true;
            _web.DefaultBackgroundColor = ToDrawing(PageBackground());
        }
        if (!string.Equals(folder, _mappedFolder, StringComparison.OrdinalIgnoreCase))
        {
            _mappedFolder = folder;
            _navigatedName = null;
        }
        _ready = false;
        var url = $"https://{Host}/{Uri.EscapeDataString(name)}";
        // Reload keeps no history and lets Chromium hold the old frame until the new one paints; but only once our document
        // is really there (a reload during the first navigation would reload about:blank)
        if (!_navigating && string.Equals(_navigatedName, name, StringComparison.OrdinalIgnoreCase) && core.Source == url)
        {
            core.Reload();
        }
        else
        {
            _navigatedName = name;
            _docName = name;
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
        json.Append(CultureInfo.InvariantCulture, $"{{\"lines\":{lines},\"svg\":{(IsSvg ? "true" : "false")}");
        if (_haveTop)
            json.Append(CultureInfo.InvariantCulture, $",\"restore\":{{\"line\":{_topLine},\"frac\":{_topFraction:0.####},\"sf\":{_topScroll:0.####}}}");
        json.Append('}');
        return $"<meta name=\"{PreviewBridge.MetaName}\" content=\"{json.Replace("\"", "&quot;")}\">";
    }

    private async Task<bool> EnsureWebAsync()
    {
        if (_web?.CoreWebView2 is not null) return true;
        if (_init is null)
        {
            if (WebViewRuntime.AvailableVersion() is null)
            {
                ShowMessage(Options.Labels.RuntimeMissing, Options.Labels.RuntimeMissingHint);
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

    private async Task CreateAsync()
    {
        HideMessage();
        var web = new WebView2
        {
            DefaultBackgroundColor = ToDrawing(ThemeBackground),
            ZoomFactor = _zoom,
        };
        web.PreviewKeyDown += OnWebKeyDown;
        _web = web;
        _root.Children.Add(web);
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
            ShowMessage(Options.Labels.LoadFailed, ex.Message);
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

        core.AddWebResourceRequestedFilter(Options.AllowRemoteContent ? $"https://{Host}/*" : "*", CoreWebView2WebResourceContext.All);
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
        if (Trace is { } trace)
        {
            core.NavigationCompleted += (_, e) => trace($"nav done ok={e.IsSuccess} status={e.WebErrorStatus} http={e.HttpStatusCode}");
            core.NavigationStarting += (_, e) => trace($"nav start {e.Uri} kind={e.NavigationKind} cancel={e.Cancel}");
            core.WebResourceRequested += (_, e) => trace($"req {e.Request.Uri} served={e.Response is not null}");
        }
    }

    // ───────────── browser events ─────────────

    private bool IsDocument(Uri uri) =>
        uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')), _docName, StringComparison.OrdinalIgnoreCase);

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
        if (!Options.AllowRemoteContent && uri.Scheme is "http" or "https")
            e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
    }

    /// <summary>
    /// A file next to the document, from disk. (SetVirtualHostNameToFolderMapping cannot be used: a mapped host bypasses
    /// WebResourceRequested, so the document itself would come from disk instead of the unsaved editor buffer.)
    /// </summary>
    private CoreWebView2WebResourceResponse ServeFile(CoreWebView2Environment env, Uri uri)
    {
        if (_mappedFolder is not null)
        {
            try
            {
                var root = Path.GetFullPath(_mappedFolder);
                var path = Path.GetFullPath(Path.Combine(root, Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))));
                if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                {
                    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
                    return env.CreateWebResourceResponse(stream, 200, "OK",
                        $"Content-Type: {ContentTypes.Of(path)}{Crlf}Content-Length: {stream.Length}{Crlf}Cache-Control: no-cache");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // locked / invalid name: 404 below
            }
        }
        return env.CreateWebResourceResponse(null, 404, "Not Found", "");
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
        if (uri.Scheme is "http" or "https" && !uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true; // never a second browser window
        if (e.IsUserInitiated && Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) Dispatcher.BeginInvoke(() => FollowLink(uri));
    }

    private void FollowLink(Uri uri)
    {
        if (_disposed) return;
        if (uri.Scheme == "https" && uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase))
        {
            if (_mappedFolder is null) return;
            try
            {
                var root = Path.GetFullPath(_mappedFolder);
                var path = Path.GetFullPath(Path.Combine(root, Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))));
                if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) LocalLinkClicked?.Invoke(path);
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
                    DocumentReady?.Invoke();
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
        if (e.Key == Key.F12 && Keyboard.Modifiers == ModifierKeys.None)
        {
            OpenDevTools();
            e.Handled = true;
        }
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
        if (p._web is { } web && !p._pageBackgroundSet) web.DefaultBackgroundColor = ToDrawing(p.ThemeBackground);
        if (!p.IsSvg || p._navigatedName is null) return;
        p._pageBackgroundSet = false;
        p._dirty = true;
        if (p.IsVisible) p.Schedule();
    }

    private void ShowMessage(string title, string hint)
    {
        _message.Children.Clear();
        var t = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        var h = new TextBlock { Text = hint, FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        h.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        _message.Children.Add(t);
        _message.Children.Add(h);
        _message.Visibility = Visibility.Visible;
    }

    private void HideMessage() => _message.Visibility = Visibility.Collapsed;

    private void RemoveCover()
    {
        if (_cover is null) return;
        _root.Children.Remove(_cover);
        _cover = null;
    }

    internal static System.Drawing.Color ToDrawing(Color c) => System.Drawing.Color.FromArgb(255, c.R, c.G, c.B);

    internal static string Css(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    internal static Color Blend(Color a, Color b, double t)
    {
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }
}
