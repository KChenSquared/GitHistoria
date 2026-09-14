namespace Historia.Tests;

/// <summary>The shared wheel-direction sign used by the custom graph / tab-strip handlers.</summary>
public class WheelInputTests
{
    [Theory]
    [InlineData(WheelScrollDirection.Standard, true, 1)]
    [InlineData(WheelScrollDirection.Standard, false, 1)]
    [InlineData(WheelScrollDirection.Inverted, true, -1)]
    [InlineData(WheelScrollDirection.Inverted, false, -1)]
    [InlineData(WheelScrollDirection.Auto, false, 1)]   // Windows / standard OS
    [InlineData(WheelScrollDirection.Auto, true, -1)]   // macOS / Linux natural scrolling
    public void Sign_matches_setting_and_probe(WheelScrollDirection setting, bool natural, double expected)
        => Assert.Equal(expected, WheelInput.Sign(setting, natural));
}
