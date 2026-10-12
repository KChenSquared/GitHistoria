namespace Historia.ViewModels;

/// <summary>
/// One line of the AI Integrations list: the numbered label, whether the profile is the active
/// one, and whether it is the selected one. Rows are replaced wholesale whenever the list
/// refreshes, so a row never changes after it is created.
/// </summary>
public sealed partial class AiProfileRowViewModel : ObservableObject
{
    private readonly Action<string> _select;
    private readonly Func<string, Task> _toggleActive;

    public AiProfileRowViewModel(string id, string label, bool isActive, bool isSelected,
        Action<string> select, Func<string, Task> toggleActive)
    {
        Id = id;
        Label = label;
        IsActive = isActive;
        IsSelected = isSelected;
        _select = select;
        _toggleActive = toggleActive;
    }

    public string Id { get; }

    /// <summary><c>1. Name</c>, numbered by list position.</summary>
    public string Label { get; }

    public bool IsActive { get; }

    public bool IsSelected { get; }

    /// <summary>Palette key of the check glyph: green for the active profile, faint otherwise.</summary>
    public string CheckColorKey => IsActive ? "ColorSuccess" : "ColorTextFaint";

    public string ActiveTip => IsActive ? "Active" : "Make active";

    [RelayCommand]
    private void Select() => _select(Id);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ToggleActive() => _toggleActive(Id);
}
