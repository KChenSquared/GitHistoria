namespace Historia.Tests;

/// <summary>
/// Phase 4 of the v10 graph memory footprint plan: <see cref="GraphBackgroundWriter"/> is the one
/// app-wide serial queue for commit-graph writes and the inactive-tab headless pipeline. These
/// tests use <see cref="RecordingGitCliService"/> so no real <c>git.exe</c> invocation is needed
/// — only that the RIGHT arguments would have been sent, in the right order, without ever
/// touching <c>git maintenance</c> or git config.
/// </summary>
public sealed class GraphBackgroundWriterTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    public GraphBackgroundWriterTests()
    {
        _repo = TestRepositoryBuilder.Create();
        _repo.WriteFile("f.txt", "1\n").Stage().Commit("c1");
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
        await using var ctx = _host.Open(_repo.Path);

        var order = new List<int>();
        var sync = new object();
        var gate1 = new TaskCompletionSource();

        writer.EnqueueInactiveTab(ctx, async ct =>
        {
            lock (sync) order.Add(1);
            await gate1.Task.WaitAsync(ct); // job 1 blocks until released
            lock (sync) order.Add(2);
        });
        writer.EnqueueInactiveTab(ctx, ct =>
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
        await using var ctx = _host.Open(_repo.Path);

        var secondRan = new TaskCompletionSource();
        writer.EnqueueInactiveTab(ctx, _ => throw new InvalidOperationException("boom"));
        writer.EnqueueInactiveTab(ctx, ct => { secondRan.SetResult(); return Task.CompletedTask; });

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

    private sealed class BlockingGate : IBackgroundWorkGate
    {
        private TaskCompletionSource _tcs = new();
        public bool IsOpen => _tcs.Task.IsCompleted;
        public Task WaitUntilOpenAsync(CancellationToken ct = default) => _tcs.Task.WaitAsync(ct);
        public void ReportGraphSettled(RepositoryContext context) { }
        public void Open() => _tcs.TrySetResult();
    }
}
