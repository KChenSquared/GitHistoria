namespace Historia.Tests;

/// <summary>T3.6 of the debloat audit: the app-band tab drag geometry lifted out of
/// <c>AppBandControl</c>.</summary>
[Trait("tier", "unit")]
public class AppBandDragReorderTests
{
    [Theory]
    [InlineData(100, 103, false)]   // a click jitter under the 4 px threshold
    [InlineData(100, 104, true)]    // exactly the threshold
    [InlineData(100, 90, true)]     // travel the other way
    public void PassesThreshold_flips_a_click_into_a_drag(double press, double now, bool expected)
    {
        Assert.Equal(expected, AppBandDragReorder.PassesThreshold(press, now, threshold: 4));
    }

    // Three 100 px tabs -> mid-points at 50, 150, 250.
    private static readonly double[] Three = [100, 100, 100];

    [Theory]
    [InlineData(0, 0)]
    [InlineData(49, 0)]
    [InlineData(51, 1)]     // past tab 0's mid-point
    [InlineData(160, 2)]    // past tab 1's mid-point
    [InlineData(400, 2)]    // way past the end -> clamped to the last index
    public void TargetIndex_lands_on_the_slot_whose_midpoint_the_cursor_passed(double panelX, int expected)
    {
        Assert.Equal(expected, AppBandDragReorder.TargetIndex(panelX, Three, fallbackWidth: 186));
    }

    [Fact]
    public void TargetIndex_uses_the_fallback_width_for_unmeasured_tabs()
    {
        double[] widths = [0, 0, 0];   // nothing laid out yet
        // fallback 100 -> mid-points at 50, 150, 250, same as the measured case.
        Assert.Equal(1, AppBandDragReorder.TargetIndex(60, widths, fallbackWidth: 100));
        Assert.Equal(0, AppBandDragReorder.TargetIndex(40, widths, fallbackWidth: 100));
    }

    [Theory]
    [InlineData(10, -1)]    // inside the left zone
    [InlineData(28, 0)]     // exactly at the zone edge -> neutral
    [InlineData(400, 0)]    // mid-strip
    [InlineData(775, 1)]    // inside the right zone (width 800, zone 28 -> trigger past 772)
    public void EdgeDirection_triggers_only_inside_a_zone(double viewportX, int expected)
    {
        Assert.Equal(expected, AppBandDragReorder.EdgeDirection(viewportX, viewportWidth: 800, zone: 28));
    }
}
