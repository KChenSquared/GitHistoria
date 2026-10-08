namespace Historia.Theming;

/// <summary>Geometry metrics for the app-wide scrollbar theme.</summary>
public static class ScrollBarMetrics
{
    /// <summary>Width of vertical scrollbar lane and height of horizontal scrollbar lane.</summary>
    public const double Lane = 8;

    /// <summary>Cross-axis thickness of the rounded pill thumb.</summary>
    public const double Thumb = 6;

    /// <summary>Minimum main-axis length of the thumb so it remains draggable on large content.</summary>
    public const double ThumbMinLength = 24;

    /// <summary>Breathing gap between nested section scrollbars and the outer sidebar scrollbar.</summary>
    public const double NestedGap = 2;
}
