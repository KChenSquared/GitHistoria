namespace Historia.Diagnostics;

/// <summary>
/// Application version resolution derived from the informational version metadata.
/// </summary>
public static class AppVersion
{
    private static readonly Lazy<string> CachedCurrent = new(() =>
    {
        var entry = typeof(AppVersion).Assembly;
        var informational = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(entry)
            ?.InformationalVersion;
        return Display(informational);
    });

    /// <summary>Gets the current application version stripped of build metadata.</summary>
    public static string Current => CachedCurrent.Value;

    /// <summary>
    /// Strips build metadata (anything after '+') from an informational version string.
    /// Returns "0.0.0" if the input is null, empty, or whitespace.
    /// </summary>
    public static string Display(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
            return "0.0.0";

        var plus = informational.IndexOf('+');
        var version = (plus < 0 ? informational : informational[..plus]).Trim();
        return version.Length == 0 ? "0.0.0" : version;
    }
}
