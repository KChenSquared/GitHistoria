[assembly: AvaloniaTestApplication(typeof(HistoriaApp.Tests.TestAppBuilder))]

namespace HistoriaApp.Tests;

/// <summary>
/// Headless Avalonia bootstrap for the App test suite. Minimal on
/// purpose: an <see cref="Application"/> carrying the real app's resources and styles (Fluent,
/// AvaloniaEdit and the scrollbar theme) + Inter fonts so controls construct, lay out as they
/// ship and resolve their <c>DynamicResource</c> keys, but none of the real
/// <c>Historia.App</c> startup / DI. Tests construct a control, drive its logic, and
/// assert — no pixel or snapshot assertions (that layer is still deferred; see
/// <c>Historia-DevDocs/todo/add-historia-app-tests-project.md</c>).
/// </summary>
public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .WithInterFont();
}

/// <summary>The throwaway <see cref="Application"/> headless tests run inside.</summary>
public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        // App.axaml loaded into a second Application only to lift its resources and styles; its
        // startup (OnFrameworkInitializationCompleted) never runs. The type is looked up by name
        // because the Historia.App namespace hides the Historia.App class.
        var appType = typeof(ScrollBarMetrics).Assembly.GetType("Historia.App", throwOnError: true)!;
        var app = (Application)Activator.CreateInstance(appType)!;
        app.Initialize();
        foreach (var key in app.Resources.Keys.ToList())
            Resources[key] = app.Resources[key];
        var styles = app.Styles.ToList();
        app.Styles.Clear();
        Styles.AddRange(styles);

        // The Dark palette, as the app applies at startup, so the styles' DynamicResource brushes
        // resolve. A pressed Button's fill is ColorSurface; left unresolved it is null, and the
        // button stops hit-testing at its centre, so a click there never raises Click.
        _ = new AvaloniaThemeApplier(new DarkThemeService());
    }

    private sealed class DarkThemeService : IThemeService
    {
        public EffectiveTheme Effective => EffectiveTheme.Dark;
        public ThemeMode CurrentMode => ThemeMode.Dark;
        public bool HighContrastEnabled => false;
        public string EffectiveCssThemeName => "dark";
        public IReadOnlyDictionary<string, Rgb> Palette => HistoriaPalette.For(EffectiveTheme.Dark);
        public IReadOnlyList<Rgb> LaneColors => [];
        public bool CustomThemeActive => false;

#pragma warning disable CS0067
        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
#pragma warning restore CS0067

        public Task ApplyModeAsync(ThemeMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetCustomThemeEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task SelectCustomThemeAsync(string themeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHighContrastAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    }
}
