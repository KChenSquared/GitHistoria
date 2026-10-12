namespace Historia.ViewModels;

/// <summary>
/// One line of the Historia menu: an action, a separator or a submenu. The view turns the
/// entries into menu items each time the menu opens.
/// </summary>
public sealed class AppMenuEntry
{
    private AppMenuEntry(string header, IRelayCommand? command, bool isSeparator, bool isChecked, IReadOnlyList<AppMenuEntry>? children)
    {
        Header = header;
        Command = command;
        IsSeparator = isSeparator;
        IsChecked = isChecked;
        Children = children ?? [];
    }

    /// <summary>The visible text; empty for a separator.</summary>
    public string Header { get; }

    /// <summary>What choosing the entry does; null for a separator or a submenu.</summary>
    public IRelayCommand? Command { get; }

    public bool IsSeparator { get; }

    /// <summary>True when the entry is the current choice of its submenu.</summary>
    public bool IsChecked { get; }

    /// <summary>The submenu entries; empty for a plain entry.</summary>
    public IReadOnlyList<AppMenuEntry> Children { get; }

    public bool HasChildren => Children.Count > 0;

    public static AppMenuEntry Separator() => new("", null, true, false, null);

    public static AppMenuEntry Item(string header, IRelayCommand command, bool isChecked = false)
        => new(header, command, false, isChecked, null);

    public static AppMenuEntry Submenu(string header, IReadOnlyList<AppMenuEntry> children)
        => new(header, null, false, false, children);
}
