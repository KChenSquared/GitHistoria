namespace Historia.App.Tests;

public class GraphHoverDwellTests
{
    [Fact]
    public void Delay_is_one_second()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), GraphHoverDwell.Delay);
    }

    [Fact]
    public void A_new_target_is_not_shown_before_it_elapses()
    {
        var dwell = new GraphHoverDwell();
        var target = new HoverTarget(HoverTargetKind.Message, 0, -1);
        var step = dwell.Move(target);

        Assert.Equal(DwellStep.Restart, step);
        Assert.Equal(target, dwell.Pending);
        Assert.Null(dwell.Shown);
    }

    [Fact]
    public void Moving_within_the_same_target_keeps_the_pending_wait()
    {
        var dwell = new GraphHoverDwell();
        var target = new HoverTarget(HoverTargetKind.Message, 0, -1);
        dwell.Move(target);

        var step2 = dwell.Move(target);
        Assert.Equal(DwellStep.Keep, step2);
        Assert.Equal(target, dwell.Pending);
        Assert.Null(dwell.Shown);
    }

    [Fact]
    public void A_different_target_restarts_the_wait()
    {
        var dwell = new GraphHoverDwell();
        var target1 = new HoverTarget(HoverTargetKind.Message, 0, -1);
        var target2 = new HoverTarget(HoverTargetKind.Message, 1, -1);

        dwell.Move(target1);
        var step = dwell.Move(target2);

        Assert.Equal(DwellStep.Restart, step);
        Assert.Equal(target2, dwell.Pending);
        Assert.Null(dwell.Shown);
    }

    [Fact]
    public void Leaving_every_target_clears()
    {
        var dwell = new GraphHoverDwell();
        var target = new HoverTarget(HoverTargetKind.Message, 0, -1);
        dwell.Move(target);

        var step = dwell.Move(null);
        Assert.Equal(DwellStep.Clear, step);
        Assert.Null(dwell.Pending);
        Assert.Null(dwell.Shown);
    }

    [Fact]
    public void A_shown_target_stays_shown_while_the_pointer_stays()
    {
        var dwell = new GraphHoverDwell();
        var target = new HoverTarget(HoverTargetKind.Message, 0, -1);
        dwell.Move(target);

        var elapsed = dwell.Elapse();
        Assert.Equal(target, elapsed);
        Assert.Equal(target, dwell.Shown);
        Assert.Null(dwell.Pending);

        var step = dwell.Move(target);
        Assert.Equal(DwellStep.Keep, step);
        Assert.Equal(target, dwell.Shown);
    }

    [Fact]
    public void Elapse_with_nothing_pending_returns_null()
    {
        var dwell = new GraphHoverDwell();
        Assert.Null(dwell.Elapse());

        var target = new HoverTarget(HoverTargetKind.Message, 0, -1);
        dwell.Move(target);
        dwell.Reset();

        Assert.Null(dwell.Elapse());
        Assert.Null(dwell.Pending);
        Assert.Null(dwell.Shown);
    }
}
