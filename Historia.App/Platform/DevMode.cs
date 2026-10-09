namespace Historia.Platform;

/// <summary>
/// Gates the Developer menu entry. Enabled when an <c>enableDevMode</c> marker file sits
/// next to the executable, or when the user turns on the developer panel setting in
/// Settings → Advanced. Release builds expose the panel only when the user turns the setting on.
/// </summary>
internal static class DevMode
{
    private static readonly bool MarkerFile =
        File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "enableDevMode"));

    public static bool Enabled { get; private set; } = MarkerFile;

    public static void Initialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Enabled = MarkerFile || settings.DeveloperPanelEnabled;
    }
}
