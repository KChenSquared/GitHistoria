namespace Historia.ViewModels;

/// <summary>One entry of the identity menu: a choice, a checked state and the command that applies
/// it, or a separator.</summary>
public sealed class ProfileMenuItemViewModel
{
    private ProfileMenuItemViewModel(string header, bool isChecked, bool isSeparator, System.Windows.Input.ICommand? command, object? parameter)
    {
        Header = header;
        IsChecked = isChecked;
        IsSeparator = isSeparator;
        Command = command;
        CommandParameter = parameter;
    }

    public string Header { get; }

    /// <summary>True for the entry that is the repository's current identity.</summary>
    public bool IsChecked { get; }

    public bool IsSeparator { get; }

    public System.Windows.Input.ICommand? Command { get; }

    public object? CommandParameter { get; }

    internal static ProfileMenuItemViewModel Separator() => new("", false, true, null, null);

    internal static ProfileMenuItemViewModel Item(string header, bool isChecked, System.Windows.Input.ICommand command, object? parameter = null)
        => new(header, isChecked, false, command, parameter);
}
