namespace Historia.ViewModels;

/// <summary>
/// One entry of the app band's tab strip: a repository tab or the pending tab of a running
/// clone. Holds what both kinds share, the leading separator and the drag state.
/// </summary>
public abstract partial class AppBandItemViewModel : ObservableObject
{
    /// <summary>True when a one pixel separator is drawn on the tab's leading edge.</summary>
    [ObservableProperty]
    public partial bool ShowLeadingSeparator { get; private set; }

    /// <summary>True while the tab is the one being dragged.</summary>
    [ObservableProperty]
    public partial bool IsDragging { get; private set; }

    internal void SetLeadingSeparator(bool show) => ShowLeadingSeparator = show;

    internal void SetDragging(bool dragging) => IsDragging = dragging;
}
