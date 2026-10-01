namespace Historia.Tests;

/// <summary>
/// A tab the user leaves while it is still loading stops loading: it never starts identity or
/// refresh work it hadn't begun, and the focused-tab status refresh it had begun is cancelled —
/// a cold <c>git status</c> on a huge working tree must not compete with the tab the user moved
/// on to. The background queue gives it its status later.
/// </summary>
[Trait("tier", "git")]
public sealed class WorkspaceFocusLossTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];
    private readonly List<ServiceProvider> _providers = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _providers)
            await p.DisposeAsync();
        await _host.DisposeAsync();
        foreach (var r in _repos)
            r.Dispose();
    }

    private TestRepositoryBuilder CreateRepo(string name)
    {
        var r = TestRepositoryBuilder.FromTemplate($"focus-loss-{name}", b =>
        {
            b.WriteFile("a.txt", $"{name}\n").Stage();
            b.Commit("init");
        });
        _repos.Add(r);
        return r;
    }

    private sealed class RecordingIdentity(IRepoIdentityService inner) : IRepoIdentityService
    {
        public readonly ConcurrentQueue<RepositoryContext> Resolved = new();

        public Task<RepoIdentityKey> ResolveAsync(RepositoryContext context, CancellationToken ct = default)
        {
            Resolved.Enqueue(context);
            return inner.ResolveAsync(context, ct);
        }
    }

    private (WorkspaceService Workspace, RecordingIdentity Identity) NewWorkspace(IGitCliService cli)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService>(_host.Settings);
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton(_host.Locator);
        sc.AddSingleton(cli);
        sc.AddSingleton<ICredentialVaultService, FakeVault>();
        var provider = sc.BuildServiceProvider();
        _providers.Add(provider);

        var identity = new RecordingIdentity(new RepoIdentityService(cli));
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        var ws = new WorkspaceService(provider, cli, new RecentRepositoryService(_host.Settings), profiles, identity);
        return (ws, identity);
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }
        return condition();
    }

    [Fact(Timeout = 60000)]
    public async Task Leaving_a_tab_before_its_first_paint_starts_none_of_its_loading_work()
    {
        var a = CreateRepo("a");
        var b = CreateRepo("b");
        var cli = new BlockingGitCliService(_host.Cli, _ => false);
        var (ws, identity) = NewWorkspace(cli);

        // A is focused and waits for its graph's first paint, which never comes.
        var ctxA = await ws.OpenAsync(a.Path);
        // Opening B moves focus while A is still waiting.
        var ctxB = await ws.OpenAsync(b.Path);
        await Task.Delay(1000);

        Assert.DoesNotContain(ctxA, identity.Resolved);
        Assert.DoesNotContain(cli.Requests, r => BlockingGitCliService.IsIn(r, ctxA.RepositoryPath)
            && (r.Arguments.Contains("for-each-ref") || r.Arguments.Contains("rev-list")));

        ctxB.MarkFirstGraphPaintDone();
        ctxA.MarkFirstGraphPaintDone();
        await ws.DisposeAsync();
    }

    [Fact(Timeout = 60000)]
    public async Task Leaving_a_tab_cancels_its_status_refresh_and_the_background_queue_redoes_it()
    {
        var a = CreateRepo("a");
        var b = CreateRepo("b");
        var cli = new BlockingGitCliService(_host.Cli,
            r => BlockingGitCliService.IsIn(r, a.Path) && r.Arguments.Contains("status"));
        var (ws, _) = NewWorkspace(cli);

        var ctxA = await ws.OpenAsync(a.Path);
        ctxA.MarkFirstGraphPaintDone(); // identity + refs, then the focused-tab status refresh
        await cli.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var ctxB = await ws.OpenAsync(b.Path);
        await cli.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cli.Release.TrySetResult();

        // Once B has loaded, the background queue refreshes A's status.
        ctxB.MarkFirstGraphPaintDone();
        Assert.True(await EventuallyAsync(
            () => cli.Requests.Count(r => BlockingGitCliService.IsIn(r, a.Path) && r.Arguments.Contains("status")) >= 2,
            TimeSpan.FromSeconds(20)), "A's status was never refreshed again");

        await ws.DisposeAsync();
    }
}
