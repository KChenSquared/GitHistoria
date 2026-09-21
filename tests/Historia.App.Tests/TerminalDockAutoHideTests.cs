namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class TerminalDockAutoHideTests
{
    [Fact]
    public void ShouldAutoHide_OpenAndEmpty_True()
    {
        Assert.True(TerminalDockControl.ShouldAutoHide(open: true, sessionCount: 0));
    }

    [Fact]
    public void ShouldAutoHide_ClosedOrNonEmpty_False()
    {
        Assert.False(TerminalDockControl.ShouldAutoHide(open: false, sessionCount: 0));
        Assert.False(TerminalDockControl.ShouldAutoHide(open: true, sessionCount: 1));
        Assert.False(TerminalDockControl.ShouldAutoHide(open: true, sessionCount: 3));
        Assert.False(TerminalDockControl.ShouldAutoHide(open: false, sessionCount: 2));
    }
}
