using System.Windows;
using System.Windows.Controls;

namespace BetterNotepad.UI.Controls;

/// <summary>
/// Horizontal panel for the tab chips: chips keep their natural width until the strip is full, then the widest
/// ones shrink first (their titles trim) down to their minimum width; beyond that the strip scrolls.
/// <see cref="AvailableWidth"/> is the visible width of the strip (the parent scroll viewer measures us with infinity).
/// </summary>
public sealed class TabStripPanel : Panel
{
    public static readonly DependencyProperty AvailableWidthProperty = DependencyProperty.Register(
        nameof(AvailableWidth), typeof(double), typeof(TabStripPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double AvailableWidth
    {
        get => (double)GetValue(AvailableWidthProperty);
        set => SetValue(AvailableWidthProperty, value);
    }

    private double[] _widths = [];

    protected override Size MeasureOverride(Size constraint)
    {
        var items = InternalChildren.Cast<UIElement>().ToList();
        var inf = new Size(double.PositiveInfinity, constraint.Height);
        foreach (var c in items) c.Measure(inf);

        // slot widths include the chips' margins (DesiredSize does too)
        var natural = items.Select(c => c.DesiredSize.Width).ToArray();
        var mins = items.Select(c => c is FrameworkElement fe ? Math.Min(fe.MinWidth + fe.Margin.Left + fe.Margin.Right, c.DesiredSize.Width) : 0).ToArray();
        _widths = Fit(natural, mins, AvailableWidth > 0 ? AvailableWidth : double.PositiveInfinity);

        double height = 0;
        for (var i = 0; i < items.Count; i++)
        {
            items[i].Measure(new Size(_widths[i], constraint.Height));
            height = Math.Max(height, items[i].DesiredSize.Height);
        }
        return new Size(_widths.Sum(), height);
    }

    protected override Size ArrangeOverride(Size arrange)
    {
        double x = 0;
        for (var i = 0; i < InternalChildren.Count && i < _widths.Length; i++)
        {
            InternalChildren[i].Arrange(new Rect(x, 0, _widths[i], arrange.Height));
            x += _widths[i];
        }
        return arrange;
    }

    /// <summary>Water-filling: lowers a common width cap until everything fits (never below a chip's minimum).</summary>
    private static double[] Fit(double[] natural, double[] mins, double avail)
    {
        if (natural.Sum() <= avail || natural.Length == 0) return (double[])natural.Clone();

        double lo = 0, hi = natural.Max();
        for (var iter = 0; iter < 30; iter++)
        {
            var cap = (lo + hi) / 2;
            var sum = 0.0;
            for (var i = 0; i < natural.Length; i++) sum += Math.Max(mins[i], Math.Min(natural[i], cap));
            if (sum > avail) hi = cap;
            else lo = cap;
        }
        var result = new double[natural.Length];
        for (var i = 0; i < natural.Length; i++) result[i] = Math.Max(mins[i], Math.Min(natural[i], lo));
        return result;
    }
}
