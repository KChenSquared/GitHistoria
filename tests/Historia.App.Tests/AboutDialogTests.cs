namespace HistoriaApp.Tests;

/// <summary>The About dialog's version formatting and credits, and the guarantee that Settings
/// carries no About section (the credits must live in the dialog instead).</summary>
[Trait("tier", "ui")]
public sealed class AboutDialogTests
{
    private sealed class FakeThemeProbe : ISystemThemeProbe
    {
        public bool IsLightTheme => false;
        public bool IsHighContrast => false;
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067
    }

    private sealed class FakeVault : ICredentialVaultService
    {
        public Task StoreTokenAsync(string key, string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteTokenAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeCmdrPathService : ICmdrPathService
    {
        public CmdrPathStatus GetStatus() => new CmdrPathStatus.Unavailable();
        public CmdrPathApplyResult Apply(PathChange confirmed) => new CmdrPathApplyResult.Failed("Unavailable");
    }

    [Fact]
    public void FormatVersion_splits_sha() =>
        Assert.Equal("Version 1.0.0 (dac352a)",
            Historia.Platform.AboutDialog.FormatVersion("1.0.0+dac352a1e8b7c9d0e1f2a3b4c5d6e7f8a9b0c1d2"));

    [Theory]
    [InlineData("1.0.0", "Version 1.0.0")]
    [InlineData("1.0.0+", "Version 1.0.0")]
    [InlineData("1.2.3+abc", "Version 1.2.3 (abc)")]
    public void FormatVersion_without_sha(string input, string expected) =>
        Assert.Equal(expected, Historia.Platform.AboutDialog.FormatVersion(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+dac352a")]
    public void FormatVersion_null_is_unknown(string? input) =>
        Assert.Equal("Version unknown", Historia.Platform.AboutDialog.FormatVersion(input));

    [Fact]
    public void Credits_keep_the_three_attributions()
    {
        var credits = Historia.Platform.AboutDialog.Credits;
        Assert.Equal(3, credits.Count);
        Assert.Contains(credits, c => c.Contains("Material Symbols") && c.Contains("Apache License 2.0"));
        Assert.Contains(credits, c => c.Contains("SourceGit") && c.Contains("MIT License"));
        Assert.Contains(credits, c => c.Contains("AvaloniaEdit") && c.Contains("TextMateSharp") && c.Contains("DiffPlex"));
    }

    [AvaloniaFact]
    public void SettingsView_has_no_About_section()
    {
        var sc = new ServiceCollection();
        var settings = SettingsService.Load();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<SettingsService>(settings);
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<IDialogService, Historia.Platform.AvaloniaDialogService>();
        sc.AddSingleton<IScrollDirectionProbe, Historia.Platform.ScrollDirectionProbe>();
        sc.AddSingleton<IRepoActionBus, RepoActionBus>();
        sc.AddSingleton<IRepoNetworkActionRunner, RepoNetworkActionRunner>();
        sc.AddSingleton<IAutoFetchHost, AutoFetchHost>();
        sc.AddSingleton<IAutoFetchService, AutoFetchService>();
        sc.AddSingleton<Historia.Services.UI.INavigationBus, Historia.Services.UI.NavigationBus>();
        sc.AddSingleton<IKeyboardCommandRouter, KeyboardCommandRouter>();
        sc.AddSingleton<INotificationService, NotificationService>();
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton<ISystemThemeProbe, FakeThemeProbe>();
        sc.AddSingleton<IThemeService, ThemeService>();
        sc.AddSingleton<ICustomThemeService, CustomThemeService>();
        sc.AddSingleton<IWorkspaceService, WorkspaceService>();
        sc.AddSingleton<IProfileService, ProfileService>();
        sc.AddSingleton<IGitCliService, GitCliService>();
        var locator = new GitExecutableLocator();
        locator.ResolveAsync(null).GetAwaiter().GetResult();
        sc.AddSingleton<IGitExecutableLocator>(locator);
        sc.AddSingleton<IBackgroundWorkGate, BackgroundWorkGate>();
        sc.AddSingleton<IGraphLayoutService, GraphLayoutService>();
        sc.AddSingleton<IRepoStateCache, RepoStateCache>();
        sc.AddSingleton<IStashService, StashService>();
        sc.AddSingleton<IConflictService, ConflictService>();
        sc.AddSingleton<IPatchService, PatchService>();
        sc.AddSingleton<ICherryPickService, CherryPickService>();
        sc.AddSingleton<IRebaseService, RebaseService>();
        sc.AddSingleton<ISubmoduleService, SubmoduleService>();
        sc.AddSingleton<IWorktreeService, WorktreeService>();
        sc.AddSingleton<ISshKeyService, SshKeyService>();
        sc.AddSingleton<ICloneService, CloneService>();
        sc.AddSingleton<ITerminalBackend, PortaPtyBackend>();
        sc.AddSingleton<ITerminalSessionService, TerminalSessionService>();
        sc.AddSingleton<IRecentRepositoryService, RecentRepositoryService>();
        sc.AddSingleton<IRepoIdentityService, RepoIdentityService>();
        sc.AddSingleton<ICredentialVaultService, FakeVault>();
        sc.AddSingleton<IExternalLauncher, ExternalLauncher>();
        sc.AddSingleton<ICmdrPathService, FakeCmdrPathService>();
        var sp = sc.BuildServiceProvider();

        var view = new SettingsView(sp);
        var window = new Window { Width = 900, Height = 2400, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
        Assert.NotEmpty(texts);
        Assert.DoesNotContain(texts, t => t == "About");
        Assert.DoesNotContain(texts, t => t.Contains("Material Symbols"));

        window.Close();
    }
}
