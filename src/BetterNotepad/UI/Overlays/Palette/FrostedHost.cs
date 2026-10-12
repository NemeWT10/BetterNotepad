using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace BetterNotepad.UI.Palette;

/// <summary>
/// Frosted glass under a floating card (the Windows 11 acrylic look): a blurred live copy of the content
/// beneath, clipped to the card's rounded shape, under the card's own translucent panel brush — the text
/// behind a palette never shows through sharp. Exists only while the overlay is open.
/// The host takes the card's placement (alignment, margin, max width) and is what Motion animates.
/// </summary>
internal sealed class FrostedHost : Grid
{
    private const double Pad = 30;

    private readonly Visual _source;
    private readonly VisualBrush _brush;
    private readonly Grid _clipHost = new() { IsHitTestVisible = false };
    private readonly double _radius;

    public FrostedHost(FrameworkElement card, Visual source, double cornerRadius = 14)
    {
        _source = source;
        _radius = cornerRadius;
        _brush = new VisualBrush(source)
        {
            Stretch = Stretch.None,
            ViewboxUnits = BrushMappingMode.Absolute,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
        var blur = new Rectangle
        {
            Fill = _brush,
            Margin = new Thickness(-Pad),
            Effect = new BlurEffect { Radius = 26, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
        };
        _clipHost.Children.Add(blur);
        Children.Add(_clipHost);
        Children.Add(card);
        SizeChanged += (_, _) => Sync();
        Loaded += (_, _) => Sync();
    }

    /// <summary>Re-aims the blurred copy at what is under the card now (size or window changed).</summary>
    public void Sync()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || VisualTreeHelper.GetParent(this) is not Visual parent) return;
        Point origin;
        try
        {
            // layout position (without the appear animation's transform) in the source's coordinates
            origin = parent.TransformToVisual(_source).Transform(new Point()) + VisualTreeHelper.GetOffset(this);
        }
        catch (InvalidOperationException)
        {
            return; // not connected to the same tree (yet)
        }
        _brush.Viewbox = new Rect(origin.X - Pad, origin.Y - Pad, ActualWidth + 2 * Pad, ActualHeight + 2 * Pad);
        _clipHost.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), _radius, _radius);
    }
}
