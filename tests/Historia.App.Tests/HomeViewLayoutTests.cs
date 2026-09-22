namespace HistoriaApp.Tests;

public sealed class HomeViewLayoutTests
{
    [AvaloniaFact]
    public void ActionRow_WrapsNewRepository_WhenNarrow()
    {
        var openBtn = ViewKit.TextButton("Open repository…", () => { }, primary: true);
        var cloneBtn = ViewKit.TextButton("Clone…", () => { });
        var newBtn = ViewKit.TextButton("New repository…", () => { });
        var panel = HomeView.BuildActionRow(openBtn, cloneBtn, newBtn);

        var window = new Window { Width = 260, Height = 400, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(newBtn.Bounds.Y > openBtn.Bounds.Y,
            $"Expected New repository ({newBtn.Bounds}) to wrap below Open repository ({openBtn.Bounds})");
        Assert.True(newBtn.Bounds.Right <= panel.Bounds.Width + 0.5,
            $"Expected New repository right edge ({newBtn.Bounds.Right}) <= panel width ({panel.Bounds.Width})");

        window.Close();
    }

    [AvaloniaFact]
    public void ActionRow_AllInline_WhenWide()
    {
        var openBtn = ViewKit.TextButton("Open repository…", () => { }, primary: true);
        var cloneBtn = ViewKit.TextButton("Clone…", () => { });
        var newBtn = ViewKit.TextButton("New repository…", () => { });
        var panel = HomeView.BuildActionRow(openBtn, cloneBtn, newBtn);

        var window = new Window { Width = 640, Height = 400, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(openBtn.Bounds.Y, cloneBtn.Bounds.Y);
        Assert.Equal(openBtn.Bounds.Y, newBtn.Bounds.Y);

        window.Close();
    }

    [AvaloniaFact]
    public void ActionRow_ReturnsInline_AfterWidening()
    {
        var openBtn = ViewKit.TextButton("Open repository…", () => { }, primary: true);
        var cloneBtn = ViewKit.TextButton("Clone…", () => { });
        var newBtn = ViewKit.TextButton("New repository…", () => { });
        var panel = HomeView.BuildActionRow(openBtn, cloneBtn, newBtn);

        var window = new Window { Width = 260, Height = 400, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(newBtn.Bounds.Y > openBtn.Bounds.Y);

        window.Width = 640;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(openBtn.Bounds.Y, cloneBtn.Bounds.Y);
        Assert.Equal(openBtn.Bounds.Y, newBtn.Bounds.Y);

        window.Close();
    }

    [AvaloniaFact]
    public void RecentScroller_ScrollBarDoesNotOverlapRows()
    {
        var contentPanel = new StackPanel { Spacing = 4 };
        for (var i = 0; i < 20; i++)
        {
            contentPanel.Children.Add(new Border
            {
                Height = 40,
                Child = new TextBlock { Text = $"Recent repo row {i}" }
            });
        }

        var scroller = HomeView.BuildRecentScroller(contentPanel);
        scroller.AllowAutoHide = false;

        var window = new Window { Width = 400, Height = 300, Content = scroller };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var scrollBar = scroller.GetVisualDescendants()
            .OfType<ScrollBar>()
            .FirstOrDefault(s => s.Orientation == Avalonia.Layout.Orientation.Vertical);

        Assert.NotNull(scrollBar);
        Assert.True(scrollBar.IsVisible);

        var scrollBarTopLeft = scrollBar.TranslatePoint(new Point(0, 0), contentPanel);
        var scrollBarBottomRight = scrollBar.TranslatePoint(new Point(scrollBar.Bounds.Width, scrollBar.Bounds.Height), contentPanel);
        Assert.NotNull(scrollBarTopLeft);
        Assert.NotNull(scrollBarBottomRight);

        var scrollBarBoundsInContent = new Rect(scrollBarTopLeft.Value, scrollBarBottomRight.Value);

        foreach (var child in contentPanel.Children)
        {
            Assert.False(
                scrollBarBoundsInContent.Intersects(child.Bounds),
                $"ScrollBar overlaps row at {child.Bounds}; scrollbar in content space is {scrollBarBoundsInContent}");
        }

        window.Close();
    }
}
