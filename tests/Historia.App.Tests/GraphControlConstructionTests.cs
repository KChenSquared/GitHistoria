using Avalonia;
using Avalonia.Headless.XUnit;
using Historia.Controls;
using Xunit;

namespace HistoriaApp.Tests;

/// <summary>
/// Characterization tripwire for the Tier 3 extractions from <see cref="CommitGraphControl"/>
/// and <see cref="AppBandControl"/> (debloat audit §5.1 / §5.2). These do not exercise the
/// data-bound render pass — they pin the invariant that the controls still <em>construct</em>
/// (their field initializers + ctor build a large child-control tree) and survive a
/// service-less measure/arrange without throwing. A botched rewire of an extracted helper that
/// touches the ctor or the layout overrides trips this before the diff lands.
/// </summary>
[Trait("tier", "ui")]
public class GraphControlConstructionTests
{
    [AvaloniaFact]
    public void CommitGraphControl_constructs_and_lays_out_without_services()
    {
        var graph = new CommitGraphControl();

        graph.Measure(new Size(1000, 700));
        graph.Arrange(new Rect(0, 0, 1000, 700));

        Assert.True(double.IsFinite(graph.DesiredSize.Width));
        Assert.True(double.IsFinite(graph.DesiredSize.Height));
        Assert.Equal(1000, graph.Bounds.Width, 1);
    }

    [AvaloniaFact]
    public void AppBandControl_constructs_and_lays_out_without_services()
    {
        var band = new AppBandControl();

        band.Measure(new Size(900, 64));
        band.Arrange(new Rect(0, 0, 900, 64));

        Assert.True(double.IsFinite(band.DesiredSize.Height));
        // Ctor pins the single-row band height (RowHeight 31 + 1).
        Assert.Equal(32, band.Height, 1);

        band.Dispose();
    }
}
