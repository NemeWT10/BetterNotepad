using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace BetterNotepad.Hex;

public sealed partial class HexView
{
    private bool _dragging;
    private DispatcherTimer? _dragTimer;
    private Point _dragPoint;

    // ------------------------------------------------------------------ selection API

    /// <summary>Selects bytes [start, start + length) and scrolls them into view.</summary>
    public void Select(long start, long length, bool scrollIntoView = true)
    {
        if (_length == 0) return;
        start = Math.Clamp(start, 0, _length - 1);
        var end = Math.Clamp(start + Math.Max(1, length) - 1, start, _length - 1);
        _anchor = start;
        _caret = end;
        AfterSelectionChanged(scrollIntoView ? end : -1, scrollStart: start);
    }

    /// <summary>"Go to offset": selects <paramref name="length"/> bytes there and brings them into view (centered when far away).</summary>
    public void GoToOffset(long offset, long length = 1) => Select(offset, length);

    public void SelectAll()
    {
        if (_length == 0) return;
        _anchor = 0;
        _caret = _length - 1;
        AfterSelectionChanged(-1);
    }

    /// <summary>Collapses the selection to the caret byte.</summary>
    public void ClearSelection()
    {
        _anchor = _caret;
        AfterSelectionChanged(-1);
    }

    private void SetCaret(long offset, bool extend, bool scroll = true)
    {
        if (_length == 0) return;
        _caret = Math.Clamp(offset, 0, _length - 1);
        if (!extend) _anchor = _caret;
        AfterSelectionChanged(scroll ? _caret : -1);
    }

    private void AfterSelectionChanged(long ensureVisible, long scrollStart = -1)
    {
        if (ensureVisible >= 0)
        {
            if (scrollStart >= 0 && scrollStart != ensureVisible)
            {
                // a range: show its start, and its end if both fit
                ScrollIntoView(ensureVisible);
                ScrollIntoView(scrollStart);
            }
            else ScrollIntoView(ensureVisible);
        }

        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ reading and copying

    /// <summary>Bytes of the selection, or <see langword="null"/> when it is empty or larger than <see cref="MaxCopyBytes"/>.</summary>
    public byte[]? GetSelectedBytes()
    {
        if (_source is null || _length == 0 || SelectionLength > MaxCopyBytes || SelectionLength > int.MaxValue) return null;
        var buffer = new byte[SelectionLength];
        var read = _source.Read(SelectionStart, buffer);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    /// <summary>Reads bytes from the source (for a status bar value inspector, for example).</summary>
    public byte[] ReadBytes(long offset, int count)
    {
        if (_source is null || offset < 0 || offset >= _length) return [];
        var buffer = new byte[(int)Math.Min(count, _length - offset)];
        var read = _source.Read(offset, buffer);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    /// <summary>The selection as hex text (or null when too big / empty).</summary>
    public string? GetSelectionAsHex(HexCopyFormat format = HexCopyFormat.Spaced)
    {
        var bytes = GetSelectedBytes();
        if (bytes is null) return null;
        return format == HexCopyFormat.Dump
            ? HexFormat.ToDump(bytes, SelectionStart, EffectiveBytesPerRow, _table, _length)
            : HexFormat.ToHex(bytes, format);
    }

    /// <summary>The selection as the text column shows it (dots for unprintable bytes).</summary>
    public string? GetSelectionAsText()
    {
        var bytes = GetSelectedBytes();
        return bytes is null ? null : HexFormat.ToText(bytes, _table);
    }

    /// <summary>Copies the selection as hex to the clipboard. False when nothing could be copied (empty, too big, clipboard busy).</summary>
    public bool CopyAsHex(HexCopyFormat format = HexCopyFormat.Spaced) => TrySetClipboard(GetSelectionAsHex(format));

    /// <summary>Copies the selection as text to the clipboard.</summary>
    public bool CopyAsText() => TrySetClipboard(GetSelectionAsText());

    private static bool TrySetClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                System.Threading.Thread.Sleep(15); // another program holds the clipboard for a moment
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ mouse

    private bool TryHit(Point p, HexPane pane, out long offset)
    {
        offset = 0;
        if (_length == 0 || _geo.BytesPerRow == 0) return false;
        var bpr = _geo.BytesPerRow;
        var row = (long)Math.Floor((p.Y - _headerHeight) / _geo.RowHeight + _scrollRow);
        row = Math.Clamp(row, 0, Math.Max(0, TotalRows - 1));
        var col = pane == HexPane.Hex ? _geo.HitHex(p.X) : _geo.HitText(p.X);
        offset = Math.Min(_length - 1, row * bpr + col);
        return true;
    }

    /// <summary>The byte under a point of this control (for context menus and tooltips). False over the header, the scroll bar or an empty file.</summary>
    public bool TryGetOffsetAt(Point point, out long offset)
    {
        offset = 0;
        if (point.Y < _headerHeight || point.X < 0 || point.X > ActualWidth - (_scroll.Visibility == Visibility.Visible ? _scrollBarWidth : 0)) return false;
        return TryHit(point, _geo.PaneAt(point.X), out offset);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (!PointerDown(e.GetPosition(this), (Keyboard.Modifiers & ModifierKeys.Shift) != 0)) return;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        PointerMove(e.GetPosition(this));
    }

    /// <summary>Starts a selection at a point (a click); the pane under the point becomes the active pane.</summary>
    internal bool PointerDown(Point p, bool extend)
    {
        if (_length == 0 || p.Y < _headerHeight) return false;
        var pane = _geo.PaneAt(p.X);
        if (!TryHit(p, pane, out var offset)) return false;
        _activePane = pane;
        SetCaret(offset, extend, scroll: false);
        _dragging = true;
        _dragPoint = p;
        return true;
    }

    /// <summary>Extends the selection while dragging; outside the rows it starts the auto-scroll.</summary>
    internal void PointerMove(Point p)
    {
        if (!_dragging) return;
        _dragPoint = p;
        ExtendDrag();
        var bodyBottom = _headerHeight + _bodyHeight;
        var outside = _dragPoint.Y < _headerHeight || _dragPoint.Y > bodyBottom;
        if (outside && _dragTimer is null)
        {
            _dragTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
            _dragTimer.Tick += (_, _) =>
            {
                var d = _dragPoint.Y < _headerHeight ? _dragPoint.Y - _headerHeight : _dragPoint.Y > bodyBottom ? _dragPoint.Y - bodyBottom : 0;
                if (d == 0) return;
                ScrollToRow(_scrollRow + Math.Clamp(d / _geo.RowHeight, -6, 6) * 0.6);
                ExtendDrag();
            };
            _dragTimer.Start();
        }
        else if (!outside) StopDragTimer();
    }

    internal void PointerUp() => EndDrag();

    private void ExtendDrag()
    {
        // the byte under the pointer, whichever pane it is over: a drag may cross from the hex to the text column
        if (TryHit(_dragPoint, _geo.PaneAt(_dragPoint.X), out var offset) && offset != _caret) SetCaret(offset, extend: true, scroll: false);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndDrag();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndDrag();
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        StopDragTimer();
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void StopDragTimer()
    {
        _dragTimer?.Stop();
        _dragTimer = null;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_length == 0) return;
        var lines = SystemParameters.WheelScrollLines;
        var rowsPerNotch = lines <= 0 ? Math.Max(1, ViewportRowsCore - 1) : lines;
        ScrollToRow(_targetRow - e.Delta / 120.0 * rowsPerNotch, animate: true);
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_length == 0) return;
        var mods = Keyboard.Modifiers;
        var shift = (mods & ModifierKeys.Shift) != 0;
        var ctrl = (mods & ModifierKeys.Control) != 0;
        var bpr = EffectiveBytesPerRow;
        var page = Math.Max(1, (long)Math.Floor(ViewportRowsCore) - 1) * bpr;
        var handled = true;
        switch (e.Key)
        {
            case Key.Left: SetCaret(_caret - 1, shift); break;
            case Key.Right: SetCaret(_caret + 1, shift); break;
            case Key.Up:
                if (_caret >= bpr) SetCaret(_caret - bpr, shift);
                break;
            case Key.Down:
                if (_caret + bpr <= _length - 1) SetCaret(_caret + bpr, shift);
                else if (_caret / bpr < (_length - 1) / bpr) SetCaret(_length - 1, shift);
                break;
            case Key.PageUp: SetCaret(Math.Max(0, _caret - page), shift); break;
            case Key.PageDown: SetCaret(_caret + page, shift); break;
            case Key.Home: SetCaret(ctrl ? 0 : _caret - _caret % bpr, shift); break;
            case Key.End: SetCaret(ctrl ? _length - 1 : Math.Min(_length - 1, _caret - _caret % bpr + bpr - 1), shift); break;
            case Key.Escape: ClearSelection(); break;
            case Key.A when ctrl: SelectAll(); break;
            case Key.C when ctrl: if (shift) CopyAsText(); else CopyAsHex(); break;
            case Key.Insert when ctrl: CopyAsHex(); break;
            default: handled = false; break;
        }

        e.Handled = handled;
    }
}
