namespace Historia.Tests;

/// <summary>
/// On a large working tree the focused tab's first <c>git status</c> waits for the graph's crawl
/// to warm up (<see cref="RepositoryContext.GraphWarmed"/>): a cold status there reads every file
/// and slows each crawl step several-fold while both run.
/// </summary>
[Trait("tier", "git")]
public sealed class LargeWorkingTreeStatusTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo = TestRepositoryBuilder.FromTemplate("large-tree-status", b =>
    {
        b.WriteFile("a.txt", "a\n").Stage();
        b.Commit("init");
    });
    private readonly List<ServiceProvider> _providers = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _providers)
            await p.DisposeAsync();
        await _host.DisposeAsync();
        _repo.Dispose();
    }

    private WorkspaceService NewWorkspace(IGitCliService cli)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService>(_host.Settings);
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton(_host.Locator);
        sc.AddSingleton(cli);
        sc.AddSingleton<ICredentialVaultService, FakeVault>();
        var provider = sc.BuildServiceProvider();
        _providers.Add(provider);
        return new WorkspaceService(provider, cli, new RecentRepositoryService(_host.Settings),
            new ProfileService(_host.Settings, new FakeVault(), cli), new RepoIdentityService(cli));
    }

    private static bool IsStatus(GitCliRequest r) => r.Arguments.Contains("status");

    [Fact(Timeout = 60000)]
    public async Task The_first_status_waits_until_the_graph_has_warmed_up()
    {
        var cli = new InterceptingGitCliService(_host.Cli);
        var ws = NewWorkspace(cli);

        var ctx = await ws.OpenAsync(_repo.Path);
        ctx.Watcher.TreatAsLargeWorkingTreeForTesting();
        ctx.MarkFirstGraphPaintDone();
        await ctx.FirstFullLoad.WaitAsync(TimeSpan.FromSeconds(20));

        await Task.Delay(1000);
        Assert.DoesNotContain(cli.Requests, IsStatus);

        ctx.MarkGraphWarmed();
        var sw = Stopwatch.StartNew();
        while (!cli.Requests.Any(IsStatus) && sw.Elapsed < TimeSpan.FromSeconds(20))
            await Task.Delay(25);
        Assert.Contains(cli.Requests, IsStatus);

        await ws.DisposeAsync();
    }

    [Fact(Timeout = 60000)]
    public async Task A_normal_working_tree_gets_its_status_without_waiting_for_the_graph()
    {
        var cli = new InterceptingGitCliService(_host.Cli);
        var ws = NewWorkspace(cli);

        var ctx = await ws.OpenAsync(_repo.Path);
        ctx.MarkFirstGraphPaintDone();
        await ctx.FirstFullLoad.WaitAsync(TimeSpan.FromSeconds(20));

        var sw = Stopwatch.StartNew();
        while (!cli.Requests.Any(IsStatus) && sw.Elapsed < TimeSpan.FromSeconds(20))
            await Task.Delay(25);
        Assert.Contains(cli.Requests, IsStatus);
        Assert.False(ctx.GraphWarmed.IsCompleted);

        await ws.DisposeAsync();
    }
}
