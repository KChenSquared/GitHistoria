namespace Historia.ViewModels;

/// <summary>
/// The tab of one open repository: its name, whether it is the focused tab, whether it is linked
/// to an owner tab (a submodule opened from it), its developer auto-fetch bar and the commands
/// behind its buttons and context menu. The strip replaces the whole set whenever the open
/// repositories, the focus or the tab settings change, so only the auto-fetch bar, the separator
/// and the drag state change on a live tab.
/// </summary>
public sealed partial class RepoTabViewModel : AppBandItemViewModel
{
    private readonly AppBandViewModel _host;

    internal RepoTabViewModel(
        AppBandViewModel host,
        RepositoryContext context,
        bool isActive,
        double bodyWidth,
        double nameMaxWidth,
        AutoFetchTabState autoFetchState,
        bool showAutoFetchBar)
    {
        _host = host;
        Context = context;
        IsActive = isActive;
        BodyWidth = bodyWidth;
        NameMaxWidth = nameMaxWidth;
        ShowAutoFetchBar = showAutoFetchBar;
        AutoFetchBarToken = AppBandViewModel.AutoFetchBarToken(autoFetchState);
    }

    /// <summary>The repository the tab shows.</summary>
    public RepositoryContext Context { get; }

    public string Title => Context.DisplayName;

    /// <summary>The working directory, shown as the name's tooltip.</summary>
    public string RepositoryPath => Context.RepositoryPath;

    /// <summary>True for the focused repository's tab.</summary>
    public bool IsActive { get; }

    /// <summary>True when the tab belongs to an owner tab. Such a tab is drawn shorter and shares the owner's separator.</summary>
    public bool IsLinked => Context.LinkOwner is not null;

    /// <summary>True when the repository has a folder the menu can open and whose path it can copy.</summary>
    public bool HasLocation => !string.IsNullOrEmpty(Context.RepositoryPath);

    /// <summary>The fixed width of the tab body, or NaN to size it to its name.</summary>
    public double BodyWidth { get; }

    /// <summary>The widest the name grows; unbounded when the tabs have a fixed width.</summary>
    public double NameMaxWidth { get; }

    /// <summary>Palette key of the auto-fetch bar's colour.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFetchedThisCycle))]
    [NotifyPropertyChangedFor(nameof(IsPendingThisCycle))]
    public partial string AutoFetchBarToken { get; private set; }

    public bool IsFetchedThisCycle => AutoFetchBarToken == AppBandViewModel.AutoFetchBarToken(AutoFetchTabState.FetchedThisCycle);

    public bool IsPendingThisCycle => AutoFetchBarToken == AppBandViewModel.AutoFetchBarToken(AutoFetchTabState.PendingThisCycle);

    /// <summary>True while the developer auto-fetch bar is shown down the tab's left edge.</summary>
    [ObservableProperty]
    public partial bool ShowAutoFetchBar { get; private set; }

    internal void SetAutoFetchState(AutoFetchTabState state) => AutoFetchBarToken = AppBandViewModel.AutoFetchBarToken(state);

    internal void SetAutoFetchBarVisible(bool visible) => ShowAutoFetchBar = visible;

    /// <summary>Focuses the repository and shows its view in the detail pane.</summary>
    [RelayCommand]
    private void Activate() => _host.ActivateTab(Context);

    /// <summary>Closes the repository, offering to save its unsaved work first.</summary>
    [RelayCommand]
    private Task CloseAsync() => _host.CloseTabAsync(Context);

    /// <summary>Focuses the repository and opens or focuses its terminal.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task OpenTerminalAsync() => _host.OpenTabTerminalAsync(Context);

    [RelayCommand(CanExecute = nameof(HasLocation))]
    private void Reveal() => _host.RevealTab(Context);

    [RelayCommand(CanExecute = nameof(HasLocation), AllowConcurrentExecutions = true)]
    private Task CopyPathAsync() => _host.CopyTabPathAsync(Context);
}
