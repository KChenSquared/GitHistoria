namespace HistoriaApp.Tests;

/// <summary>
/// The Developer panel's custom-theme row — <c>[Use custom theme] [saved themes ▾] [Manage
/// themes]</c> — reflects the stored settings, drives the real <see cref="ThemeService"/>, and
/// opens the Themes panel when there is no theme to use yet.
/// </summary>
[Trait("tier", "ui")]
public sealed class DeveloperViewCustomThemeTests
{
    private const string Hint =
        "Replaces the Light / Dark / System choice while a theme is selected. High Contrast still wins. Turned off when Historia starts without developer mode.";

    private static readonly CustomTheme Alpha = new() { Id = "a", Name = "Alpha", Base = "Dark" };
    private static readonly CustomTheme Beta = new() { Id = "b", Name = "Beta", Base = "Light" };

    private sealed record Harness(DeveloperView View, InMemorySettingsService Settings, IThemeService Theme, List<string> Navigations);

    private static Harness Create(AppSettings settings, params CustomTheme[] themes)
    {
        var cfg = new InMemorySettingsService(settings);
        var custom = new InMemoryCustomThemeService(themes);
        var theme = new ThemeService(cfg, new FixedThemeProbe(), custom);
        var nav = new NavigationBus();
        var navigations = new List<string>();
        nav.NavigationRequested += navigations.Add;

        var sp = DeveloperViewServices.Build(cfg, theme, custom, nav);
        var view = new DeveloperView(sp);
        view.Measure(new Size(800, 600));
        view.Arrange(new Rect(0, 0, 800, 600));
        return new Harness(view, cfg, theme, navigations);
    }

    private static CheckBox UseBox(Control view) =>
        view.GetLogicalDescendants().OfType<CheckBox>().Single(c => (c.Content as string) == "Use custom theme");

    private static Panel Row(Control view) => (Panel)UseBox(view).Parent!;

    [AvaloniaFact]
    public void Row_reflects_settings_and_carries_the_hint()
    {
        var h = Create(new AppSettings { UseCustomTheme = true, CustomThemeId = "b" }, Beta, Alpha);

        // Directly under the title.
        var root = h.View.GetLogicalDescendants().OfType<ScrollViewer>().First().Content as StackPanel
            ?? throw new InvalidOperationException("Developer panel root StackPanel not found.");
        Assert.Same(Row(h.View).Parent, root.Children[1]);

        Assert.True(UseBox(h.View).IsChecked);
        var combo = Row(h.View).Children.OfType<ComboBox>().Single();
        Assert.Equal(220, combo.Width);
        Assert.Equal(new[] { "Alpha", "Beta" }, combo.ItemsSource!.Cast<string>());
        Assert.Equal(1, combo.SelectedIndex);
        Assert.Contains(Row(h.View).Children, c => c is Button { Content: "Manage themes" });
        Assert.Equal(Hint, HelpHintMap.Of(h.View)["Use custom theme"]);

        // A stored id that no longer resolves shows the first theme.
        var stale = Create(new AppSettings { CustomThemeId = "gone" }, Alpha, Beta);
        Assert.False(UseBox(stale.View).IsChecked);
        Assert.Equal(0, Row(stale.View).Children.OfType<ComboBox>().Single().SelectedIndex);

        // With no saved themes there is nothing to pick from.
        var none = Create(new AppSettings());
        Assert.False(UseBox(none.View).IsChecked);
        Assert.Empty(Row(none.View).Children.OfType<ComboBox>());
        Assert.Contains(Row(none.View).Children, c => c is Button { Content: "Manage themes" });
        Assert.Equal(Hint, HelpHintMap.Of(none.View)["Use custom theme"]);
    }

    [AvaloniaFact]
    public void Ticking_with_no_saved_themes_enables_and_opens_the_Themes_panel()
    {
        var h = Create(new AppSettings { AppTheme = ThemeMode.Light });

        UseBox(h.View).IsChecked = true;

        Assert.True(h.Settings.Current.UseCustomTheme);
        Assert.False(h.Theme.CustomThemeActive);
        Assert.Equal(EffectiveTheme.Light, h.Theme.Effective);
        Assert.Equal(new[] { "themes" }, h.Navigations);

        // With a saved theme, ticking selects it and stays on the panel.
        var withTheme = Create(new AppSettings { AppTheme = ThemeMode.Light }, Alpha);
        UseBox(withTheme.View).IsChecked = true;
        Assert.True(withTheme.Settings.Current.UseCustomTheme);
        Assert.Equal("a", withTheme.Settings.Current.CustomThemeId);
        Assert.True(withTheme.Theme.CustomThemeActive);
        Assert.Equal(EffectiveTheme.Dark, withTheme.Theme.Effective);
        Assert.Empty(withTheme.Navigations);

        UseBox(withTheme.View).IsChecked = false;
        Assert.False(withTheme.Settings.Current.UseCustomTheme);
        Assert.Equal("a", withTheme.Settings.Current.CustomThemeId);
        Assert.Equal(EffectiveTheme.Light, withTheme.Theme.Effective);
    }

    [AvaloniaFact]
    public void Picking_a_theme_in_the_dropdown_stores_its_id()
    {
        // Stored whether or not the box is ticked.
        var off = Create(new AppSettings { CustomThemeId = "a" }, Alpha, Beta);
        Row(off.View).Children.OfType<ComboBox>().Single().SelectedIndex = 1;
        Assert.Equal("b", off.Settings.Current.CustomThemeId);
        Assert.False(off.Settings.Current.UseCustomTheme);
        Assert.False(off.Theme.CustomThemeActive);

        var on = Create(new AppSettings { AppTheme = ThemeMode.Dark, UseCustomTheme = true, CustomThemeId = "a" }, Alpha, Beta);
        Assert.Equal(EffectiveTheme.Dark, on.Theme.Effective);
        Row(on.View).Children.OfType<ComboBox>().Single().SelectedIndex = 1;
        Assert.Equal("b", on.Settings.Current.CustomThemeId);
        Assert.True(on.Theme.CustomThemeActive);
        Assert.Equal(EffectiveTheme.Light, on.Theme.Effective); // Beta's base
    }
}

/// <summary>The services <see cref="DeveloperView"/> resolves, around the given settings,
/// theme and navigation instances.</summary>
internal static class DeveloperViewServices
{
    public static IServiceProvider Build(
        ISettingsService settings, IThemeService theme, ICustomThemeService customThemes, INavigationBus nav,
        IDialogService? dialogs = null)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton(nav);
        sc.AddSingleton<IWorkspaceService, NoWorkspaceService>();
        sc.AddSingleton(settings);
        sc.AddSingleton(theme);
        sc.AddSingleton(customThemes);
        if (dialogs is not null)
            sc.AddSingleton(dialogs);
        return sc.BuildServiceProvider();
    }

    private sealed class NoWorkspaceService : IWorkspaceService
    {
        public RepositoryContext? Focused => null;
        public IReadOnlyList<RepositoryContext> OpenRepositories => [];

#pragma warning disable CS0067
        public event EventHandler<FocusedRepositoryChangedEventArgs>? FocusedRepositoryChanged;
        public event EventHandler<OpenRepositoriesChangedEventArgs>? OpenRepositoriesChanged;
#pragma warning restore CS0067

        public void Focus(RepositoryContext context) { }
        public Task<RepositoryContext> OpenAsync(string repositoryPath, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RepositoryContext> InitAsync(string path, string? initialBranch, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RepositoryContext> CloneAsync(string url, string targetDirectory, CloneOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RepositoryContext> OpenSubmoduleAsync(RepositoryContext owner, SubmoduleInfo submodule, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CloseAsync(RepositoryContext context) => Task.CompletedTask;
        public void EnsureInitialized(RepositoryContext context) { }
        public void Move(RepositoryContext context, int newIndex) { }
        public Task RestoreSessionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Settings held in memory: updates apply and raise <see cref="Changed"/>; nothing is
/// written to disk.</summary>
internal sealed class InMemorySettingsService(AppSettings? initial = null) : ISettingsService
{
    public AppSettings Current { get; private set; } = initial ?? new AppSettings();

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default)
    {
        var old = Current;
        Current = mutate(old);
        Changed?.Invoke(this, new SettingsChangedEventArgs(old, Current));
        return Task.CompletedTask;
    }

    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Custom themes held in memory, listed by name like the real service.</summary>
internal sealed class InMemoryCustomThemeService(params CustomTheme[] themes) : ICustomThemeService
{
    private readonly List<CustomTheme> _themes = [.. themes];

    public event EventHandler? Changed;

    public IReadOnlyList<CustomTheme> List() =>
        _themes.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

    public CustomTheme? Get(string id) => _themes.FirstOrDefault(t => t.Id == id);

    public Task<CustomTheme> SaveAsync(CustomTheme theme, CancellationToken ct = default)
    {
        _themes.RemoveAll(t => t.Id == theme.Id);
        _themes.Add(theme);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(theme);
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        _themes.RemoveAll(t => t.Id == id);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public CustomTheme NewFrom(string baseName) => throw new NotSupportedException();

    public ResolvedPalette Compose(CustomTheme theme) => new CustomThemeService().Compose(theme);
}

/// <summary>A dark, non-high-contrast OS.</summary>
internal sealed class FixedThemeProbe : ISystemThemeProbe
{
    public bool IsLightTheme => false;
    public bool IsHighContrast => false;
#pragma warning disable CS0067
    public event EventHandler? Changed;
#pragma warning restore CS0067
}
