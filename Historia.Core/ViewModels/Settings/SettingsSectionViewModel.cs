namespace Historia.ViewModels;

/// <summary>
/// A collapsible section of the settings page: its title, an optional hint and whether its body
/// shows. <see cref="Toggle"/> is the user's action and reports the new state to the owner;
/// <see cref="Sync"/> follows state that changed elsewhere without reporting it back.
/// </summary>
public sealed partial class SettingsSectionViewModel : ObservableObject
{
    private readonly Action<bool>? _toggled;

    public SettingsSectionViewModel(string title, string? hint = null, Action<bool>? toggled = null)
    {
        Title = title;
        Hint = hint;
        _toggled = toggled;
    }

    public string Title { get; }

    /// <summary>Text for the help badge beside the title, or null when the section has none.</summary>
    public string? Hint { get; }

    public bool HasHint => !string.IsNullOrEmpty(Hint);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    public partial bool IsExpanded { get; private set; }

    /// <summary>The chevron shown at the right of the header.</summary>
    public string Glyph => IsExpanded ? "▼" : "▶";

    [RelayCommand]
    public void Toggle()
    {
        var next = !IsExpanded;
        IsExpanded = next;
        _toggled?.Invoke(next);
    }

    /// <summary>Shows or hides the body without reporting it to the owner.</summary>
    public void Sync(bool expanded) => IsExpanded = expanded;
}
