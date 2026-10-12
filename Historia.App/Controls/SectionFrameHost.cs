namespace Historia.Controls;

/// <summary>
/// Where a split drag past the lower section's minimum takes its room from. <c>Begin</c> returns the
/// lender's current height and its minimum when a drag starts; <c>Apply</c> is told each height the
/// lender gives up or takes back.
/// </summary>
public sealed record SectionLender(Func<(double Start, double Min)> Begin, Action<double> Apply);

/// <summary>
/// A panel split into a <see cref="Top"/> section and a framed <see cref="Bottom"/> section with a
/// header row (<see cref="Header"/> on the left, <see cref="HeaderCenter"/> in the middle,
/// <see cref="HeaderEnd"/> on the right). Setting <see cref="TopHeader"/> or <see cref="TopHeaderEnd"/>
/// frames the top section the same way, with its own header row. The top section's height is dragged
/// from the gap between the sections or from the divider under the lower header, and is remembered
/// per <see cref="RepoId"/>, so every panel of that repository opens with the same split. A drag
/// past the lower section's minimum borrows from <see cref="Lender"/> when one is set.
/// </summary>
/// <remarks>The section bodies are not wrapped in a scroll viewer: they scroll and virtualize themselves.</remarks>
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

    public static readonly StyledProperty<Control?> HeaderCenterProperty =
        AvaloniaProperty.Register<SectionFrameHost, Control?>(nameof(HeaderCenter));

    public static readonly StyledProperty<Control?> TopHeaderProperty =
        AvaloniaProperty.Register<SectionFrameHost, Control?>(nameof(TopHeader));

    public static readonly StyledProperty<Control?> TopHeaderEndProperty =
        AvaloniaProperty.Register<SectionFrameHost, Control?>(nameof(TopHeaderEnd));

    public static readonly StyledProperty<string?> RepoIdProperty =
        AvaloniaProperty.Register<SectionFrameHost, string?>(nameof(RepoId));

    private readonly RowDefinition _topRow;
    private readonly ContentControl _top = new();
    private readonly ContentControl _header = new();
    private readonly ContentControl _headerEnd = new();
    private readonly ContentControl _headerCenter = new();
    private readonly ContentControl _bottom = new();
    private readonly ContentControl _topHeader = new();
    private readonly ContentControl _topHeaderEnd = new();
    private readonly ContentControl _topBody = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
    };

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

        TopFrame = SectionFrame.Build(
            left: _topHeader,
            center: null,
            right: _topHeaderEnd,
            body: _topBody,
            border: new Thickness(1, 1, 1, 0),
            minHeight: SectionFrame.TopMinHeight,
            out _,
            scrollBody: false);
        TopFrame.IsVisible = false;
        SetRow(TopFrame, 0);
        Children.Add(TopFrame);

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
            center: _headerCenter,
            right: _headerEnd,
            body: _bottom,
            border: new Thickness(1),
            minHeight: SectionFrame.BottomMinHeight,
            out var dividerHandle,
            scrollBody: false);
        DividerHandle = dividerHandle;
        SetRow(Frame, 2);
        Children.Add(Frame);

        // With no lender set the wrapper reports a lender of zero height, which lends nothing.
        (Func<(double Start, double Min)> Begin, Action<double> Apply) lender = (
            () => Lender?.Begin() ?? (0, 0),
            h => Lender?.Apply(h));
        SectionFrame.AttachRowResize(SplitHandle, this, _topRow, h => SectionFrame.SetSplitHeight(SplitKey, h), lender);
        SectionFrame.AttachRowResize(DividerHandle, this, _topRow, h => SectionFrame.SetSplitHeight(SplitKey, h), lender);

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

    /// <summary>The middle of the lower section's header row.</summary>
    public Control? HeaderCenter
    {
        get => GetValue(HeaderCenterProperty);
        set => SetValue(HeaderCenterProperty, value);
    }

    /// <summary>The left side of the upper section's header row; setting it frames the upper section.</summary>
    public Control? TopHeader
    {
        get => GetValue(TopHeaderProperty);
        set => SetValue(TopHeaderProperty, value);
    }

    /// <summary>The right side of the upper section's header row; setting it frames the upper section.</summary>
    public Control? TopHeaderEnd
    {
        get => GetValue(TopHeaderEndProperty);
        set => SetValue(TopHeaderEndProperty, value);
    }

    /// <summary>Where a split drag past the lower section's minimum borrows height from.</summary>
    public SectionLender? Lender { get; set; }

    /// <summary>Key under which the split height is remembered.</summary>
    public string? RepoId
    {
        get => GetValue(RepoIdProperty);
        set => SetValue(RepoIdProperty, value);
    }

    /// <summary>The border around the lower section.</summary>
    internal Border Frame { get; }

    /// <summary>The border around the upper section when it has a header row.</summary>
    internal Border TopFrame { get; }

    /// <summary>The gap between the two sections; dragging it resizes the top section.</summary>
    internal Border SplitHandle { get; }

    /// <summary>The line under the lower header; dragging it resizes the top section.</summary>
    internal Control DividerHandle { get; }

    private string SplitKey => RepoId ?? "";

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TopProperty)
            ApplyTop();
        else if (change.Property == TopHeaderProperty)
        {
            _topHeader.Content = change.NewValue;
            ApplyTop();
        }
        else if (change.Property == TopHeaderEndProperty)
        {
            _topHeaderEnd.Content = change.NewValue;
            ApplyTop();
        }
        else if (change.Property == BottomProperty)
            _bottom.Content = change.NewValue;
        else if (change.Property == HeaderProperty)
            _header.Content = change.NewValue;
        else if (change.Property == HeaderCenterProperty)
            _headerCenter.Content = change.NewValue;
        else if (change.Property == HeaderEndProperty)
            _headerEnd.Content = change.NewValue;
        else if (change.Property == RepoIdProperty)
            _topRow.Height = new GridLength(SectionFrame.GetSplitHeight(SplitKey), GridUnitType.Pixel);
    }

    // The upper section is framed when it has a header row and plain otherwise. A control sits in
    // one parent at a time, so both holders are emptied before the right one takes it.
    private void ApplyTop()
    {
        var framed = TopHeader is not null || TopHeaderEnd is not null;
        _top.Content = null;
        _topBody.Content = null;
        if (framed)
            _topBody.Content = Top;
        else
            _top.Content = Top;
        _top.IsVisible = !framed;
        TopFrame.IsVisible = framed;
    }

    private void ApplySplit() => SectionFrame.ApplySplitHeight(this, _topRow, SplitKey);
}
