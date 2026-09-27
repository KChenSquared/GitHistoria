namespace Historia.Tests;

/// <summary>the navigation bus provides a read side so a native control can tell
/// whether Home or a repository is showing.</summary>
[Trait("tier", "unit")]
public class NavigationBusTests
{
    [Fact]
    public void CurrentRoute_starts_at_home_and_tracks_reports()
    {
        var bus = new NavigationBus();
        Assert.Equal("", bus.CurrentRoute);

        var seen = new List<string>();
        bus.RouteChanged += seen.Add;

        bus.ReportRoute("repo");
        bus.ReportRoute("settings");
        bus.ReportRoute("");

        Assert.Equal("", bus.CurrentRoute);
        Assert.Equal(new[] { "repo", "settings", "" }, seen);
    }

    [Fact]
    public void ReportRoute_deduplicates_identical_reports()
    {
        var bus = new NavigationBus();
        var count = 0;
        bus.RouteChanged += _ => count++;

        bus.ReportRoute("repo");
        bus.ReportRoute("repo");
        bus.ReportRoute("repo");

        Assert.Equal(1, count);
    }
}
