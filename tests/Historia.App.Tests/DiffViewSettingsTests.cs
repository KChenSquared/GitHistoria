namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class DiffViewSettingsTests : IDisposable
{
    private readonly string _repoDir;
    private readonly ServiceProvider _sp;
    private readonly RepositoryContext _context;

    public DiffViewSettingsTests()
    {
        _repoDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "historia-diff-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoDir);
        LibGit2Sharp.Repository.Init(_repoDir);

        File.WriteAllText(System.IO.Path.Combine(_repoDir, "test.txt"), "line 1\nline 2\n");
        using (var repo = new LibGit2Sharp.Repository(_repoDir))
        {
            LibGit2Sharp.Commands.Stage(repo, "test.txt");
            var sig = new LibGit2Sharp.Signature("Test", "test@example.com", DateTimeOffset.Now);
            repo.Commit("Initial commit", sig, sig, new LibGit2Sharp.CommitOptions());
        }

        File.WriteAllText(System.IO.Path.Combine(_repoDir, "test.txt"), "line 1 modified with very long content that exceeds standard column limits\nline 2\n");

        var sc = new ServiceCollection();
        var settings = SettingsService.Load();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<IRepoLayoutService>(new FakeLayoutService());
        sc.AddSingleton<ICredentialVaultService>(new FakeVault());
        var locator = new GitExecutableLocator();
        locator.ResolveAsync(null).GetAwaiter().GetResult();
        sc.AddSingleton<IGitExecutableLocator>(locator);
        sc.AddSingleton<IGitCliService, GitCliService>();
        _sp = sc.BuildServiceProvider();

        _context = RepositoryContext.Open(_repoDir, _sp);
    }

    public void Dispose()
    {
        try { _context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _sp.Dispose(); } catch { }
        try { Directory.Delete(_repoDir, true); } catch { }
    }

    [AvaloniaFact]
    public async Task Side_by_side_setting_selects_the_initial_mode()
    {
        var sbsSettings = new LocalFakeSettingsService(new AppSettings { DefaultDiffView = DiffViewMode.SideBySide });
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "test.txt" };
        var sbsView = new DiffView(_context, req, sbsSettings, _sp);
        var sbsPresenter = await WaitForPresenterAsync(sbsView);

        Assert.IsType<SideBySideDiffPresenter>(sbsPresenter);
        Assert.NotEqual(Environment.CurrentManagedThreadId, sbsView.LastModelCreationThreadId);
        Assert.True(sbsView.LastModelCreationThreadId > 0);

        var unifiedSettings = new LocalFakeSettingsService(new AppSettings { DefaultDiffView = DiffViewMode.Unified });
        var unifiedView = new DiffView(_context, req, unifiedSettings, _sp);
        var unifiedPresenter = await WaitForPresenterAsync(unifiedView);

        Assert.IsType<CombinedDiffPresenter>(unifiedPresenter);
    }

    [AvaloniaFact]
    public async Task Toggling_mode_persists_it_via_settings()
    {
        var settings = new LocalFakeSettingsService(new AppSettings { DefaultDiffView = DiffViewMode.Unified });
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "test.txt" };
        var view = new DiffView(_context, req, settings, _sp);
        var presenter = await WaitForPresenterAsync(view);

        Assert.IsType<CombinedDiffPresenter>(presenter);
        Assert.Equal(DiffViewMode.Unified, settings.Current.DefaultDiffView);

        // Toggle to SideBySide
        view.ToggleModeForTesting();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(DiffViewMode.SideBySide, settings.Current.DefaultDiffView);
        Assert.IsType<SideBySideDiffPresenter>(view.PresenterForTesting);

        // Toggle back to Unified
        view.ToggleModeForTesting();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(DiffViewMode.Unified, settings.Current.DefaultDiffView);
        Assert.IsType<CombinedDiffPresenter>(view.PresenterForTesting);
    }

    [AvaloniaFact]
    public async Task New_diff_view_opens_in_last_toggled_mode()
    {
        var settings = new LocalFakeSettingsService(new AppSettings { DefaultDiffView = DiffViewMode.Unified });
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "test.txt" };
        var view1 = new DiffView(_context, req, settings, _sp);
        await WaitForPresenterAsync(view1);

        // Toggle mode on first view
        view1.ToggleModeForTesting();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(DiffViewMode.SideBySide, settings.Current.DefaultDiffView);

        // Second view should open with SideBySide
        var view2 = new DiffView(_context, req, settings, _sp);
        var presenter2 = await WaitForPresenterAsync(view2);
        Assert.IsType<SideBySideDiffPresenter>(presenter2);
    }

    [AvaloniaFact]
    public async Task Disposed_view_ignores_late_load_result()
    {
        var settings = new LocalFakeSettingsService(new AppSettings { DefaultDiffView = DiffViewMode.Unified });
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "test.txt" };
        var view = new DiffView(_context, req, settings, _sp);

        // Dispose immediately before load completes
        view.Dispose();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(view.PresenterForTesting);
    }

    [AvaloniaFact]
    public async Task Word_wrap_setting_wraps_diff_lines()
    {
        var wrapSettings = new LocalFakeSettingsService(new AppSettings
        {
            DefaultDiffView = DiffViewMode.Unified,
            DiffWordWrap = true,
        });
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "test.txt" };
        var view = new DiffView(_context, req, wrapSettings, _sp);
        var presenter = await WaitForPresenterAsync(view);

        var combined = Assert.IsType<CombinedDiffPresenter>(presenter);
        Assert.True(combined.WordWrap);
    }

    [AvaloniaFact]
    public async Task Word_wrap_off_leaves_lines_unwrapped()
    {
        var noWrapSettings = new LocalFakeSettingsService(new AppSettings
        {
            DefaultDiffView = DiffViewMode.Unified,
            DiffWordWrap = false,
        });
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "test.txt" };
        var view = new DiffView(_context, req, noWrapSettings, _sp);
        var presenter = await WaitForPresenterAsync(view);

        var combined = Assert.IsType<CombinedDiffPresenter>(presenter);
        Assert.False(combined.WordWrap);
    }

    private static async Task<Control> WaitForPresenterAsync(DiffView view)
    {
        for (var i = 0; i < 50; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (view.PresenterForTesting is { } presenter and not TextBlock)
                return presenter;
            await Task.Delay(20);
        }
        throw new TimeoutException("Presenter did not load in time.");
    }

    private sealed class LocalFakeSettingsService(AppSettings initial) : ISettingsService
    {
        public AppSettings Current { get; private set; } = initial;
        public event EventHandler<SettingsChangedEventArgs>? Changed;
        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default)
        {
            var old = Current;
            Current = mutate(Current);
            Changed?.Invoke(this, new SettingsChangedEventArgs(old, Current));
            return Task.CompletedTask;
        }
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeLayoutService : IRepoLayoutService
    {
        public IReadOnlyList<GraphColumn> GlobalColumns => GraphColumnDefaults.All;
        public event EventHandler<RepoLayoutChangedEventArgs>? Changed { add { } remove { } }
        public RepoLayout Get(string repoPath) => new();
        public Task UpdateAsync(string repoPath, Func<RepoLayout, RepoLayout> mutate) => Task.CompletedTask;
        public Task UpdateGlobalAsync(Func<IReadOnlyList<GraphColumn>, IReadOnlyList<GraphColumn>> mutate) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeVault : ICredentialVaultService
    {
        public Task StoreTokenAsync(string key, string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteTokenAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }
}
