namespace Historia.Tests;

/// <summary>T3.1 of the debloat audit: the lane-colour maths lifted out of
/// <c>CommitGraphControl.LaneRgb</c> / <c>BadgeInk</c>.</summary>
public class GraphLanePaletteTests
{
    private static readonly IReadOnlyList<Rgb> Lanes =
    [
        Rgb.FromRgb(200, 100, 50),
        Rgb.FromRgb(50, 200, 100),
        Rgb.FromRgb(100, 50, 200),
    ];

    [Theory]
    [InlineData(0, EffectiveTheme.Dark)]
    [InlineData(5, EffectiveTheme.Light)]
    [InlineData(20, EffectiveTheme.HighContrast)]
    [InlineData(-3, EffectiveTheme.Dark)]
    public void Empty_lane_list_falls_back_to_LanePalette(int index, EffectiveTheme mode)
    {
        Assert.Equal(
            LanePalette.ColorFor(index, mode),
            GraphLanePalette.LaneRgb(index, [], mode));
    }

    [Fact]
    public void First_cycle_returns_the_lane_entry_verbatim()
    {
        for (var i = 0; i < Lanes.Count; i++)
            Assert.Equal(Lanes[i], GraphLanePalette.LaneRgb(i, Lanes, EffectiveTheme.Dark));
    }

    [Theory]
    [InlineData(1, 0.82)]   // cycle 1: 1 - 1*0.18
    [InlineData(2, 0.64)]   // cycle 2: 1 - 2*0.18
    [InlineData(3, 0.50)]   // cycle 3: 1 - min(0.5, 0.54)  -> clamped
    [InlineData(10, 0.50)]  // deep cycles stay clamped at 0.5
    public void Past_the_first_cycle_darkens_by_the_clamped_factor(int cycle, double factor)
    {
        // index = cycle*3 lands on lane 0 each time (n == 3).
        var expected = Rgb.FromRgb(
            (byte)(Lanes[0].R * factor), (byte)(Lanes[0].G * factor), (byte)(Lanes[0].B * factor));
        Assert.Equal(expected, GraphLanePalette.LaneRgb(cycle * Lanes.Count, Lanes, EffectiveTheme.Dark));
    }

    [Fact]
    public void Negative_index_wraps_and_is_treated_as_cycle_zero()
    {
        // -1 % 3 -> i = 2; -1 / 3 -> cycle 0, so the entry comes back undarkened.
        Assert.Equal(Lanes[2], GraphLanePalette.LaneRgb(-1, Lanes, EffectiveTheme.Dark));
        Assert.Equal(Lanes[1], GraphLanePalette.LaneRgb(-2, Lanes, EffectiveTheme.Dark));
    }

    [Theory]
    [InlineData(255, 255, 255, true)]   // white — luminance 1.0
    [InlineData(0, 0, 0, false)]        // black — luminance 0
    [InlineData(255, 255, 0, true)]     // yellow — well above 0.55
    [InlineData(0, 0, 255, false)]      // blue — well below 0.55
    public void PrefersDarkInk_splits_on_relative_luminance(byte r, byte g, byte b, bool dark)
    {
        Assert.Equal(dark, GraphLanePalette.PrefersDarkInk(Rgb.FromRgb(r, g, b)));
    }
}
