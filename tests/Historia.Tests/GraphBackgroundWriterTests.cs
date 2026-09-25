namespace Historia.Tests;

/// <summary>
/// <see cref="GraphBackgroundWriter"/> is the one
/// app-wide serial queue for commit-graph writes and the inactive-tab headless pipeline. These
/// tests use <see cref="RecordingGitCliService"/> so no real <c>git.exe</c> invocation is needed
/// — only that the RIGHT arguments would have been sent, in the right order, without ever
/// touching <c>git maintenance</c> or git config.
/// </summary>
[Trait("tier", "git")]
public sealed class GraphBackgroundWriterTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    public GraphBackgroundWriterTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("seed-1", b => b.WriteFile("f.txt", "1\n").Stage().Commit("c1"));
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    private async Task<RecordingGitCliService> WriteMissingGraphAsync(int rowCount)
    {
        var cli = new RecordingGitCliService();
        var writer = new GraphBackgroundWriter(cli, _host.Settings);
        await _host.Settings.UpdateAsync(s => s with { WriteCommitGraphForLargeRepos = true });
        await using var ctx = _host.Open(_repo.Path);

        writer.MaybeSchedule(ctx, rowCount);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (cli.Requests.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        return cli;
    }

    [Fact]
    public async Task MaybeSchedule_below_the_threshold_never_writes()
    {
        var cli = new RecordingGitCliService();
        var writer = new GraphBackgroundWriter(cli, _host.Settings);
        await _host.Settings.UpdateAsync(s => s with { WriteCommitGraphForLargeRepos = true });
        await using var ctx = _host.Open(_repo.Path);

        writer.MaybeSchedule(ctx, GraphBackgroundWriter.MinCommitsToWrite - 1);
        await Task.Delay(200); // give a wrongly-scheduled write a chance to show up

        Assert.Empty(cli.Requests);
    }

    [Fact]
    public async Task MaybeSchedule_off_by_setting_never_writes()
    {
        var cli = new RecordingGitCliService();
        var writer = new GraphBackgroundWriter(cli, _host.Settings);
        await _host.Settings.UpdateAsync(s => s with { WriteCommitGraphForLargeRepos = false });
        await using var ctx = _host.Open(_repo.Path);

        writer.MaybeSchedule(ctx, GraphBackgroundWriter.MinCommitsToWrite + 1);
        await Task.Delay(200);

        Assert.Empty(cli.Requests);
    }

    [Fact]
    public async Task A_missing_commit_graph_is_written_plain_reachable_at_below_normal_priority()
    {
        var cli = await WriteMissingGraphAsync(GraphBackgroundWriter.MinCommitsToWrite);

        var req = Assert.Single(cli.Requests);
        Assert.Equal(["commit-graph", "write", "--reachable"], req.Arguments);
        Assert.True(req.LowerPriority);
    }

    [Fact]
    public async Task Never_calls_git_maintenance_or_touches_git_config()
    {
        var cli = await WriteMissingGraphAsync(GraphBackgroundWriter.MinCommitsToWrite);

        Assert.All(cli.Requests, r =>
        {
            Assert.DoesNotContain("maintenance", r.Arguments);
            Assert.DoesNotContain("config", r.Arguments);
        });
    }

    [Fact]
    public async Task A_second_MaybeSchedule_for_an_already_written_graph_does_not_write_again()
    {
        var cli = await WriteMissingGraphAsync(GraphBackgroundWriter.MinCommitsToWrite);
        Assert.Single(cli.Requests);

        // Simulate the write having actually landed on disk (RecordingGitCliService doesn't
        // touch the filesystem), then call MaybeSchedule again as a later spill chunk would.
        var gitDir = Path.Combine(_repo.Path, ".git");
        Directory.CreateDirectory(Path.Combine(gitDir, "objects", "info"));
        File.WriteAllText(Path.Combine(gitDir, "objects", "info", "commit-graph"), "stub");

        var writer2 = new GraphBackgroundWriter(cli, _host.Settings); // fresh instance: _attemptedFresh empty
        await using var ctx2 = _host.Open(_repo.Path);
        writer2.MaybeSchedule(ctx2, GraphBackgroundWriter.MinCommitsToWrite);
        await Task.Delay(200);

        Assert.Single(cli.Requests); // still just the one write from before
    }

    [Fact]
    public async Task EnqueueInactiveTab_runs_jobs_one_at_a_time_in_enqueue_order()
    {
        var writer = new GraphBackgroundWriter(new RecordingGitCliService(), _host.Settings);
        // Two distinct contexts — EnqueueInactiveTab now de-dupes by context, so
        // this ordering test (which predates that fix) must not enqueue the same context twice.
        await using var ctx1 = _host.Open(_repo.Path);
        await using var ctx2 = _host.Open(_repo.Path);

        var order = new List<int>();
        var sync = new object();
        var gate1 = new TaskCompletionSource();

        writer.EnqueueInactiveTab(ctx1, async ct =>
        {
            lock (sync) order.Add(1);
            await gate1.Task.WaitAsync(ct); // job 1 blocks until released
            lock (sync) order.Add(2);
        });
        writer.EnqueueInactiveTab(ctx2, ct =>
        {
            lock (sync) order.Add(3); // must run only after job 1 finishes
            return Task.CompletedTask;
        });

        await Task.Delay(200);
        lock (sync) Assert.Equal([1], order); // job 2 hasn't started — job 1 is still blocked

        gate1.SetResult();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (sync) if (order.Count == 3) break;
            await Task.Delay(20);
        }

        lock (sync) Assert.Equal([1, 2, 3], order);
    }

    [Fact]
    public async Task EnqueueInactiveTab_one_jobs_failure_does_not_stall_the_rest()
    {
        var writer = new GraphBackgroundWriter(new RecordingGitCliService(), _host.Settings);
        // Two distinct contexts — see the ordering test above for why.
        await using var ctx1 = _host.Open(_repo.Path);
        await using var ctx2 = _host.Open(_repo.Path);

        var secondRan = new TaskCompletionSource();
        writer.EnqueueInactiveTab(ctx1, _ => throw new InvalidOperationException("boom"));
        writer.EnqueueInactiveTab(ctx2, ct => { secondRan.SetResult(); return Task.CompletedTask; });

        await secondRan.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EnqueueInactiveTab_waits_for_the_gate_before_running()
    {
        var fakeGate = new BlockingGate();
        var writer = new GraphBackgroundWriter(new RecordingGitCliService(), _host.Settings, fakeGate);
        await using var ctx = _host.Open(_repo.Path);

        var ran = new TaskCompletionSource();
        writer.EnqueueInactiveTab(ctx, ct => { ran.SetResult(); return Task.CompletedTask; });

        await Task.Delay(200);
        Assert.False(ran.Task.IsCompleted); // gate closed — job must not have started

        fakeGate.Open();
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WriteCommitGraphCoreAsync_runs_the_git_process_with_an_uncancellable_token()
    {
        var cli = new TokenRecordingGitCliService();
        var writer = new GraphBackgroundWriter(cli, _host.Settings);
        await _host.Settings.UpdateAsync(s => s with { WriteCommitGraphForLargeRepos = true });
        await using var ctx = _host.Open(_repo.Path);

        writer.MaybeSchedule(ctx, GraphBackgroundWriter.MinCommitsToWrite);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (cli.Tokens.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        var ct = Assert.Single(cli.Tokens);
        Assert.False(ct.CanBeCanceled);
    }

    [Fact]
    public async Task Cancelling_the_tab_lifetime_mid_write_does_not_cancel_the_git_process()
    {
        // Verified against Historia.Core.Services.Git.GitCliService.RunAsync: the request's own
        // CancellationToken is what's registered to TryKill the process (see
        // GitCliProcessSetup.TryKill -> Process.Kill(entireProcessTree: true), i.e. TerminateProcess
        // on Windows). Passing CancellationToken.None for the process run itself (as
        // GraphBackgroundWriter.WriteCommitGraphCoreAsync now does) means nothing the caller does
        // to its own RepositoryContext.Lifetime can reach that registration.
        var block = new TaskCompletionSource();
        var cli = new TokenRecordingGitCliService(block);
        var writer = new GraphBackgroundWriter(cli, _host.Settings);
        await _host.Settings.UpdateAsync(s => s with { WriteCommitGraphForLargeRepos = true });
        var ctx = _host.Open(_repo.Path);

        writer.MaybeSchedule(ctx, GraphBackgroundWriter.MinCommitsToWrite);
        var startDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (cli.Tokens.Count == 0 && DateTime.UtcNow < startDeadline)
            await Task.Delay(20);
        Assert.Single(cli.Tokens); // the write has started and is now blocked mid-run

        await ctx.DisposeAsync(); // the tab closes while the git process is still "running"
        await Task.Delay(150); // give a wrongly-cancelled write a chance to unwind

        block.SetResult(); // let the fake "process" finish, as if the real one ran to completion

        // Prove the first write actually completed and recorded state (rather than having been
        // silently cancelled) by triggering a refresh through the SAME writer instance, whose
        // in-memory _lastWrite would otherwise be empty.
        var gitDir = Path.Combine(_repo.Path, ".git");
        Directory.CreateDirectory(Path.Combine(gitDir, "objects", "info"));
        File.WriteAllText(Path.Combine(gitDir, "objects", "info", "commit-graph"), "stub");

        await using var ctx2 = _host.Open(_repo.Path);
        writer.MaybeSchedule(ctx2, GraphBackgroundWriter.MinCommitsToWrite + 100_000);

        var refreshDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (cli.Tokens.Count < 2 && DateTime.UtcNow < refreshDeadline)
            await Task.Delay(20);

        Assert.Equal(2, cli.Tokens.Count); // the refresh only fires if the first write's state stuck
        Assert.All(cli.Tokens, t => Assert.False(t.CanBeCanceled));
    }

    [Fact]
    public async Task EnqueueInactiveTab_a_context_already_queued_is_not_queued_again()
    {
        var writer = new GraphBackgroundWriter(new RecordingGitCliService(), _host.Settings);
        await using var ctxA = _host.Open(_repo.Path);
        await using var ctxB = _host.Open(_repo.Path);

        var blockA = new TaskCompletionSource();
        writer.EnqueueInactiveTab(ctxA, async ct => await blockA.Task.WaitAsync(ct));

        await Task.Delay(150); // let A start running and occupy the drain loop

        var bRuns = 0;
        writer.EnqueueInactiveTab(ctxB, ct => { Interlocked.Increment(ref bRuns); return Task.CompletedTask; });
        // ctxB's job is still queued behind A (not yet running) — this second call must be a no-op.
        writer.EnqueueInactiveTab(ctxB, ct => { Interlocked.Increment(ref bRuns); return Task.CompletedTask; });

        blockA.SetResult();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Volatile.Read(ref bRuns) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await Task.Delay(200); // give a wrongly-duplicated second run a chance to also fire

        Assert.Equal(1, bRuns);
    }

    [Fact]
    public async Task An_active_tab_write_completes_while_an_inactive_job_is_still_parked()
    {
        var cli = new RecordingGitCliService();
        var writer = new GraphBackgroundWriter(cli, _host.Settings);
        await _host.Settings.UpdateAsync(s => s with { WriteCommitGraphForLargeRepos = true });
        await using var ctx = _host.Open(_repo.Path);

        var headlessStarted = new TaskCompletionSource();
        var headlessGate = new TaskCompletionSource();
        writer.EnqueueInactiveTab(ctx, async ct =>
        {
            headlessStarted.SetResult();
            await headlessGate.Task.WaitAsync(ct); // parked indefinitely, holding only _headlessSerial
        });

        await headlessStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // With the headless job still parked, the active tab's own commit-graph write must still
        // go through — it only ever needs _serial, which the headless job never touches.
        writer.MaybeSchedule(ctx, GraphBackgroundWriter.MinCommitsToWrite);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (cli.Requests.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.Single(cli.Requests);
    }

    // ── RefreshAfterFetch ──

    [Fact]
    public async Task RefreshAfterFetch_with_an_existing_graph_writes_split_with_an_expire_time()
    {
        var gitDir = Path.Combine(_repo.Path, ".git");
        Directory.CreateDirectory(Path.Combine(gitDir, "objects", "info"));
        File.WriteAllText(Path.Combine(gitDir, "objects", "info", "commit-graph"), "stub");

        var cli = new RecordingGitCliService();
        var writer = new GraphBackgroundWriter(cli, _host.Settings);
        await _host.Settings.UpdateAsync(s => s with { WriteCommitGraphForLargeRepos = true });
        await using var ctx = _host.Open(_repo.Path);

        writer.RefreshAfterFetch(ctx);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (cli.Requests.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        var req = Assert.Single(cli.Requests);
        Assert.Equal("commit-graph", req.Arguments[0]);
        Assert.Equal("write", req.Arguments[1]);
        Assert.Contains("--split", req.Arguments);
        Assert.Contains(req.Arguments, a => a.StartsWith("--expire-time="));
        Assert.True(req.LowerPriority);
    }

    [Fact]
    public async Task RefreshAfterFetch_with_no_graph_but_a_known_large_meta_header_writes_plain_reachable()
    {
        // A `.meta.hdr` left by an earlier headless/foreground walk is the ONLY way this repo's
        // size is known without a fresh commit-graph write — write one directly rather than
        // spilling 30,000 real RawCommit rows through GraphMetaWriter.
        var hdr = new GraphMetaStore.Header
        {
            Version = GraphMetaStore.Version,
            Fingerprint = 0x1234,
            IncludeRemoteBranches = true,
            RowCount = GraphBackgroundWriter.MinCommitsToWrite,
            RecordCount = GraphBackgroundWriter.MinCommitsToWrite,
            Exhausted = true,
            WalkEngine = GraphWalkCache.CliWalkMarker,
        };
        var hdrPath = GraphCachePaths.File(_repo.Path, GraphMetaStore.HdrExt);
        Directory.CreateDirectory(Path.GetDirectoryName(hdrPath)!);
        File.WriteAllBytes(hdrPath, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(hdr, GraphMetaStore.Json));

        var cli = new RecordingGitCliService();
        var writer = new GraphBackgroundWriter(cli, _host.Settings);
        await _host.Settings.UpdateAsync(s => s with { WriteCommitGraphForLargeRepos = true });
        await using var ctx = _host.Open(_repo.Path);

        writer.RefreshAfterFetch(ctx);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (cli.Requests.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        var req = Assert.Single(cli.Requests);
        Assert.Equal(["commit-graph", "write", "--reachable"], req.Arguments);
    }

    [Fact]
    public async Task RefreshAfterFetch_with_no_graph_and_an_unknown_size_never_writes()
    {
        var cli = new RecordingGitCliService();
        var writer = new GraphBackgroundWriter(cli, _host.Settings);
        await _host.Settings.UpdateAsync(s => s with { WriteCommitGraphForLargeRepos = true });
        await using var ctx = _host.Open(_repo.Path);

        writer.RefreshAfterFetch(ctx);
        await Task.Delay(200); // give a wrongly-triggered write a chance to show up

        Assert.Empty(cli.Requests);
    }

    [Fact]
    public async Task RefreshAfterFetch_off_by_setting_never_writes()
    {
        var gitDir = Path.Combine(_repo.Path, ".git");
        Directory.CreateDirectory(Path.Combine(gitDir, "objects", "info"));
        File.WriteAllText(Path.Combine(gitDir, "objects", "info", "commit-graph"), "stub");

        var cli = new RecordingGitCliService();
        var writer = new GraphBackgroundWriter(cli, _host.Settings);
        await _host.Settings.UpdateAsync(s => s with { WriteCommitGraphForLargeRepos = false });
        await using var ctx = _host.Open(_repo.Path);

        writer.RefreshAfterFetch(ctx);
        await Task.Delay(200);

        Assert.Empty(cli.Requests);
    }

    /// <summary>Local fake — not <see cref="RecordingGitCliService"/> in <c>Fakes.cs</c> — because
    /// this milestone doesn't own that shared file: it also records the <see cref="CancellationToken"/>
    /// each call receives, and can optionally block mid-call to simulate a long-running write.</summary>
    private sealed class TokenRecordingGitCliService(TaskCompletionSource? blockUntil = null) : IGitCliService
    {
        public List<GitCliRequest> Requests { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public bool IsGitAvailable => true;

        public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
            => RunAsync(request, progress: null, onStdoutLine: null, ct);

        public async Task<GitCliResult> RunAsync(
            GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
        {
            Requests.Add(request);
            Tokens.Add(ct);
            if (blockUntil is not null)
                await blockUntil.Task;
            return new GitCliResult(0, "", "", GitErrorKind.None, TimeSpan.Zero);
        }
    }

    private sealed class BlockingGate : IBackgroundWorkGate
    {
        private TaskCompletionSource _tcs = new();
        public bool IsOpen => _tcs.Task.IsCompleted;
        public Task WaitUntilOpenAsync(CancellationToken ct = default) => _tcs.Task.WaitAsync(ct);
        public void ReportGraphSettled(RepositoryContext context) { }
        public void Open() => _tcs.TrySetResult();
    }
}
