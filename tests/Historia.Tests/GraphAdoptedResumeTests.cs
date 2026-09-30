namespace Historia.Tests;

/// <summary>
/// A walk that resumes after another writer's rows were adopted (the store's row count jumped past
/// the stream position) must not append rows the store already holds.
/// </summary>
[Trait("tier", "git")]
public sealed class GraphAdoptedResumeTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _repos)
            r.Dispose();
        await _host.DisposeAsync();
    }

    private static readonly GraphQuery Q = new() { RefScope = GraphRefScope.AllRefs };

    /// <summary>Open for the foreground spill of painted rows, never open for the background
    /// full-history spill — so the store holds exactly what the test wrote and nothing races it.</summary>
    private sealed class PaintOnlyGate : IBackgroundWorkGate
    {
        public bool IsOpen => true;
        public Task WaitUntilOpenAsync(CancellationToken ct = default) => Task.Delay(Timeout.Infinite, ct);
        public void ReportGraphSettled(RepositoryContext context) { }
    }

    private static List<RawCommit> LoadRawCommits(string repoPath)
    {
        using var gitRepo = new Repository(repoPath);
        return gitRepo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = gitRepo.Refs }).Select(c =>
        {
            var meta = new CommitMetadata
            {
                Id = c.Id.ToGitObjectId(),
                MessageShort = c.MessageShort,
                AuthorName = c.Author.Name,
                AuthorEmail = c.Author.Email,
                AuthorWhen = c.Author.When,
                CommitWhen = c.Committer.When,
                ParentCount = c.Parents.Count(),
            };
            return new RawCommit(c.Id.ToGitObjectId(), c.Parents.Select(p => p.Id.ToGitObjectId()).ToArray(), meta);
        }).ToList();
    }

    private static List<GitObjectId> Ids(GraphLayoutResult r) =>
        Enumerable.Range(0, r.Layout.RowCount).Select(i => r.Layout.Commits[i].Id).ToList();

    private async Task<List<GitObjectId>> InMemoryOrderAsync(string repoPath)
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
        var memSvc = new GraphLayoutService(_host.Settings, _host.Locator);
        await using var memCtx = _host.Open(repoPath);
        return Ids(await memSvc.ComputeLayoutAsync(memCtx, Q));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_resume_after_adopting_rows_past_the_stream_does_not_duplicate_them()
    {
        const int commitCount = 3_000;
        const int painted = 100;
        const int adoptedTo = 2_900;
        var repo = TestRepositoryBuilder.FromTemplate("linear-3000", b => b.LinearHistory(commitCount));
        _repos.Add(repo);
        var expected = await InMemoryOrderAsync(repo.Path);
        Assert.Equal(commitCount, expected.Count);

        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        var svc = new GraphLayoutService(
            _host.Settings, _host.Locator, _host.Provider.GetRequiredService<IGitCliService>(), new PaintOnlyGate());
        await using var ctx = _host.Open(repo.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();

        // The first compute paints `painted` rows, writes them under the walk key, and leaves the
        // primed walk alive well past them (the walk overscans).
        var layout = await svc.ComputeLayoutAsync(ctx, Q, painted);
        Assert.Equal(painted, layout.Layout.RowCount);
        var probe = GraphMetaStore.ProbeExisting(repo.Path, includeRemoteBranches: true);
        Assert.True(probe.Ok);
        Assert.Equal(painted, probe.RowCount);

        // Another writer under the same walk key runs ahead of both the paint and the live walk.
        var raw = LoadRawCommits(repo.Path);
        Assert.Equal(expected, raw.Select(r => r.Id).ToList());
        var other = new GraphMetaWriter(repo.Path, probe.WalkKey, includeRemoteBranches: true);
        var (outcome, headerRows) = other.AppendSpillBatch(raw.GetRange(painted, adoptedTo - painted), painted, adoptedTo, exhausted: false);
        Assert.Equal(SpillOutcome.Appended, outcome);
        Assert.Equal(adoptedTo, headerRows);

        // Extending adopts those rows, then resumes the live walk from behind them.
        for (var i = 0; i < 40 && layout.Layout.RowCount < commitCount; i++)
            layout = await svc.ExtendLayoutAsync(ctx, layout, additionalRows: 500);
        Assert.Equal(commitCount, layout.Layout.RowCount);
        Assert.Equal(expected, Ids(layout));

        var after = GraphMetaStore.ProbeExisting(repo.Path, includeRemoteBranches: true);
        Assert.True(after.Ok);
        Assert.Equal(commitCount, after.RowCount);
        Assert.Equal(commitCount, after.RecordCount);
        using var store = DiskCommitStore.Open(repo.Path, after.RowCount, after.RecordCount, after.OrderVersion);
        var stored = Enumerable.Range(0, store.Count).Select(i => store.ReadRaw(i).Id).ToList();
        Assert.Equal(commitCount, stored.Distinct().Count());
        Assert.Equal(expected, stored);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_headless_reorder_still_maps_every_batch()
    {
        const int baseCommits = 20;
        var repo = TestRepositoryBuilder.FromTemplate("linear-20", b => b.LinearHistory(baseCommits));
        _repos.Add(repo);
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        await using (var ctx = _host.Open(repo.Path))
            await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

        repo.Checkout("feature", create: true);
        for (var i = 0; i < 12; i++)
            repo.WriteFile("f.txt", $"feature {i}\n").Stage().Commit($"feature commit {i:D2}");

        GraphLayoutService.TestSpillBatchRowsOverride = 5;
        try
        {
            await using var ctx2 = _host.Open(repo.Path);
            await svc.RunHeadlessCacheAsync(ctx2, Q, CancellationToken.None);
        }
        finally
        {
            GraphLayoutService.TestSpillBatchRowsOverride = 0;
        }

        var expected = await InMemoryOrderAsync(repo.Path);
        Assert.Equal(baseCommits + 12, expected.Count);

        var probe = GraphMetaStore.ProbeExisting(repo.Path, includeRemoteBranches: true);
        Assert.True(probe.Ok);
        Assert.True(probe.OrderVersion > 0, "expected the reorder's order file to be in effect");
        Assert.Equal(expected.Count, probe.RowCount);
        using var store = DiskCommitStore.Open(repo.Path, probe.RowCount, probe.RecordCount, probe.OrderVersion);
        Assert.Equal(expected, Enumerable.Range(0, store.Count).Select(i => store.ReadRaw(i).Id).ToList());
    }
}
