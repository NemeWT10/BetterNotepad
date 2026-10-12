using System.Windows;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Tests;

public sealed class PinGeometryTests
{
    private static readonly Rect Work = new(0, 0, 1920, 1040);
    private static readonly Size Min = PinGeometry.MinSize;

    [Fact]
    public void Grows_around_the_center_with_the_ratio_kept()
    {
        var r = new Rect(500, 300, 400, 500);
        var next = PinGeometry.ScaleAroundCenter(r, 1.1, Work, Min);
        Assert.Equal(440, next.Width, 6);
        Assert.Equal(550, next.Height, 6);
        Assert.Equal(r.Left + r.Width / 2, next.Left + next.Width / 2, 6);
        Assert.Equal(r.Top + r.Height / 2, next.Top + next.Height / 2, 6);
    }

    [Fact]
    public void Never_exceeds_the_work_area_and_keeps_the_ratio()
    {
        var r = new Rect(100, 100, 800, 1000);
        var next = PinGeometry.ScaleAroundCenter(r, 1.5, Work, Min);
        Assert.True(next.Height <= Work.Height + 0.001);
        Assert.Equal(0.8, next.Width / next.Height, 6);
        Assert.True(Work.Contains(next));
    }

    [Fact]
    public void Shrinking_stops_at_the_minimum_size()
    {
        var r = new Rect(100, 100, 300, 300);
        for (var i = 0; i < 50; i++) r = PinGeometry.ScaleAroundCenter(r, 1 / PinGeometry.WheelStep, Work, Min);
        Assert.Equal(Min.Width, r.Width, 6);
        Assert.Equal(r.Width, r.Height, 6); // square stays square
    }

    [Fact]
    public void Growing_near_an_edge_moves_the_window_back_inside()
    {
        var r = new Rect(1700, 900, 200, 120);
        var next = PinGeometry.ScaleAroundCenter(r, 1.3, Work, Min);
        Assert.True(next.Right <= Work.Right + 0.001 && next.Bottom <= Work.Bottom + 0.001);
        Assert.Equal(260, next.Width, 6);
    }

    [Fact]
    public void Off_screen_test_windows_are_not_pulled_onto_the_screen()
    {
        var r = new Rect(-32000, -32000, 400, 300);
        var next = PinGeometry.ScaleAroundCenter(r, 1.1, Work, Min, keepInside: false);
        Assert.True(next.Left < -30000);
        Assert.Equal(440, next.Width, 6);
    }

    [Fact]
    public void First_placement_puts_the_cursor_on_the_bar_inside_the_monitor()
    {
        var near = PinGeometry.PlaceNear(new Point(800, 400), new Size(480, 400), Work);
        Assert.True(near.Contains(new Point(800, 400)));
        Assert.True(near.Top <= 400 && 400 - near.Top < 40, "the cursor lands on the top bar");

        var corner = PinGeometry.PlaceNear(new Point(1910, 1030), new Size(520, 680), Work);
        Assert.True(Work.Contains(corner));
        Assert.Equal(new Size(520, 680), corner.Size);
    }

    [Fact]
    public void A_huge_size_is_trimmed_to_the_monitor()
    {
        var r = PinGeometry.PlaceNear(new Point(10, 10), new Size(5000, 5000), new Rect(0, 0, 1000, 800));
        Assert.True(r.Width <= 900.001 && r.Height <= 720.001);
    }
}
