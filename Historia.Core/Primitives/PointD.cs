namespace Historia;

/// <summary>
/// A 2-D point in device-independent units. <see cref="Historia.Core"/> owns no UI
/// framework, so geometric math uses this instead of
/// <c>System.Windows.Point</c> / <c>Avalonia.Point</c>. The App layer converts at its boundary.
/// </summary>
public readonly record struct PointD(double X, double Y)
{
    public override string ToString() => $"({X};{Y})";
}
