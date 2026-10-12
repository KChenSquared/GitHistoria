namespace Historia.Views;

/// <summary>Negates a <see cref="bool"/> binding, in both directions. A non-bool value leaves the target untouched.</summary>
public sealed class InvertedBoolConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly InvertedBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : Avalonia.Data.BindingOperations.DoNothing;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : Avalonia.Data.BindingOperations.DoNothing;
}
