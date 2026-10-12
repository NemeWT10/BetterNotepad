using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace BetterNotepad.Imaging;

/// <summary>
/// Image viewer with a simple editor (BetterShot-style tools). Viewing: fit / 100 % / wheel zoom around the cursor with a smooth
/// glide, pan by dragging (Space or the middle button while a tool is active), checkerboard under transparency, animated GIF
/// (plays only while visible), rotate, copy, save in another format. Editing: crop, rotate / flip, resize, pen, highlighter,
/// line, arrow, rectangle, ellipse, text, blur, pixelate, undo / redo — all kept as an edit list rendered on a screen-sized
/// proxy, applied to the full-resolution picture only when saving or copying. UI thread only.
/// </summary>
public sealed partial class ImageViewer : UserControl, IDisposable
{
    public static readonly DependencyProperty ThemeBackgroundProperty = DependencyProperty.Register(
        nameof(ThemeBackground), typeof(Color), typeof(ImageViewer), new PropertyMetadata(Color.FromRgb(0x12, 0x14, 0x1C), (d, _) => ((ImageViewer)d).OnThemeChanged()));

    public static readonly DependencyProperty ThemeForegroundProperty = DependencyProperty.Register(
        nameof(ThemeForeground), typeof(Color), typeof(ImageViewer), new PropertyMetadata(Color.FromRgb(0xE4, 0xE7, 0xF0), (d, _) => ((ImageViewer)d).OnThemeChanged()));

    private static readonly Color[] PaletteColors =
    [
        Color.FromRgb(0xFF, 0x3B, 0x30), Color.FromRgb(0xFF, 0x95, 0x00), Color.FromRgb(0xFF, 0xCC, 0x00), Color.FromRgb(0x34, 0xC7, 0x59),
        Color.FromRgb(0x00, 0xC7, 0xBE), Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0xBF, 0x5A, 0xF2), Color.FromRgb(0xFF, 0xFF, 0xFF),
        Color.FromRgb(0x1C, 0x1C, 0x1E),
    ];

    private static readonly double[] StrokeDip = [2.5, 4.5, 8];
    private static readonly double[] MarkerDip = [12, 20, 32];
    private static readonly double[] TextDip = [16, 24, 38];

    private readonly ImagingOptions _options;
    private readonly ImagingLabels _l;

    // layers
    private readonly Grid _root = new();
    private readonly Canvas _stage = new() { ClipToBounds = true, Background = Brushes.Transparent, Focusable = false };
    private readonly Rectangle _checker = new() { IsHitTestVisible = false };
    private readonly Image _image = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly ShapeLayer _shapes = new();
    private readonly Overlay _overlay;
    private readonly MatrixTransform _transform = new();
    private readonly Border _cropChip;
    private readonly TextBox _textInput = new();
    private readonly Border _textFrame = new();
    private readonly Border _infoPill;
    private readonly TextBlock _infoText = new() { FontSize = 11.5 };
    private readonly TextBlock _message = new() { FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };

    // toolbar
    private readonly Border _toolbar;
    private readonly StackPanel _viewBar = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _editBar = new() { Orientation = Orientation.Horizontal };
    private readonly ToolButton _zoomText;
    private readonly TextBlock _zoomLabel = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
    private readonly ToolButton _playButton;
    private readonly Border _playSeparatorHost = new();
    private readonly Dictionary<EditTool, ToolButton> _toolButtons = new();
    private readonly ToolButton _styleButton;
    private readonly Ellipse _styleDot = new() { Width = 16, Height = 16, StrokeThickness = 1.5 };
    private readonly ToolButton _resizeButton;
    private readonly ToolButton _undoButton;
    private readonly ToolButton _redoButton;
    private readonly Border _stylePanel;
    private readonly Border _resizePanel;
    private readonly List<Swatch> _swatches = [];
    private readonly List<SizeChip> _sizeChips = [];
    private TextBox? _resizeW;
    private TextBox? _resizeH;
    private ToolButton? _resizeLink;
    private bool _resizeSync;
    private readonly List<System.Windows.Media.Effects.DropShadowEffect> _effects = [];

    // picture
    private string? _path;
    private ImageInfo? _info;
    private Composer.State? _proxy;
    private Composer.State? _detail;
    private GifAnimation.FrameData[]? _frames;
    private GifAnimation? _gif;
    private bool _playing = true;
    private bool _released;
    private CancellationTokenSource? _loadCts;
    private Task? _detailTask;
    private readonly EditDocument _doc = new();
    private readonly Checkpoints _checkpoints = new();
    private Size _logical;
    private double _rasterScale = 1;

    // view
    private double _zoom = 1, _ox, _oy;
    private (double Z, double X, double Y) _target;
    private (double Z, double X, double Y) _animFrom;
    private TimeSpan _animStart;
    private bool _animating;
    private bool _fit = true;
    private Window? _window;

    // interaction
    private bool _editing;
    private EditTool _tool;
    private Color _color = PaletteColors[0];
    private int _size = 1;
    private bool _panning;
    private Point _panStart;
    private (double X, double Y) _panOrigin;
    private bool _spaceDown;
    private bool _drawing;
    private Point _downImage;
    private List<Point>? _penPoints;
    private Rect? _regionDraft;
    private Rect? _crop;
    private CropHandle _cropDrag;
    private Rect _cropStart;
    private Point? _textOrigin;
    private bool _disposed;

    public ImageViewer(ImagingOptions? options = null)
    {
        _options = options ?? new ImagingOptions();
        _l = _options.Labels;
        Anim.Enabled = _options.AnimationsEnabled;
        Focusable = true;
        FocusVisualStyle = null;
        SetResourceReference(BackgroundProperty, "Brush.Editor.Bg");
        SetResourceReference(ThemeBackgroundProperty, "Color.Editor.Bg");
        SetResourceReference(ThemeForegroundProperty, "Color.Text");
        _overlay = new Overlay(this);

        // stage: checkerboard (screen-sized tiles, soft shadow) under the picture, drawings, then the screen-space overlay
        _checker.Effect = Shadow(22, 4, 0.28);
        _image.RenderTransform = _transform;
        _shapes.RenderTransform = _transform;
        _stage.Children.Add(_checker);
        _stage.Children.Add(_image);
        _stage.Children.Add(_shapes);
        _stage.Children.Add(_overlay);
        _root.Children.Add(_stage);

        // text tool input: the caption is typed in place
        _textInput.Background = Brushes.Transparent;
        _textInput.BorderThickness = new Thickness(0);
        _textInput.AcceptsReturn = true;
        _textInput.Padding = new Thickness(0);
        _textInput.FontFamily = Annotation.TextFont;
        _textInput.FontWeight = FontWeights.SemiBold;
        _textInput.MinWidth = 40;
        _textInput.SetResourceReference(TextBox.CaretBrushProperty, "Brush.Accent");
        _textInput.PreviewKeyDown += OnTextKey;
        _textInput.LostKeyboardFocus += (_, _) => CommitText();
        _textFrame.Child = _textInput;
        _textFrame.BorderThickness = new Thickness(1);
        _textFrame.CornerRadius = new CornerRadius(4);
        _textFrame.Padding = new Thickness(3, 1, 3, 1);
        _textFrame.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
        _textFrame.Visibility = Visibility.Collapsed;
        _stage.Children.Add(_textFrame);

        // crop confirm chip
        var applyCrop = new ToolButton(IconKind.Check, _l.ApplyCrop, "Enter");
        applyCrop.Click += (_, _) => ApplyCrop();
        var cancelCrop = new ToolButton(IconKind.Close, _l.CancelCrop, "Esc");
        cancelCrop.Click += (_, _) => CancelCrop();
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal };
        chipRow.Children.Add(applyCrop);
        chipRow.Children.Add(cancelCrop);
        _cropChip = Ui.Panel(chipRow, 12, new Thickness(3));
        _cropChip.Visibility = Visibility.Collapsed;
        _cropChip.Effect = Shadow(20, 5, 0.4);
        _stage.Children.Add(_cropChip);

        // info pill
        _infoText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelTextDim");
        _infoPill = Ui.Panel(_infoText, 9, new Thickness(10, 4, 10, 5));
        _infoPill.Effect = Shadow(14, 3, 0.25);
        _infoPill.HorizontalAlignment = HorizontalAlignment.Left;
        _infoPill.VerticalAlignment = VerticalAlignment.Top;
        _infoPill.Margin = new Thickness(14);
        _infoPill.IsHitTestVisible = false;
        _infoPill.Visibility = Visibility.Collapsed;
        _root.Children.Add(_infoPill);
        _message.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        _root.Children.Add(_message);

        // view toolbar
        var zoomOut = Btn(IconKind.ZoomOut, _l.ZoomOut, "−", () => ZoomBy(1 / 1.25));
        _zoomText = new ToolButton(IconKind.Fit, _l.ZoomLevel) { Width = 58, Content = _zoomLabel };
        _zoomText.Click += (_, _) => ZoomToActual();
        var zoomIn = Btn(IconKind.ZoomIn, _l.ZoomIn, "+", () => ZoomBy(1.25));
        var fit = Btn(IconKind.Fit, _l.Fit, "0", () => ZoomToFit());
        var rotate = Btn(IconKind.RotateRight, _l.Rotate, "R", () => Rotate(1));
        _playButton = Btn(IconKind.Pause, _l.Pause, null, TogglePlay);
        var edit = Btn(IconKind.Edit, _l.Edit, "E", BeginEdit);
        foreach (var e in new UIElement[] { zoomOut, _zoomText, zoomIn, Ui.VSeparator(), fit, rotate }) _viewBar.Children.Add(e);
        _playSeparatorHost.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { Ui.VSeparator(), _playButton } };
        _viewBar.Children.Add(_playSeparatorHost);
        _viewBar.Children.Add(Ui.VSeparator());
        _viewBar.Children.Add(edit);

        // edit toolbar
        _undoButton = Btn(IconKind.Undo, _l.Undo, "Ctrl+Z", Undo);
        _redoButton = Btn(IconKind.Redo, _l.Redo, "Ctrl+Y", Redo);
        _styleDot.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Brush.PanelBorder");
        _styleButton = new ToolButton(IconKind.Check, _l.ColorAndSize) { Content = _styleDot };
        _styleButton.Click += (_, _) => TogglePanel(_stylePanel!, _styleButton);
        _resizeButton = Btn(IconKind.Resize, _l.Resize, null, () => TogglePanel(_resizePanel!, _resizeButton!));
        var done = Btn(IconKind.Check, _l.Done, "Esc", EndEdit);
        _editBar.Children.Add(_undoButton);
        _editBar.Children.Add(_redoButton);
        _editBar.Children.Add(Ui.VSeparator());
        _editBar.Children.Add(ToolBtn(EditTool.Crop, IconKind.Crop, _l.Crop, "C"));
        _editBar.Children.Add(Ui.VSeparator());
        _editBar.Children.Add(ToolBtn(EditTool.Pen, IconKind.Pen, _l.Pen, "P"));
        _editBar.Children.Add(ToolBtn(EditTool.Highlighter, IconKind.Highlighter, _l.Highlighter, "H"));
        _editBar.Children.Add(ToolBtn(EditTool.Line, IconKind.Line, _l.Line, "L"));
        _editBar.Children.Add(ToolBtn(EditTool.Arrow, IconKind.Arrow, _l.Arrow, "A"));
        _editBar.Children.Add(ToolBtn(EditTool.Rectangle, IconKind.Rectangle, _l.Rectangle, "R"));
        _editBar.Children.Add(ToolBtn(EditTool.Ellipse, IconKind.Ellipse, _l.Ellipse, "O"));
        _editBar.Children.Add(ToolBtn(EditTool.Text, IconKind.Text, _l.Text, "T"));
        _editBar.Children.Add(ToolBtn(EditTool.Blur, IconKind.Blur, _l.Blur, "B"));
        _editBar.Children.Add(ToolBtn(EditTool.Pixelate, IconKind.Pixelate, _l.Pixelate, "X"));
        _editBar.Children.Add(_styleButton);
        _editBar.Children.Add(Ui.VSeparator());
        _editBar.Children.Add(Btn(IconKind.RotateLeft, _l.RotateLeft, "Shift+R", () => Rotate(-1)));
        _editBar.Children.Add(Btn(IconKind.RotateRight, _l.RotateRight, null, () => Rotate(1)));
        _editBar.Children.Add(Btn(IconKind.FlipHorizontal, _l.FlipHorizontal, null, () => _doc.Push(new FlipOp(true))));
        _editBar.Children.Add(Btn(IconKind.FlipVertical, _l.FlipVertical, null, () => _doc.Push(new FlipOp(false))));
        _editBar.Children.Add(_resizeButton);
        _editBar.Children.Add(Ui.VSeparator());
        _editBar.Children.Add(done);
        _editBar.Visibility = Visibility.Collapsed;

        var bars = new Grid();
        bars.Children.Add(_viewBar);
        bars.Children.Add(_editBar);
        _toolbar = Ui.Panel(bars, 14, new Thickness(4));
        _toolbar.Effect = Shadow(28, 8, 0.45);
        var box = new Viewbox { Child = _toolbar, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(12, 0, 12, 14), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
        _root.Children.Add(box);

        _stylePanel = BuildStylePanel();
        _resizePanel = BuildResizePanel();
        _stylePanel.Effect = Shadow(28, 8, 0.45);
        _resizePanel.Effect = Shadow(28, 8, 0.45);
        _root.Children.Add(_stylePanel);
        _root.Children.Add(_resizePanel);

        Content = _root;
        UpdateStyleDot();
        UpdateToolbarState();
        _playSeparatorHost.Visibility = Visibility.Collapsed;

        _doc.Changed += OnDocChanged;
        _stage.SizeChanged += (_, _) => OnStageSized();
        _stage.MouseLeftButtonDown += OnLeftDown;
        _stage.MouseMove += OnMove;
        _stage.MouseLeftButtonUp += OnLeftUp;
        _stage.MouseDown += OnOtherDown;
        _stage.MouseUp += OnOtherUp;
        _stage.LostMouseCapture += (_, _) => EndGestures();
        _stage.MouseWheel += OnWheel;
        _root.PreviewMouseDown += OnRootPreviewDown;
        ContextMenuOpening += OnContextMenuOpening;
        ContextMenu = new ContextMenu();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => OnVisibilityChanged();
        OnThemeChanged();
    }

    // ───────────── public surface ─────────────

    public string? FilePath => _path;

    /// <summary>Size / format of the file as opened (null before the first load).</summary>
    public ImageInfo? Info => _info;

    /// <summary>Current picture size after the edits.</summary>
    public Size PixelSize => _logical;

    public bool IsModified => _doc.IsModified;

    public bool IsEditing => _editing;

    public bool CanUndo => _doc.CanUndo;

    public bool CanRedo => _doc.CanRedo;

    public IReadOnlyList<EditOp> Edits => _doc.Ops;

    public EditTool Tool => _tool;

    /// <summary>Zoom in percent of device pixels (100 = one image pixel per screen pixel).</summary>
    public double ZoomPercent => _zoom * Dpi * 100;

    public event Action? ModifiedChanged;

    public event Action? EditingChanged;

    /// <summary>"Save as…" from the context menu: the app shows its dialog, then calls <see cref="SaveAsync"/>.</summary>
    public event Action? SaveAsRequested;

    /// <summary>The picture went to the clipboard (the app shows its toast).</summary>
    public event Action? Copied;

    public event Action<Exception>? LoadFailed;

    /// <summary>Opens a file (discarding edits). Decodes off the UI thread at display size.</summary>
    public async Task LoadAsync(string path)
    {
        _path = path;
        _doc.Reset();
        _checkpoints.Clear();
        CancelCrop();
        await DecodeBaseAsync(refit: true);
    }

    public void BeginEdit()
    {
        if (_editing || _proxy is null) return;
        _editing = true;
        StopGif();
        if (_gif is not null) Recompose();
        _viewBar.Visibility = Visibility.Collapsed;
        _editBar.Visibility = Visibility.Visible;
        Anim.Appear(_editBar, rise: 6, fromScale: 0.98, ms: 240);
        UpdateToolbarState();
        UpdateInfo();
        Focus();
        EditingChanged?.Invoke();
    }

    public void EndEdit()
    {
        if (!_editing) return;
        CommitText();
        CancelCrop();
        ClosePanels();
        SetTool(EditTool.None);
        _editing = false;
        _editBar.Visibility = Visibility.Collapsed;
        _viewBar.Visibility = Visibility.Visible;
        Anim.Appear(_viewBar, rise: 6, fromScale: 0.98, ms: 240);
        UpdateInfo();
        UpdatePlayback();
        EditingChanged?.Invoke();
    }

    public void SetTool(EditTool tool)
    {
        if (tool != EditTool.None && !_editing) BeginEdit();
        if (_tool == EditTool.Crop && tool != EditTool.Crop) CancelCrop();
        if (_tool == EditTool.Text) CommitText();
        _tool = tool;
        foreach (var (t, b) in _toolButtons) b.IsChecked = t == tool;
        if (tool == EditTool.Crop && _crop is null && _logical.Width > 0)
        {
            // start with the whole picture selected, ready to be pulled in from the edges
            _crop = new Rect(_logical);
            ShowCropChip();
        }
        UpdateCursor();
        _overlay.InvalidateVisual();
    }

    public void Rotate(int quarters) => _doc.Push(new RotateOp(quarters));

    public void Undo()
    {
        CommitText();
        _doc.Undo();
    }

    public void Redo() => _doc.Redo();

    public void ZoomToFit(bool animate = true)
    {
        _fit = true;
        var (z, x, y) = FitView();
        GlideTo(z, x, y, animate);
        DropDetailIfIdle();
    }

    public void ZoomToActual() => ZoomAround(1 / Dpi, ViewCenter, animate: true);

    public void ZoomBy(double factor) => ZoomAround(_target.Z * factor, ViewCenter, animate: true);

    /// <summary>The edited picture at full resolution (decoded from the file again, edits applied off the UI thread).</summary>
    public async Task<BitmapSource?> RenderResultAsync()
    {
        if (_path is null) return null;
        var path = _path;
        var ops = _doc.Ops.ToList();
        return await Sta.Run(() =>
        {
            var d = ImageLoader.Decode(path, 0);
            var bmp = Composer.Flatten(new Composer.State(d.Image, 1, d.Screen), ops);
            if (!bmp.IsFrozen) bmp.Freeze();
            return bmp;
        });
    }

    /// <summary>Writes the edited picture to <paramref name="path"/> (format from the extension unless given). Saving over the
    /// open file bakes the edits in and reopens it (the edit list starts empty).</summary>
    public async Task SaveAsync(string path, ImageFileFormat? format = null)
    {
        if (_path is null) return;
        CommitText();
        var source = _path;
        var ops = _doc.Ops.ToList();
        var fmt = format ?? ImageCodecs.FromExtension(path) ?? ImageFileFormat.Png;
        var quality = _options.JpegQuality;
        await Sta.Run(() =>
        {
            var d = ImageLoader.Decode(source, 0);
            ImageCodecs.Save(Composer.Flatten(new Composer.State(d.Image, 1, d.Screen), ops), path, fmt, quality);
            return true;
        });
        if (string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
        {
            var wasEditing = _editing;
            await LoadAsync(path);
            if (wasEditing && !_editing) BeginEdit();
        }
    }

    /// <summary>Copies the (edited) picture: bitmap + PNG (keeps transparency for apps that read it).</summary>
    public async Task CopyAsync()
    {
        var bmp = await RenderResultAsync();
        if (bmp is null) return;
        var data = new DataObject();
        data.SetImage(bmp);
        var png = new System.IO.MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        enc.Save(png);
        png.Position = 0;
        data.SetData("PNG", png, false);
        try
        {
            Clipboard.SetDataObject(data, true);
            Copied?.Invoke();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // clipboard busy: try once more shortly
            await Task.Delay(120);
            Clipboard.SetDataObject(data, true);
            Copied?.Invoke();
        }
    }

    /// <summary>Frees the decoded pictures of a hidden tab (edits are kept); showing it again decodes the file anew.</summary>
    public void ReleaseContent()
    {
        StopGif();
        _gif = null;
        _frames = null;
        _proxy = null;
        _detail = null;
        _checkpoints.Clear();
        _image.Source = null;
        _released = _path is not null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loadCts?.Cancel();
        ReleaseContent();
        _released = false;
        StopGlide();
        _doc.Changed -= OnDocChanged;
    }

    /// <summary>Animated GIF currently advancing frames (for tests).</summary>
    internal bool IsAnimating => _gif?.IsRunning == true;

    // ───────────── loading ─────────────

    private double Dpi => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? VisualTreeHelper.GetDpi(this).DpiScaleX;

    private int ProxySide()
    {
        var w = _stage.ActualWidth > 0 ? _stage.ActualWidth : SystemParameters.PrimaryScreenWidth;
        var h = _stage.ActualHeight > 0 ? _stage.ActualHeight : SystemParameters.PrimaryScreenHeight;
        return (int)Math.Clamp(Math.Max(w, h) * Dpi * 1.5, 1600, 4096);
    }

    private async Task DecodeBaseAsync(bool refit)
    {
        if (_path is null) return;
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        StopGif();
        _gif = null;
        _detail = null;
        _detailTask = null;
        ShowMessage(_l.Loading, delayed: true);
        Decoded d;
        try
        {
            d = await ImageLoader.DecodeAsync(_path, ProxySide(), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (cts != _loadCts) return;
            _proxy = null;
            _image.Source = null;
            ShowMessage(_l.LoadFailed, delayed: false);
            LoadFailed?.Invoke(ex);
            return;
        }
        if (cts != _loadCts || _disposed) return;
        _released = false;
        HideMessage();
        _info = d.Info;
        _proxy = new Composer.State(d.Image, d.Scale, d.Screen);
        _frames = d.Frames;
        if (_frames is not null) _gif = new GifAnimation(_frames, d.Screen, Dispatcher);
        _playSeparatorHost.Visibility = _gif is null ? Visibility.Collapsed : Visibility.Visible;
        _checkpoints.Clear();
        Recompose();
        if (refit || _fit) ZoomToFit(animate: false);
        UpdateInfo();
        UpdatePlayback();
    }

    /// <summary>Zoomed past the proxy's resolution: decode the full picture once (dropped again on fit).</summary>
    private void EnsureDetail()
    {
        if (_proxy is null || _detail is not null || _detailTask is not null || _path is null) return;
        if (_proxy.Scale >= 0.999 || _gif is not null) return;
        if (_zoom * Dpi <= _rasterScale * 1.15) return;
        var path = _path;
        var cts = _loadCts;
        _detailTask = Run();

        async Task Run()
        {
            try
            {
                var d = await ImageLoader.DecodeAsync(path, 0, cts?.Token ?? default);
                if (path != _path || cts != _loadCts || _disposed) return;
                _detail = new Composer.State(d.Image, 1, d.Screen);
                _checkpoints.Clear();
                Recompose();
            }
            catch
            {
                // keep the proxy
            }
            finally
            {
                _detailTask = null;
            }
        }
    }

    private void DropDetailIfIdle()
    {
        if (_detail is null) return;
        _detail = null;
        _checkpoints.Clear();
        Recompose();
    }

    // ───────────── composition ─────────────

    private void OnDocChanged()
    {
        var before = _logical;
        Recompose();
        UpdateToolbarState();
        UpdateInfo();
        UpdatePlayback();
        if (before != _logical)
        {
            _crop = null;
            _cropChip.Visibility = Visibility.Collapsed;
            if (_tool == EditTool.Crop) SetTool(EditTool.None);
            ZoomToFit(animate: true);
        }
        ModifiedChanged?.Invoke();
    }

    private void Recompose()
    {
        var start = _detail ?? _proxy;
        if (start is null) return;
        var cap = Math.Max(start.Raster.PixelWidth, start.Raster.PixelHeight);
        var r = Composer.Run(start, _doc.Ops, cap, _checkpoints);
        _logical = r.Logical;
        _rasterScale = r.Scale;
        var showGif = _gif is not null && _doc.Ops.Count == 0 && !_editing;
        _image.Source = showGif ? _gif!.Surface : r.Raster;
        _image.Width = _logical.Width;
        _image.Height = _logical.Height;
        _shapes.Width = _logical.Width;
        _shapes.Height = _logical.Height;
        _shapes.Shapes = r.Pending;
        _shapes.InvalidateVisual();
        ApplyTransform();
    }

    // ───────────── view transform ─────────────

    private Rect Viewport
    {
        get
        {
            var w = _stage.ActualWidth;
            var h = _stage.ActualHeight;
            var bottom = _toolbar.ActualHeight > 0 ? _toolbar.ActualHeight + 26 : 64;
            return new Rect(18, 18, Math.Max(10, w - 36), Math.Max(10, h - 18 - bottom));
        }
    }

    private Point ViewCenter => new(_stage.ActualWidth / 2, Viewport.Y + Viewport.Height / 2);

    private double FitZoom()
    {
        if (_logical.Width <= 0 || _logical.Height <= 0) return 1;
        var v = Viewport;
        var z = Math.Min(v.Width / _logical.Width, v.Height / _logical.Height);
        return Math.Min(z, 1 / Dpi); // small pictures stay pixel-exact instead of blowing up
    }

    private (double Z, double X, double Y) FitView()
    {
        var z = FitZoom();
        var v = Viewport;
        return (z, v.X + (v.Width - _logical.Width * z) / 2, v.Y + (v.Height - _logical.Height * z) / 2);
    }

    private (double X, double Y) Clamp(double z, double x, double y)
    {
        var w = _stage.ActualWidth;
        var h = _stage.ActualHeight;
        var v = Viewport;
        double cw = _logical.Width * z, ch = _logical.Height * z;
        const double pad = 48;
        x = cw <= v.Width ? v.X + (v.Width - cw) / 2 : Math.Clamp(x, w - cw - pad, pad);
        y = ch <= v.Height ? v.Y + (v.Height - ch) / 2 : Math.Clamp(y, h - ch - pad, pad);
        return (x, y);
    }

    private void ZoomAround(double z, Point at, bool animate)
    {
        if (_logical.Width <= 0) return;
        var min = Math.Min(FitZoom(), 1 / Dpi) * 0.25;
        var max = 32 / Dpi;
        z = Math.Clamp(z, min, max);
        var (tz, tx, ty) = _target;
        var x = at.X - (at.X - tx) * (z / tz);
        var y = at.Y - (at.Y - ty) * (z / tz);
        (x, y) = Clamp(z, x, y);
        _fit = Math.Abs(z - FitZoom()) < 1e-4;
        GlideTo(z, x, y, animate);
    }

    private void GlideTo(double z, double x, double y, bool animate)
    {
        _target = (z, x, y);
        if (!animate || !Anim.Enabled() || !IsVisible)
        {
            StopGlide();
            (_zoom, _ox, _oy) = (z, x, y);
            ApplyTransform();
            EnsureDetail();
            return;
        }
        _animFrom = (_zoom, _ox, _oy);
        _animStart = TimeSpan.MinValue;
        if (!_animating)
        {
            _animating = true;
            CompositionTarget.Rendering += OnGlideFrame;
        }
    }

    private void OnGlideFrame(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero;
        if (_animStart == TimeSpan.MinValue) _animStart = now;
        var t = Math.Clamp((now - _animStart).TotalMilliseconds / 200, 0, 1);
        var k = 1 - Math.Pow(1 - t, 3);
        // zoom glides in log space so in and out feel equally fast
        _zoom = Math.Exp(Math.Log(_animFrom.Z) + (Math.Log(_target.Z) - Math.Log(_animFrom.Z)) * k);
        var zk = Math.Abs(_target.Z - _animFrom.Z) < 1e-9 ? k : (_zoom - _animFrom.Z) / (_target.Z - _animFrom.Z);
        _ox = _animFrom.X + (_target.X - _animFrom.X) * zk;
        _oy = _animFrom.Y + (_target.Y - _animFrom.Y) * zk;
        ApplyTransform();
        if (t >= 1)
        {
            StopGlide();
            EnsureDetail();
        }
    }

    private void StopGlide()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnGlideFrame;
    }

    private void ApplyTransform()
    {
        _transform.Matrix = new Matrix(_zoom, 0, 0, _zoom, _ox, _oy);
        var w = _logical.Width * _zoom;
        var h = _logical.Height * _zoom;
        Canvas.SetLeft(_checker, _ox);
        Canvas.SetTop(_checker, _oy);
        _checker.Width = Math.Max(0, w);
        _checker.Height = Math.Max(0, h);
        // magnified pixels stay crisp; scaled-down photos use the smooth filter
        var devicePerRaster = _zoom * Dpi / Math.Max(1e-6, _rasterScale);
        RenderOptions.SetBitmapScalingMode(_image, devicePerRaster >= 2.5 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        _zoomLabel.Text = $"{Math.Round(ZoomPercent):0} %";
        if (_crop is not null) PlaceCropChip();
        if (_textOrigin is { } o) PlaceText(o);
        _overlay.InvalidateVisual();
    }

    private Point ToImage(Point screen) => new((screen.X - _ox) / _zoom, (screen.Y - _oy) / _zoom);

    private Point ToScreen(Point image) => new(image.X * _zoom + _ox, image.Y * _zoom + _oy);

    private Rect ToScreen(Rect r) => new(ToScreen(r.TopLeft), ToScreen(r.BottomRight));

    private void OnStageSized()
    {
        if (_fit)
        {
            var (z, x, y) = FitView();
            StopGlide();
            _target = (z, x, y);
            (_zoom, _ox, _oy) = (z, x, y);
            ApplyTransform();
        }
        else
        {
            var (x, y) = Clamp(_zoom, _ox, _oy);
            _target = (_zoom, x, y);
            (_ox, _oy) = (x, y);
            ApplyTransform();
        }
    }

    // ───────────── GIF playback (only while visible) ─────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null) _window.StateChanged += OnWindowState;
        UpdatePlayback();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null) _window.StateChanged -= OnWindowState;
        _window = null;
        StopGif();
        StopGlide();
    }

    private void OnWindowState(object? sender, EventArgs e) => UpdatePlayback();

    private void OnVisibilityChanged()
    {
        if (IsVisible && _released && !_disposed) _ = DecodeBaseAsync(refit: false);
        UpdatePlayback();
        if (!IsVisible) StopGlide();
    }

    private void UpdatePlayback()
    {
        if (_gif is null) return;
        var run = _playing && !_editing && _doc.Ops.Count == 0 && IsVisible && IsLoaded &&
                  _window is { WindowState: not WindowState.Minimized, IsVisible: true };
        if (run)
        {
            if (_image.Source != _gif.Surface) _image.Source = _gif.Surface;
            _gif.Start();
        }
        else
        {
            _gif.Stop();
        }
        _playButton.Icon = _playing ? IconKind.Pause : IconKind.Play;
        _playButton.ToolTip = Ui.Tip(_playing ? _l.Pause : _l.Play, null);
    }

    private void StopGif() => _gif?.Stop();

    private void TogglePlay()
    {
        _playing = !_playing;
        UpdatePlayback();
    }

    // ───────────── toolbar bits ─────────────

    private ToolButton Btn(IconKind icon, string tip, string? keys, Action click)
    {
        var b = new ToolButton(icon, tip, keys);
        b.Click += (_, _) => click();
        return b;
    }

    private ToolButton ToolBtn(EditTool tool, IconKind icon, string tip, string keys)
    {
        var b = new ToolButton(icon, tip, keys, toggles: true);
        b.Click += (_, _) => SetTool(b.IsChecked == true ? tool : EditTool.None);
        _toolButtons[tool] = b;
        return b;
    }

    private void UpdateToolbarState()
    {
        _undoButton.IsEnabled = _doc.CanUndo;
        _redoButton.IsEnabled = _doc.CanRedo;
    }

    private void UpdateStyleDot()
    {
        _styleDot.Fill = new SolidColorBrush(_color);
        var d = 10 + _size * 3;
        _styleDot.Width = d;
        _styleDot.Height = d;
        _textInput.Foreground = new SolidColorBrush(_color);
    }

    private void UpdateInfo()
    {
        if (_info is null || !_options.ShowInfo || _editing)
        {
            _infoPill.Visibility = Visibility.Collapsed;
            return;
        }
        var w = (int)Math.Round(_logical.Width);
        var h = (int)Math.Round(_logical.Height);
        var parts = new List<string> { string.Format(System.Globalization.CultureInfo.CurrentCulture, _l.SizeFormat, w, h), _info.Format, FileSize(_info.FileSize) };
        if (_info.Frames > 1) parts.Add(string.Format(System.Globalization.CultureInfo.CurrentCulture, _l.FramesFormat, _info.Frames));
        _infoText.Text = string.Join("  ·  ", parts);
        _infoPill.Visibility = Visibility.Visible;
    }

    private static string FileSize(long b)
    {
        var c = System.Globalization.CultureInfo.CurrentCulture;
        return b < 1024 ? $"{b} B" : b < 1 << 20 ? (b / 1024.0).ToString("0.#", c) + " KB" : (b / 1048576.0).ToString("0.#", c) + " MB";
    }

    private Border BuildStylePanel()
    {
        var swatches = new WrapPanel { Width = 30 * 5, Margin = new Thickness(2) };
        foreach (var c in PaletteColors)
        {
            var s = new Swatch(c, "") { IsChecked = c == _color };
            s.Checked += (_, _) =>
            {
                _color = s.Color;
                foreach (var o in _swatches) if (o != s) o.IsChecked = false;
                UpdateStyleDot();
            };
            _swatches.Add(s);
            swatches.Children.Add(s);
        }
        var sizes = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(2) };
        var names = new[] { _l.Thin, _l.Medium, _l.Thick };
        for (var i = 0; i < 3; i++)
        {
            var chip = new SizeChip(5 + i * 4, names[i], i) { IsChecked = i == _size };
            chip.Checked += (_, _) =>
            {
                _size = chip.Index;
                foreach (var o in _sizeChips) if (o != chip) o.IsChecked = false;
                UpdateStyleDot();
            };
            _sizeChips.Add(chip);
            sizes.Children.Add(chip);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(swatches);
        var sep = Ui.VSeparator();
        sep.Margin = new Thickness(4, 6, 4, 6);
        row.Children.Add(sep);
        row.Children.Add(sizes);
        var panel = Ui.Panel(row, 14, new Thickness(6));
        panel.HorizontalAlignment = HorizontalAlignment.Left;
        panel.VerticalAlignment = VerticalAlignment.Bottom;
        panel.Visibility = Visibility.Collapsed;
        return panel;
    }

    private Border BuildResizePanel()
    {
        _resizeW = Ui.NumberField();
        _resizeH = Ui.NumberField();
        _resizeLink = new ToolButton(IconKind.Link, _l.KeepRatio, null, toggles: true) { IsChecked = true, Width = 30, Height = 30 };
        _resizeW.TextChanged += (_, _) => SyncResize(fromWidth: true);
        _resizeH.TextChanged += (_, _) => SyncResize(fromWidth: false);
        var grid = new Grid { Margin = new Thickness(8, 6, 8, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        void Put(UIElement e, int r, int c)
        {
            Grid.SetRow(e, r);
            Grid.SetColumn(e, c);
            grid.Children.Add(e);
        }
        var lw = Ui.Label(_l.Width, 12, dim: true);
        lw.Margin = new Thickness(0, 0, 12, 0);
        var lh = Ui.Label(_l.Height, 12, dim: true);
        lh.Margin = new Thickness(0, 0, 12, 0);
        Put(lw, 0, 0);
        Put(_resizeW, 0, 1);
        Put(Ui.Label(_l.Pixels, 11.5, dim: true).With(t => t.Margin = new Thickness(8, 0, 0, 0)), 0, 2);
        Put(_resizeLink.With(b => { b.Margin = new Thickness(0, 2, 0, 2); b.HorizontalAlignment = HorizontalAlignment.Center; }), 1, 1);
        Put(lh, 2, 0);
        Put(_resizeH, 2, 1);
        Put(Ui.Label(_l.Pixels, 11.5, dim: true).With(t => t.Margin = new Thickness(8, 0, 0, 0)), 2, 2);
        var apply = Ui.Primary(_l.Apply, ApplyResize);
        apply.Margin = new Thickness(8, 10, 8, 6);
        apply.HorizontalAlignment = HorizontalAlignment.Stretch;
        var stack = new StackPanel();
        stack.Children.Add(grid);
        stack.Children.Add(apply);
        var panel = Ui.Panel(stack, 14, new Thickness(6));
        panel.HorizontalAlignment = HorizontalAlignment.Left;
        panel.VerticalAlignment = VerticalAlignment.Bottom;
        panel.Visibility = Visibility.Collapsed;
        panel.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ApplyResize();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ClosePanels();
                Focus();
                e.Handled = true;
            }
        };
        return panel;
    }

    private void SyncResize(bool fromWidth)
    {
        if (_resizeSync || _resizeLink?.IsChecked != true || _logical.Width <= 0) return;
        var src = fromWidth ? _resizeW! : _resizeH!;
        var dst = fromWidth ? _resizeH! : _resizeW!;
        if (!int.TryParse(src.Text, out var v) || v <= 0) return;
        _resizeSync = true;
        dst.Text = Math.Max(1, (int)Math.Round(fromWidth ? v * _logical.Height / _logical.Width : v * _logical.Width / _logical.Height)).ToString();
        _resizeSync = false;
    }

    private void ApplyResize()
    {
        if (int.TryParse(_resizeW?.Text, out var w) && int.TryParse(_resizeH?.Text, out var h) && w is > 0 and <= 30000 && h is > 0 and <= 30000 &&
            (w != (int)Math.Round(_logical.Width) || h != (int)Math.Round(_logical.Height)))
            _doc.Push(new ResizeOp(w, h));
        ClosePanels();
        Focus();
    }

    private void TogglePanel(Border panel, FrameworkElement anchor)
    {
        var open = panel.Visibility == Visibility.Visible && panel.Opacity > 0.5;
        ClosePanels();
        if (open) return;
        if (panel == _resizePanel)
        {
            _resizeSync = true;
            _resizeW!.Text = ((int)Math.Round(_logical.Width)).ToString();
            _resizeH!.Text = ((int)Math.Round(_logical.Height)).ToString();
            _resizeSync = false;
        }
        panel.Visibility = Visibility.Visible;
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var at = anchor.TranslatePoint(new Point(anchor.ActualWidth / 2, 0), _root);
        var left = Math.Clamp(at.X - panel.DesiredSize.Width / 2, 8, Math.Max(8, _root.ActualWidth - panel.DesiredSize.Width - 8));
        var bottom = Math.Max(8, _root.ActualHeight - at.Y + 10);
        panel.Margin = new Thickness(left, 0, 0, bottom);
        panel.RenderTransformOrigin = new Point(0.5, 1);
        Anim.Appear(panel, rise: 8, fromScale: 0.96, ms: 260);
        if (panel == _resizePanel)
        {
            _resizeW!.Focus();
            _resizeW.SelectAll();
        }
    }

    private void ClosePanels()
    {
        Anim.Disappear(_stylePanel);
        Anim.Disappear(_resizePanel);
    }

    private void OnRootPreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        foreach (var (panel, button) in new (Border, FrameworkElement)[] { (_stylePanel, _styleButton), (_resizePanel, _resizeButton) })
            if (panel.Visibility == Visibility.Visible && !IsInside(src, panel) && !IsInside(src, button))
                Anim.Disappear(panel);
    }

    private static bool IsInside(DependencyObject? d, DependencyObject container)
    {
        while (d is not null)
        {
            if (ReferenceEquals(d, container)) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void ShowMessage(string text, bool delayed)
    {
        _message.Text = text;
        _message.Visibility = Visibility.Visible;
        if (delayed && Anim.Enabled())
        {
            // only a slow open shows "Opening…" (fades in after 300 ms)
            var a = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { BeginTime = TimeSpan.FromMilliseconds(300) };
            _message.BeginAnimation(OpacityProperty, a);
        }
        else
        {
            _message.BeginAnimation(OpacityProperty, null);
            _message.Opacity = delayed ? 0 : 1;
        }
    }

    private void HideMessage()
    {
        _message.BeginAnimation(OpacityProperty, null);
        _message.Visibility = Visibility.Collapsed;
    }

    private System.Windows.Media.Effects.DropShadowEffect Shadow(double blur, double depth, double opacity)
    {
        var e = Ui.Shadow(blur, depth, opacity);
        _effects.Add(e);
        return e;
    }

    private void OnThemeChanged()
    {
        var shadow = TryFindResource("Color.Shadow") is Color sc ? sc : Colors.Black;
        foreach (var e in _effects) e.Color = shadow;
        var bg = ThemeBackground;
        var fg = ThemeForeground;
        var dark = 0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B < 128;
        var a = dark ? Blend(bg, fg, 0.075) : Colors.White;
        var b = dark ? Blend(bg, fg, 0.13) : Blend(Colors.White, fg, 0.075);
        var tile = new DrawingGroup();
        using (var dc = tile.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(a), null, new Rect(0, 0, 16, 16));
            var bb = new SolidColorBrush(b);
            dc.DrawRectangle(bb, null, new Rect(0, 0, 8, 8));
            dc.DrawRectangle(bb, null, new Rect(8, 8, 8, 8));
        }
        var brush = new DrawingBrush(tile) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        RenderOptions.SetCachingHint(brush, CachingHint.Cache);
        brush.Freeze();
        _checker.Fill = brush;
    }

    private static Color Blend(Color a, Color b, double t)
    {
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    public Color ThemeBackground
    {
        get => (Color)GetValue(ThemeBackgroundProperty);
        set => SetValue(ThemeBackgroundProperty, value);
    }

    public Color ThemeForeground
    {
        get => (Color)GetValue(ThemeForegroundProperty);
        set => SetValue(ThemeForegroundProperty, value);
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_proxy is null || _drawing || _panning)
        {
            e.Handled = true;
            return;
        }
        var m = ContextMenu!;
        m.Items.Clear();
        MenuItem Item(string header, string? gesture, Action click)
        {
            var i = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            i.Click += (_, _) => click();
            return i;
        }
        m.Items.Add(Item(_l.Copy, "Ctrl+C", () => _ = CopyAsync()));
        m.Items.Add(Item(_l.SaveAs, null, () => SaveAsRequested?.Invoke()));
        m.Items.Add(new Separator());
        m.Items.Add(Item(_l.Fit, "0", () => ZoomToFit()));
        m.Items.Add(Item(_l.ActualSize, "1", ZoomToActual));
        m.Items.Add(Item(_l.Rotate, "R", () => Rotate(1)));
        m.Items.Add(new Separator());
        m.Items.Add(_editing ? Item(_l.Done, "Esc", EndEdit) : Item(_l.Edit, "E", BeginEdit));
    }
}

internal static class FluentExtensions
{
    public static T With<T>(this T o, Action<T> set)
    {
        set(o);
        return o;
    }
}
