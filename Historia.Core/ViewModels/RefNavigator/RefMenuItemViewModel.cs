namespace Historia.ViewModels;

/// <summary>
/// One item of a ref navigator row's data-bound context menu. A header of <c>-</c> draws as a
/// separator.
/// </summary>
public sealed class RefMenuItemViewModel
{
    /// <summary>Max characters of a branch or tag name shown in a menu item before it is
    /// ellipsis-trimmed; long names otherwise force the popup wider than the screen.</summary>
    public const int NameMaxChars = 25;

    public static RefMenuItemViewModel Separator { get; } = new("-");

    public RefMenuItemViewModel(string header, System.Windows.Input.ICommand? command = null, object? commandParameter = null)
    {
        Header = header;
        Command = command;
        CommandParameter = commandParameter;
    }

    public string Header { get; }

    public System.Windows.Input.ICommand? Command { get; }

    public object? CommandParameter { get; }

    /// <summary>Ellipsis-trims <paramref name="name"/> to <see cref="NameMaxChars"/> for display;
    /// the operation itself still uses the full name.</summary>
    public static string TrimName(string name) =>
        name.Length <= NameMaxChars ? name : name[..(NameMaxChars - 1)] + "…";
}
