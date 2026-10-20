using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BetterNotepad.Imaging;

internal enum CropHandle { None, New, Move, N, S, E, W, NE, NW, SE, SW }

public sealed partial class ImageViewer
{
    // ───────────── mouse ─────────────

    private bool PanGesture => !_editing || _tool == EditTool.None || _spaceDown;

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        e.Handled = PointerDown(e.GetPosition(_stage), e.ClickCount, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    /// <summary>Left button down at a stage point (also driven directly by the render harness).</summary>
    internal bool PointerDown(Point p, int clicks, bool shift)
    {
        if (_proxy is null) return false;
        if (clicks == 2 && PanGesture)
        {
            // double-click: fit ↔ 100 % around the cursor
            if (_fit) ZoomAround(1 / Dpi, p, animate: true);
            else ZoomToFit();
            return true;
        }
        if (PanGesture)
        {
            BeginPan(p);
            return true;
        }
        CommitText();
        var ip = ToImage(p);
        _downImage = ip;
        switch (_tool)
        {
            case EditTool.Text:
                BeginText(ip);
                return true;
            case EditTool.Crop:
                _cropDrag = HitCrop(p);
                _cropStart = _crop ?? Rect.Empty;
                if (_cropDrag == CropHandle.None) _cropDrag = CropHandle.New;
                _cropChip.Visibility = Visibility.Collapsed;
                break;
            case EditTool.Pen:
            case EditTool.Highlighter:
                _penPoints = [ip];
                break;
        }
        _drawing = true;
        _stage.CaptureMouse();
        UpdateDraft(ip, shift);
        return true;
    }

    private void OnMove(object sender, MouseEventArgs e) => PointerMove(e.GetPosition(_stage), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));

    internal void PointerMove(Point p, bool shift)
    {
        if (_panning)
        {
            var d = p - _panStart;
            var (x, y) = Clamp(_zoom, _panOrigin.X + d.X, _panOrigin.Y + d.Y);
            StopGlide();
            _fit = false;
            (_ox, _oy) = (x, y);
            _target = (_zoom, x, y);
            ApplyTransform();
            return;
        }
        if (_drawing)
        {
            UpdateDraft(ToImage(p), shift);
            return;
        }
        UpdateCursor(p);
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e) => PointerUp(e.GetPosition(_stage), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));

    internal void PointerUp(Point p, bool shift)
    {
        if (_panning)
        {
            EndPan();
            return;
        }
        if (!_drawing) return;
        _drawing = false;
        FinishDraft(ToImage(p), shift);
        _stage.ReleaseMouseCapture();
    }

    private void OnOtherDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _proxy is null) return;
        Focus();
        BeginPan(e.GetPosition(_stage));
        e.Handled = true;
    }

    private void OnOtherUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && _panning) EndPan();
    }

    private void BeginPan(Point p)
    {
        _panning = true;
        _panStart = p;
        _panOrigin = (_ox, _oy);
        _stage.CaptureMouse();
        _stage.Cursor = Cursors.SizeAll;
    }

    private void EndPan()
    {
        _panning = false;
        _stage.ReleaseMouseCapture();
        UpdateCursor();
    }

    private void EndGestures()
    {
        if (_panning)
        {
            _panning = false;
            UpdateCursor();
        }
        if (_drawing)
        {
            _drawing = false;
            _shapes.Draft = null;
            _shapes.InvalidateVisual();
            _regionDraft = null;
            _overlay.InvalidateVisual();
        }
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_proxy is null) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (!_options.WheelZooms && !ctrl)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return; // a pinned window's own gesture (Alt+wheel = window size)
            // the wheel scrolls a zoomed-in picture (Shift: sideways), like a page
            var sideways = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            var (tz, tx, ty) = _target;
            var (x, y) = sideways ? Clamp(tz, tx + e.Delta * 0.6, ty) : Clamp(tz, tx, ty + e.Delta * 0.6);
            GlideTo(tz, x, y, animate: true);
            e.Handled = true;
            return;
        }
        ZoomAround(_target.Z * Math.Pow(1.0016, e.Delta), e.GetPosition(_stage), animate: true);
        e.Handled = true;
    }

    private void UpdateCursor(Point? at = null)
    {
        if (_panning) return;
        Cursor c;
        if (_spaceDown || !_editing || _tool == EditTool.None) c = Cursors.Arrow;
        else if (_tool == EditTool.Text) c = Cursors.IBeam;
        else if (_tool == EditTool.Crop && at is { } p)
            c = HitCrop(p) switch
            {
                CropHandle.Move => Cursors.SizeAll,
                CropHandle.N or CropHandle.S => Cursors.SizeNS,
                CropHandle.E or CropHandle.W => Cursors.SizeWE,
                CropHandle.NE or CropHandle.SW => Cursors.SizeNESW,
                CropHandle.NW or CropHandle.SE => Cursors.SizeNWSE,
                _ => Cursors.Cross,
            };
        else c = Cursors.Cross;
        _stage.Cursor = c;
    }

    // ───────────── drawing tools ─────────────

    private double StrokeImage => (_tool == EditTool.Highlighter ? MarkerDip : StrokeDip)[_size] / _zoom;

    private static Point Snap(Point a, Point b)
    {
        // Shift: the angle snaps to 15° steps
        var v = b - a;
        var ang = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 12)) * (Math.PI / 12);
        return a + new Vector(Math.Cos(ang), Math.Sin(ang)) * v.Length;
    }

    private static Point Square(Point a, Point b)
    {
        var v = b - a;
        var s = Math.Max(Math.Abs(v.X), Math.Abs(v.Y));
        return new Point(a.X + Math.Sign(v.X) * s, a.Y + Math.Sign(v.Y) * s);
    }

    private void UpdateDraft(Point ip, bool shift)
    {
        switch (_tool)
        {
            case EditTool.Pen:
            case EditTool.Highlighter:
                if (_penPoints is null) return;
                if (shift && _tool == EditTool.Highlighter) _penPoints = [_penPoints[0], ip];
                else if (_penPoints.Count == 0 || (ip - _penPoints[^1]).Length * _zoom >= 1.5) _penPoints.Add(ip);
                _shapes.Draft = new Annotation(_tool, _color, StrokeImage, [.. _penPoints]);
                break;
            case EditTool.Line:
            case EditTool.Arrow:
                _shapes.Draft = new Annotation(_tool, _color, StrokeImage, [_downImage, shift ? Snap(_downImage, ip) : ip]);
                break;
            case EditTool.Rectangle:
            case EditTool.Ellipse:
                _shapes.Draft = new Annotation(_tool, _color, StrokeImage, [_downImage, shift ? Square(_downImage, ip) : ip]);
                break;
            case EditTool.Blur:
            case EditTool.Pixelate:
                _regionDraft = Rect.Intersect(new Rect(_downImage, ip), new Rect(_logical));
                _overlay.InvalidateVisual();
                return;
            case EditTool.Crop:
                DragCrop(ip, shift);
                return;
        }
        _shapes.InvalidateVisual();
    }

    private void FinishDraft(Point ip, bool shift)
    {
        UpdateDraft(ip, shift);
        switch (_tool)
        {
            case EditTool.Blur:
            case EditTool.Pixelate:
                if (_regionDraft is { IsEmpty: false } r && r.Width * _zoom >= 4 && r.Height * _zoom >= 4)
                    _doc.Push(new RegionOp(r, _tool == EditTool.Pixelate));
                _regionDraft = null;
                _overlay.InvalidateVisual();
                return;
            case EditTool.Crop:
                if (_crop is { } c && (c.Width < 2 || c.Height < 2)) _crop = _cropStart.IsEmpty ? null : _cropStart;
                ShowCropChip();
                _overlay.InvalidateVisual();
                return;
        }
        var draft = _shapes.Draft;
        _shapes.Draft = null;
        _penPoints = null;
        if (draft is null) return;
        var big = draft.Kind is EditTool.Pen or EditTool.Highlighter || (draft.Points[^1] - draft.Points[0]).Length * _zoom >= 3;
        if (big) _doc.Push(new ShapeOp(draft));
        else _shapes.InvalidateVisual();
    }

    // ───────────── crop ─────────────

    private CropHandle HitCrop(Point screen)
    {
        if (_crop is not { } c) return CropHandle.None;
        var r = ToScreen(c);
        const double t = 9;
        bool nearL = Math.Abs(screen.X - r.Left) <= t, nearR = Math.Abs(screen.X - r.Right) <= t;
        bool nearT = Math.Abs(screen.Y - r.Top) <= t, nearB = Math.Abs(screen.Y - r.Bottom) <= t;
        bool inX = screen.X > r.Left - t && screen.X < r.Right + t, inY = screen.Y > r.Top - t && screen.Y < r.Bottom + t;
        if (nearT && nearL) return CropHandle.NW;
        if (nearT && nearR) return CropHandle.NE;
        if (nearB && nearL) return CropHandle.SW;
        if (nearB && nearR) return CropHandle.SE;
        if (nearT && inX) return CropHandle.N;
        if (nearB && inX) return CropHandle.S;
        if (nearL && inY) return CropHandle.W;
        if (nearR && inY) return CropHandle.E;
        return r.Contains(screen) ? CropHandle.Move : CropHandle.None;
    }

    private void DragCrop(Point ip, bool shift)
    {
        var bounds = new Rect(_logical);
        var s = _cropStart;
        double l = s.Left, t = s.Top, r = s.Right, b = s.Bottom;
        var d = ip - _downImage;
        switch (_cropDrag)
        {
            case CropHandle.New:
                var end = shift ? Square(_downImage, ip) : ip;
                _crop = Rect.Intersect(new Rect(_downImage, end), bounds);
                break;
            case CropHandle.Move:
                var x = Math.Clamp(s.X + d.X, 0, bounds.Width - s.Width);
                var y = Math.Clamp(s.Y + d.Y, 0, bounds.Height - s.Height);
                _crop = new Rect(x, y, s.Width, s.Height);
                break;
            default:
                if (_cropDrag is CropHandle.W or CropHandle.NW or CropHandle.SW) l = Math.Clamp(s.Left + d.X, 0, r - 1);
                if (_cropDrag is CropHandle.E or CropHandle.NE or CropHandle.SE) r = Math.Clamp(s.Right + d.X, l + 1, bounds.Width);
                if (_cropDrag is CropHandle.N or CropHandle.NW or CropHandle.NE) t = Math.Clamp(s.Top + d.Y, 0, b - 1);
                if (_cropDrag is CropHandle.S or CropHandle.SW or CropHandle.SE) b = Math.Clamp(s.Bottom + d.Y, t + 1, bounds.Height);
                _crop = new Rect(new Point(l, t), new Point(r, b));
                break;
        }
        if (_crop is { IsEmpty: true }) _crop = null;
        _overlay.InvalidateVisual();
    }

    private void ShowCropChip()
    {
        if (_crop is null)
        {
            _cropChip.Visibility = Visibility.Collapsed;
            return;
        }
        var first = _cropChip.Visibility != Visibility.Visible;
        _cropChip.Visibility = Visibility.Visible;
        PlaceCropChip();
        if (first) Anim.Appear(_cropChip, rise: 6, fromScale: 0.94, ms: 240);
    }

    private void PlaceCropChip()
    {
        if (_crop is not { } c || _cropChip.Visibility != Visibility.Visible) return;
        _cropChip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var r = ToScreen(c);
        var size = _cropChip.DesiredSize;
        // under the frame's right end; when that would sit on the toolbar, inside the frame clear of the corner handle
        var below = r.Bottom + 10;
        var outside = below + size.Height < Viewport.Bottom + 8;
        var x = Math.Clamp(r.Right - size.Width - (outside ? 0 : 16), 8, Math.Max(8, _stage.ActualWidth - size.Width - 8));
        var y = outside ? below : Math.Max(8, r.Bottom - size.Height - 16);
        Canvas.SetLeft(_cropChip, x);
        Canvas.SetTop(_cropChip, y);
    }

    private void ApplyCrop()
    {
        if (_crop is not { } c) return;
        var r = new Rect(Math.Round(c.X), Math.Round(c.Y), Math.Round(c.Width), Math.Round(c.Height));
        _crop = null;
        _cropChip.Visibility = Visibility.Collapsed;
        if (r.Width >= 1 && r.Height >= 1 && (r.Width < _logical.Width - 0.5 || r.Height < _logical.Height - 0.5))
            _doc.Push(new CropOp(r));
        SetTool(EditTool.None);
        _overlay.InvalidateVisual();
    }

    private void CancelCrop()
    {
        _crop = null;
        _cropChip.Visibility = Visibility.Collapsed;
        _overlay.InvalidateVisual();
    }

    // ───────────── text ─────────────

    private void BeginText(Point ip)
    {
        _textOrigin = ip;
        _textZoom = _zoom;
        _textInput.Text = "";
        _textInput.FontSize = TextDip[_size];
        _textInput.Foreground = new SolidColorBrush(_color);
        _textFrame.Visibility = Visibility.Visible;
        PlaceText(ip);
        Dispatcher.BeginInvoke(() => _textInput.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void PlaceText(Point ip)
    {
        var s = ToScreen(ip);
        // the frame's border + padding sit outside the caption so the text lands exactly where it was typed
        Canvas.SetLeft(_textFrame, s.X - 4);
        Canvas.SetTop(_textFrame, s.Y - 2);
        _textInput.FontSize = TextDip[_size] * (_zoom / ZoomAtTextStart);
    }

    private double ZoomAtTextStart => _textZoom > 0 ? _textZoom : _zoom;

    private double _textZoom;

    private void OnTextKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelText();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            CommitText();
            Focus();
            e.Handled = true;
        }
    }

    private void CommitText()
    {
        if (_textOrigin is not { } o) return;
        var text = _textInput.Text.TrimEnd();
        _textOrigin = null;
        _textFrame.Visibility = Visibility.Collapsed;
        var zoom = ZoomAtTextStart;
        _textZoom = 0;
        if (text.Length > 0) _doc.Push(new ShapeOp(new Annotation(EditTool.Text, _color, TextDip[_size] / zoom, [o], text)));
    }

    private void CancelText()
    {
        _textOrigin = null;
        _textZoom = 0;
        _textFrame.Visibility = Visibility.Collapsed;
        Focus();
    }

    // ───────────── keyboard ─────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _textOrigin is not null || e.OriginalSource is TextBox) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (alt) return;
        var handled = true;
        switch (e.Key)
        {
            case Key.Space when !_spaceDown:
                _spaceDown = true;
                UpdateCursor();
                break;
            case Key.Z when ctrl && !shift:
                Undo();
                break;
            case Key.Y when ctrl:
            case Key.Z when ctrl && shift:
                Redo();
                break;
            case Key.C when ctrl:
                _ = CopyAsync();
                break;
            case Key.Escape:
                if (_stylePanel.Visibility == Visibility.Visible || _resizePanel.Visibility == Visibility.Visible) ClosePanels();
                else if (_crop is not null) CancelCrop();
                else if (_tool != EditTool.None) SetTool(EditTool.None);
                else if (_editing) EndEdit();
                else handled = false;
                break;
            case Key.Enter when _crop is not null:
                ApplyCrop();
                break;
            case Key.OemPlus or Key.Add:
                ZoomBy(1.25);
                break;
            case Key.OemMinus or Key.Subtract:
                ZoomBy(1 / 1.25);
                break;
            case Key.D0 or Key.NumPad0:
                ZoomToFit();
                break;
            case Key.D1 or Key.NumPad1:
                ZoomToActual();
                break;
            default:
                handled = !ctrl && HandleLetter(e.Key, shift);
                break;
        }
        if (handled) e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space && _spaceDown)
        {
            _spaceDown = false;
            UpdateCursor();
            e.Handled = true;
        }
    }

    private bool HandleLetter(Key key, bool shift)
    {
        if (!_editing)
        {
            switch (key)
            {
                case Key.R:
                    Rotate(shift ? -1 : 1);
                    return true;
                case Key.E when !_compact:
                    BeginEdit();
                    return true;
                case Key.I when _options.ShowInfoButton && !_compact:
                    InfoRequested?.Invoke();
                    return true;
            }
            return false;
        }
        if (key == Key.R && shift)
        {
            Rotate(-1);
            return true;
        }
        EditTool? t = key switch
        {
            Key.C => EditTool.Crop,
            Key.P => EditTool.Pen,
            Key.H => EditTool.Highlighter,
            Key.L => EditTool.Line,
            Key.A => EditTool.Arrow,
            Key.R => EditTool.Rectangle,
            Key.O => EditTool.Ellipse,
            Key.T => EditTool.Text,
            Key.B => EditTool.Blur,
            Key.X => EditTool.Pixelate,
            _ => null,
        };
        if (t is null) return false;
        SetTool(_tool == t ? EditTool.None : t.Value);
        return true;
    }

    // ───────────── layers ─────────────

    /// <summary>Drawings not yet burnt into the raster, plus the one being drawn (image pixels; the shared transform scales).</summary>
    private sealed class ShapeLayer : FrameworkElement
    {
        public IReadOnlyList<Annotation> Shapes { get; set; } = [];

        public Annotation? Draft { get; set; }

        public ShapeLayer()
        {
            IsHitTestVisible = false;
            ClipToBounds = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            foreach (var s in Shapes) s.Render(dc);
            Draft?.Render(dc);
        }
    }

    /// <summary>Screen-space chrome over the picture: crop frame (dimmed outside, thirds, handles, size) and region drafts.</summary>
    private sealed class Overlay : FrameworkElement
    {
        private readonly ImageViewer _v;
        private static readonly Brush DimBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)));
        private static readonly Pen Edge = FrozenPen(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF), 1);
        private static readonly Pen EdgeShadow = FrozenPen(Color.FromArgb(0x55, 0, 0, 0), 3);
        private static readonly Pen Thirds = FrozenPen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 1);
        private static readonly Pen Handle = FrozenPen(Colors.White, 3.5, PenLineCap.Round);
        private static readonly Pen HandleShadow = FrozenPen(Color.FromArgb(0x50, 0, 0, 0), 6, PenLineCap.Round);

        public Overlay(ImageViewer v)
        {
            _v = v;
            IsHitTestVisible = false;
        }

        protected override Size MeasureOverride(Size availableSize) => default;

        protected override void OnRender(DrawingContext dc)
        {
            var stage = new Rect(0, 0, _v._stage.ActualWidth, _v._stage.ActualHeight);
            if (_v._regionDraft is { IsEmpty: false } rd) DrawRegion(dc, _v.ToScreen(rd));
            if (_v._crop is not { } c || _v._tool != EditTool.Crop) return;
            var r = _v.ToScreen(c);
            var pic = _v.ToScreen(new Rect(_v._logical));
            var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(Rect.Intersect(pic, stage)), new RectangleGeometry(r));
            dc.DrawGeometry(DimBrush, null, outside);
            dc.DrawRectangle(null, EdgeShadow, r);
            for (var i = 1; i <= 2; i++)
            {
                var x = r.Left + r.Width * i / 3;
                var y = r.Top + r.Height * i / 3;
                dc.DrawLine(Thirds, new Point(x, r.Top), new Point(x, r.Bottom));
                dc.DrawLine(Thirds, new Point(r.Left, y), new Point(r.Right, y));
            }
            dc.DrawRectangle(null, Edge, r);
            var len = Math.Min(18, Math.Min(r.Width, r.Height) / 3);
            foreach (var pen in new[] { HandleShadow, Handle })
            {
                Corner(dc, pen, r.TopLeft, len, 1, 1);
                Corner(dc, pen, r.TopRight, len, -1, 1);
                Corner(dc, pen, r.BottomLeft, len, 1, -1);
                Corner(dc, pen, r.BottomRight, len, -1, -1);
                var mx = r.Left + r.Width / 2;
                var my = r.Top + r.Height / 2;
                var half = Math.Min(10, r.Width / 6);
                var halfV = Math.Min(10, r.Height / 6);
                dc.DrawLine(pen, new Point(mx - half, r.Top), new Point(mx + half, r.Top));
                dc.DrawLine(pen, new Point(mx - half, r.Bottom), new Point(mx + half, r.Bottom));
                dc.DrawLine(pen, new Point(r.Left, my - halfV), new Point(r.Left, my + halfV));
                dc.DrawLine(pen, new Point(r.Right, my - halfV), new Point(r.Right, my + halfV));
            }
            // size pill above the frame
            var label = string.Format(CultureInfo.CurrentCulture, _v._l.SizeFormat, (int)Math.Round(c.Width), (int)Math.Round(c.Height));
            var ft = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(Ui.Font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 11.5, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var pill = new Rect(r.Left, r.Top - ft.Height - 14, ft.Width + 16, ft.Height + 6);
            if (pill.Top < 4) pill.Y = r.Top + 8;
            if (pill.Top < 4) pill.Y = 4;
            pill.X = Math.Clamp(pill.X, 4, Math.Max(4, stage.Width - pill.Width - 4));
            dc.DrawRoundedRectangle(Frozen(new SolidColorBrush(Color.FromArgb(0xB8, 0x10, 0x12, 0x18))), null, pill, pill.Height / 2, pill.Height / 2);
            dc.DrawText(ft, new Point(pill.X + 8, pill.Y + 3));
        }

        private static void DrawRegion(DrawingContext dc, Rect r)
        {
            var fill = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));
            var dash = new Pen(Brushes.White, 1.5) { DashStyle = new DashStyle([4, 3], 0) };
            dash.Freeze();
            dc.DrawRoundedRectangle(fill, EdgeShadow, r, 3, 3);
            dc.DrawRoundedRectangle(null, dash, r, 3, 3);
        }

        private static void Corner(DrawingContext dc, Pen pen, Point p, double len, int dx, int dy)
        {
            dc.DrawLine(pen, p, new Point(p.X + len * dx, p.Y));
            dc.DrawLine(pen, p, new Point(p.X, p.Y + len * dy));
        }

        private static Brush Frozen(SolidColorBrush b)
        {
            b.Freeze();
            return b;
        }

        private static Pen FrozenPen(Color c, double w, PenLineCap cap = PenLineCap.Flat)
        {
            var p = new Pen(new SolidColorBrush(c), w) { StartLineCap = cap, EndLineCap = cap };
            p.Freeze();
            return p;
        }
    }
}
