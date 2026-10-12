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

    private sealed record Harness(ThemesView View, Window Window, InMemorySettingsService Settings, InMemoryCustomThemeService Themes, IThemeService Theme) : IDisposable
    {
        public void Dispose()
        {
            View.Dispose();
            Window.Close();
        }
    }

    private static Harness Create(AppSettings settings, params CustomTheme[] themes)
    {
        var cfg = new InMemorySettingsService(settings);
        var custom = new InMemoryCustomThemeService(themes);
        var theme = new ThemeService(cfg, new FixedThemeProbe(), custom);
        var sp = DeveloperViewServices.Build(cfg, theme, custom, new NavigationBus(), new NoDialogs());
        var view = new ThemesView(sp);
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        Pump(window);
        return new Harness(view, window, cfg, custom, theme);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Click(Harness h, string caption)
    {
        var button = h.View.GetVisualDescendants().OfType<Button>()
            .First(b => b.IsEffectivelyVisible && (b.Content as string) == caption);
        Assert.True(button.Command!.CanExecute(button.CommandParameter));
        button.Command.Execute(button.CommandParameter);
        Pump(h.Window);
    }

    [AvaloniaFact]
    public void New_theme_is_selected_when_the_override_is_on_with_none_selected()
    {
        using var h = Create(new AppSettings { AppTheme = ThemeMode.Light, UseCustomTheme = true });
        Assert.False(h.Theme.CustomThemeActive);

        Click(h, "from Dark");

        var created = Assert.Single(h.Themes.List());
        Assert.Equal(created.Id, h.Settings.Current.CustomThemeId);
        Assert.True(h.Theme.CustomThemeActive);
        Assert.Equal(EffectiveTheme.Dark, h.Theme.Effective);
        Assert.Equal(ThemeMode.Light, h.Theme.CurrentMode);
    }

    [AvaloniaFact]
    public void Saving_from_the_editor_selects_the_theme_when_the_stored_id_is_stale()
    {
        using var h = Create(new AppSettings { UseCustomTheme = true, CustomThemeId = "gone" }, Alpha);
        Assert.False(h.Theme.CustomThemeActive);

        Click(h, "Edit");
        Click(h, "Save");

        Assert.Equal("a", h.Settings.Current.CustomThemeId);
        Assert.True(h.Theme.CustomThemeActive);
    }

    [AvaloniaFact]
    public void Saving_leaves_the_selection_alone_when_the_override_is_off_or_a_theme_is_selected()
    {
        using var off = Create(new AppSettings());
        Click(off, "from Dark");
        Assert.Single(off.Themes.List());
        Assert.Null(off.Settings.Current.CustomThemeId);
        Assert.False(off.Settings.Current.UseCustomTheme);

        using var selected = Create(new AppSettings { UseCustomTheme = true, CustomThemeId = "a" }, Alpha);
        Click(selected, "from Light");
        Assert.Equal(2, selected.Themes.List().Count);
        Assert.Equal("a", selected.Settings.Current.CustomThemeId);
    }

    [AvaloniaFact]
    public void The_list_shows_each_theme_with_its_swatch_and_active_marker()
    {
        var tinted = Alpha with { Tokens = new Dictionary<string, string> { ["bg"] = "#123456" } };
        using var h = Create(new AppSettings { UseCustomTheme = true, CustomThemeId = "a" }, tinted);

        var texts = h.View.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Alpha  (active)", texts);
        Assert.Contains("base: Dark", texts);
        Assert.DoesNotContain("No custom themes yet.", texts);

        var swatch = h.View.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("swatch") && b.Width == 18);
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(swatch.Background);
        Assert.Equal(Color.FromRgb(0x12, 0x34, 0x56), brush.Color);
    }

    [AvaloniaFact]
    public void An_invalid_hex_shows_inline_and_blocks_Save_until_it_is_fixed()
    {
        using var h = Create(new AppSettings(), Alpha);
        Click(h, "Edit");

        var bgBox = h.View.GetVisualDescendants().OfType<TextBox>()
            .First(b => b.Watermark == HistoriaPalette.Get(EffectiveTheme.Dark, "bg").ToHex());
        bgBox.Text = "zzz";
        Pump(h.Window);

        var texts = h.View.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("invalid", texts);
        Assert.Contains("1 invalid colour(s) — fix before saving.", texts);

        Click(h, "Save");
        Assert.True(h.View.ViewModel.IsEditing);

        bgBox.Text = "#102030";
        Pump(h.Window);
        Click(h, "Save");
        Assert.False(h.View.ViewModel.IsEditing);
        Assert.Equal("#102030", h.Themes.Get("a")!.Tokens["bg"]);
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
