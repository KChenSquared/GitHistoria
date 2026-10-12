namespace Historia.ViewModels;

/// <summary>
/// What the About window shows: the build version line and the third-party credits. The window
/// itself only adds the theme-appropriate emblem.
/// </summary>
public sealed class AboutDialogViewModel : ViewModelBase
{
    private const int ShortShaLength = 7;

    /// <summary>The third-party attribution lines shown under the version. Licence terms require
    /// these to stay visible somewhere in the app.</summary>
    public static IReadOnlyList<string> CreditLines { get; } =
    [
        "Icons: Material Symbols by Google (Apache License 2.0)",
        "Diff viewer and conflict editor adapted from SourceGit (MIT License)",
        "Editor: AvaloniaEdit · Syntax: TextMateSharp (MIT) · Diff: DiffPlex (Apache License 2.0)",
    ];

    public AboutDialogViewModel(string? informationalVersion)
    {
        VersionText = FormatVersion(informationalVersion);
    }

    /// <summary>The formatted version line, such as <c>Version 1.0.0 (dac352a)</c>.</summary>
    public string VersionText { get; }

    public IReadOnlyList<string> Credits => CreditLines;

    /// <summary>
    /// Formats an assembly informational version (<c>1.0.0+&lt;commit sha&gt;</c>) for display:
    /// the text before <c>+</c>, followed by the first seven characters of the sha in
    /// parentheses when there is one. A null or empty input reads <c>Version unknown</c>.
    /// </summary>
    public static string FormatVersion(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
            return "Version unknown";

        var plus = informational.IndexOf('+');
        var version = (plus < 0 ? informational : informational[..plus]).Trim();
        if (version.Length == 0)
            return "Version unknown";

        var sha = plus < 0 ? string.Empty : informational[(plus + 1)..].Trim();
        if (sha.Length == 0)
            return $"Version {version}";

        var shortSha = sha.Length > ShortShaLength ? sha[..ShortShaLength] : sha;
        return $"Version {version} ({shortSha})";
    }

    /// <summary>The running executable's informational version, or null when it has none.</summary>
    public static string? CurrentInformationalVersion()
    {
        var entry = System.Reflection.Assembly.GetEntryAssembly();
        return entry is null
            ? null
            : System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(entry)
                ?.InformationalVersion;
    }
}
