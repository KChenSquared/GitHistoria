namespace Historia.ViewModels;

/// <summary>One saved custom theme in the Themes list. The three buttons run the host's commands
/// with this row as the parameter.</summary>
public sealed class ThemeRowViewModel(
    CustomTheme theme,
    bool isActive,
    string swatchHex,
    IRelayCommand editCommand,
    IRelayCommand duplicateCommand,
    IRelayCommand deleteCommand)
{
    public CustomTheme Theme { get; } = theme;

    public string Name => Theme.Name;

    public bool IsActive { get; } = isActive;

    public string DisplayName => Theme.Name + (IsActive ? "  (active)" : "");

    public string BaseLabel => "base: " + Theme.Base;

    /// <summary>The theme's background colour, or its base theme's when the theme sets none.</summary>
    public string SwatchHex { get; } = swatchHex;

    public IRelayCommand EditCommand { get; } = editCommand;

    public IRelayCommand DuplicateCommand { get; } = duplicateCommand;

    public IRelayCommand DeleteCommand { get; } = deleteCommand;
}
