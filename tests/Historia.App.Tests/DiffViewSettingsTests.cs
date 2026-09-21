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
        sc.AddSingleton<LibGit2Service>();
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
        var sbsScroller = await WaitForScrollViewerAsync(sbsView);

        // SideBySide view renders a 3-column Grid as scroller content
        Assert.IsType<Grid>(sbsScroller.Content);

        var unifiedSettings = new LocalFakeSettingsService(new AppSettings { DefaultDiffView = DiffViewMode.Unified });
        var unifiedView = new DiffView(_context, req, unifiedSettings, _sp);
        var unifiedScroller = await WaitForScrollViewerAsync(unifiedView);

        // Unified view renders a StackPanel as scroller content
        Assert.IsType<StackPanel>(unifiedScroller.Content);
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
        var scroller = await WaitForScrollViewerAsync(view);

        // When word wrap is on, horizontal scrollbar is disabled
        Assert.Equal(ScrollBarVisibility.Disabled, scroller.HorizontalScrollBarVisibility);

        // Code block has TextWrapping.Wrap
        var textBlocks = (scroller.Content as Control)!.GetLogicalDescendants().OfType<SelectableTextBlock>().ToList();
        Assert.NotEmpty(textBlocks);
        Assert.All(textBlocks, tb => Assert.Equal(TextWrapping.Wrap, tb.TextWrapping));
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
        var scroller = await WaitForScrollViewerAsync(view);

        // When word wrap is off, horizontal scrollbar is Auto
        Assert.Equal(ScrollBarVisibility.Auto, scroller.HorizontalScrollBarVisibility);

        // Code block has TextWrapping.NoWrap
        var textBlocks = (scroller.Content as Control)!.GetLogicalDescendants().OfType<SelectableTextBlock>().ToList();
        Assert.NotEmpty(textBlocks);
        Assert.All(textBlocks, tb => Assert.Equal(TextWrapping.NoWrap, tb.TextWrapping));
    }

    private static async Task<ScrollViewer> WaitForScrollViewerAsync(DiffView view)
    {
        for (var i = 0; i < 50; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (view.Content is DockPanel dp && dp.Children.OfType<ContentControl>().FirstOrDefault()?.Content is ScrollViewer sv)
                return sv;
            await Task.Delay(20);
        }
        throw new TimeoutException("ScrollViewer did not load in time.");
    }

    private sealed class LocalFakeSettingsService(AppSettings initial) : ISettingsService
    {
        public AppSettings Current { get; } = initial;
        public event EventHandler<SettingsChangedEventArgs>? Changed { add { } remove { } }
        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default) => Task.CompletedTask;
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
