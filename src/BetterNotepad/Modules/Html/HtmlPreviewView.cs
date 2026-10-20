using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BetterNotepad.Core;
using BetterNotepad.Core.Localization;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Views;
using BetterNotepad.Html;
using BetterNotepad.UI;
using BetterNotepad.UI.Controls;
using BetterNotepad.UI.Windows;
using Glyphs = BetterNotepad.UI.Controls.Glyphs;
using TextDocument = BetterNotepad.Documents.TextDocument;

namespace BetterNotepad.Modules.Html;

/// <summary>
/// <see cref="IPreviewView"/> around <see cref="HtmlPreview"/> / <see cref="SvgPreview"/> (WebView2, created when first shown):
/// live refresh from the unsaved buffer while visible, two-way scroll sync, double-click → source line, links (local files →
/// tab, web → system browser), a calm one-line notice when internet content was blocked, airspace covers while in-window
/// overlays lie over the native page, and release of the browser after ~20 s hidden (at once in the tray).
/// </summary>
internal sealed class HtmlPreviewView : IPreviewView, IReleasableView
{
    /// <summary>Hidden this long → the WebView goes (with the last one, the browser processes end).</summary>
    internal static TimeSpan ReleaseAfter = TimeSpan.FromSeconds(20);

    private readonly TextDocument _doc;
    private readonly WebPreview _web;
    private readonly Grid _root = new();
    private readonly Border _notice = new();
    private readonly TextBlock _noticeText = new();
    private readonly Run _noticeCount = new();
    private readonly Run _noticeHosts = new();
    private Button _noticeSettings = null!;
    private Button _noticeClose = null!;
    private bool _noticeDismissed;
    private bool _bufferStale = true;
    private Debouncer? _release;
    private bool _released;
    private bool _disposed;
    private MainWindow? _window;
    private bool _coverWanted;
    private bool _softCover;
    private bool _softDismissed;

    public HtmlPreviewView(TextDocument doc, bool svg)
    {
        _doc = doc;
        IsSvg = svg;
        _web = svg ? new SvgPreview(HtmlModule.Options) : new HtmlPreview(HtmlModule.Options);
        _web.LineRequested += line => LineRequested?.Invoke(line);
        _web.Scrolled += (line, fraction) => Scrolled?.Invoke(line, fraction);
        _web.LocalLinkClicked += OpenLocal;
        _web.BlockedChanged += UpdateNotice;
        _web.DocumentReady += EvaluateAirspace; // a WebView created while an overlay is open is covered as well
        // a soft cover (a toast over the page) gives way as soon as the user reaches for the page
        _web.PreviewMouseDown += (_, _) => DropSoftCover();
        _web.PreviewMouseWheel += (_, _) => DropSoftCover();

        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.SetResourceReference(Panel.BackgroundProperty, "Brush.Editor.Bg");
        _root.Focusable = true;
        _root.FocusVisualStyle = null;
        // Preview mode focuses the view: the keyboard goes into the page itself
        _root.GotKeyboardFocus += (_, e) =>
        {
            if (ReferenceEquals(e.NewFocus, _root)) _web.FocusPage();
        };
        BuildNotice();
        Grid.SetRow(_notice, 0);
        _root.Children.Add(_notice);
        Grid.SetRow(_web, 1);
        _root.Children.Add(_web);

        _doc.BufferChanged += OnBufferChanged;
        _root.IsVisibleChanged += OnVisibleChanged;
        _root.Loaded += OnLoaded;
        _root.Unloaded += OnUnloaded;
        HtmlModule.Live.Add(this);
    }

    public FrameworkElement Element => _root;

    public event Action<int>? LineRequested;

    public event Action<int, double>? Scrolled;

    public bool IsSvg { get; }

    public TextDocument Document => _doc;

    /// <summary>For tests and snapshots.</summary>
    internal WebPreview Preview => _web;

    internal bool IsReleased => _released;

    internal bool NoticeShown => _notice.Visibility == Visibility.Visible;

    internal string NoticeText => _noticeText.Text;

    /// <summary>The page is on screen with a browser behind it.</summary>
    internal bool IsLive => !_disposed && _root.IsVisible && _web.IsBrowserCreated;

    public void ScrollToLine(int line, double lineFraction, double scrollFraction) => _web.ScrollToLine(line, lineFraction, scrollFraction);

    // ───────────── content ─────────────

    private void OnBufferChanged(object? sender, EventArgs e)
    {
        if (_root.IsVisible) PushText();
        else _bufferStale = true; // hidden (Edit mode, other tab): no copy of the text per keystroke
    }

    private void PushText()
    {
        if (_disposed || !_doc.HasBuffer) return;
        _bufferStale = false;
        _released = false;
        _web.Update(_doc.Buffer.Text, _doc.FilePath, ProjectRoot());
    }

    /// <summary>
    /// The folder served next to the page: the folder open in the Explorer when the file lies inside it (so "../css/x.css"
    /// works across a project), else the file's own folder.
    /// </summary>
    private string? ProjectRoot()
    {
        if (_doc.FilePath is not { } path || Path.GetDirectoryName(path) is not { } folder) return null;
        var windows = new List<MainWindow>();
        if (_window is not null) windows.Add(_window);
        if (App.Current?.MainAppWindow is { } main && main != _window) windows.Add(main);
        foreach (var w in windows)
            if (w.Model.ExplorerRoot is { } root && WebPreview.IsUnder(folder, root)) return root;
        return null;
    }

    /// <summary>A file the page uses changed in another tab (unsaved CSS / JS): load the page again.</summary>
    internal bool DependsOn(string path) => _web.LoadedFiles.Contains(path);

    internal void RefreshNow() => _web.Refresh();

    /// <summary>The internet-content setting changed: load again (the notice follows the new result).</summary>
    internal void OnPolicyChanged()
    {
        if (HtmlModule.Options.AllowRemoteContent) HideNotice();
        _web.Refresh(now: true);
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (_root.IsVisible)
        {
            _release?.Cancel();
            if (_bufferStale) PushText();
        }
        else
        {
            // hidden for a while → the browser goes (re-created and rendered again when shown)
            (_release ??= new Debouncer(ReleaseAfter, ReleaseIfHidden)).Trigger();
        }
        EvaluateAirspace();
    }

    private void ReleaseIfHidden()
    {
        if (_disposed || _root.IsVisible || _released) return;
        _web.ReleaseWebView();
        _coverWanted = false;
        _softCover = false;
        _bufferStale = true;
        _released = true;
        Log.Info($"html: preview released ({_doc.Title}); WebViews left: {WebViewRuntime.ActiveViews}");
    }

    /// <summary>The window went to the tray: free the browser now (instead of 20 s later).</summary>
    public void ReleaseRendering()
    {
        _release?.Cancel();
        ReleaseIfHidden();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loc.LanguageChanged += OnLanguageChanged;
        AttachWindow(MainWindow.Of(_root));
        ApplyTexts();
        if (_bufferStale && _root.IsVisible) PushText();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Loc.LanguageChanged -= OnLanguageChanged;
        AttachWindow(null);
    }

    private void AttachWindow(MainWindow? w)
    {
        if (_window == w) return;
        if (_window is not null) _window.AirspaceChanged -= EvaluateAirspace;
        _window = w;
        if (w is not null) w.AirspaceChanged += EvaluateAirspace;
    }

    private void OnLanguageChanged() => ApplyTexts();

    private void ApplyTexts()
    {
        _noticeSettings.ToolTip = Loc.T("html.blockedSettingsTip");
        _noticeClose.ToolTip = Loc.T("html.blockedDismiss");
        if (_noticeSettings.Content is StackPanel { Children: [_, TextBlock label] }) label.Text = Loc.T("html.blockedSettings");
        UpdateNotice();
    }

    // ───────────── airspace ─────────────

    /// <summary>
    /// A native WebView window lies above every WPF element: while the palette, settings, the tab switcher or a dialog is
    /// open the page becomes a still picture of itself (hard cover). A toast over the page covers it softly: the first click
    /// or wheel on the page brings the live page back (the toast then sinks behind it).
    /// </summary>
    internal void EvaluateAirspace()
    {
        if (_disposed) return;
        var w = _window;
        if (w is null || !_web.NeedsCover || !_root.IsVisible)
        {
            _softCover = false;
            if (_coverWanted || _web.IsCovered) ApplyCover(false);
            return;
        }
        var hard = w.HasCoveringOverlay;
        var toast = !hard && w.ToastArea is { } area && Bounds(w) is { } mine && mine.IntersectsWith(area);
        if (!toast) _softDismissed = false;
        _softCover = toast;
        var want = hard || (toast && !_softDismissed);
        if (want != _coverWanted || (!want && _web.IsCovered)) ApplyCover(want);
    }

    private void ApplyCover(bool want)
    {
        _coverWanted = want;
        _ = _web.SetCoveredAsync(want);
    }

    private void DropSoftCover()
    {
        if (!_softCover || !_coverWanted) return;
        _softDismissed = true;
        ApplyCover(false);
    }

    private Rect? Bounds(MainWindow w)
    {
        try
        {
            return _web.TransformToAncestor(w).TransformBounds(new Rect(0, 0, _web.ActualWidth, _web.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // ───────────── blocked-content notice ─────────────

    private void BuildNotice()
    {
        _notice.Visibility = Visibility.Collapsed;
        _notice.BorderThickness = new Thickness(0, 0, 0, 1);
        _notice.Padding = new Thickness(14, 2, 6, 2);
        _notice.SetResourceReference(Border.BackgroundProperty, "Brush.BgAlt");
        _notice.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");

        var grid = new Grid { MinHeight = 30 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Icon { Glyph = Glyphs.Shield, FontSize = 12.5, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        grid.Children.Add(icon);

        _noticeText.FontSize = 12;
        _noticeText.VerticalAlignment = VerticalAlignment.Center;
        _noticeText.TextTrimming = TextTrimming.CharacterEllipsis;
        _noticeText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        _noticeHosts.SetResourceReference(TextElement.ForegroundProperty, "Brush.TextFaint");
        _noticeText.Inlines.Add(_noticeCount);
        _noticeText.Inlines.Add(_noticeHosts);
        Grid.SetColumn(_noticeText, 1);
        grid.Children.Add(_noticeText);

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Icon { Glyph = Glyphs.Settings, FontSize = 11, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        _noticeSettings = new Button { Content = content, Padding = new Thickness(9, 3, 10, 4), Margin = new Thickness(12, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
        _noticeSettings.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
        _noticeSettings.SetResourceReference(Control.ForegroundProperty, "Brush.AccentText");
        _noticeSettings.Click += (_, _) => HtmlModule.OpenRemoteSetting(_window);
        Grid.SetColumn(_noticeSettings, 2);
        grid.Children.Add(_noticeSettings);

        _noticeClose = new Button { Content = new Icon { Glyph = Glyphs.CloseThin, FontSize = 9 }, Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
        _noticeClose.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        _noticeClose.Click += (_, _) =>
        {
            _noticeDismissed = true; // for this tab: the next page with blocked content does not nag again
            HideNotice();
        };
        Grid.SetColumn(_noticeClose, 3);
        grid.Children.Add(_noticeClose);
        _notice.Child = grid;
    }

    /// <summary>Shown while the page has blocked requests; hidden once a load ends without any (or the setting allows them).</summary>
    private void UpdateNotice()
    {
        if (_disposed) return;
        var count = _web.BlockedCount;
        if (count == 0 || _noticeDismissed || HtmlModule.Options.AllowRemoteContent)
        {
            if (count == 0 || HtmlModule.Options.AllowRemoteContent) HideNotice();
            return;
        }
        var hosts = _web.BlockedHosts.ToList();
        _noticeCount.Text = Loc.Plural("html.blocked", count);
        var shown = string.Join(", ", hosts.Take(2));
        if (hosts.Count > 2) shown += " " + Loc.T("html.blockedMore", hosts.Count - 2);
        _noticeHosts.Text = hosts.Count > 0 ? "   ·   " + shown : "";
        _noticeText.ToolTip = Loc.T("html.blockedTip", string.Join("\n", hosts));
        if (_notice.Visibility != Visibility.Visible)
        {
            _notice.Visibility = Visibility.Visible;
            Motion.Appear(_notice, rise: -6, fromScale: 1, ms: 220);
        }
    }

    private void HideNotice()
    {
        if (_notice.Visibility == Visibility.Collapsed) return;
        _notice.Visibility = Visibility.Collapsed;
    }

    // ───────────── links ─────────────

    private MainWindow? Owner => _window ?? MainWindow.Of(_root);

    private void OpenLocal(string path)
    {
        if (File.Exists(path)) Owner?.OpenFile(path);
        else if (Directory.Exists(path)) HtmlModule.Shell(path, Owner);
        else Owner?.Toasts.Show(Loc.T("html.linkNotFound", Path.GetFileName(path)), ToastKind.Warning);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _release?.Cancel();
        HtmlModule.Live.Remove(this);
        _doc.BufferChanged -= OnBufferChanged;
        _root.IsVisibleChanged -= OnVisibleChanged;
        Loc.LanguageChanged -= OnLanguageChanged;
        AttachWindow(null);
        _web.Dispose();
    }
}
