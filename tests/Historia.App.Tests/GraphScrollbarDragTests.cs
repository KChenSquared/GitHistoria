namespace HistoriaApp.Tests;

/// <summary>Thumb drags on the commit graph's vertical scrollbar. Avalonia's <c>Track</c>
/// measures each drag move against the thumb's last-arranged position, so pointer moves that
/// queue up before a layout pass (the UI thread starved while rows load) are double-counted
/// and the thumb races to the bottom (findings F1/F2). Nothing errors when that regresses, so
/// it is pinned here (Rule 36), along with the opt-in <c>graph-scroll</c> log (F4).</summary>
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
        Assert.Contains(lines, m => m.StartsWith("track ", StringComparison.Ordinal) && m.Contains("arranged=False", StringComparison.Ordinal));
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
}
