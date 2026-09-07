namespace Historia;

/// <summary>
/// Borderless splash shown on launch while the shell loads out of sight (§6.2.2): a complete
/// pre-composited image — the Historia emblem and wordmark on a solid background.
///
/// Built-in themes get <c>splash-dark.png</c> when the effective theme is Dark (explicit Dark,
/// or System resolving to dark) and <c>splash-light.png</c> otherwise. A custom theme supplies
/// its own image (an <c>avares://</c> URI or a path to the user's PNG) and its own 0–10 s
/// duration. <see cref="App"/> constructs this, shows it, and closes it once the interval
/// elapses and the main window is revealed.
/// </summary>
public partial class SplashWindow : Window
{
    private const string FallbackUri = "avares://Historia/Assets/splash-light.png";

    public SplashWindow() : this(dark: false, FallbackUri) { }

    public SplashWindow(bool dark, string imageUri)
    {
        InitializeComponent();
        Background = dark ? Brushes.Black : Brushes.White;
        Art.Source = Load(imageUri) ?? Load(FallbackUri);
    }

    /// <summary>Load an <c>avares://</c> resource or an on-disk PNG; null on any failure so the
    /// caller can fall back rather than crash the launch.</summary>
    private static Bitmap? Load(string uri)
    {
        try
        {
            if (uri.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
                return new Bitmap(AssetLoader.Open(new Uri(uri)));
            return File.Exists(uri) ? new Bitmap(uri) : null;
        }
        catch
        {
            return null;
        }
    }
}
