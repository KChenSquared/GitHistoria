namespace Historia.Tests;

/// <summary>
/// <see cref="RefSnapshotProvider"/> must report exactly the refs LibGit2Sharp reports — same
/// names, same targets, same peeled commits — or the graph's walk key would drift and every
/// on-disk graph store would miss once. It must also scan once per ref change however many
/// callers ask, and never let one cancelled caller cancel a scan others are waiting on.
/// </summary>
[Trait("tier", "git")]
public sealed class RefSnapshotProviderTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    public RefSnapshotProviderTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("ref-shapes", BuildRefShapes);
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    /// <summary>Every ref shape tip resolution has to handle: a local branch, a remote branch
    /// and its symbolic <c>origin/HEAD</c>, a dangling symref, lightweight and annotated tags, a
    /// tag of a tag, a tag of a tree, a stash, and a mix of packed and loose refs.</summary>
    internal static void BuildRefShapes(TestRepositoryBuilder b)
    {
        b.LinearHistory(6);
        b.Git("branch", "feature", "HEAD~2");
        b.Git("update-ref", "refs/remotes/origin/main", "HEAD~1");
        b.Git("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        b.Git("symbolic-ref", "refs/heads/dangling", "refs/heads/does-not-exist");
        b.Git("tag", "light", "HEAD~3");
        b.Git("tag", "-a", "-m", "annotated\n\nbody line", "annotated", "HEAD~4");
        b.Git("-c", "advice.nestedTag=false", "tag", "-a", "-m", "tag of a tag", "nested", "annotated");
        b.Git("tag", "tree-tag", "HEAD^{tree}");
        b.Git("pack-refs", "--all");
        b.Git("tag", "loose-after-pack", "HEAD~5");
        b.WriteFile("f.txt", "stashed\n");
        b.Git("stash", "push", "-q");
    }

    [Fact]
    public async Task Snapshot_matches_libgit2_for_every_resolvable_ref()
    {
        await using var ctx = _host.Open(_repo.Path);
        var snapshot = await ctx.RefSnapshots.GetAsync();

        using var repo = new Repository(ctx.RepositoryPath);
        var expected = repo.Refs
            .Select(r => (Ref: r, Direct: r.ResolveToDirectReference()))
            .Where(x => x.Direct?.TargetIdentifier is not null)
            .Select(x =>
            {
                var target = x.Direct!.TargetIdentifier!;
                Commit? peeled;
                try { peeled = repo.Lookup<Commit>(target) ?? repo.Lookup(target)?.Peel<Commit>(); }
                catch (InvalidSpecificationException) { peeled = null; }
                return (Name: x.Ref.CanonicalName, Target: target, Peeled: peeled?.Sha);
            })
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

        var actual = snapshot.Refs
            .Select(r => (Name: r.CanonicalName, Target: r.Target!.Value.ToString(), Peeled: r.PeeledCommit?.ToString()))
            .ToList();

        Assert.Equal(expected, actual);
        Assert.Contains(actual, r => r.Name == "refs/tags/nested" && r.Peeled is not null);
        Assert.Contains(actual, r => r.Name == "refs/tags/tree-tag" && r.Peeled is null);
        Assert.Equal("refs/remotes/origin/main", snapshot.Find("refs/remotes/origin/HEAD")!.SymrefTarget);
        Assert.Null(snapshot.Find("refs/heads/dangling"));
    }

    [Fact]
    public async Task Head_and_annotations_come_through()
    {
        await using var ctx = _host.Open(_repo.Path);
        var snapshot = await ctx.RefSnapshots.GetAsync();

        Assert.Equal("refs/heads/main", snapshot.HeadSymref);
        Assert.Equal(new GitObjectId(_repo.HeadSha), snapshot.HeadTarget);
        Assert.False(snapshot.IsHeadDetached);

        var annotated = snapshot.Find("refs/tags/annotated")!;
        Assert.True(annotated.IsAnnotatedTag);
        Assert.Equal("Test User", annotated.TaggerName);
        Assert.StartsWith("annotated\n\nbody line", annotated.Message);
        Assert.NotNull(annotated.TaggerWhen);

        _repo.Git("checkout", "-q", "--detach", "HEAD~1");
        var detached = await ctx.RefSnapshots.GetAsync();
        Assert.True(detached.IsHeadDetached);
        Assert.Equal(new GitObjectId(_repo.HeadSha), detached.HeadTarget);
    }

    [Fact]
    public async Task Unchanged_refs_are_served_from_the_cache()
    {
        await using var ctx = _host.Open(_repo.Path);
        var first = await ctx.RefSnapshots.GetAsync();
        var second = await ctx.RefSnapshots.GetAsync();

        Assert.Same(first, second);
        Assert.Equal(1, ctx.RefSnapshots.ScanCountForTesting);
    }

    [Fact]
    public async Task A_ref_write_rescans()
    {
        await using var ctx = _host.Open(_repo.Path);
        await ctx.RefSnapshots.GetAsync();

        _repo.Git("branch", "added-later", "HEAD~1");
        var after = await ctx.RefSnapshots.GetAsync();
        Assert.NotNull(after.Find("refs/heads/added-later"));
        // At least one rescan: the context's file watcher also sees the write, and its refresh
        // can scan the refs mid-write too when the machine is busy.
        Assert.True(ctx.RefSnapshots.ScanCountForTesting >= 2);
    }

    /// <summary>Counts <c>for-each-ref</c> runs that overlap, and holds each one open a while so
    /// callers pile up behind it.</summary>
    private sealed class OverlapCountingCli(IGitCliService inner) : IGitCliService
    {
        private int _running;
        private int _maxRunning;
        public int MaxConcurrentScans => Volatile.Read(ref _maxRunning);
        public bool IsGitAvailable => inner.IsGitAvailable;

        public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default) =>
            RunAsync(request, progress: null, onStdoutLine: null, ct);

        public async Task<GitCliResult> RunAsync(GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
        {
            if (request.Arguments is not ["for-each-ref", ..])
                return await inner.RunAsync(request, progress, onStdoutLine, ct);

            var now = Interlocked.Increment(ref _running);
            int seen;
            while ((seen = Volatile.Read(ref _maxRunning)) < now && Interlocked.CompareExchange(ref _maxRunning, now, seen) != seen) { }
            try
            {
                await Task.Delay(200, ct);
                return await inner.RunAsync(request, progress, onStdoutLine, ct);
            }
            finally { Interlocked.Decrement(ref _running); }
        }
    }

    [Fact]
    public async Task Scans_never_overlap_while_refs_keep_changing()
    {
        await using var ctx = _host.Open(_repo.Path);
        var cli = new OverlapCountingCli(_host.Provider.GetRequiredService<IGitCliService>());
        var provider = new RefSnapshotProvider(cli, ctx.GitDirectory, ctx.RepositoryPath, CancellationToken.None);

        var first = provider.GetAsync();
        _repo.Git("branch", "burst-1", "HEAD~1");
        var second = provider.GetAsync();
        _repo.Git("branch", "burst-2", "HEAD~2");
        var third = provider.GetAsync();
        await Task.WhenAll(first, second, third);

        Assert.Equal(1, cli.MaxConcurrentScans);
        Assert.NotNull((await third).Find("refs/heads/burst-1"));
        Assert.NotNull((await third).Find("refs/heads/burst-2"));
    }

    [Fact]
    public async Task Concurrent_callers_share_one_scan()
    {
        await using var ctx = _host.Open(_repo.Path);
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ctx.RefSnapshots.GetAsync()));

        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Equal(1, ctx.RefSnapshots.ScanCountForTesting);
    }

    [Fact]
    public async Task A_cancelled_caller_does_not_cancel_the_shared_scan()
    {
        await using var ctx = _host.Open(_repo.Path);
        using var cts = new CancellationTokenSource();

        var cancelled = ctx.RefSnapshots.GetAsync(cts.Token);
        var other = ctx.RefSnapshots.GetAsync();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var snapshot = await other;
        Assert.NotEmpty(snapshot.Refs);
        Assert.Equal(1, ctx.RefSnapshots.ScanCountForTesting);
    }
}
