namespace HistoriaApp.Tests;

[Trait("tier", "ui")]

public sealed class GraphCrawlGuardTests : IDisposable
{
    private readonly string _repoDir;
    private readonly ServiceProvider _sp;
    private readonly RepositoryContext _context;

    public GraphCrawlGuardTests()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "historia-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoDir);
        LibGit2Sharp.Repository.Init(_repoDir);

        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService>(SettingsService.Load());
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton<LibGit2Service>();
        var locator = new GitExecutableLocator();
        locator.ResolveAsync(null).GetAwaiter().GetResult();
        sc.AddSingleton<IGitExecutableLocator>(locator);
        sc.AddSingleton<IGitCliService, GitCliService>();
        sc.AddSingleton<ICredentialVaultService, FakeVault>();
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
    public async Task Crawl_waits_for_first_full_load_past_the_old_30s_mark()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), CommitGraphControl.StuckGuardCeiling);

        // Initially FirstFullLoad is incomplete
        Assert.False(_context.FirstFullLoad.IsCompleted);

        // A simulated 40ms ceiling (shorter than FirstFullLoad delay) fires:
        var firedCeiling = !await CommitGraphControl.WaitForCrawlGuardAsync(_context, TimeSpan.FromMilliseconds(40), CancellationToken.None);
        Assert.True(firedCeiling, "Ceiling shorter than FirstFullLoad should fire");

        // Now test waiting with 120s ceiling where FirstFullLoad completes at T+40ms:
        var waitTask = CommitGraphControl.WaitForCrawlGuardAsync(_context, TimeSpan.FromSeconds(120), CancellationToken.None);
        await Task.Delay(40);
        _context.MarkFirstFullLoadDone();

        var completedViaFullLoad = await waitTask;
        Assert.True(completedViaFullLoad, "Should complete via FirstFullLoad rather than firing ceiling");
    }

    [AvaloniaFact]
    public async Task Back_to_back_refs_changed_raises_produce_one_recompute()
    {
        var graph = new CommitGraphControl();
        graph.ContextForTesting = _context;

        var recomputeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceedRecompute = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        graph.RecomputeOverrideForTesting = async ctx =>
        {
            recomputeStarted.TrySetResult();
            await proceedRecompute.Task;
        };

        // Fire first RefsChanged event
        var args = new RefsChangedEventArgs([], [], []);
        graph.OnRefsChanged(null, args);

        // Wait until first recompute is actively running
        await recomputeStarted.Task;

        // While first recompute is in flight, fire multiple back-to-back RefsChanged events
        graph.OnRefsChanged(null, args);
        graph.OnRefsChanged(null, args);
        graph.OnRefsChanged(null, args);

        // Unblock first recompute
        proceedRecompute.TrySetResult();

        // Await the active coalesced task
        if (graph.ActiveRefsRecomputeTask is { } task)
            await task;

        // First run + exactly ONE coalesced trailing run = 2 recomputes total (not 4)
        Assert.Equal(2, graph.RefsRecomputeCount);
    }

    [AvaloniaFact]
    public async Task A_cancelled_wait_throws_instead_of_reporting_the_ceiling()
    {
        Assert.False(_context.FirstFullLoad.IsCompleted);

        using var cts = new CancellationTokenSource();
        var waitTask = CommitGraphControl.WaitForCrawlGuardAsync(_context, TimeSpan.FromSeconds(120), cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waitTask);
    }

    private sealed class FakeVault : ICredentialVaultService
    {
        public Task StoreTokenAsync(string key, string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteTokenAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }
}
