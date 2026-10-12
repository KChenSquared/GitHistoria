namespace Historia.Views;

/// <summary>
/// Maps a <see cref="FileChangeKind"/> to the word shown in the staging lists: New (added or
/// untracked), Deleted, Modified, Renamed, Copied, Type-changed, Conflict, and Changed for the rest.
/// </summary>
public sealed class FileChangeKindLabelConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly FileChangeKindLabelConverter Instance = new();

    public static string Label(FileChangeKind kind) => kind switch
    {
        FileChangeKind.Added or FileChangeKind.Untracked => "New",
        FileChangeKind.Deleted => "Deleted",
        FileChangeKind.Modified => "Modified",
        FileChangeKind.Renamed => "Renamed",
        FileChangeKind.Copied => "Copied",
        FileChangeKind.TypeChanged => "Type-changed",
        FileChangeKind.Conflicted => "Conflict",
        _ => "Changed",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is FileChangeKind kind ? Label(kind) : "Changed";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
