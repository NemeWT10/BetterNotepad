using System.Windows;
using System.Windows.Media;

namespace BetterNotepad.Hex;

public sealed partial class HexView
{
    private double Crisp(double v) => (Math.Floor(v * _pixelsPerDip) + 0.5) / _pixelsPerDip;

    private double Snap(double v) => Math.Round(v * _pixelsPerDip) / _pixelsPerDip;

    private GlyphRun Run(ushort[] glyphs, double x, double baseline)
    {
        var advances = new double[glyphs.Length];
        Array.Fill(advances, _geo.CharWidth);
        return new GlyphRun(_glyphs!, 0, false, _fontSize, (float)_pixelsPerDip, glyphs, new Point(x, baseline), advances, null, null, null, null, null, null);
    }

    private ushort[] GlyphsFor(string text, int maxChars)
    {
        var map = _glyphs!.CharacterToGlyphMap;
        var shown = text.Length <= maxChars ? text : text[..Math.Max(0, maxChars - 1)] + "…";
        var result = new ushort[shown.Length];
        for (var i = 0; i < shown.Length; i++)
        {
            if (map.TryGetValue(shown[i], out var g)) result[i] = g;
            else result[i] = map.TryGetValue('?', out var q) ? q : _spaceGlyph;
        }

        return result;
    }

    private bool HasContent(ushort[] glyphs)
    {
        foreach (var g in glyphs) if (g != _spaceGlyph) return true;
        return false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        EnsureMetrics();
        var size = new Size(ActualWidth, ActualHeight);
        if (Background is { } bg) dc.DrawRectangle(bg, null, new Rect(size));
        if (_glyphs is null || size.Width <= 0 || size.Height <= 0) return;

        var g = _geo;
        var bpr = g.BytesPerRow;
        var rowH = g.RowHeight;
        var fg = Foreground ?? SystemColors.WindowTextBrush;
        var faint = FaintForeground ?? fg;
        var focused = IsKeyboardFocusWithin || AlwaysActiveSelection;
        var selBrush = focused ? SelectionBrush : (InactiveSelectionBrush ?? SelectionBrush);
        var contentRight = size.Width - (_scroll.Visibility == Visibility.Visible ? _scrollBarWidth : 0);
        var bodyHeight = Math.Max(0, size.Height - _headerHeight);

        // column dividers
        var sepBrush = SeparatorBrush ?? faint;
        var sepPen = new Pen(sepBrush, 1 / _pixelsPerDip);
        if (_length > 0)
        {
            dc.DrawLine(sepPen, new Point(Crisp(g.Divider1), 0), new Point(Crisp(g.Divider1), size.Height));
            dc.DrawLine(sepPen, new Point(Crisp(g.Divider2), 0), new Point(Crisp(g.Divider2), size.Height));
        }

        var firstRow = (long)Math.Floor(_scrollRow);
        var y0 = _headerHeight - (_scrollRow - firstRow) * rowH;
        var visibleRows = (int)Math.Ceiling(bodyHeight / rowH) + 2;
        var nRows = (int)Math.Max(0, Math.Min(TotalRows - firstRow, visibleRows));
        if (nRows > 0 && _source is not null)
        {
            var need = nRows * bpr;
            if (_rowBuffer.Length < need) _rowBuffer = new byte[need];
            var read = _source.Read(firstRow * bpr, _rowBuffer.AsSpan(0, need));
            var selStart = SelectionStart;
            var selEnd = selStart + SelectionLength - 1;
            var single = SelectionLength == 1;
            var caretPen = new Pen(CaretBrush ?? fg, 1.5);

            dc.PushClip(new RectangleGeometry(new Rect(0, _headerHeight, size.Width, bodyHeight)));
            for (var r = 0; r < nRows; r++)
            {
                var rowOffset = (firstRow + r) * bpr;
                var y = y0 + r * rowH;
                if (y > size.Height) break;
                var n = Math.Clamp(read - r * bpr, 0, bpr);
                var caretInRow = _caret >= rowOffset && _caret < rowOffset + bpr && _caret < _length;

                if (caretInRow && CurrentLineBrush is { } line) dc.DrawRectangle(line, null, new Rect(0, y, contentRight, rowH));

                // selection (one range shared by both panes)
                if (_length > 0 && selEnd >= rowOffset && selStart < rowOffset + n)
                {
                    var i0 = (int)Math.Max(0, selStart - rowOffset);
                    var i1 = (int)Math.Min(n - 1, selEnd - rowOffset);
                    if (i1 >= i0 && selBrush is not null)
                    {
                        var multiRow = selStart < rowOffset || selEnd >= rowOffset + bpr;
                        var radius = multiRow ? 0 : 3;
                        if (!single || _activePane == HexPane.Hex)
                        {
                            var (l, rt) = g.HexSpan(i0, i1);
                            dc.DrawRoundedRectangle(selBrush, null, new Rect(Snap(l), y, Snap(rt) - Snap(l), rowH), radius, radius);
                        }

                        if (!single || _activePane == HexPane.Text)
                        {
                            var (l, rt) = g.TextSpan(i0, i1);
                            dc.DrawRoundedRectangle(selBrush, null, new Rect(Snap(l), y, Snap(rt) - Snap(l), rowH), radius, radius);
                        }
                    }
                }

                // caret: a frame in both panes, emphasized in the one you clicked
                if (caretInRow && _length > 0)
                {
                    var ci = (int)(_caret - rowOffset);
                    var (hl, hr) = g.HexSpan(ci, ci);
                    var (tl, tr) = g.TextSpan(ci, ci);
                    var hexRect = new Rect(Crisp(hl), y + 1, Snap(hr) - Snap(hl), rowH - 2);
                    var textRect = new Rect(Crisp(tl), y + 1, Snap(tr) - Snap(tl), rowH - 2);
                    var activeHex = _activePane == HexPane.Hex;
                    if (!activeHex) dc.PushOpacity(0.5);
                    dc.DrawRoundedRectangle(null, activeHex ? caretPen : new Pen(caretPen.Brush, 1), hexRect, 3, 3);
                    if (!activeHex) dc.Pop();
                    if (activeHex) dc.PushOpacity(0.5);
                    dc.DrawRoundedRectangle(null, !activeHex ? caretPen : new Pen(caretPen.Brush, 1), textRect, 3, 3);
                    if (activeHex) dc.Pop();
                }

                // glyphs
                var baseline = y + _baseline;
                var offsetGlyphs = new ushort[g.OffsetDigits];
                var o = rowOffset;
                for (var d = g.OffsetDigits - 1; d >= 0; d--)
                {
                    offsetGlyphs[d] = _digitGlyph[(int)(o & 0xF)];
                    o >>= 4;
                }

                dc.DrawGlyphRun(caretInRow ? fg : faint, Run(offsetGlyphs, g.XOffset, baseline));

                var hexNormal = new ushort[g.HexChars];
                var hexFaint = new ushort[g.HexChars];
                var textNormal = new ushort[bpr];
                var textFaint = new ushort[bpr];
                Array.Fill(hexNormal, _spaceGlyph);
                Array.Fill(hexFaint, _spaceGlyph);
                Array.Fill(textNormal, _spaceGlyph);
                Array.Fill(textFaint, _spaceGlyph);
                for (var i = 0; i < n; i++)
                {
                    var b = _rowBuffer[r * bpr + i];
                    var col = HexGeometry.HexCol(i);
                    var target = b == 0 ? hexFaint : hexNormal;
                    target[col] = _digitGlyph[b >> 4];
                    target[col + 1] = _digitGlyph[b & 0xF];
                    if (_textShown[b]) textNormal[i] = _textGlyph[b];
                    else textFaint[i] = _dotGlyph;
                }

                if (HasContent(hexNormal)) dc.DrawGlyphRun(fg, Run(hexNormal, g.XHex, baseline));
                if (HasContent(hexFaint)) dc.DrawGlyphRun(faint, Run(hexFaint, g.XHex, baseline));
                if (HasContent(textNormal)) dc.DrawGlyphRun(fg, Run(textNormal, g.XText, baseline));
                if (HasContent(textFaint)) dc.DrawGlyphRun(faint, Run(textFaint, g.XText, baseline));
            }

            dc.Pop();
        }
        else if (_length == 0 && EmptyText.Length > 0)
        {
            var glyphs = GlyphsFor(EmptyText, 80);
            var w = glyphs.Length * g.CharWidth;
            dc.DrawGlyphRun(faint, Run(glyphs, Snap(Math.Max(0, (contentRight - w) / 2)), Snap(_headerHeight + bodyHeight / 2 - rowH / 2 + _baseline)));
        }

        if (ShowHeader) DrawHeader(dc, size, fg, faint, sepPen);
    }

    private void DrawHeader(DrawingContext dc, Size size, Brush fg, Brush faint, Pen sepPen)
    {
        var g = _geo;
        var bpr = g.BytesPerRow;
        if (Background is { } bg) dc.DrawRectangle(bg, null, new Rect(0, 0, size.Width, _headerHeight));
        var baseline = _baseline + 1;
        var offsetLabel = GlyphsFor(OffsetLabel, g.OffsetDigits + 1);
        if (HasContent(offsetLabel)) dc.DrawGlyphRun(faint, Run(offsetLabel, g.XOffset, baseline));

        var normal = new ushort[g.HexChars];
        var current = new ushort[g.HexChars];
        Array.Fill(normal, _spaceGlyph);
        Array.Fill(current, _spaceGlyph);
        var caretCol = _length > 0 ? (int)(_caret % bpr) : -1;
        for (var i = 0; i < bpr; i++)
        {
            var col = HexGeometry.HexCol(i);
            var target = i == caretCol ? current : normal;
            target[col] = _digitGlyph[i >> 4 & 0xF];
            target[col + 1] = _digitGlyph[i & 0xF];
        }

        dc.DrawGlyphRun(faint, Run(normal, g.XHex, baseline));
        if (caretCol >= 0) dc.DrawGlyphRun(fg, Run(current, g.XHex, baseline));
        var textLabel = GlyphsFor(TextLabel, bpr + 2);
        if (HasContent(textLabel)) dc.DrawGlyphRun(faint, Run(textLabel, g.XText, baseline));

        var y = Crisp(_headerHeight - 1);
        dc.DrawLine(sepPen, new Point(0, y), new Point(size.Width, y));
    }
}
