namespace HistoriaApp.Tests;

[Trait("tier", "unit")]
public sealed class FrameBenchSettingsTests
{
    [Fact]
    public void Bench_settings_turn_logging_off()
    {
        var settings = Historia.Diagnostics.FrameBenchRunner.BuildSettings(
            Historia.Diagnostics.FrameBenchMode.All);

        Assert.False(settings.LoggingEnabled);
        Assert.False(settings.AutoFetchEnabled);
    }
}
