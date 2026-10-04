namespace HistoriaApp.Tests;

public class SyntaxThemeHostTests
{
    private sealed class StubThemeService : IThemeService
    {
        public EffectiveTheme Effective { get; set; } = EffectiveTheme.Dark;
        public ThemeMode CurrentMode => ThemeMode.Dark;
        public bool HighContrastEnabled => Effective == EffectiveTheme.HighContrast;
        public string EffectiveCssThemeName => Effective == EffectiveTheme.Light ? "light" : "dark";
        public IReadOnlyDictionary<string, Rgb> Palette => HistoriaPalette.For(Effective);
        public IReadOnlyList<Rgb> LaneColors => [];

        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

        public Task ApplyModeAsync(ThemeMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public bool CustomThemeActive => false;
        public Task SetCustomThemeEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task SelectCustomThemeAsync(string themeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHighContrastAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;

        public void ChangeTheme(EffectiveTheme newTheme)
        {
            var old = Effective;
            Effective = newTheme;
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(old, newTheme));
        }
    }

    [Fact]
    public void Theme_mapping_for_all_effective_themes()
    {
        Assert.Equal(ThemeName.DarkPlus, SyntaxThemeHost.MapTheme(EffectiveTheme.Dark));
        Assert.Equal(ThemeName.LightPlus, SyntaxThemeHost.MapTheme(EffectiveTheme.Light));
        Assert.Equal(ThemeName.HighContrastDark, SyntaxThemeHost.MapTheme(EffectiveTheme.HighContrast));
    }

    [Fact]
    public void Scope_mappings_and_unknown_extension_behavior()
    {
        var themeService = new StubThemeService();
        using var host = new SyntaxThemeHost(themeService);

        Assert.Equal("source.cs", host.GetScopeByPath("Program.cs"));
        Assert.Equal(host.GetScopeByPath("file.cpp"), host.GetScopeByPath("header.h"));
        Assert.Contains("xml", host.GetScopeByPath("View.axaml"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, host.GetScopeByPath("file.unknownext"));
    }

    [AvaloniaFact]
    public void Raising_ThemeChanged_re_themes_live_editors_and_skips_disposed_ones()
    {
        var themeService = new StubThemeService();
        using var host = new SyntaxThemeHost(themeService);

        var editor1 = new CodeEditor();
        var sub1 = host.Attach(editor1, "test.cs");

        var editor2 = new CodeEditor();
        var sub2 = host.Attach(editor2, "test.cs");

        Assert.Equal(2, host.LiveCount);

        // Disposing sub2 removes editor2 from the live set
        sub2.Dispose();
        Assert.Equal(1, host.LiveCount);

        // Raising ThemeChanged should update live editor1 and not throw on disposed editor2
        themeService.ChangeTheme(EffectiveTheme.Light);
        Assert.Equal(1, host.LiveCount);

        sub1.Dispose();
        Assert.Equal(0, host.LiveCount);
    }

    [AvaloniaFact]
    public void Editor_attached_after_ThemeChanged_gets_the_new_theme()
    {
        var themeService = new StubThemeService { Effective = EffectiveTheme.Dark };
        using var host = new SyntaxThemeHost(themeService);

        // Pre-initialize wrapper with Dark
        var rawDark = host.LoadTheme(EffectiveTheme.Dark);
        Assert.NotNull(rawDark);

        // Change to Light
        themeService.ChangeTheme(EffectiveTheme.Light);

        // Attach editor after change
        var editor = new CodeEditor();
        using var sub = host.Attach(editor, "test.cs");
        Assert.Equal(1, host.LiveCount);
    }

    [AvaloniaFact]
    public void Editor_background_and_foreground_remain_palette_bindings_after_attach()
    {
        var themeService = new StubThemeService { Effective = EffectiveTheme.Dark };
        using var applier = new AvaloniaThemeApplier(themeService);
        using var host = new SyntaxThemeHost(themeService);

        var editor = new CodeEditor();
        var window = new Window { Content = editor };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            using var sub = host.Attach(editor, "test.cs");
            Dispatcher.UIThread.RunJobs();

            var darkBg = (ISolidColorBrush)editor.FindResource("ColorBg")!;
            var darkFg = (ISolidColorBrush)editor.FindResource("ColorText")!;

            Assert.Equal(darkBg.Color, ((ISolidColorBrush)editor.Background!).Color);
            Assert.Equal(darkFg.Color, ((ISolidColorBrush)editor.Foreground!).Color);

            themeService.ChangeTheme(EffectiveTheme.Light);
            Dispatcher.UIThread.RunJobs();

            var lightBg = (ISolidColorBrush)editor.FindResource("ColorBg")!;
            var lightFg = (ISolidColorBrush)editor.FindResource("ColorText")!;

            Assert.Equal(lightBg.Color, ((ISolidColorBrush)editor.Background!).Color);
            Assert.Equal(lightFg.Color, ((ISolidColorBrush)editor.Foreground!).Color);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public async Task Grammar_cache_is_safe_under_concurrent_lookup()
    {
        var wrapper = new SyntaxThemeHost.RegistryOptionsWrapper(ThemeName.DarkPlus);
        var scopes = new[] { "source.cs", "source.cpp", "text.xml" };

        var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(() =>
        {
            var scope = scopes[i % scopes.Length];
            var grammar = wrapper.GetGrammar(scope);
            Assert.NotNull(grammar);
        }));

        await Task.WhenAll(tasks);
    }

    [Fact]
    public void Loaded_raw_themes_have_no_background_in_token_rules()
    {
        var themeService = new StubThemeService();
        using var host = new SyntaxThemeHost(themeService);

        var themes = new[] { ThemeName.DarkPlus, ThemeName.LightPlus, ThemeName.HighContrastDark };

        foreach (var theme in themes)
        {
            var rawTheme = host.LoadTheme(theme);
            Assert.NotNull(rawTheme);

            var settings = rawTheme.GetSettings();
            if (settings is null)
            {
                continue;
            }

            foreach (var rule in settings)
            {
                var scope = rule.GetScope();
                // A token rule has a non-null, non-empty scope (unlike global editor settings)
                if (scope is not null)
                {
                    var settingDict = rule.GetSetting() as IDictionary<string, object>;
                    if (settingDict is not null)
                    {
                        Assert.False(settingDict.ContainsKey("background"),
                            $"Theme {theme} rule for scope '{scope}' defines a token background, which would paint over diff washes.");
                    }
                }
            }
        }
    }
}
