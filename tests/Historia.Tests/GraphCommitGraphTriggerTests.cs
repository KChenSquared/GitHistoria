namespace Historia.Tests;

/// <summary>
/// When <see cref="GraphLayoutService"/> checks a repository's commit-graph: after each layout
/// it computes (the check waits for the background-work gate, so it never delays first paint) and
/// at the end of an inactive tab's headless job. Real repositories and a real CLI, wrapped to
/// record every <c>commit-graph write</c>.
/// </summary>
[Trait("tier", "git")]
public sealed class GraphCommitGraphTriggerTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        foreach (var r in _repos)
            r.Dispose();
    }

    private static GraphQuery Q => new() { RefScope = GraphRefScope.AllRefs, IncludeRemoteBranches = true };

    private TestRepositoryBuilder NewRepo()
    {
        var repo = TestRepositoryBuilder.FromTemplate("commit-graph-trigger", b => b.LinearHistory(20));
        _repos.Add(repo);
        return repo;
    }

    private (GraphLayoutService Service, InterceptingGitCliService Cli) NewService(IBackgroundWorkGate gate)
    {
        var cli = new InterceptingGitCliService(_host.Provider.GetRequiredService<IGitCliService>());
        return (new GraphLayoutService(_host.Settings, _host.Locator, cli, gate), cli);
    }

    private RepositoryContext Open(TestRepositoryBuilder repo)
    {
        var ctx = _host.Open(repo.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();
        return ctx;
    }

    private static List<(GitCliRequest Request, DateTime At)> Writes(ConcurrentQueue<(GitCliRequest, DateTime)> log) =>
        [.. log.Where(e => e.Item1.Arguments.Count > 1 && e.Item1.Arguments[0] == "commit-graph" && e.Item1.Arguments[1] == "write")];

    private static ConcurrentQueue<(GitCliRequest, DateTime)> Record(InterceptingGitCliService cli)
    {
        var log = new ConcurrentQueue<(GitCliRequest, DateTime)>();
        cli.Intercept = r => { log.Enqueue((r, DateTime.UtcNow)); return null; };
        return log;
    }

    private static async Task ChecksSettledAsync(GraphLayoutService service)
    {
        var writer = service.BackgroundWriterForTesting!;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (writer.PendingChecksForTesting > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.Equal(0, writer.PendingChecksForTesting);
    }

    [Fact]
    public async Task The_first_layout_returns_before_any_commit_graph_write_starts()
    {
        var repo = NewRepo();
        var gate = new BlockingGate();
        var (service, cli) = NewService(gate);
        var log = Record(cli);
        await using var ctx = Open(repo);

        await service.ComputeLayoutAsync(ctx, Q, 50);
        var computed = DateTime.UtcNow;
        Assert.Empty(Writes(log));
        Assert.False(GraphBackgroundWriter.HasCommitGraph(ctx.GitDirectory));
        Assert.True(service.BackgroundWriterForTesting!.PendingChecksForTesting > 0);

        gate.Open();
        await ChecksSettledAsync(service);

        var write = Assert.Single(Writes(log));
        Assert.True(write.At >= computed);
        Assert.True(GraphBackgroundWriter.HasCommitGraph(ctx.GitDirectory));
        service.Forget(ctx);
    }

    [Fact]
    public async Task A_current_graph_is_not_written()
    {
        var repo = NewRepo();
        repo.Git("commit-graph", "write", "--reachable");
        var gate = new BlockingGate();
        gate.Open();
        var (service, cli) = NewService(gate);
        var log = Record(cli);
        await using var ctx = Open(repo);

        await service.ComputeLayoutAsync(ctx, Q, 50);
        await ChecksSettledAsync(service);

        Assert.Empty(Writes(log));
        service.Forget(ctx);
    }

    [Fact]
    public async Task A_commit_after_the_write_is_refreshed_on_the_refs_changed_recompute()
    {
        var repo = NewRepo();
        repo.Git("commit-graph", "write", "--reachable");
        var gate = new BlockingGate();
        gate.Open();
        var (service, cli) = NewService(gate);
        var log = Record(cli);
        await using var ctx = Open(repo);
        var first = await service.ComputeLayoutAsync(ctx, Q, 50);
        await ChecksSettledAsync(service);

        repo.WriteFile("new.txt", "new\n").Stage().Commit("new");
        await service.RecomputeAsync(ctx, Q, first, InvalidationReason.RefsChanged, 50);
        await ChecksSettledAsync(service);

        var write = Assert.Single(Writes(log));
        Assert.Contains("--split", write.Request.Arguments);
        service.Forget(ctx);
    }

    [Fact]
    public async Task An_inactive_tabs_headless_job_writes_a_missing_graph_once()
    {
        var repo = NewRepo();
        var gate = new BlockingGate();
        gate.Open();
        var (service, cli) = NewService(gate);
        var log = Record(cli);
        await using var ctx = _host.Open(repo.Path);

        await service.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);
        await ChecksSettledAsync(service);
        await service.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);
        await ChecksSettledAsync(service);

        Assert.Single(Writes(log));
        Assert.True(GraphBackgroundWriter.HasCommitGraph(ctx.GitDirectory));
    }

    private sealed class BlockingGate : IBackgroundWorkGate
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsOpen => _tcs.Task.IsCompleted;
        public Task WaitUntilOpenAsync(CancellationToken ct = default) => _tcs.Task.WaitAsync(ct);
        public void ReportGraphSettled(RepositoryContext context) { }
        public void Open() => _tcs.TrySetResult();
    }
}
