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

    private static void EnsureAppStylesLoaded()
    {
        if (Application.Current is { } app && !app.Resources.ContainsKey(typeof(ScrollBar)))
        {
            var appType = typeof(Historia.Theming.ScrollBarMetrics).Assembly.GetType("Historia.App")!;
            var historiaApp = (Application)Activator.CreateInstance(appType)!;
            historiaApp.Initialize();
            foreach (var key in historiaApp.Resources.Keys)
            {
                app.Resources[key] = historiaApp.Resources[key];
            }
            var styles = historiaApp.Styles.ToList();
            historiaApp.Styles.Clear();
            foreach (var style in styles)
            {
                app.Styles.Add(style);
            }
        }
    }

    [AvaloniaFact]
    public void Vertical_bar_is_lane_wide()
    {
        EnsureAppStylesLoaded();
        var dock = new TerminalDockControl();
        var window = new Window { Width = 600, Height = 400, Content = dock };
        dock.IsVisible = true;
        dock.Height = 200;
        dock.VerticalScrollBarForTesting.IsVisible = true;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var bar = dock.VerticalScrollBarForTesting;
            Assert.True(bar.IsVisible);
            Assert.Equal(ScrollBarMetrics.Lane, bar.Bounds.Width);
        }
        finally
        {
            window.Close();
        }
    }
}
