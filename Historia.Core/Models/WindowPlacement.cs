namespace Historia.Models;

/// <summary>
/// Main-window geometry, persisted to <c>window.json</c> in <c>AppPaths.MachineDirectory</c>
/// (§V3-9.2). Deliberately <b>not</b> a field on <see cref="AppSettings"/> — monitor coordinates
/// must not follow the user to another machine (D15).
/// </summary>
public sealed record WindowPlacement
{
    public required double Left { get; init; }
    public required double Top { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public bool Maximized { get; init; }
}
