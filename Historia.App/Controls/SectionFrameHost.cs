namespace Historia.Controls;

/// <summary>
/// A panel split into a <see cref="Top"/> section and a framed <see cref="Bottom"/> section with a
/// header row (<see cref="Header"/> on the left, <see cref="HeaderEnd"/> on the right). The top
/// section's height is dragged from the gap between the sections or from the divider under the
/// header, and is remembered per <see cref="RepoId"/>, so every panel of that repository
/// opens with the same split.
/// </summary>
/// <remarks>The bottom body is not wrapped in a scroll viewer: it scrolls and virtualizes itself.</remarks>
public sealed class SectionFrameHost : Grid
{
    public static readonly StyledProperty<Control?> TopProperty =
        AvaloniaProperty.Register<SectionFrameHost, Control?>(nameof(Top));

    public static readonly StyledProperty<Control?> BottomProperty =
        AvaloniaProperty.Register<SectionFrameHost, Control?>(nameof(Bottom));

    public static readonly StyledProperty<Control?> HeaderProperty =
        AvaloniaProperty.Register<SectionFrameHost, Control?>(nameof(Header));

    public static readonly StyledProperty<Control?> HeaderEndProperty =
        AvaloniaProperty.Register<SectionFrameHost, Control?>(nameof(HeaderEnd));

    public static readonly StyledProperty<string?> RepoIdProperty =
        AvaloniaProperty.Register<SectionFrameHost, string?>(nameof(RepoId));

    private readonly RowDefinition _topRow;
    private readonly ContentControl _top = new();
    private readonly ContentControl _header = new();
    private readonly ContentControl _headerEnd = new();
    private readonly ContentControl _bottom = new();

    public SectionFrameHost()
    {
        ClipToBounds = true;

        _topRow = new RowDefinition(new GridLength(SectionFrame.DefaultTopHeight, GridUnitType.Pixel))
        {
            MinHeight = SectionFrame.TopMinHeight,
        };
        RowDefinitions.Add(_topRow);
        RowDefinitions.Add(new RowDefinition(new GridLength(SectionFrame.HandleHeight, GridUnitType.Pixel)));
        RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)) { MinHeight = SectionFrame.BottomMinHeight });

        SetRow(_top, 0);
        Children.Add(_top);

        SplitHandle = new Border
        {
            Height = SectionFrame.HandleHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
        };
        SetRow(SplitHandle, 1);
        Children.Add(SplitHandle);

        Frame = SectionFrame.Build(
            left: _header,
            center: null,
            right: _headerEnd,
            body: _bottom,
            border: new Thickness(1),
            minHeight: SectionFrame.BottomMinHeight,
            out var dividerHandle,
            scrollBody: false);
        DividerHandle = dividerHandle;
        SetRow(Frame, 2);
        Children.Add(Frame);

        SectionFrame.AttachRowResize(SplitHandle, this, _topRow, h => SectionFrame.SetSplitHeight(SplitKey, h));
        SectionFrame.AttachRowResize(DividerHandle, this, _topRow, h => SectionFrame.SetSplitHeight(SplitKey, h));

        AttachedToVisualTree += (_, _) => ApplySplit();
        SizeChanged += (_, _) => ApplySplit();
    }

    /// <summary>The upper section, shown above the divider.</summary>
    public Control? Top
    {
        get => GetValue(TopProperty);
        set => SetValue(TopProperty, value);
    }

    /// <summary>The body of the framed lower section.</summary>
    public Control? Bottom
    {
        get => GetValue(BottomProperty);
        set => SetValue(BottomProperty, value);
    }

    /// <summary>The left side of the lower section's header row.</summary>
    public Control? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>The right side of the lower section's header row.</summary>
    public Control? HeaderEnd
    {
        get => GetValue(HeaderEndProperty);
        set => SetValue(HeaderEndProperty, value);
    }

    /// <summary>Key under which the split height is remembered.</summary>
    public string? RepoId
    {
        get => GetValue(RepoIdProperty);
        set => SetValue(RepoIdProperty, value);
    }

    /// <summary>The border around the lower section.</summary>
    internal Border Frame { get; }

    /// <summary>The gap between the two sections; dragging it resizes the top section.</summary>
    internal Border SplitHandle { get; }

    /// <summary>The line under the lower header; dragging it resizes the top section.</summary>
    internal Control DividerHandle { get; }

    private string SplitKey => RepoId ?? "";

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TopProperty)
            _top.Content = change.NewValue;
        else if (change.Property == BottomProperty)
            _bottom.Content = change.NewValue;
        else if (change.Property == HeaderProperty)
            _header.Content = change.NewValue;
        else if (change.Property == HeaderEndProperty)
            _headerEnd.Content = change.NewValue;
        else if (change.Property == RepoIdProperty)
            _topRow.Height = new GridLength(SectionFrame.GetSplitHeight(SplitKey), GridUnitType.Pixel);
    }

    private void ApplySplit() => SectionFrame.ApplySplitHeight(this, _topRow, SplitKey);
}
