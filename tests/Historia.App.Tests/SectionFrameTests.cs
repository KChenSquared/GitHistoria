namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class SectionFrameTests
{
    [Fact]
    public void ClampTopHeight_below_min_returns_min()
    {
        var clamped = SectionFrame.ClampTopHeight(100, 500, 150, 150);
        Assert.Equal(150, clamped);
    }

    [Fact]
    public void ClampTopHeight_above_max_clamps_to_available_minus_handle_and_bottom_min()
    {
        // available (400) - handle (7) - bottomMin (150) = 243
        var clamped = SectionFrame.ClampTopHeight(300, 400, 150, 150);
        Assert.Equal(243, clamped);
    }

    [Fact]
    public void ClampTopHeight_when_available_too_small_returns_top_min()
    {
        // available 200 - 7 - 150 = 43 < topMin (150) -> clamp to 150
        var clamped = SectionFrame.ClampTopHeight(200, 200, 150, 150);
        Assert.Equal(150, clamped);
    }

    [AvaloniaFact]
    public void Header_row_is_centred_between_border_and_rule()
    {
        var border = SectionFrame.Build(
            left: new TextBlock { Text = "Files:" },
            center: null,
            right: null,
            body: new StackPanel(),
            border: new Thickness(1),
            minHeight: 150,
            out _);

        var window = new Window { Width = 400, Height = 300, Content = border };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var inner = (DockPanel)border.Child!;
        var head = (Grid)inner.Children[0];
        var strip = (Border)inner.Children[1];
        var line = (Border)strip.Child!;
        var scroll = (ScrollViewer)inner.Children[2];

        Assert.Equal(SectionFrame.HeaderRowHeight, head.Bounds.Height);

        // Gap above: distance from inside edge of top border (Y = 1) to head top
        var headTop = head.TranslatePoint(new Point(0, 0), border)!.Value.Y;
        var gapAbove = headTop - 1.0;
        Assert.Equal(SectionFrame.HeaderGap, gapAbove);

        // Gap below: distance from head bottom to line top
        var headBottom = head.TranslatePoint(new Point(0, head.Bounds.Height), border)!.Value.Y;
        var lineTop = line.TranslatePoint(new Point(0, 0), border)!.Value.Y;
        var gapBelow = lineTop - headBottom;
        Assert.Equal(SectionFrame.HeaderGap, gapBelow);

        // List gap: distance from line bottom to scroll top
        var lineBottom = line.TranslatePoint(new Point(0, line.Bounds.Height), border)!.Value.Y;
        var scrollTop = scroll.TranslatePoint(new Point(0, 0), border)!.Value.Y;
        var listGap = scrollTop - lineBottom;
        Assert.Equal(SectionFrame.DividerBottomGap, listGap);

        window.Close();
    }

    [AvaloniaFact]
    public void Header_row_is_32_with_or_without_checkbox()
    {
        var frameWithCheckbox = SectionFrame.Build(
            left: new CheckBox(),
            center: null,
            right: null,
            body: new StackPanel(),
            border: new Thickness(1),
            minHeight: 150,
            out _);

        var frameWithTextBlock = SectionFrame.Build(
            left: new TextBlock { Text = "Files:" },
            center: null,
            right: null,
            body: new StackPanel(),
            border: new Thickness(1),
            minHeight: 150,
            out _);

        var panel = new StackPanel();
        panel.Children.Add(frameWithCheckbox);
        panel.Children.Add(frameWithTextBlock);

        var window = new Window { Width = 400, Height = 400, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var head1 = (Grid)((DockPanel)frameWithCheckbox.Child!).Children[0];
        var head2 = (Grid)((DockPanel)frameWithTextBlock.Child!).Children[0];

        Assert.Equal(32, head1.Bounds.Height);
        Assert.Equal(32, head2.Bounds.Height);

        window.Close();
    }

    [AvaloniaFact]
    public void Divider_drag_resizes_top_row()
    {
        var topRow = new RowDefinition(new GridLength(200, GridUnitType.Pixel)) { MinHeight = SectionFrame.TopMinHeight };
        var handleRow = new RowDefinition(new GridLength(SectionFrame.HandleHeight, GridUnitType.Pixel));
        var starRow = new RowDefinition(new GridLength(1, GridUnitType.Star)) { MinHeight = SectionFrame.BottomMinHeight };

        var grid = new Grid
        {
            Width = 400,
            Height = 600,
            RowDefinitions = { topRow, handleRow, starRow }
        };

        var handle = new Border
        {
            Height = SectionFrame.HandleHeight,
            Background = Brushes.Transparent
        };
        Grid.SetRow(handle, 1);
        grid.Children.Add(handle);

        double committedHeight = 0;
        int committedCount = 0;
        SectionFrame.AttachRowResize(handle, grid, topRow, h =>
        {
            committedHeight = h;
            committedCount++;
        });

        var window = new Window { Width = 400, Height = 600, Content = grid };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var handleCenter = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;

        // Drag down by 50 px
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, handleCenter, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, new Point(handleCenter.X, handleCenter.Y + 50));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(250, topRow.Height.Value);

        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, new Point(handleCenter.X, handleCenter.Y + 50), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(250, committedHeight);
        Assert.Equal(1, committedCount);

        window.Close();
    }

    [AvaloniaFact]
    public void Apply_does_not_write_back_clamped_height()
    {
        const string repoId = "test-repo-split-clamp";
        SectionFrame.SetSplitHeight(repoId, 900);

        var topRow = new RowDefinition(new GridLength(200, GridUnitType.Pixel)) { MinHeight = SectionFrame.TopMinHeight };
        var grid = new Grid
        {
            Width = 400,
            Height = 400,
            RowDefinitions =
            {
                topRow,
                new RowDefinition(new GridLength(SectionFrame.HandleHeight, GridUnitType.Pixel)),
                new RowDefinition(new GridLength(1, GridUnitType.Star)) { MinHeight = SectionFrame.BottomMinHeight }
            }
        };

        var window = new Window { Width = 400, Height = 400, Content = grid };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        SectionFrame.ApplySplitHeight(grid, topRow, repoId);

        // Clamped to 400 - 7 - 150 = 243
        Assert.Equal(243, topRow.Height.Value);
        // Stored height unchanged
        Assert.Equal(900, SectionFrame.GetSplitHeight(repoId));

        window.Close();
    }

    [Fact]
    public void ClampGrowHeight_below_min_returns_min()
    {
        var clamped = SectionFrame.ClampGrowHeight(50, 96, 96, 400, SectionFrame.SplitAreaMinHeight);
        Assert.Equal(96, clamped);
    }

    [Fact]
    public void ClampGrowHeight_limited_by_donor_room()
    {
        var clamped = SectionFrame.ClampGrowHeight(300, 96, 96, 400, SectionFrame.SplitAreaMinHeight);
        Assert.Equal(189, clamped);
    }

    [Fact]
    public void ClampGrowHeight_within_room_returns_proposed()
    {
        var clamped = SectionFrame.ClampGrowHeight(150, 96, 96, 400, SectionFrame.SplitAreaMinHeight);
        Assert.Equal(150, clamped);
    }

    [Fact]
    public void ClampGrowHeight_when_donor_below_min_returns_min()
    {
        var clamped = SectionFrame.ClampGrowHeight(200, 96, 96, 250, SectionFrame.SplitAreaMinHeight);
        Assert.Equal(96, clamped);
    }

    [Fact]
    public void ClampGrowHeight_shrinks_current_when_donor_below_min()
    {
        var clamped = SectionFrame.ClampGrowHeight(180, 96, 180, 250, SectionFrame.SplitAreaMinHeight);
        Assert.Equal(123, clamped);
    }

    [AvaloniaFact]
    public void AttachBottomResize_drag_up_grows_within_bounds()
    {
        var handle = new Border
        {
            Height = SectionFrame.HandleHeight,
            Background = Brushes.Transparent
        };
        var root = new Panel { Width = 400, Height = 400 };
        root.Children.Add(handle);

        var applied = new List<double>();
        double committedValue = 0;
        int committedCount = 0;
        SectionFrame.AttachBottomResize(
            handle,
            root,
            begin: () => (96, 96, 150),
            apply: v => applied.Add(v),
            committed: v =>
            {
                committedValue = v;
                committedCount++;
            });

        var window = new Window { Width = 400, Height = 400, Content = root };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var handleCenter = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;

        // Press
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, handleCenter, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // Drag up 30 -> 126
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, new Point(handleCenter.X, handleCenter.Y - 30));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(126, applied.Last());

        // Drag up 200 -> 150 (clamped to max)
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, new Point(handleCenter.X, handleCenter.Y - 200));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(150, applied.Last());

        // Drag down 50 from start -> 96 (clamped to min)
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, new Point(handleCenter.X, handleCenter.Y + 50));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(96, applied.Last());

        // Release
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, new Point(handleCenter.X, handleCenter.Y + 50), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(96, committedValue);
        Assert.Equal(1, committedCount);

        window.Close();
    }

    [AvaloniaFact]
    public void AttachBottomResize_capture_lost_commits()
    {
        var handle = new Border
        {
            Height = SectionFrame.HandleHeight,
            Background = Brushes.Transparent
        };
        var root = new Panel { Width = 400, Height = 400 };
        root.Children.Add(handle);

        double committedValue = 0;
        int committedCount = 0;
        SectionFrame.AttachBottomResize(
            handle,
            root,
            begin: () => (96, 96, 150),
            apply: _ => { },
            committed: v =>
            {
                committedValue = v;
                committedCount++;
            });

        IPointer? pointer = null;
        handle.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        var window = new Window { Width = 400, Height = 400, Content = root };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var handleCenter = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;

        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, handleCenter, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, new Point(handleCenter.X, handleCenter.Y - 30));
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(pointer);
        pointer.Capture(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(126, committedValue);
        Assert.Equal(1, committedCount);

        window.Close();
    }
}
