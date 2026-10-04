namespace HistoriaApp.Tests;

/// <summary>The app-band tab strip shows a pending tab for the duration of a clone,
/// driven through a stub <see cref="ICloneService"/> rather than a real clone.</summary>
[Trait("tier", "ui")]
public sealed class CloneTabTests
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

    private sealed class StubCloneService : ICloneService
    {
        public CloneStatus? Current { get; set; }
        public bool IsRunning => Current is not null;
        public event EventHandler? Changed;
        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
        public Task StartAsync(CloneRequest request) => Task.CompletedTask;
        public void Cancel() { }
    }

    private static ServiceProvider CreateServices(StubCloneService clone)
    {
        var sc = new ServiceCollection();
        var settings = SettingsService.Load();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<SettingsService>(settings);
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<IDialogService, Historia.Platform.AvaloniaDialogService>();
        sc.AddSingleton<IScrollDirectionProbe, Historia.Platform.ScrollDirectionProbe>();
        sc.AddSingleton<IRepoActionBus, RepoActionBus>();
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
        sc.AddSingleton<Historia.Services.Storage.IRepoStateCache, Historia.Services.Storage.RepoStateCache>();
        sc.AddSingleton<IStashService, StashService>();
        sc.AddSingleton<IConflictService, ConflictService>();
        sc.AddSingleton<IPatchService, PatchService>();
        sc.AddSingleton<ICherryPickService, CherryPickService>();
        sc.AddSingleton<IRebaseService, RebaseService>();
        sc.AddSingleton<ISubmoduleService, SubmoduleService>();
        sc.AddSingleton<IWorktreeService, WorktreeService>();
        sc.AddSingleton<ISshKeyService, SshKeyService>();
        sc.AddSingleton<ICloneService>(clone);
        sc.AddSingleton<ITerminalBackend, PortaPtyBackend>();
        sc.AddSingleton<ITerminalSessionService, TerminalSessionService>();
        sc.AddSingleton<IRecentRepositoryService, RecentRepositoryService>();
        sc.AddSingleton<IRepoIdentityService, RepoIdentityService>();
        sc.AddSingleton<ICredentialVaultService, FakeVault>();
        sc.AddSingleton<Historia.Services.UI.IExternalLauncher, ExternalLauncher>();
        return sc.BuildServiceProvider();
    }

    private static (AppBandControl Band, ServiceProvider Sp, Window Window) CreateBand(StubCloneService clone)
    {
        var sp = CreateServices(clone);
        var band = new AppBandControl();
        band.Initialize(sp);
        var window = new Window { Width = 1200, Height = 60, Content = band };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (band, sp, window);
    }

    [AvaloniaFact]
    public async Task A_running_clone_adds_one_more_tab_than_open_repositories()
    {
        var clone = new StubCloneService();
        var (band, sp, window) = CreateBand(clone);
        var baselineCount = band.TabViewsForTesting.Count;

        clone.Current = new CloneStatus("https://example.com/r.git", "/tmp/my-repo", null, false);
        clone.RaiseChanged();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(baselineCount + 1, band.TabViewsForTesting.Count);

        window.Close();
        await sp.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task The_pending_tab_shows_the_target_folder_name_and_status_text()
    {
        var clone = new StubCloneService();
        var (band, sp, window) = CreateBand(clone);

        clone.Current = new CloneStatus("https://example.com/r.git", "/tmp/my-repo", null, false);
        clone.RaiseChanged();
        Dispatcher.UIThread.RunJobs();

        var pending = band.TabViewsForTesting.Single(v => v.Tag is not RepositoryContext);
        var texts = pending.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.Contains("my-repo", texts);
        Assert.Contains(clone.Current.Describe(), texts);

        window.Close();
        await sp.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task A_progress_tick_updates_the_text_without_replacing_the_pending_tab_instance()
    {
        var clone = new StubCloneService { Current = new CloneStatus("https://example.com/r.git", "/tmp/my-repo", null, false) };
        var (band, sp, window) = CreateBand(clone);

        var before = band.TabViewsForTesting.Single(v => v.Tag is not RepositoryContext);

        clone.Current = clone.Current with { Progress = new GitProgress { Phase = "Receiving objects", Percent = 42 } };
        clone.RaiseChanged();
        Dispatcher.UIThread.RunJobs();

        var after = band.TabViewsForTesting.Single(v => v.Tag is not RepositoryContext);
        Assert.Same(before, after); // same Border instance — no strip rebuild for a progress tick
        var texts = after.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(clone.Current.Describe(), texts);

        window.Close();
        await sp.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Clearing_current_removes_the_pending_tab()
    {
        var clone = new StubCloneService { Current = new CloneStatus("https://example.com/r.git", "/tmp/my-repo", null, false) };
        var (band, sp, window) = CreateBand(clone);
        Assert.Contains(band.TabViewsForTesting, v => v.Tag is not RepositoryContext);

        clone.Current = null;
        clone.RaiseChanged();
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(band.TabViewsForTesting, v => v.Tag is not RepositoryContext);

        window.Close();
        await sp.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task The_pending_tab_has_no_repository_context_tag_so_drag_reorder_ignores_it()
    {
        var clone = new StubCloneService { Current = new CloneStatus("https://example.com/r.git", "/tmp/my-repo", null, false) };
        var (band, sp, window) = CreateBand(clone);

        var pending = band.TabViewsForTesting.Single(v => v.Tag is not RepositoryContext);
        Assert.Null(pending.Tag);

        window.Close();
        await sp.DisposeAsync();
    }
}
