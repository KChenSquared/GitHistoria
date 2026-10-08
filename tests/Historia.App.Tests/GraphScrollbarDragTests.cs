namespace HistoriaApp.Tests;

/// <summary>Thumb drags on the commit graph's vertical scrollbar. Avalonia's <c>Track</c>
/// measures each drag move against the thumb's last-arranged position, so pointer moves that
/// queue up before a layout pass (the UI thread starved while rows load) are double-counted
/// and the thumb races to the bottom; and a sideways-only move divides by zero in its drag math,
/// which rows arriving mid-drag turn into a NaN <c>Track.Value</c> and a thumb stuck at the
/// bottom. Nothing errors when either regresses, so both are pinned here, along with the opt-in
/// <c>graph-scroll</c> log.</summary>
[Trait("tier", "ui")]
public sealed class GraphScrollbarDragTests
{
    private sealed class RecordingLog : Historia.Diagnostics.IAppLog
    {
        public List<(string Category, string Message)> Lines { get; } = [];
        public void Trace(string category, string message) => Lines.Add((category, message));
        public void Debug(string category, string message) => Lines.Add((category, message));
        public void Info(string category, string message) => Lines.Add((category, message));
        public void Warn(string category, string message, Exception? ex = null) => Lines.Add((category, message));
        public void Error(string category, string message, Exception? ex = null) => Lines.Add((category, message));
        public IDisposable Operation(string category, string message) => new Nothing();
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private sealed record Hosted(Window Window, CommitGraphControl Graph, ScrollBar Bar, Thumb Thumb, Track Track);

    private static Hosted Host(int rows)
    {
        var graph = new CommitGraphControl { ContentRowCountOverrideForTesting = rows };
        var window = new Window { Width = 900, Height = 700, Content = graph };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var bar = graph.VerticalScrollBarForTesting;
        Assert.True(bar.IsVisible);
        return new Hosted(window, graph, bar, Thumb(bar), Track(bar));
    }

    private static Thumb Thumb(ScrollBar bar) => bar.GetVisualDescendants().OfType<Thumb>().Single();
    private static Track Track(ScrollBar bar) => bar.GetVisualDescendants().OfType<Track>().Single();

    /// <summary>A pointer move with no layout pass after it, the way moves queue up under
    /// input starvation. The public <c>HeadlessWindowExtensions.MouseMove</c> runs jobs (and so
    /// layout) after every move, so this calls Avalonia.Headless's internal
    /// <c>IHeadlessWindow.MouseMove</c> directly.</summary>
    private static void QueuedMove(Window window, Point point)
    {
        var move = typeof(Avalonia.Headless.HeadlessWindowExtensions).Assembly
            .GetType("Avalonia.Headless.IHeadlessWindow")?.GetMethod("MouseMove");
        Assert.NotNull(move);
        move.Invoke(window.PlatformImpl, [point, RawInputModifiers.LeftMouseButton]);
    }

    private static Point ThumbCentreIn(Window window, Thumb thumb)
        => thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;

    /// <summary>Value per pixel of thumb travel.</summary>
    private static double Density(Track track, ScrollBar bar)
        => (bar.Maximum - bar.Minimum) / (track.Bounds.Height - track.Thumb!.Bounds.Height);

    [Fact]
    public void Scroll_drag_trace_is_off_by_default()
    {
        Assert.False(CommitGraphControl.TraceScrollDrags);
    }

    [AvaloniaFact]
    public void Thumb_drag_writes_graph_scroll_lines()
    {
        var h = Host(2000);
        var log = new RecordingLog();
        h.Graph.ScrollLogForTesting = log;
        try
        {
            var start = ThumbCentreIn(h.Window, h.Thumb);
            Avalonia.Headless.HeadlessWindowExtensions.MouseDown(h.Window, start, MouseButton.Left);
            QueuedMove(h.Window, start + new Point(0, 10));
            QueuedMove(h.Window, start + new Point(0, 20));
            Dispatcher.UIThread.RunJobs();
            h.Graph.ContentRowCountOverrideForTesting = 4000;
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.HeadlessWindowExtensions.MouseUp(h.Window, start + new Point(0, 20), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            h.Window.Close();
        }

        var lines = log.Lines.Where(l => l.Category == "graph-scroll").Select(l => l.Message).ToList();
        Assert.Contains(lines, m => m.StartsWith("track ", StringComparison.Ordinal) && m.Contains(" ptr=", StringComparison.Ordinal) && m.Contains(" trackValue=", StringComparison.Ordinal));
        Assert.Contains(lines, m => m.StartsWith("maximum ", StringComparison.Ordinal));
        Assert.Contains(lines, m => m == "drag end");
    }

    [AvaloniaFact]
    public void Thumb_drag_with_logging_off_is_silent_and_safe()
    {
        Assert.False(Historia.Diagnostics.AppLog.Enabled);
        var h = Host(2000);
        try
        {
            var start = ThumbCentreIn(h.Window, h.Thumb);
            Avalonia.Headless.HeadlessWindowExtensions.MouseDown(h.Window, start, MouseButton.Left);
            QueuedMove(h.Window, start + new Point(0, 10));
            QueuedMove(h.Window, start + new Point(0, 20));
            Dispatcher.UIThread.RunJobs();
            h.Graph.ContentRowCountOverrideForTesting = 4000;
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.HeadlessWindowExtensions.MouseUp(h.Window, start + new Point(0, 20), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.InRange(h.Bar.Value, h.Bar.Minimum, h.Bar.Maximum);
        }
        finally
        {
            h.Window.Close();
        }
    }

    /// <summary>Upstream canary: a stock <see cref="ScrollBar"/> still double-counts queued
    /// thumb moves. When this fails, Avalonia has fixed it.</summary>
    [AvaloniaFact]
    public void Stock_scrollbar_double_counts_queued_thumb_moves()
    {
        var bar = new ScrollBar
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            Minimum = 0,
            Maximum = 10_000,
            ViewportSize = 400,
            AllowAutoHide = false,
        };
        var window = new Window { Width = 100, Height = 500, Content = bar };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var thumb = Thumb(bar);
            var density = Density(Track(bar), bar);
            var start = ThumbCentreIn(window, thumb);
            Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, start, MouseButton.Left);
            for (var i = 1; i <= 5; i++)
                QueuedMove(window, start + new Point(0, 10 * i));
            Dispatcher.UIThread.RunJobs();

            Assert.True(bar.Value > 50 * density * 1.5,
                $"Avalonia no longer double-counts queued thumb moves (value {bar.Value:F1} for 50 px at {density:F2}/px); "
                + "the UpdateLayout call in CommitGraphControl's Scroll handler can be removed.");
            Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, start + new Point(0, 50), MouseButton.Left);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Five queued 10 px moves land exactly where five separate ones would, with
    /// the thumb still under the pointer.</summary>
    [AvaloniaFact]
    public void Queued_thumb_moves_land_where_separate_moves_do()
    {
        var h = Host(2000);
        try
        {
            var density = Density(h.Track, h.Bar);
            var start = ThumbCentreIn(h.Window, h.Thumb);
            var end = QueuedDrag(h, start);

            Assert.Equal(50 * density, h.Bar.Value, 1.0);
            Assert.Equal(end.Y, ThumbCentreIn(h.Window, h.Thumb).Y, 1.0);
        }
        finally
        {
            h.Window.Close();
        }
    }

    /// <summary>Queued moves while rows keep streaming in (10% more per layout pass) never
    /// pin the thumb to the bottom or leave it below the pointer.</summary>
    [AvaloniaFact]
    public void Queued_moves_while_rows_grow_never_snap_to_the_bottom()
    {
        var h = Host(2000);
        try
        {
            var rows = 2000;
            var pointer = ThumbCentreIn(h.Window, h.Thumb);
            Avalonia.Headless.HeadlessWindowExtensions.MouseDown(h.Window, pointer, MouseButton.Left);
            for (var step = 0; step < 5; step++)
            {
                QueuedMove(h.Window, pointer + new Point(0, 10));
                pointer += new Point(0, 20);
                QueuedMove(h.Window, pointer);
                rows += rows / 10;
                h.Graph.ContentRowCountOverrideForTesting = rows;
                Dispatcher.UIThread.RunJobs();

                Assert.True(h.Bar.Value < h.Bar.Maximum, $"step {step}: value {h.Bar.Value:F1} reached maximum {h.Bar.Maximum:F1}");
                var thumbY = ThumbCentreIn(h.Window, h.Thumb).Y;
                Assert.True(thumbY <= pointer.Y + 1, $"step {step}: thumb centre {thumbY:F1} below pointer {pointer.Y:F1}");
            }
            Avalonia.Headless.HeadlessWindowExtensions.MouseUp(h.Window, pointer, MouseButton.Left);
        }
        finally
        {
            h.Window.Close();
        }
    }

    [AvaloniaFact]
    public void Scroll_offset_matches_the_bar_after_a_queued_drag()
    {
        var h = Host(2000);
        try
        {
            var end = QueuedDrag(h, ThumbCentreIn(h.Window, h.Thumb));
            Avalonia.Headless.HeadlessWindowExtensions.MouseUp(h.Window, end, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.True(h.Bar.Value > 0);
            Assert.Equal(h.Bar.Value, h.Graph.ScrollOffsetForTesting);
        }
        finally
        {
            h.Window.Close();
        }
    }

    /// <summary>The extra layout pass is for thumb drags only: a track click pages by
    /// one viewport with no layout pass inside its input event, while a thumb move gets one.</summary>
    [AvaloniaFact]
    public void Track_clicks_do_not_force_a_layout_pass()
    {
        var h = Host(2000);
        try
        {
            var thumbBottom = h.Thumb.TranslatePoint(new Point(h.Thumb.Bounds.Width / 2, h.Thumb.Bounds.Height), h.Window)!.Value;
            var below = thumbBottom + new Point(0, 40);
            Avalonia.Headless.HeadlessWindowExtensions.MouseMove(h.Window, below);

            var passes = 0;
            h.Bar.LayoutUpdated += (_, _) => passes++;
            RawMouse(h.Window, "MouseDown", [below, MouseButton.Left, RawInputModifiers.None]);
            Assert.Equal(0, passes);
            RawMouse(h.Window, "MouseUp", [below, MouseButton.Left, RawInputModifiers.None]);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(h.Bar.ViewportSize, h.Graph.ScrollOffsetForTesting, 1.0);

            // Control: the same counter sees the pass a thumb move forces.
            var start = ThumbCentreIn(h.Window, h.Thumb);
            Avalonia.Headless.HeadlessWindowExtensions.MouseDown(h.Window, start, MouseButton.Left);
            passes = 0;
            QueuedMove(h.Window, start + new Point(0, 10));
            Assert.True(passes > 0);
            Avalonia.Headless.HeadlessWindowExtensions.MouseUp(h.Window, start + new Point(0, 10), MouseButton.Left);
        }
        finally
        {
            h.Window.Close();
        }
    }

    /// <summary>A sideways-only drag move gives Avalonia's Track a zero value change, and its
    /// drag math divides by it. If rows then arrive (Maximum changes, the Track re-arranges)
    /// before the next move, the NaN reaches the Thumb's grab point: Track.Value goes NaN and the
    /// thumb sticks at the bottom. The graph drops such moves before the Track sees them.</summary>
    [AvaloniaFact]
    public void Sideways_move_then_rows_arriving_leaves_the_thumb_on_the_pointer()
    {
        var h = Host(60000);
        try
        {
            var start = ThumbCentreIn(h.Window, h.Thumb);
            Avalonia.Headless.HeadlessWindowExtensions.MouseDown(h.Window, start, MouseButton.Left);
            QueuedMove(h.Window, start + new Point(0, 20));
            Dispatcher.UIThread.RunJobs();
            QueuedMove(h.Window, start + new Point(2, 20));
            h.Graph.ContentRowCountOverrideForTesting += 5000;
            Dispatcher.UIThread.RunJobs();
            QueuedMove(h.Window, start + new Point(2, 30));
            Dispatcher.UIThread.RunJobs();

            Assert.False(double.IsNaN(h.Track.Value));
            Assert.Equal(h.Bar.Value, h.Track.Value, 0.5);
            Assert.Equal(start.Y + 30, ThumbCentreIn(h.Window, h.Thumb).Y, 2.0);
            Avalonia.Headless.HeadlessWindowExtensions.MouseUp(h.Window, start + new Point(2, 30), MouseButton.Left);
        }
        finally
        {
            h.Window.Close();
        }
    }

    /// <summary>Press on the thumb at <paramref name="start"/>, then five queued 10 px moves
    /// and one layout pass. Returns the pointer's final position; the button stays down.</summary>
    private static Point QueuedDrag(Hosted h, Point start)
    {
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(h.Window, start, MouseButton.Left);
        for (var i = 1; i <= 5; i++)
            QueuedMove(h.Window, start + new Point(0, 10 * i));
        Dispatcher.UIThread.RunJobs();
        return start + new Point(0, 50);
    }

    /// <summary>Raw pointer input with no layout pass after it (see <see cref="QueuedMove"/>).</summary>
    private static void RawMouse(Window window, string method, object[] args)
    {
        var m = typeof(Avalonia.Headless.HeadlessWindowExtensions).Assembly
            .GetType("Avalonia.Headless.IHeadlessWindow")?.GetMethod(method);
        Assert.NotNull(m);
        m.Invoke(window.PlatformImpl, args);
    }

    [AvaloniaFact]
    public void Graph_bars_use_the_shared_lane()
    {
        var graph = new CommitGraphControl { ContentRowCountOverrideForTesting = 2000 };
        var window = new Window { Width = 300, Height = 300, Content = graph };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var vBar = graph.VerticalScrollBarForTesting;
            var hBar = graph.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Horizontal);

            Assert.True(vBar.IsVisible);
            Assert.True(hBar.IsVisible);
            Assert.Equal(ScrollBarMetrics.Lane, vBar.Bounds.Width);
            Assert.Equal(ScrollBarMetrics.Lane, hBar.Bounds.Height);

            Assert.True(vBar.Bounds.Bottom <= hBar.Bounds.Top + 0.5);
        }
        finally
        {
            window.Close();
        }
    }
}
