using System.Windows;

namespace BetterNotepad.UI.Windows;

/// <summary>
/// Placement math of pinned-on-top windows (pure, unit-tested): Alt+wheel resizing around the center with the
/// aspect ratio kept, clamped to the monitor's work area, and the first placement near the cursor.
/// All values in DIPs.
/// </summary>
public static class PinGeometry
{
    /// <summary>One wheel notch of Alt+wheel: 10 % bigger / smaller.</summary>
    public const double WheelStep = 1.1;

    public static readonly Size MinSize = new(220, 160);

    /// <summary>
    /// Scales <paramref name="r"/> by <paramref name="factor"/> around its center, keeping the aspect ratio:
    /// never smaller than <paramref name="min"/>, never larger than <paramref name="work"/>; with
    /// <paramref name="keepInside"/> the result is moved back into the work area.
    /// </summary>
    public static Rect ScaleAroundCenter(Rect r, double factor, Rect work, Size min, bool keepInside = true)
    {
        if (r.Width <= 0 || r.Height <= 0 || factor <= 0) return r;
        var aspect = r.Width / r.Height;
        var w = r.Width * factor;
        var h = r.Height * factor;
        if (factor < 1)
        {
            // shrinking stops at the minimum size (the ratio wins over the minimum of the other side)
            if (w < min.Width) { w = min.Width; h = w / aspect; }
            if (h < min.Height) { h = min.Height; w = h * aspect; }
            if (w > r.Width || h > r.Height) return r; // already at the minimum
        }
        if (!work.IsEmpty && work.Width > 0 && work.Height > 0)
        {
            if (w > work.Width) { w = work.Width; h = w / aspect; }
            if (h > work.Height) { h = work.Height; w = h * aspect; }
        }
        var x = r.Left + (r.Width - w) / 2;
        var y = r.Top + (r.Height - h) / 2;
        var result = new Rect(x, y, w, h);
        return keepInside ? KeepInside(result, work) : result;
    }

    /// <summary>Moves (never resizes, unless it is larger) a rectangle into the work area.</summary>
    public static Rect KeepInside(Rect r, Rect work)
    {
        if (work.IsEmpty || work.Width <= 0 || work.Height <= 0) return r;
        var w = Math.Min(r.Width, work.Width);
        var h = Math.Min(r.Height, work.Height);
        var x = Math.Clamp(r.Left, work.Left, Math.Max(work.Left, work.Right - w));
        var y = Math.Clamp(r.Top, work.Top, Math.Max(work.Top, work.Bottom - h));
        return new Rect(x, y, w, h);
    }

    /// <summary>
    /// First placement of a pinned window without remembered bounds: the cursor lands on the top bar,
    /// a little in from the left edge, and the whole window stays on the cursor's monitor.
    /// </summary>
    public static Rect PlaceNear(Point cursor, Size size, Rect work)
    {
        var w = size.Width;
        var h = size.Height;
        if (!work.IsEmpty && work.Width > 0 && work.Height > 0)
        {
            w = Math.Min(w, work.Width * 0.9);
            h = Math.Min(h, work.Height * 0.9);
        }
        return KeepInside(new Rect(cursor.X - 56, cursor.Y - 16, w, h), work);
    }

    /// <summary>A reasonable first size: documents (PDF, images) are portrait pages, text is a note.</summary>
    public static Size DefaultSize(bool page) => page ? new Size(520, 680) : new Size(480, 400);
}
