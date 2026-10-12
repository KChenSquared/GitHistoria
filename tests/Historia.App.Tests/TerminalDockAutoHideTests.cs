namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class TerminalDockAutoHideTests
{
    [Fact]
    public void ShouldAutoHide_OpenAndEmpty_True()
    {
        Assert.True(TerminalDockViewModel.ShouldAutoHide(open: true, sessionCount: 0));
    }

    [Fact]
    public void ShouldAutoHide_ClosedOrNonEmpty_False()
    {
        Assert.False(TerminalDockViewModel.ShouldAutoHide(open: false, sessionCount: 0));
        Assert.False(TerminalDockViewModel.ShouldAutoHide(open: true, sessionCount: 1));
        Assert.False(TerminalDockViewModel.ShouldAutoHide(open: true, sessionCount: 3));
        Assert.False(TerminalDockViewModel.ShouldAutoHide(open: false, sessionCount: 2));
    }

    [AvaloniaFact]
    public void Vertical_bar_is_lane_wide()
    {
        var dock = new TerminalDockView();
        var window = new Window { Width = 600, Height = 400, Content = dock };
        dock.IsVisible = true;
        dock.Height = 200;
        dock.VScroll.IsVisible = true;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var bar = dock.VScroll;
            Assert.True(bar.IsVisible);
            Assert.Equal(ScrollBarMetrics.Lane, bar.Bounds.Width);
        }
        finally
        {
            window.Close();
        }
    }
}
