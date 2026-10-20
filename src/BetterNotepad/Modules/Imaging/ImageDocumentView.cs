using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterNotepad.Core;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Views;
using BetterNotepad.Editor;
using BetterNotepad.Imaging;
using BetterNotepad.Modules.Info;
using BetterNotepad.UI;
using BetterNotepad.UI.Controls;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Modules.Imaging;

/// <summary>
/// The image tab: the viewer / editor (BetterNotepad.Imaging) over the document's shared edit list, an info panel on the right
/// (the Info tab's file details, hashes and metadata with "Clear metadata"), the status line (pixel size, file size, format,
/// zoom). A hidden tab drops its decoded picture after a while, the tray right away; it decodes again when shown.
/// </summary>
public sealed class ImageDocumentView : Grid, IDocumentView, IZoomableView, ICompactView, IReleasableView, IStatusInfoView
{
    private const double PanelWidth = 372;

    private readonly ImageDocument _doc;
    private readonly ImageViewer _viewer;
    private readonly Border _panel = new() { Width = PanelWidth, BorderThickness = new Thickness(1, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Grid _panelBody = new();
    private readonly TextBlock _panelTitle = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _panelClose = new() { Width = 30, Height = 30 };
    private InfoView? _info;
    private Debouncer? _release;
    private bool _compact;
    private bool _opened;
    private bool _disposed;

    public ImageDocumentView(ImageDocument doc)
    {
        _doc = doc;
        SetResourceReference(BackgroundProperty, "Brush.Editor.Bg");
        Focusable = false;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _viewer = new ImageViewer(new ImagingOptions
        {
            Labels = ImagingStrings.Labels(),
            AnimationsEnabled = () => Motion.I.Enabled,
            WheelZooms = false, // like the PDF and the editor: the wheel scrolls, Ctrl+wheel zooms
            ShowInfo = false, // the status bar says it
            ShowInfoButton = true,
            SetClipboard = AppEnv.TestMode ? data => ImagingModule.LastTestCopy = data : null, // test instances never touch the clipboard
            BuildContextMenu = FillMenu,
        }, doc.Edits)
        {
            ReadOnly = doc.IsLocked,
        };
        Children.Add(_viewer);
        BuildPanel();
        SetColumn(_panel, 1);
        Children.Add(_panel);

        _viewer.InfoRequested += ToggleInfo;
        _viewer.SaveAsRequested += () => MainWindow.Of(this)?.RunCommand("file.saveAs");
        _viewer.Copied += () => MainWindow.Of(this)?.Toasts.Show(Loc.T("img.copied"), ToastKind.Success, seconds: 1.4);
        _viewer.StateChanged += RaiseStatus;
        _viewer.LoadFailed += ex => Log.Warn($"image: cannot open {doc.FilePath}: {ex.Message}");
        _doc.Attach(this);
        _doc.Reloaded += OnReloaded;
        _doc.SourceChanged += OnSourceChanged;
        _doc.FileChanged += OnFileChanged;
        _doc.PropertyChanged += OnDocChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public Document Document => _doc;

    public FrameworkElement Element => this;

    public CodeEditor? Editor => null;

    internal ImageViewer Viewer => _viewer;

    internal bool InfoShown => _panel.Visibility == Visibility.Visible;

    internal InfoView? InfoPanel => _info;

    internal BitmapSource? CaptureBase() => _viewer.CaptureBase();

    /// <summary>The context menu's items (snapshots render it).</summary>
    internal void FillMenuForTest(ContextMenu m) => FillMenu(m);

    // ───────────── actions (toolbar, keys, palette) ─────────────

    public void ToggleEdit()
    {
        if (_doc.IsLocked)
        {
            MainWindow.Of(this)?.Toasts.Show(Loc.T("img.locked"), ToastKind.Warning, Loc.T("save.unlock"), () => MainWindow.Of(this)?.SetLocked(_doc, false));
            return;
        }
        if (_viewer.IsEditing) _viewer.EndEdit();
        else _viewer.BeginEdit();
    }

    public void Copy() => _ = CopyAsync();

    private async Task CopyAsync()
    {
        try
        {
            await _viewer.CopyAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"image: copy failed: {ex.Message}");
            MainWindow.Of(this)?.Toasts.Show(Loc.T("img.copyFailed", ex.Message), ToastKind.Error);
        }
    }

    public void RotateBy(int quarters)
    {
        if (_doc.IsLocked) ToggleEdit(); // says why
        else _viewer.Rotate(quarters);
    }

    public void ActualSize() => _viewer.ZoomToActual();

    // ───────────── info panel ─────────────

    private void BuildPanel()
    {
        _panel.SetResourceReference(Border.BackgroundProperty, "Brush.Chrome");
        _panel.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        var dock = new DockPanel();
        var head = new Grid { Height = 46, Margin = new Thickness(18, 0, 8, 0) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _panelTitle.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        _panelTitle.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");
        head.Children.Add(_panelTitle);
        _panelClose.SetResourceReference(StyleProperty, "IconButton");
        _panelClose.Content = new Icon { Glyph = Glyphs.Close, FontSize = 10 };
        _panelClose.Click += (_, _) => SetInfo(false);
        SetColumn(_panelClose, 1);
        head.Children.Add(_panelClose);
        var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(-18, 0, -8, 0) };
        line.SetResourceReference(Border.BackgroundProperty, "Brush.Border");
        head.Children.Add(line);
        SetColumnSpan(line, 2);
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(_panelBody);
        _panel.Child = dock;
        _panel.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            SetInfo(false);
            e.Handled = true;
        };
        ApplyTexts();
    }

    public void ToggleInfo() => SetInfo(!InfoShown);

    internal void SetInfo(bool show)
    {
        if (_compact) show = false;
        _viewer.InfoOpen = show;
        if (show == InfoShown) return;
        if (show)
        {
            RebuildInfo();
            _panel.Visibility = Visibility.Visible;
            Motion.SlideIn(_panel, fromX: 18, ms: 240);
        }
        else
        {
            _panel.Visibility = Visibility.Collapsed;
            _panelBody.Children.Clear();
            _info?.Dispose();
            _info = null;
            if (IsKeyboardFocusWithin || Keyboard.FocusedElement is null) FocusContent();
        }
    }

    /// <summary>The Info tab's view of the file (as it is on disk): details, SHA-256 / MD5, metadata, "Clear metadata".</summary>
    private void RebuildInfo()
    {
        _panelBody.Children.Clear();
        _info?.Dispose();
        _info = null;
        if (_doc.FilePath is not { } path) return;
        var file = new FileInfoDocument(path, DocumentKind.Info) { IsLocked = _doc.IsLocked };
        _info = new InfoView(file) { Embedded = true };
        _info.Background = Brushes.Transparent;
        _info.Stripped += OnMetadataStripped;
        _panelBody.Children.Add(_info);
    }

    /// <summary>"Clear metadata" rewrote the file from here: no "changed on disk" question (the pixels and the edits are the same).</summary>
    private void OnMetadataStripped() => _doc.RefreshDiskStamp();

    // ───────────── context menu ─────────────

    private void FillMenu(ContextMenu m)
    {
        MenuItem Item(string header, string? glyph, string? gesture, Action click, bool enabled = true, bool mirror = false)
        {
            var i = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
            if (glyph is not null)
            {
                var icon = new Icon { Glyph = glyph, FontSize = 13 };
                if (mirror)
                {
                    icon.RenderTransformOrigin = new Point(0.5, 0.5);
                    icon.RenderTransform = new ScaleTransform(-1, 1);
                }
                i.Icon = icon;
            }
            i.Click += (_, _) => click();
            return i;
        }
        var editable = !_doc.IsLocked && !_compact;
        m.Items.Add(Item(Loc.T("img.copy"), Glyphs.Copy, "Ctrl+C", Copy));
        m.Items.Add(Item(Loc.T("cmd.file.saveAs"), Glyphs.SaveAs, Keymap.GestureText("file.saveAs"), () => MainWindow.Of(this)?.RunCommand("file.saveAs")));
        m.Items.Add(new Separator());
        m.Items.Add(Item(Loc.T("img.fit"), "", Keymap.GestureText("view.zoomReset"), () => _viewer.ZoomToFit()));
        m.Items.Add(Item(Loc.T("img.actualSize"), "", "1", _viewer.ZoomToActual));
        m.Items.Add(Item(Loc.T("img.rotateRight"), "", "R", () => RotateBy(1), !_doc.IsLocked));
        m.Items.Add(Item(Loc.T("img.rotateLeft"), "", "Shift+R", () => RotateBy(-1), !_doc.IsLocked, mirror: true));
        if (_compact) return;
        m.Items.Add(new Separator());
        m.Items.Add(_viewer.IsEditing
            ? Item(Loc.T("img.done"), Glyphs.Checkmark, "Esc", _viewer.EndEdit)
            : Item(Loc.T("img.edit"), Glyphs.Edit, "E", ToggleEdit, editable));
        m.Items.Add(Item(Loc.T("img.info"), Glyphs.Info, "I", ToggleInfo));
        m.Items.Add(Item(Loc.T("info.openFolder"), Glyphs.OpenFolder, null, () => MainWindow.Of(this)?.RevealInExplorer(_doc), _doc.FilePath is not null));
    }

    // ───────────── document events ─────────────

    private void OnReloaded()
    {
        if (_doc.FilePath is { } path) _ = _viewer.OpenAsync(path, null, refit: false);
        if (InfoShown) RebuildInfo();
    }

    private void OnSourceChanged() => _viewer.SourcePath = _doc.SourcePath;

    private void OnFileChanged()
    {
        RaiseStatus();
        if (InfoShown) Dispatcher.BeginInvoke(() =>
        {
            if (InfoShown && !_disposed) RebuildInfo();
        });
    }

    private void OnDocChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Document.IsLocked):
                _viewer.ReadOnly = _doc.IsLocked;
                if (InfoShown) RebuildInfo();
                break;
            case nameof(Document.FilePath):
                _viewer.SourcePath = _doc.SourcePath; // renamed or saved as: the same picture under its new name
                RaiseStatus();
                break;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loc.LanguageChanged -= ApplyTexts;
        Loc.LanguageChanged += ApplyTexts;
        ApplyTexts();
        // decoded once laid out: the display-size copy fits this view, not the whole screen
        if (!_opened && _doc.FilePath is { } path)
        {
            _opened = true;
            _ = _viewer.OpenAsync(path, _doc.BasePath);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Loc.LanguageChanged -= ApplyTexts;

    private void ApplyTexts()
    {
        _viewer.SetLabels(ImagingStrings.Labels());
        _panelTitle.Text = Loc.T("img.panel.title");
        _panelClose.ToolTip = Loc.T("img.panel.close");
        RaiseStatus();
    }

    // ───────────── status bar ─────────────

    public event Action? StatusChanged;

    private void RaiseStatus() => StatusChanged?.Invoke();

    public string? StatusText
    {
        get
        {
            if (_viewer.Info is not { } info) return null;
            var size = _viewer.PixelSize;
            var parts = new List<string>(4) { Loc.T("img.status.px", (int)Math.Round(size.Width), (int)Math.Round(size.Height)) };
            if (_doc.FileLength > 0) parts.Add(FormatSize(_doc.FileLength));
            parts.Add(ImageCodecs.FromExtension(_doc.FilePath ?? "") is { } f ? f == ImageFileFormat.Ico ? "ICO" : ImageDocument.FormatName(f) : info.Format);
            if (info.Frames > 1 && info.Format == "GIF") parts.Add(Loc.Plural("img.frames", info.Frames));
            return string.Join("  ·  ", parts);
        }
    }

    public string? StatusZoom => _viewer.HasPicture ? Math.Round(_viewer.ZoomPercent).ToString(Loc.I.Culture) + "%" : null;

    public string? StatusZoomTip => Loc.T("img.status.zoomTip");

    public void StatusZoomClicked()
    {
        if (_viewer.IsFit) _viewer.ZoomToActual();
        else _viewer.ZoomToFit();
    }

    private static string FormatSize(long bytes)
    {
        var c = Loc.I.Culture;
        return bytes switch
        {
            < 1024 => bytes.ToString(c) + " B",
            < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", c) + " KB",
            < 1024L * 1024 * 1024 => (bytes / 1024.0 / 1024).ToString("0.#", c) + " MB",
            _ => (bytes / 1024.0 / 1024 / 1024).ToString("0.##", c) + " GB",
        };
    }

    // ───────────── IDocumentView and friends ─────────────

    public void OnActivated() => _release?.Cancel();

    /// <summary>A tab out of sight keeps its picture a little while (switching back is instant), then frees it.</summary>
    public void OnDeactivated() => (_release ??= new Debouncer(TimeSpan.FromSeconds(20), ReleaseIfHidden)).Trigger();

    private void ReleaseIfHidden()
    {
        if (!IsVisible && !_disposed) _viewer.ReleaseContent();
    }

    /// <summary>The window waits in the tray: the decoded picture goes now (the edits stay; it decodes again when shown).</summary>
    public void ReleaseRendering()
    {
        _release?.Cancel();
        _viewer.ReleaseContent();
    }

    /// <summary>Pinned on top: the picture alone, the view toolbar only while pointed at, no editing.</summary>
    public void EnterCompact()
    {
        _compact = true;
        SetInfo(false);
        _viewer.Compact = true;
    }

    public void ZoomStep(int direction) => _viewer.ZoomBy(direction > 0 ? 1.25 : 1 / 1.25);

    public void ZoomReset() => _viewer.ZoomToFit();

    public void CaptureState() { }

    public void FocusContent() => Keyboard.Focus(_viewer);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _release?.Cancel();
        Loc.LanguageChanged -= ApplyTexts;
        _doc.Detach(this);
        _doc.Reloaded -= OnReloaded;
        _doc.SourceChanged -= OnSourceChanged;
        _doc.FileChanged -= OnFileChanged;
        _doc.PropertyChanged -= OnDocChanged;
        _info?.Dispose();
        _info = null;
        _viewer.Dispose();
    }
}
