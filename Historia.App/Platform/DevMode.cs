namespace Historia.Platform;

/// <summary>
/// Gates the Developer menu entry. Enabled only when an <c>enableDevMode</c> marker file sits
/// next to the executable. The marker is copied from <c>dev/enableDevMode</c> on every build
/// but is excluded from publish output, so release packages never expose it.
/// </summary>
internal static class DevMode
{
    public static bool Enabled { get; } =
        File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "enableDevMode"));
}
