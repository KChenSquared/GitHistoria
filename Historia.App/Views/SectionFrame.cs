namespace Historia.Views;

/// <summary>
/// Shared frame and drag-resize machinery for section panels (Changes/Staged in staging,
/// Files in commit detail).
/// </summary>
internal static class SectionFrame
{
    public const double HeaderGap = 2;
    public const double HeaderRowHeight = 32;
    public const double DividerBottomGap = 6;
    public const double InnerSide = 8;
    public const double InnerBottom = 8;
    public const double HandleHeight = 7;
    public const double TopMinHeight = 150;
    public const double BottomMinHeight = 150;
    public const double DefaultTopHeight = 260;

    // UI-thread only.
    private static readonly Dictionary<string, double> SplitHeightByRepo = new(StringComparer.Ordinal);

    public static double GetSplitHeight(string repoId)
    {
        return SplitHeightByRepo.TryGetValue(repoId, out var h) ? h : DefaultTopHeight;
    }

    public static void SetSplitHeight(string repoId, double h)
    {
        SplitHeightByRepo[repoId] = h;
    }

    public static void ApplySplitHeight(Grid grid, RowDefinition topRow, string repoId)
    {
        var stored = GetSplitHeight(repoId);
        var clamped = ClampTopHeight(stored, grid.Bounds.Height, TopMinHeight, BottomMinHeight);
        topRow.Height = new GridLength(clamped, GridUnitType.Pixel);
    }

    public static double ClampTopHeight(double proposed, double available, double topMin, double bottomMin)
    {
        return Math.Clamp(proposed, topMin, Math.Max(topMin, available - HandleHeight - bottomMin));
    }

    public static void AttachRowResize(Control handle, Grid grid, RowDefinition topRow, Action<double> committed)
    {
        handle.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);

        double startY = 0;
        double startHeight = 0;
        bool isDragging = false;

        handle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
                return;

            e.Pointer.Capture(handle);
            startY = e.GetPosition(grid).Y;
            startHeight = topRow.Height.IsAbsolute ? topRow.Height.Value : DefaultTopHeight;
            isDragging = true;
            e.Handled = true;
        };

        handle.PointerMoved += (_, e) =>
        {
            if (!isDragging)
                return;

            var currentY = e.GetPosition(grid).Y;
            var proposed = startHeight + (currentY - startY);
            var clamped = ClampTopHeight(proposed, grid.Bounds.Height, TopMinHeight, BottomMinHeight);
            topRow.Height = new GridLength(clamped, GridUnitType.Pixel);
            e.Handled = true;
        };

        handle.PointerReleased += (_, e) =>
        {
            if (!isDragging)
                return;

            isDragging = false;
            e.Pointer.Capture(null);
            committed(topRow.Height.Value);
            e.Handled = true;
        };

        handle.PointerCaptureLost += (_, e) =>
        {
            if (!isDragging)
                return;

            isDragging = false;
            committed(topRow.Height.Value);
            e.Handled = true;
        };
    }

    public static Border Build(Control left, Control? center, Control? right, Control body,
        Thickness border, double minHeight, out Control dividerHandle)
    {
        var head = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            MinHeight = HeaderRowHeight,
            Margin = new Thickness(0, 0, 0, HeaderGap)
        };
        Grid.SetColumn(left, 0);
        left.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(left);

        if (center is not null)
        {
            Grid.SetColumn(center, 1);
            center.HorizontalAlignment = HorizontalAlignment.Center;
            center.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(center);
        }

        if (right is not null)
        {
            Grid.SetColumn(right, 2);
            right.HorizontalAlignment = HorizontalAlignment.Right;
            right.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(right);
        }

        var strip = new Border
        {
            Height = HandleHeight,
            Background = Brushes.Transparent,
            ZIndex = 1,
            Margin = new Thickness(0, -3, 0, DividerBottomGap - 3),
        };
        var line = new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Center,
        }.Dyn(Border.BackgroundProperty, "ColorBorder");
        strip.Child = line;
        dividerHandle = strip;

        var scroll = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var inner = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(InnerSide, HeaderGap, InnerSide, InnerBottom)
        };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(strip, Dock.Top);
        inner.Children.Add(head);
        inner.Children.Add(strip);
        inner.Children.Add(scroll);

        return new Border
        {
            Child = inner,
            MinHeight = minHeight,
            BorderThickness = border,
        }.Dyn(Border.BorderBrushProperty, "ColorBorder");
    }
}
