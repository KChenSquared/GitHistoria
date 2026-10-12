namespace Historia.Views;

/// <summary>
/// Maps a <see cref="FileChangeKind"/> to its one-letter status glyph (<c>A</c> added, <c>M</c>
/// modified, <c>D</c> deleted, <c>R</c> renamed, <c>C</c> copied, <c>T</c> type-changed, <c>U</c>
/// untracked, <c>I</c> ignored, <c>!</c> conflicted, <c>?</c> otherwise).
/// </summary>
public sealed class FileChangeKindGlyphConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly FileChangeKindGlyphConverter Instance = new();

    public static string Glyph(FileChangeKind kind) => kind switch
    {
        FileChangeKind.Added => "A",
        FileChangeKind.Modified => "M",
        FileChangeKind.Deleted => "D",
        FileChangeKind.Renamed => "R",
        FileChangeKind.Copied => "C",
        FileChangeKind.TypeChanged => "T",
        FileChangeKind.Untracked => "U",
        FileChangeKind.Ignored => "I",
        FileChangeKind.Conflicted => "!",
        _ => "?",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is FileChangeKind kind ? Glyph(kind) : "?";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
