namespace HistoriaApp.Tests;

/// <summary>
/// Saving a theme in the Themes panel selects it when the custom-theme override is on but no
/// saved theme is selected — so ticking the override with no themes, then creating one, renders
/// it straight away — and otherwise leaves the selection alone.
/// </summary>
[Trait("tier", "ui")]
public sealed class ThemesViewSelectionTests
{
    private static readonly CustomTheme Alpha = new() { Id = "a", Name = "Alpha", Base = "Dark" };

    private sealed record Harness(ThemesView View, InMemorySettingsService Settings, InMemoryCustomThemeService Themes, IThemeService Theme);

    private static Harness Create(AppSettings settings, params CustomTheme[] themes)
    {
        var cfg = new InMemorySettingsService(settings);
        var custom = new InMemoryCustomThemeService(themes);
        var theme = new ThemeService(cfg, new FixedThemeProbe(), custom);
        var sp = DeveloperViewServices.Build(cfg, theme, custom, new NavigationBus(), new NoDialogs());
        var view = new ThemesView(sp);
        view.Measure(new Size(800, 600));
        view.Arrange(new Rect(0, 0, 800, 600));
        return new Harness(view, cfg, custom, theme);
    }

    private static void Click(Control view, string caption)
    {
        var button = view.GetLogicalDescendants().OfType<Button>().First(b => (b.Content as string) == caption);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void New_theme_is_selected_when_the_override_is_on_with_none_selected()
    {
        var h = Create(new AppSettings { AppTheme = ThemeMode.Light, UseCustomTheme = true });
        Assert.False(h.Theme.CustomThemeActive);

        Click(h.View, "from Dark");

        var created = Assert.Single(h.Themes.List());
        Assert.Equal(created.Id, h.Settings.Current.CustomThemeId);
        Assert.True(h.Theme.CustomThemeActive);
        Assert.Equal(EffectiveTheme.Dark, h.Theme.Effective);
        Assert.Equal(ThemeMode.Light, h.Theme.CurrentMode);
    }

    [AvaloniaFact]
    public void Saving_from_the_editor_selects_the_theme_when_the_stored_id_is_stale()
    {
        var h = Create(new AppSettings { UseCustomTheme = true, CustomThemeId = "gone" }, Alpha);
        Assert.False(h.Theme.CustomThemeActive);

        Click(h.View, "Edit");
        Click(h.View, "Save");

        Assert.Equal("a", h.Settings.Current.CustomThemeId);
        Assert.True(h.Theme.CustomThemeActive);
    }

    [AvaloniaFact]
    public void Saving_leaves_the_selection_alone_when_the_override_is_off_or_a_theme_is_selected()
    {
        var off = Create(new AppSettings());
        Click(off.View, "from Dark");
        Assert.Single(off.Themes.List());
        Assert.Null(off.Settings.Current.CustomThemeId);
        Assert.False(off.Settings.Current.UseCustomTheme);

        var selected = Create(new AppSettings { UseCustomTheme = true, CustomThemeId = "a" }, Alpha);
        Click(selected.View, "from Light");
        Assert.Equal(2, selected.Themes.List().Count);
        Assert.Equal("a", selected.Settings.Current.CustomThemeId);
    }

    private sealed class NoDialogs : IDialogService
    {
        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel) => Task.FromResult(false);
        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false, string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None) => Task.FromResult(false);
        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options, int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None) => Task.FromResult(-1);
        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PromptAsync(string title, string message, string? defaultValue = null, string confirmLabel = "OK", bool primaryConfirm = false) => Task.FromResult<string?>(null);
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
