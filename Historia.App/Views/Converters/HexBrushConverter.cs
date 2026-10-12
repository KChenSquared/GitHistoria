namespace Historia.Views;

/// <summary>
/// Turns a hex colour string (<c>#RRGGBB</c> or <c>#RRGGBBAA</c>) into a solid brush, for swatches
/// that show a user's colour. The colour is user data, not a palette colour. A null or unparseable
/// string gives no brush.
/// </summary>
public sealed class HexBrushConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly HexBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && ThemesViewModel.TryParseHex(s, out var color)
            ? new SolidColorBrush(color.ToAvalonia())
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
