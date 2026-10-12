namespace Historia.ViewModels;

/// <summary>The sidebar's collapsible sections, top to bottom.</summary>
public enum RefSectionKind
{
    Local,
    Remotes,
    Tags,
    Stashes,
    Submodules,
    Worktrees,
}

/// <summary>
/// One section of the ref navigator: its header (title with count, caret, filter and add buttons),
/// its optional filter box and its flat list of rows. The rows are empty while the section is
/// collapsed and are replaced as a whole whenever they change.
/// </summary>
public sealed partial class RefSectionViewModel : ObservableObject
{
    private readonly RefNavigatorViewModel _host;
    private bool _suppressFilterChange;

    internal RefSectionViewModel(RefNavigatorViewModel host, RefSectionKind kind, bool openByDefault)
    {
        _host = host;
        Kind = kind;
        IsOpen = openByDefault;
        Title = DefaultTitle(kind);
        Rows = [];
    }

    public RefSectionKind Kind { get; }

    /// <summary>Whether the section has a filter button and box.</summary>
    public bool HasFilter => Kind is RefSectionKind.Local or RefSectionKind.Remotes or RefSectionKind.Tags or RefSectionKind.Stashes;

    /// <summary>Whether the header carries the Add remote button.</summary>
    public bool HasAddButton => Kind == RefSectionKind.Remotes;

    /// <summary>The header text with its count, as written ("Local Branches (5)").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    public partial string Title { get; private set; }

    /// <summary>The header text as shown: the title in capitals.</summary>
    public string HeaderText => Title.ToUpperInvariant();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caret))]
    [NotifyPropertyChangedFor(nameof(ShowFilterBox))]
    public partial bool IsOpen { get; private set; }

    public string Caret => IsOpen ? "▾" : "▸";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFilterBox))]
    public partial bool IsFilterOpen { get; private set; }

    /// <summary>True while the filter box sits below the header: the section is open and its filter is.</summary>
    public bool ShowFilterBox => HasFilter && IsOpen && IsFilterOpen;

    /// <summary>The filter text. Typing re-ranks the section's matches live.</summary>
    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    /// <summary>The section's rows, replaced wholesale on every change.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<RefRowViewModel> Rows { get; private set; }

    internal bool HasActiveFilter => IsFilterOpen && !string.IsNullOrWhiteSpace(FilterText);

    internal void SetTitle(string title) => Title = title;

    internal void SetRows(IReadOnlyList<RefRowViewModel> rows) => Rows = rows;

    internal void SetOpen(bool open) => IsOpen = open;

    internal void SetFilterOpen(bool open) => IsFilterOpen = open;

    /// <summary>Clears the filter without asking the host to rebuild.</summary>
    internal void ClearFilterQuietly()
    {
        _suppressFilterChange = true;
        try { FilterText = ""; }
        finally { _suppressFilterChange = false; }
    }

    partial void OnFilterTextChanged(string value)
    {
        if (!_suppressFilterChange)
            _host.OnFilterTextChanged(this);
    }

    /// <summary>Expands or collapses the section.</summary>
    [RelayCommand]
    private void Toggle() => _host.ToggleSection(this);

    /// <summary>Shows or hides the filter box; showing it also expands the section.</summary>
    [RelayCommand]
    private void ToggleFilter() => _host.ToggleFilter(this);

    /// <summary>Clears the filter text and hides the box.</summary>
    [RelayCommand]
    private void CloseFilter() => _host.CloseFilter(this);

    /// <summary>Starts the Add remote flow. Only the Remotes section has the button.</summary>
    [RelayCommand]
    private Task AddRemote() => _host.AddRemoteCommand.ExecuteAsync(null);

    private static string DefaultTitle(RefSectionKind kind) => kind switch
    {
        RefSectionKind.Local => "Local Branches (0)",
        RefSectionKind.Remotes => "Remotes (0)",
        RefSectionKind.Tags => "Tags (0)",
        RefSectionKind.Stashes => "Stashes",
        RefSectionKind.Submodules => "Submodules",
        _ => "Worktrees",
    };
}
