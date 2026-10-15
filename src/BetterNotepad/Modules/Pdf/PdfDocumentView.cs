using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Views;
using BetterNotepad.Editor;
using BetterNotepad.Modules.Info;
using BetterNotepad.Pdf;
using BetterNotepad.UI;
using BetterNotepad.UI.Controls;
using BetterNotepad.UI.Windows;
using Glyphs = BetterNotepad.UI.Controls.Glyphs;

namespace BetterNotepad.Modules.Pdf;

/// <summary>
/// PDF tab: a bar (thumbnails, page, zoom, search, print, convert to Word), a thumbnails sidebar and a virtualized list
/// of pages that renders only what is visible at the current zoom. Text can be selected across pages (drag with
/// auto-scroll, Shift+click, double / triple click, Ctrl+A) and copied. Protected files ask for their password.
/// </summary>
public sealed class PdfDocumentView : Grid, IDocumentView, IZoomableView, ICompactView, IReleasableView
{
    private const double PointToDip = 96.0 / 72.0;
    private const double ThumbWidth = 112;
    /// <summary>Copy limit (UTF-16 chars, ~8 MB): larger selections copy whole pages up to it and say so.</summary>
    internal static int MaxCopyChars { get; set; } = 4_000_000;
    /// <summary>Caret meaning "to the end of the page" (Ctrl+A, dragging into the gap below a page) - no text layout needed.</summary>
    private const int PageEnd = int.MaxValue;
    private const double AutoScrollEdge = 36;

    private readonly FileInfoDocument _doc;
    private readonly PdfStrip _pages = new(14, new Thickness(24, 20, 24, 28));
    private readonly PdfStrip _thumbs = new(14, new Thickness(16, 14, 16, 14));
    private readonly Border _sidebar = new() { Width = ThumbWidth + 34, BorderThickness = new Thickness(0, 0, 1, 0) };
    private readonly Grid _body = new();
    private readonly TextBox _pageBox = new() { Width = 44, TextAlignment = TextAlignment.Center, FontSize = 12 };
    private readonly TextBlock _pageCount = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(6, 0, 14, 0) };
    private readonly TextBlock _zoomText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Width = 44, TextAlignment = TextAlignment.Center };
    private readonly TextBox _search = new() { Width = 190, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _searchHint = new() { FontSize = 12, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(30, 0, 0, 0) };
    private readonly TextBlock _hitCount = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(8, 0, 4, 0), MinWidth = 36 };
    private readonly TextBlock _message = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, TextAlignment = TextAlignment.Center, FontSize = 13 };
    private readonly ToggleButton _sideToggle = new();
    private readonly List<(FrameworkElement Element, string Key, string? Command)> _tips = [];
    private readonly Dictionary<int, PdfPageText> _layouts = new();
    private readonly HashSet<int> _layoutsLoading = new();
    private readonly StackPanel _locked = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 400, Visibility = Visibility.Collapsed };
    private readonly TextBlock _lockedTitle = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 0) };
    private readonly TextBlock _lockedText = new() { FontSize = 13, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), LineHeight = 19 };
    private readonly TextBlock _unlockLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _autoScroll = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
    private FrameworkElement? _barContent;
    private PdfDocumentHandle? _handle;
    private CancellationTokenSource? _life = new();
    private double _zoom = 1;
    private bool _fitWidth = true;
    private string _query = "";
    private IReadOnlyList<PdfSearchHit> _hits = [];
    private int _hit = -1;
    // selection: anchor = where it started, focus = the moving end; (page, caret) each, caret PageEnd = end of page
    private (int Page, int Caret) _anchor = (-1, 0);
    private (int Page, int Caret) _focus = (-1, 0);
    private bool _selecting;
    private Point _dragPoint;
    private bool _copying;
    private bool _busy;
    private bool _opening;
    private bool _isLocked;
    private bool _hibernated;

    public PdfDocumentView(FileInfoDocument doc)
    {
        _doc = doc;
        SetResourceReference(BackgroundProperty, "Brush.BgAlt");
        Focusable = true;
        FocusVisualStyle = null;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Children.Add(BuildBar());

        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _sidebar.SetResourceReference(Border.BackgroundProperty, "Brush.Chrome");
        _sidebar.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        _sidebar.Child = _thumbs;
        _body.Children.Add(_sidebar);
        SetColumn(_pages, 1);
        _body.Children.Add(_pages);
        _message.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        SetColumn(_message, 1);
        _body.Children.Add(_message);
        BuildLocked();
        SetColumn(_locked, 1);
        _body.Children.Add(_locked);
        SetRow(_body, 1);
        Children.Add(_body);

        _pages.Realized += slot => _ = RenderPage(slot);
        _pages.Released += slot => slot.Cts?.Cancel();
        _pages.CurrentChanged += OnCurrentPage;
        _pages.SizeChanged += (_, e) =>
        {
            if (_fitWidth && e.WidthChanged && _handle is not null) ApplyZoom(FitZoom(), fit: true);
        };
        _pages.PreviewMouseWheel += OnWheel;
        _pages.PreviewMouseLeftButtonDown += OnPageMouseDown;
        _pages.PreviewMouseMove += OnPageMouseMove;
        _pages.PreviewMouseLeftButtonUp += OnPageMouseUp;
        _pages.LostMouseCapture += (_, _) => EndDrag();
        _autoScroll.Tick += OnAutoScroll;
        _pages.Cursor = Cursors.IBeam;
        _pages.Background = Brushes.Transparent;

        _thumbs.Realized += slot => _ = RenderThumb(slot);
        _thumbs.Released += slot => slot.Cts?.Cancel();
        _thumbs.Cursor = Cursors.Hand;
        _thumbs.Background = Brushes.Transparent;
        _thumbs.MouseLeftButtonUp += (_, e) =>
        {
            if (_thumbs.HitPage(e.GetPosition(_thumbs), out var i, out Point _)) _pages.ScrollToPage(i);
        };

        PreviewKeyDown += OnKey;
        IsVisibleChanged += OnVisibleChanged;
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            Loc.LanguageChanged -= ApplyTexts;
            EndDrag();
        };
        ApplyTexts();
    }

    public Document Document => _doc;

    public FrameworkElement Element => this;

    public CodeEditor? Editor => null;

    /// <summary>For tests and snapshots.</summary>
    internal PdfDocumentHandle? Handle => _handle;

    internal int PageCountOrZero => _handle?.PageCount ?? 0;

    internal IReadOnlyList<PdfSearchHit> Hits => _hits;

    internal double ZoomFactor => _zoom;

    internal bool FitsWidth => _fitWidth;

    /// <summary>Pages realized in the strip / with a rendered bitmap; the page at the top (tests).</summary>
    internal int LivePageCount => _pages.Live.Count;

    internal int RenderedPageCount => _pages.Live.Values.Count(s => s.Image.Source is not null);

    internal int CurrentPage => _pages.Current;

    internal bool IsHibernated => _hibernated;

    internal void ScrollToPage(int index) => _pages.ScrollToPage(index);

    /// <summary>Cancelled the password prompt: the tab shows the locked state with "Enter password".</summary>
    internal bool IsLocked => _isLocked;

    /// <summary>The open password prompt (tests and snapshots).</summary>
    internal PdfPasswordPrompt? Prompt { get; private set; }

    internal ((int Page, int Caret) Start, (int Page, int Caret) End)? Selection => HasSelection ? Ordered() : null;

    /// <summary>Pinned on top: the page gets the whole (small) window; the thumbnails stay one click away.</summary>
    public void EnterCompact()
    {
        _sideToggle.IsChecked = false;
        UpdateSidebar();
    }

    // ───────────── bar ─────────────

    private FrameworkElement BuildBar()
    {
        var bar = new Grid { Height = 42, Margin = new Thickness(8, 0, 10, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _sideToggle.SetResourceReference(StyleProperty, "IconButton");
        _sideToggle.Content = new Icon { Glyph = "", FontSize = 14 };
        _sideToggle.IsChecked = true;
        _sideToggle.Margin = new Thickness(0, 0, 10, 0);
        _sideToggle.Click += (_, _) => UpdateSidebar();
        Tip(_sideToggle, "pdf.thumbnails");
        left.Children.Add(_sideToggle);
        _pageBox.SetResourceReference(StyleProperty, "Field");
        _pageBox.Padding = new Thickness(4, 3, 4, 3);
        _pageBox.KeyDown += OnPageBoxKey;
        _pageBox.GotKeyboardFocus += (_, _) => _pageBox.SelectAll();
        Tip(_pageBox, "pdf.pageTip");
        left.Children.Add(_pageBox);
        _pageCount.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        left.Children.Add(_pageCount);
        left.Children.Add(Separator());
        left.Children.Add(BarButton("", "pdf.zoomOut", () => ZoomStep(-1), "view.zoomOut"));
        _zoomText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        left.Children.Add(_zoomText);
        left.Children.Add(BarButton("", "pdf.zoomIn", () => ZoomStep(+1), "view.zoomIn"));
        left.Children.Add(BarButton("", "pdf.fitWidth", ZoomReset, "view.zoomReset"));
        bar.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var searchHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
        _search.SetResourceReference(StyleProperty, "Field");
        _search.Padding = new Thickness(14, 1, 4, 1); // the Field template applies Padding twice (host margin + text view)
        _search.TextAlignment = TextAlignment.Left;
        _search.HorizontalContentAlignment = HorizontalAlignment.Left;
        _search.KeyDown += OnSearchKey;
        _search.TextChanged += (_, _) => _searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var searchIcon = new Icon { Glyph = Glyphs.Search, FontSize = 12, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(10, 0, 0, 0) };
        searchIcon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        _searchHint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextFaint");
        searchHost.Children.Add(_search);
        searchHost.Children.Add(searchIcon);
        searchHost.Children.Add(_searchHint);
        right.Children.Add(searchHost);
        _hitCount.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        right.Children.Add(_hitCount);
        right.Children.Add(BarButton(Glyphs.ChevronUp, "pdf.prevHit", () => _ = NextHit(-1)));
        right.Children.Add(BarButton(Glyphs.ChevronDown, "pdf.nextHit", () => _ = NextHit(+1)));
        right.Children.Add(Separator());
        right.Children.Add(BarButton(Glyphs.Print, "pdf.print", () => _ = PrintAsync()));
        right.Children.Add(BarButton("", "pdf.toWord", () => _ = ConvertAsync()));
        Grid.SetColumn(right, 2);
        bar.Children.Add(right);

        var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
        line.SetResourceReference(Border.BackgroundProperty, "Brush.Border");
        var head = new Grid();
        head.SetResourceReference(BackgroundProperty, "Brush.Editor.Bg");
        head.Children.Add(bar);
        head.Children.Add(line);
        _barContent = bar;
        return head;
    }

    private Button BarButton(string glyph, string tipKey, Action click, string? command = null)
    {
        var b = new Button { Content = new Icon { Glyph = glyph, FontSize = 13 }, Width = 30, Height = 30 };
        b.SetResourceReference(StyleProperty, "IconButton");
        b.Click += (_, _) => click();
        Tip(b, tipKey, command);
        return b;
    }

    private static Border Separator()
    {
        var s = new Border { Width = 1, Height = 18, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        s.SetResourceReference(Border.BackgroundProperty, "Brush.Border");
        return s;
    }

    private void Tip(FrameworkElement e, string key, string? command = null) => _tips.Add((e, key, command));

    private void ApplyTexts()
    {
        foreach (var (e, key, command) in _tips)
        {
            // the shortcut comes from the keymap, so a rebound (or removed) shortcut shows correctly
            var keys = new List<string>(2);
            if (command is not null && Keymap.GestureText(command) is { Length: > 0 } gesture) keys.Add(gesture);
            if (command is "view.zoomIn" or "view.zoomOut") keys.Add(Loc.T("pdf.ctrlWheel"));
            e.ToolTip = keys.Count == 0 ? Loc.T(key) : $"{Loc.T(key)} ({string.Join(", ", keys)})";
        }
        _searchHint.Text = Loc.T("pdf.searchHint");
        _lockedTitle.Text = Loc.T("pdf.locked.title");
        _lockedText.Text = Loc.T("pdf.locked.text");
        _unlockLabel.Text = Loc.T("pdf.locked.unlock");
        UpdatePageText();
        UpdateHitText();
    }

    /// <summary>Thumbnails only for an open document (not beside the locked state or a message).</summary>
    private void UpdateSidebar() =>
        _sidebar.Visibility = _sideToggle.IsChecked == true && _handle is not null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Locked state (password prompt cancelled): lock badge, explanation and "Enter password".</summary>
    private void BuildLocked()
    {
        var badge = new Border { Width = 60, Height = 60, CornerRadius = new CornerRadius(20), HorizontalAlignment = HorizontalAlignment.Center };
        badge.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
        var icon = new Icon { Glyph = Glyphs.Lock, FontSize = 26 };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
        badge.Child = icon;
        _locked.Children.Add(badge);
        _lockedTitle.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        _lockedTitle.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");
        _locked.Children.Add(_lockedTitle);
        _lockedText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        _locked.Children.Add(_lockedText);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Icon { Glyph = "\uE8D7", FontSize = 13, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center }); // Permissions (a key)
        content.Children.Add(_unlockLabel);
        var unlock = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0), MinWidth = 150 };
        unlock.SetResourceReference(StyleProperty, "PrimaryButton");
        unlock.Click += (_, _) => _ = UnlockAsync();
        _locked.Children.Add(unlock);
    }

    // ───────────── open ─────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loc.LanguageChanged -= ApplyTexts;
        Loc.LanguageChanged += ApplyTexts;
        ApplyTexts(); // shortcuts may have been rebound while the tab was hidden
        if (_handle is not null || _opening || _isLocked || _doc.FilePath is null) return;
        await OpenAsync(fromLocked: false);
    }

    /// <summary>
    /// Opens the file; a protected one first tries the password entered earlier in this session, else asks for it.
    /// Cancelling the prompt leaves the tab in the locked state.
    /// </summary>
    private async Task OpenAsync(bool fromLocked)
    {
        if (_doc.FilePath is not { } path || _opening) return;
        _opening = true;
        UpdateSidebar();
        if (!fromLocked) ShowMessage(Loc.T("pdf.opening"));
        try
        {
            PdfDocumentHandle? handle;
            var known = PdfPasswords.Get(path);
            try
            {
                handle = await PdfDocumentHandle.OpenAsync(path, known, _life?.Token ?? default);
            }
            catch (PdfOpenException ex) when (ex.Error is PdfOpenError.PasswordRequired or PdfOpenError.IncorrectPassword)
            {
                if (known is not null) PdfPasswords.Forget(path); // the file got another password meanwhile
                handle = await AskPasswordAsync(path);
            }
            if (_life is null) // disposed while opening
            {
                handle?.Dispose();
                return;
            }
            if (handle is null)
            {
                ShowLocked();
                return;
            }
            _handle = handle;
            _isLocked = false;
            _locked.Visibility = Visibility.Collapsed;
            _message.Visibility = Visibility.Collapsed;
            if (_barContent is not null) _barContent.IsEnabled = true;
            UpdateSidebar();
            _thumbs.SetSizes(Enumerable.Range(0, _handle.PageCount).Select(i => ThumbSize(i)).ToList(), keepPosition: false);
            UpdateLayout();
            ApplyZoom(FitZoom(), fit: true);
            UpdatePageText();
        }
        catch (PdfOpenException ex)
        {
            ShowMessage(OpenErrorText(ex.Error));
            Log.Warn($"pdf: open {path}: {ex.Error} {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowMessage(Loc.T("pdf.openFailed", ex.Message));
            Log.Warn($"pdf: open {path}: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _opening = false;
        }
    }

    private static string OpenErrorText(PdfOpenError error) => error switch
    {
        PdfOpenError.UnsupportedSecurity => Loc.T("pdf.unsupportedSecurity"),
        PdfOpenError.FileNotFound => Loc.T("info.missing"),
        PdfOpenError.AccessDenied => Loc.T("pdf.accessDenied"),
        _ => Loc.T("pdf.invalid"),
    };

    private void ShowMessage(string text)
    {
        _message.Text = text;
        _message.Visibility = Visibility.Visible;
        _locked.Visibility = Visibility.Collapsed;
        if (_barContent is not null) _barContent.IsEnabled = _handle is not null;
    }

    private void ShowLocked()
    {
        _isLocked = true;
        _message.Visibility = Visibility.Collapsed;
        if (_barContent is not null) _barContent.IsEnabled = false;
        if (_locked.Visibility == Visibility.Visible) return;
        _locked.Visibility = Visibility.Visible;
        Motion.Appear(_locked, rise: 10, fromScale: 0.98, ms: 260);
    }

    /// <summary>"Enter password" in the locked state (also used by tests).</summary>
    internal Task UnlockAsync() => _handle is null && !_opening ? OpenAsync(fromLocked: _isLocked) : Task.CompletedTask;

    /// <summary>
    /// The password dialog. A wrong password keeps the dialog open, shakes it and says so. Returns the opened document,
    /// or null when cancelled. The password is remembered in memory for this session only (<see cref="PdfPasswords"/>).
    /// </summary>
    private async Task<PdfDocumentHandle?> AskPasswordAsync(string path)
    {
        // another view of the same file is asking right now: wait for it and use its answer
        if (PdfPasswords.PromptInProgress(path) is { } other)
        {
            await other;
            if (PdfPasswords.Get(path) is { } shared)
            {
                try
                {
                    return await PdfDocumentHandle.OpenAsync(path, shared, _life?.Token ?? default);
                }
                catch (PdfOpenException)
                {
                    PdfPasswords.Forget(path);
                }
            }
        }
        if (MainWindow.Of(this) is not { } w) return null;
        var done = new TaskCompletionSource();
        PdfPasswords.BeginPrompt(path, done.Task);
        var prompt = new PdfPasswordPrompt();
        Prompt = prompt;
        PdfDocumentHandle? opened = null;
        try
        {
            var choice = await w.Dialogs.Show(Loc.T("pdf.pw.title"), Loc.T("pdf.pw.text", _doc.Title),
                [
                    new DialogButton("cancel", Loc.T("common.cancel"), DialogButtonKind.Ghost, IsCancel: true),
                    new DialogButton("open", Loc.T("pdf.pw.open"), DialogButtonKind.Primary, IsDefault: true),
                ],
                prompt.Element, Glyphs.Lock,
                validate: async _ =>
                {
                    var password = prompt.Password;
                    if (password.Length == 0)
                    {
                        prompt.ShowError(Loc.T("pdf.pw.empty"));
                        return false;
                    }
                    try
                    {
                        opened = await PdfDocumentHandle.OpenAsync(path, password, _life?.Token ?? default);
                        PdfPasswords.Remember(path, password);
                        return true;
                    }
                    catch (PdfOpenException ex) when (ex.Error is PdfOpenError.IncorrectPassword or PdfOpenError.PasswordRequired)
                    {
                        prompt.ShowError(Loc.T("pdf.pw.wrong"));
                        return false;
                    }
                    catch (PdfOpenException ex)
                    {
                        prompt.ShowError(OpenErrorText(ex.Error));
                        return false;
                    }
                    catch (OperationCanceledException)
                    {
                        return true; // the tab was closed meanwhile
                    }
                },
                focus: prompt.FocusTarget);
            if (choice == "open") return opened;
            opened?.Dispose();
            return null;
        }
        finally
        {
            Prompt = null;
            PdfPasswords.EndPrompt(path);
            done.TrySetResult();
        }
    }

    // ───────────── zoom ─────────────

    private static readonly double[] Steps = [0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5];

    /// <summary>Zoom shortcuts (Ctrl+= / Ctrl+- and the numpad): the same steps as the toolbar.</summary>
    public void ZoomStep(int direction)
    {
        if (direction != 0) ApplyZoom(Step(direction), fit: false);
    }

    /// <summary>Ctrl+0: fit width.</summary>
    public void ZoomReset() => ApplyZoom(FitZoom(), fit: true);

    private double Step(int dir)
    {
        if (dir > 0) return Steps.FirstOrDefault(s => s > _zoom + 0.001, Steps[^1]);
        return Steps.LastOrDefault(s => s < _zoom - 0.001, Steps[0]);
    }

    private double FitZoom()
    {
        if (_handle is null || _handle.PageCount == 0) return 1;
        var avail = (_pages.ActualWidth > 0 ? _pages.ActualWidth : 800) - _pages.Padding.Left - _pages.Padding.Right - SystemParameters.VerticalScrollBarWidth;
        var widest = _handle.PageSizes.Max(s => s.Width) * PointToDip;
        return Math.Clamp(avail / Math.Max(1, widest), 0.25, 5);
    }

    private void ApplyZoom(double zoom, bool fit)
    {
        _fitWidth = fit;
        zoom = Math.Clamp(zoom, 0.25, 5);
        if (_handle is null) return;
        if (Math.Abs(zoom - _zoom) < 0.0005 && _pages.Count == _handle.PageCount)
        {
            UpdateZoomText();
            return;
        }
        _zoom = zoom;
        _pages.SetSizes(Enumerable.Range(0, _handle.PageCount).Select(PageSize).ToList());
        UpdateZoomText();
        if (_selecting) ExtendTo(_dragPoint);
    }

    private void UpdateZoomText() => _zoomText.Text = Math.Round(_zoom * 100) + "%";

    private Size PageSize(int i)
    {
        var s = _handle!.PageSizes[i];
        return new Size(Math.Round(s.Width * _zoom * PointToDip), Math.Round(s.Height * _zoom * PointToDip));
    }

    private Size ThumbSize(int i)
    {
        var s = _handle!.PageSizes[i];
        return new Size(ThumbWidth, Math.Round(ThumbWidth * s.Height / Math.Max(1, s.Width)));
    }

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        ZoomStep(e.Delta > 0 ? +1 : -1);
    }

    // ───────────── rendering ─────────────

    private async Task RenderPage(PageSlot slot)
    {
        if (_handle is null) return;
        slot.Background = Brushes.White; // paper
        slot.Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Direction = 270, Opacity = 0.22, Color = ThemeShadow() };
        DrawOverlay(slot);
        var scale = PdfDocumentHandle.ScaleForZoom(_zoom, DpiScale);
        slot.Cts = new CancellationTokenSource();
        try
        {
            var bmp = await _handle.RenderPageAsync(slot.Index, scale, slot.Cts.Token);
            if (!_pages.Live.TryGetValue(slot.Index, out var live) || live != slot) return;
            slot.Image.Source = bmp;
            slot.RenderedScale = scale;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"pdf: render page {slot.Index + 1}: {ex.Message}");
        }
    }

    private async Task RenderThumb(PageSlot slot)
    {
        if (_handle is null) return;
        slot.Background = Brushes.White;
        slot.Cts = new CancellationTokenSource();
        DrawThumbOverlay(slot);
        try
        {
            var bmp = await _handle.GetThumbnailAsync(slot.Index, (int)Math.Ceiling(ThumbWidth * 1.6 * DpiScale), slot.Cts.Token);
            if (_thumbs.Live.TryGetValue(slot.Index, out var live) && live == slot) slot.Image.Source = bmp;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"pdf: thumbnail {slot.Index + 1}: {ex.Message}");
        }
    }

    private static Color ThemeShadow() => Application.Current?.TryFindResource("Color.Shadow") is Color c ? c : Colors.Black;

    private void DrawThumbOverlay(PageSlot slot)
    {
        slot.Overlay.Children.Clear();
        var current = slot.Index == _pages.Current;
        var frame = new Border
        {
            Width = slot.Width + 6,
            Height = slot.Height + 6,
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(current ? 2 : 1),
        };
        frame.SetResourceReference(Border.BorderBrushProperty, current ? "Brush.Accent" : "Brush.Border");
        Canvas.SetLeft(frame, -3);
        Canvas.SetTop(frame, -3);
        slot.Overlay.Children.Add(frame);
        var chip = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 2) };
        chip.SetResourceReference(Border.BackgroundProperty, current ? "Brush.AccentFill" : "Brush.Panel");
        var num = new TextBlock { Text = (slot.Index + 1).ToString(), FontSize = 11 };
        num.SetResourceReference(TextBlock.ForegroundProperty, current ? "Brush.OnAccent" : "Brush.PanelText");
        chip.Child = num;
        chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(chip, (slot.Width - chip.DesiredSize.Width) / 2);
        Canvas.SetTop(chip, slot.Height - chip.DesiredSize.Height - 6);
        slot.Overlay.Children.Add(chip);
    }

    /// <summary>Search hits (amber, the current one stronger) and the selection (accent) over one page.</summary>
    private void DrawOverlay(PageSlot slot)
    {
        slot.Overlay.Children.Clear();
        var k = _zoom * PointToDip;
        for (var h = 0; h < _hits.Count; h++)
        {
            var hit = _hits[h];
            if (hit.PageIndex != slot.Index) continue;
            foreach (var r in hit.Rects) AddRect(slot, r.Inflate(1).ToRect(k), "Brush.Warning", h == _hit ? 0.55 : 0.28);
        }
        slot.DrawnSelection = (0, 0);
        if (!Covers(slot.Index)) return;
        if (!_layouts.TryGetValue(slot.Index, out var layout))
        {
            _ = LoadLayoutAndRedraw(slot.Index);
            return;
        }
        var (a, b) = RangeOn(slot.Index, layout.Length);
        if (b <= a) return;
        foreach (var r in layout.GetSelectionRects(a, b)) AddRect(slot, r.ToRect(k), "Brush.Accent", 0.3);
        slot.DrawnSelection = (a, b);
    }

    private static void AddRect(PageSlot slot, Rect r, string brush, double opacity)
    {
        var rect = new Rectangle { Width = r.Width, Height = r.Height, Opacity = opacity, RadiusX = 2, RadiusY = 2 };
        rect.SetResourceReference(Shape.FillProperty, brush);
        Canvas.SetLeft(rect, r.Left);
        Canvas.SetTop(rect, r.Top);
        slot.Overlay.Children.Add(rect);
    }

    private void RedrawOverlays()
    {
        foreach (var slot in _pages.Live.Values) DrawOverlay(slot);
    }

    /// <summary>After the selection changed: redraws only the visible pages whose selected range differs.</summary>
    private void RedrawSelection()
    {
        foreach (var slot in _pages.Live.Values)
        {
            var want = (0, 0);
            if (Covers(slot.Index))
            {
                if (!_layouts.TryGetValue(slot.Index, out var layout))
                {
                    _ = LoadLayoutAndRedraw(slot.Index);
                    continue;
                }
                var (a, b) = RangeOn(slot.Index, layout.Length);
                if (b > a) want = (a, b);
            }
            if (slot.DrawnSelection != want) DrawOverlay(slot);
        }
    }

    // ───────────── page navigation ─────────────

    private void OnCurrentPage(int page)
    {
        UpdatePageText();
        foreach (var slot in _thumbs.Live.Values) DrawThumbOverlay(slot);
        if (page >= 0 && _thumbs.Count > page)
        {
            var r = _thumbs.SlotRect(page);
            var v = _thumbs.Scroll.VerticalOffset;
            if (r.Top < v || r.Bottom > v + _thumbs.Scroll.ViewportHeight) _thumbs.ScrollToPage(page);
        }
    }

    private void UpdatePageText()
    {
        var count = _handle?.PageCount ?? 0;
        if (!_pageBox.IsKeyboardFocused) _pageBox.Text = count == 0 ? "" : (Math.Max(0, _pages.Current) + 1).ToString();
        _pageCount.Text = count == 0 ? "" : Loc.T("pdf.ofPages", count);
    }

    private void OnPageBoxKey(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        e.Handled = true;
        if (e.Key == Key.Enter && int.TryParse(_pageBox.Text.Trim(), out var n) && _handle is not null && n >= 1 && n <= _handle.PageCount)
            _pages.ScrollToPage(n - 1);
        else if (e.Key == Key.Enter) Motion.Shake(_pageBox);
        Keyboard.Focus(this);
        UpdatePageText();
    }

    // ───────────── search ─────────────

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _search.Text = "";
            ClearSearch();
            Keyboard.Focus(this);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = NextHit(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : +1);
        }
    }

    private void ClearSearch()
    {
        _query = "";
        _hits = [];
        _hit = -1;
        UpdateHitText();
        RedrawOverlays();
    }

    internal async Task Search(string query)
    {
        if (_handle is null) return;
        if (_search.Text != query) _search.Text = query;
        _query = query;
        _hit = -1;
        if (query.Length == 0)
        {
            ClearSearch();
            return;
        }
        _hitCount.Text = "…";
        try
        {
            _hits = await _handle.SearchAsync(query, ct: _life?.Token ?? default);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_query != query) return;
        // start from the current page
        var from = Math.Max(0, _pages.Current);
        _hit = _hits.Count == 0 ? -1 : Math.Max(0, _hits.ToList().FindIndex(h => h.PageIndex >= from));
        ShowHit();
    }

    private async Task NextHit(int dir)
    {
        var q = _search.Text;
        if (q != _query)
        {
            await Search(q);
            return;
        }
        if (_hits.Count == 0)
        {
            if (q.Length > 0) Motion.Shake(_search);
            return;
        }
        _hit = (_hit + dir + _hits.Count) % _hits.Count;
        ShowHit();
    }

    private void ShowHit()
    {
        UpdateHitText();
        RedrawOverlays();
        if (_hit < 0 || _hit >= _hits.Count)
        {
            if (_query.Length > 0) Motion.Shake(_search);
            return;
        }
        var hit = _hits[_hit];
        var k = _zoom * PointToDip;
        var r = hit.Rects.Count > 0 ? hit.Rects.Aggregate((a, b) => a.Union(b)).ToRect(k) : new Rect(0, 0, 1, 1);
        _pages.BringIntoView(hit.PageIndex, r);
    }

    private void UpdateHitText() =>
        _hitCount.Text = _query.Length == 0 ? "" : _hits.Count == 0 ? Loc.T("pdf.noHits") : $"{_hit + 1} / {_hits.Count}";

    // ───────────── selection ─────────────

    private async Task<PdfPageText?> Layout(int page)
    {
        if (_handle is null) return null;
        if (_layouts.TryGetValue(page, out var l)) return l;
        try
        {
            l = await _handle.GetPageTextLayoutAsync(page, ct: _life?.Token ?? default); // cached by the engine (LRU)
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"pdf: text of page {page + 1}: {ex.Message}");
            return null;
        }
        if (_handle is null) return null;
        // keep the visible pages and both selection ends; the rest is cheap to get again
        if (_layouts.Count >= 32)
            foreach (var key in _layouts.Keys.Where(p => !_pages.Live.ContainsKey(p) && p != _anchor.Page && p != _focus.Page).ToList())
                _layouts.Remove(key);
        _layouts[page] = l;
        return l;
    }

    private async Task LoadLayoutAndRedraw(int page)
    {
        if (!_layoutsLoading.Add(page)) return;
        try
        {
            if (await Layout(page) is null) return;
        }
        finally
        {
            _layoutsLoading.Remove(page);
        }
        if (_pages.Live.TryGetValue(page, out var slot)) DrawOverlay(slot);
        if (_selecting) ExtendTo(_dragPoint);
    }

    private bool HasSelection => _anchor.Page >= 0 && _focus.Page >= 0 && _anchor != _focus;

    private ((int Page, int Caret) Start, (int Page, int Caret) End) Ordered() =>
        _anchor.Page < _focus.Page || (_anchor.Page == _focus.Page && _anchor.Caret <= _focus.Caret) ? (_anchor, _focus) : (_focus, _anchor);

    private bool Covers(int page)
    {
        if (!HasSelection) return false;
        var (s, e) = Ordered();
        return page >= s.Page && page <= e.Page;
    }

    /// <summary>Selected caret range on one page: from the start (first page) or 0, to the end (last page) or the page end.</summary>
    private (int A, int B) RangeOn(int page, int length)
    {
        var (s, e) = Ordered();
        var a = page == s.Page ? Math.Clamp(s.Caret, 0, length) : 0;
        var b = page == e.Page ? Math.Clamp(e.Caret, 0, length) : length;
        return (a, Math.Max(a, b));
    }

    private void SetSelection((int Page, int Caret) anchor, (int Page, int Caret) focus)
    {
        _anchor = anchor;
        _focus = focus;
        RedrawSelection();
    }

    internal void ClearSelection()
    {
        if (_anchor.Page < 0 && _focus.Page < 0) return;
        SetSelection((-1, 0), (-1, 0));
    }

    /// <summary>Ctrl+A: the whole document (the last page "to its end", so no page text has to be read now).</summary>
    internal void SelectAll()
    {
        if (_handle is null || _handle.PageCount == 0) return;
        SetSelection((0, 0), (_handle.PageCount - 1, PageEnd));
    }

    /// <summary>Selects from a caret on one page to a caret on another (tests and snapshots).</summary>
    internal void SelectRange(int fromPage, int fromCaret, int toPage, int toCaret) => SetSelection((fromPage, fromCaret), (toPage, toCaret));

    /// <summary>Scrolls a page to the top of the view, <paramref name="offsetWithin"/> DIPs into it (tests and snapshots).</summary>
    internal void ScrollToPage(int page, double offsetWithin) => _pages.ScrollToPage(page, offsetWithin);

    /// <summary>Visible pages whose overlay shows a part of the selection (tests).</summary>
    internal IReadOnlyList<int> PagesWithSelectionDrawn => _pages.Live.Values.Where(s => s.DrawnSelection != (0, 0)).Select(s => s.Index).Order().ToList();

    /// <summary>
    /// What a drag does, without the mouse (tests): press at a point of one page, move to a point of another (page DIPs;
    /// below the page height = the gap under it).
    /// </summary>
    internal async Task DragSelectAsync(int fromPage, Point from, int toPage, Point to)
    {
        if (await Layout(fromPage) is not { } layout) return;
        var k = _zoom * PointToDip;
        var caret = Math.Max(0, layout.HitTestCaret(from.X / k, from.Y / k));
        SetSelection((fromPage, caret), (fromPage, caret));
        await Layout(toPage);
        var r = _pages.SlotRect(toPage);
        ExtendTo(_pages.FromCanvas(new Point(r.Left + to.X, r.Top + to.Y)));
    }

    private async void OnPageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_handle is null || e.OriginalSource is DependencyObject d && FindScrollBar(d)) return;
        Keyboard.Focus(this);
        var pos = e.GetPosition(_pages);
        var clicks = e.ClickCount;
        var extend = clicks == 1 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _anchor.Page >= 0;
        if (!_pages.HitPage(pos, out var page, out var p) && !extend)
        {
            ClearSelection(); // a click beside the pages
            return;
        }
        _dragPoint = pos;
        if (clicks == 1)
        {
            _selecting = true;
            _pages.CaptureMouse();
            _autoScroll.Start(); // only while dragging
        }
        if (extend)
        {
            ExtendTo(pos);
            return;
        }
        var layout = await Layout(page);
        if (layout is null || _handle is null) return;
        var k = _zoom * PointToDip;
        var caret = Math.Max(0, layout.HitTestCaret(p.X / k, p.Y / k));
        if (clicks >= 2)
        {
            var ch = Math.Clamp(caret, 0, Math.Max(0, layout.Length - 1));
            var (s, en) = clicks == 2 ? layout.GetWordRangeAt(ch) : layout.GetLineRangeAt(ch);
            EndDrag();
            SetSelection((page, s), (page, en));
            return;
        }
        SetSelection((page, caret), (page, caret));
        if (_selecting) ExtendTo(_dragPoint); // the pointer may have moved while the page text loaded
    }

    private void OnPageMouseMove(object sender, MouseEventArgs e)
    {
        if (!_selecting) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }
        _dragPoint = e.GetPosition(_pages);
        ExtendTo(_dragPoint);
    }

    private void OnPageMouseUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (!_selecting && !_autoScroll.IsEnabled) return;
        _selecting = false;
        _autoScroll.Stop();
        if (_pages.IsMouseCaptured) _pages.ReleaseMouseCapture();
    }

    /// <summary>
    /// Moves the selection's focus to a point of the strip (also outside the pages and beyond the viewport while
    /// dragging): in a gap → the end of the page above; beside a page → the nearest character of that line.
    /// </summary>
    private void ExtendTo(Point stripPoint)
    {
        if (_handle is null || _anchor.Page < 0 || _pages.Count == 0) return;
        var c = _pages.ToCanvas(stripPoint);
        var page = Math.Clamp(_pages.PageAt(c.Y), 0, _pages.Count - 1);
        var r = _pages.SlotRect(page);
        int caret;
        if (c.Y < r.Top) caret = 0; // above the first page
        else if (c.Y > r.Bottom) caret = PageEnd; // in the gap below a page
        else
        {
            if (!_layouts.TryGetValue(page, out var layout))
            {
                _ = LoadLayoutAndRedraw(page); // continues the drag when the text arrives
                return;
            }
            var k = _zoom * PointToDip;
            caret = Math.Max(0, layout.HitTestCaret(Math.Clamp(c.X - r.Left, 0, r.Width) / k, (c.Y - r.Top) / k));
        }
        if (_focus == (page, caret)) return;
        _focus = (page, caret);
        RedrawSelection();
    }

    /// <summary>While dragging near (or past) the top / bottom / side edge, scrolls — faster the further out the pointer is.</summary>
    private void OnAutoScroll(object? sender, EventArgs e)
    {
        if (!_selecting || Mouse.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }
        var sv = _pages.Scroll;
        var p = _dragPoint;
        static double Speed(double over) => Math.Min(48, 2 + over * over / 36);
        double dy = 0, dx = 0;
        if (p.Y < AutoScrollEdge && sv.VerticalOffset > 0) dy = -Speed(AutoScrollEdge - p.Y);
        else if (p.Y > sv.ViewportHeight - AutoScrollEdge && sv.VerticalOffset < sv.ScrollableHeight) dy = Speed(p.Y - (sv.ViewportHeight - AutoScrollEdge));
        if (p.X < AutoScrollEdge && sv.HorizontalOffset > 0) dx = -Speed(AutoScrollEdge - p.X);
        else if (p.X > sv.ViewportWidth - AutoScrollEdge && sv.HorizontalOffset < sv.ScrollableWidth) dx = Speed(p.X - (sv.ViewportWidth - AutoScrollEdge));
        if (dy == 0 && dx == 0) return;
        if (dy != 0) sv.ScrollToVerticalOffset(Math.Clamp(sv.VerticalOffset + dy, 0, sv.ScrollableHeight));
        if (dx != 0) sv.ScrollToHorizontalOffset(Math.Clamp(sv.HorizontalOffset + dx, 0, sv.ScrollableWidth));
        _pages.UpdateLayout(); // the new offset before mapping the pointer to the canvas
        ExtendTo(_dragPoint);
    }

    private static bool FindScrollBar(DependencyObject d)
    {
        for (var x = d; x is not null; x = x is Visual ? VisualTreeHelper.GetParent(x) : null)
            if (x is ScrollBar) return true;
        return false;
    }

    /// <summary>
    /// Text of the selection, pages joined by a line break. Whole pages come from the plain page text, only the two
    /// end pages need their layout. Stops at <see cref="MaxCopyChars"/>: returns the last page included and whether it was cut.
    /// </summary>
    internal async Task<(string Text, int FirstPage, int LastPage, bool Truncated)> SelectedTextAsync()
    {
        if (!HasSelection || _handle is null) return ("", -1, -1, false);
        var (s, e) = Ordered();
        var sb = new StringBuilder();
        var last = s.Page;
        var truncated = false;
        for (var page = s.Page; page <= e.Page && _handle is not null; page++)
        {
            string part;
            var whole = (page != s.Page || s.Caret == 0) && (page != e.Page || e.Caret == PageEnd);
            if (whole)
            {
                part = (await _handle.GetPageTextAsync(page, _life?.Token ?? default)).Replace("­\r\n", "").Replace("­", "");
            }
            else
            {
                if (await Layout(page) is not { } layout) continue;
                var (a, b) = RangeOn(page, layout.Length);
                part = layout.GetSelectedText(a, b);
            }
            if (sb.Length + part.Length > MaxCopyChars)
            {
                truncated = true;
                if (sb.Length == 0) // one gigantic page: its beginning
                {
                    sb.Append(part, 0, MaxCopyChars);
                    last = page;
                }
                break;
            }
            last = page;
            if (part.Length == 0) continue;
            if (sb.Length > 0 && sb[^1] != '\n') sb.Append("\r\n"); // a page break is a line break
            sb.Append(part);
        }
        return (sb.ToString(), s.Page, last, truncated);
    }

    private async Task CopySelectionAsync()
    {
        if (!HasSelection || _copying || MainWindow.Of(this) is not { } w) return;
        _copying = true;
        try
        {
            var (s, e) = Ordered();
            if (e.Page - s.Page >= 40) w.Toasts.Show(Loc.T("pdf.copying"), ToastKind.Info, seconds: 2);
            var (text, first, last, truncated) = await SelectedTextAsync();
            if (text.Length == 0)
            {
                w.Toasts.Show(Loc.T("pdf.noText"), ToastKind.Info, seconds: 2.4);
                return;
            }
            Clipboard.SetText(text);
            if (truncated) w.Toasts.Show(Loc.T("pdf.copiedPartial", first + 1, last + 1, MaxCopyChars / 1_000_000), ToastKind.Warning, seconds: 6);
            else if (last > first) w.Toasts.Show(Loc.T("pdf.copiedPages", last - first + 1), ToastKind.Success, seconds: 1.8);
            else w.Toasts.Show(Loc.T("pdf.copied"), ToastKind.Success, seconds: 1.6);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"pdf: copy: {ex.Message}");
            w.Toasts.Show(ex.Message, ToastKind.Error);
        }
        finally
        {
            _copying = false;
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control && e.Key == Key.F)
        {
            e.Handled = true;
            _search.Focus();
            _search.SelectAll();
            return;
        }
        if (e.Key == Key.F3)
        {
            e.Handled = true;
            _ = NextHit(mods.HasFlag(ModifierKeys.Shift) ? -1 : +1);
            return;
        }
        if (_search.IsKeyboardFocused || _pageBox.IsKeyboardFocused) return;
        if (mods == ModifierKeys.Control && e.Key is Key.C or Key.Insert && HasSelection)
        {
            e.Handled = true;
            _ = CopySelectionAsync();
        }
        else if (mods == ModifierKeys.Control && e.Key == Key.A)
        {
            e.Handled = true;
            SelectAll();
        }
        else if (mods == ModifierKeys.None && e.Key == Key.Escape && HasSelection)
        {
            e.Handled = true;
            ClearSelection();
        }
        else if (mods == ModifierKeys.None && e.Key is Key.PageDown or Key.PageUp)
        {
            e.Handled = true;
            _pages.ScrollToPage(Math.Clamp(_pages.Current + (e.Key == Key.PageDown ? 1 : -1), 0, Math.Max(0, _pages.Count - 1)));
        }
        else if (mods == ModifierKeys.None && e.Key is Key.Home or Key.End)
        {
            e.Handled = true;
            if (e.Key == Key.Home) _pages.Scroll.ScrollToTop();
            else _pages.Scroll.ScrollToBottom();
        }
    }

    // ───────────── print and convert ─────────────

    private async Task PrintAsync()
    {
        if (_handle is null || _busy || MainWindow.Of(this) is not { } w) return;
        if (AppEnv.TestMode)
        {
            Log.Info("pdf: print skipped in test mode");
            return;
        }
        var dialog = PdfPrinter.CreateDialog(_handle.PageCount);
        if (dialog.ShowDialog() != true) return;
        _busy = true;
        try
        {
            w.Toasts.Show(Loc.T("pdf.printing"), ToastKind.Info);
            await PdfPrinter.PrintAsync(_handle, PdfPrinter.FromDialog(dialog, _handle.PageCount, Math.Max(0, _pages.Current), jobName: _doc.Title));
            w.Toasts.Show(Loc.T("pdf.printed"), ToastKind.Success);
        }
        catch (Exception ex)
        {
            Log.Warn($"pdf: print: {ex.Message}");
            w.Toasts.Show(Loc.T("pdf.printFailed", ex.Message), ToastKind.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ConvertAsync()
    {
        if (_handle is null || _busy || MainWindow.Of(this) is not { } w || _doc.FilePath is not { } path) return;
        string target;
        if (AppEnv.TestMode)
        {
            target = System.IO.Path.ChangeExtension(path, ".docx");
        }
        else
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = Loc.T("pdf.wordFilter") + "|*.docx",
                FileName = System.IO.Path.GetFileNameWithoutExtension(path) + ".docx",
                InitialDirectory = System.IO.Path.GetDirectoryName(path),
                AddExtension = true,
                DefaultExt = ".docx",
            };
            if (dlg.ShowDialog(w) != true) return;
            target = dlg.FileName;
        }
        _busy = true;
        w.Toasts.Show(Loc.T("pdf.converting"), ToastKind.Info, seconds: 6);
        try
        {
            var r = await PdfToWord.ConvertAsync(_handle, target, preferWord: !AppEnv.TestMode);
            if (r.Success)
            {
                var how = Loc.T(r.Method == PdfToWordMethod.MsWord ? "pdf.viaWord" : "pdf.viaBuiltIn");
                w.Toasts.Show(Loc.T("pdf.converted", System.IO.Path.GetFileName(r.OutputPath), how), ToastKind.Success, Loc.T("info.openFolder"),
                    () => Reveal(r.OutputPath), seconds: 6);
            }
            else
            {
                Log.Warn($"pdf: convert {path}: {r.Status} {r.Detail}");
                w.Toasts.Show(Loc.T(r.Status switch
                {
                    PdfToWordStatus.Canceled => "pdf.convertCanceled",
                    PdfToWordStatus.WordTimedOut => "pdf.convertTimedOut",
                    _ => "pdf.convertFailed",
                }), r.Status == PdfToWordStatus.Canceled ? ToastKind.Info : ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"pdf: convert {path}: {ex.Message}");
            w.Toasts.Show(Loc.T("pdf.convertFailed"), ToastKind.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private static void Reveal(string file)
    {
        if (AppEnv.TestMode) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"pdf: reveal: {ex.Message}"); }
    }

    // ───────────── IDocumentView ─────────────

    public void OnActivated() { }

    public void OnDeactivated() => _handle?.Trim();

    /// <summary>
    /// Tray: drops the rendered pages, thumbnails, text layers and the document's caches. Zoom, scroll position, search and
    /// selection stay; the visible pages render again when the view is shown.
    /// </summary>
    public void ReleaseRendering()
    {
        if (_handle is null) return;
        EndDrag();
        _pages.ReleaseAll();
        _thumbs.ReleaseAll();
        _layouts.Clear();
        _handle.Trim(includeThumbnails: true);
        _hibernated = true;
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible || !_hibernated) return;
        // deferred: this can fire in the middle of a layout pass (the pages render asynchronously anyway)
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!_hibernated || !IsVisible || _handle is null) return;
            _hibernated = false;
            _pages.UpdateVisible();
            if (_sidebar.Visibility == Visibility.Visible) _thumbs.UpdateVisible(); // a hidden sidebar realizes on its next resize
        });
    }

    public void CaptureState() { }

    public void FocusContent() => Keyboard.Focus(this);

    public void Dispose()
    {
        EndDrag();
        _life?.Cancel();
        _life?.Dispose();
        _life = null;
        foreach (var s in _pages.Live.Values) s.Cts?.Cancel();
        foreach (var s in _thumbs.Live.Values) s.Cts?.Cancel();
        _layouts.Clear();
        _handle?.Dispose();
        _handle = null;
    }
}
