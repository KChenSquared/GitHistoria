namespace Historia.Tests;

/// <summary>
/// §V10.1.2: the repo gate has three priority tiers — foreground (<see cref="RepositoryContext.WithRepositoryAsync{T}"/>),
/// background (<see cref="RepositoryContext.WithRepositoryBackgroundAsync{T}"/>, used by the commit-graph's
/// silent crawl), and spill (<see cref="RepositoryContext.WithRepositorySpillAsync{T}"/>, used only by the
/// §7.6 full-history disk spill). Ensures the spill tier yields to background-tier callers so the background
/// spill does not starve the visible-row crawl.
/// </summary>
[Trait("tier", "git")]
// Serialized: assigns process-global PerfLog.Enabled and asserts on process-global RepositoryContext.FormattedGateMarksCount.
[Collection("serial")]
public sealed class RepositoryContextGateTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    [Fact(Timeout = 20000)]
    public async Task Spill_tier_yields_to_a_background_tier_caller_even_under_a_tight_reacquire_loop()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gate-seed", b =>
        {
            b.WriteFile("a.txt", "x\n").Stage();
            b.Commit("c");
        });
        var ctx = _host.Open(repo.Path);

        using var stopCts = new CancellationTokenSource();

        // Simulate the §7.6 background spill: a tight loop of short spill-tier acquisitions with
        // no yielding delay of its own between them.
        var spillIterations = 0;
        var spillTask = Task.Run(async () =>
        {
            while (!stopCts.IsCancellationRequested)
            {
                try
                {
                    await ctx.WithRepositorySpillAsync(r =>
                    {
                        Interlocked.Increment(ref spillIterations);
                        Thread.Sleep(15); // hold the gate briefly, like a real chunk pull would
                        return 0;
                    }, stopCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
            }
        });

        // Give the spill loop a head start so it is genuinely mid-flight/contending.
        await Task.Delay(150);

        // The crawl's own gate acquisition (background tier) must not be starved by the spill's
        // tight loop — it should get a turn quickly (well under the 3s max-yield the spill is
        // allowed before it forces its own turn regardless).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var backgroundRan = false;
        await ctx.WithRepositoryBackgroundAsync(r =>
        {
            backgroundRan = true;
            return 0;
        }, CancellationToken.None);
        sw.Stop();

        stopCts.Cancel();
        await spillTask;

        Assert.True(backgroundRan);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"background-tier caller waited {sw.Elapsed} behind the spill-tier loop ({spillIterations} spill iterations) — the spill did not yield.");
    }

    [Fact]
    public async Task Gate_records_holder_and_tier_for_each_entry_point()
    {
        var prevEnabled = Historia.Diagnostics.PerfLog.Enabled;
        Historia.Diagnostics.PerfLog.Enabled = true;
        try
        {
            using var repo = TestRepositoryBuilder.FromTemplate("gate-seed", b =>
            {
                b.WriteFile("a.txt", "x\n").Stage();
                b.Commit("c");
            });
            var ctx = _host.Open(repo.Path);

            var startLineCount = File.Exists(Historia.Diagnostics.PerfLog.LogPath)
                ? File.ReadAllLines(Historia.Diagnostics.PerfLog.LogPath).Length
                : 0;

            await ctx.WithRepositoryAsync(r => { Thread.Sleep(15); return 0; });
            await ctx.WithRepositoryBackgroundAsync(r => { Thread.Sleep(15); return 0; });
            await ctx.WithRepositorySpillAsync(r => { Thread.Sleep(15); return 0; });

            var lines = File.ReadAllLines(Historia.Diagnostics.PerfLog.LogPath).Skip(startLineCount).ToList();

            Assert.Contains(lines, l => l.Contains("repo gate hold [foreground]") && l.Contains(nameof(Gate_records_holder_and_tier_for_each_entry_point)));
            Assert.Contains(lines, l => l.Contains("repo gate hold [background]") && l.Contains(nameof(Gate_records_holder_and_tier_for_each_entry_point)));
            Assert.Contains(lines, l => l.Contains("repo gate hold [spill]") && l.Contains(nameof(Gate_records_holder_and_tier_for_each_entry_point)));
        }
        finally
        {
            Historia.Diagnostics.PerfLog.Enabled = prevEnabled;
        }
    }

    [Fact]
    public async Task Gate_records_waited_behind_holder()
    {
        var prevEnabled = Historia.Diagnostics.PerfLog.Enabled;
        Historia.Diagnostics.PerfLog.Enabled = true;
        try
        {
            using var repo = TestRepositoryBuilder.FromTemplate("gate-seed", b =>
            {
                b.WriteFile("a.txt", "x\n").Stage();
                b.Commit("c");
            });
            var ctx = _host.Open(repo.Path);

            var startLineCount = File.Exists(Historia.Diagnostics.PerfLog.LogPath)
                ? File.ReadAllLines(Historia.Diagnostics.PerfLog.LogPath).Length
                : 0;

            var holderTask = ctx.WithRepositoryAsync(r => { Thread.Sleep(30); return 1; });
            await Task.Delay(5);

            var waiterTask = ctx.WithRepositoryAsync(r => 2);
            await Task.WhenAll(holderTask, waiterTask);

            var lines = File.ReadAllLines(Historia.Diagnostics.PerfLog.LogPath).Skip(startLineCount).ToList();
            Assert.Contains(lines, l => l.Contains("repo gate wait") && l.Contains("behind [foreground]"));
        }
        finally
        {
            Historia.Diagnostics.PerfLog.Enabled = prevEnabled;
        }
    }

    [Fact]
    public async Task Gate_marks_are_not_formatted_when_perf_logging_is_disabled()
    {
        var prevEnabled = Historia.Diagnostics.PerfLog.Enabled;
        Historia.Diagnostics.PerfLog.Enabled = false;
        try
        {
            using var repo = TestRepositoryBuilder.FromTemplate("gate-seed", b =>
            {
                b.WriteFile("a.txt", "x\n").Stage();
                b.Commit("c");
            });
            var ctx = _host.Open(repo.Path);

            var startCount = Volatile.Read(ref RepositoryContext.FormattedGateMarksCount);
            var startLogSize = File.Exists(Historia.Diagnostics.PerfLog.LogPath)
                ? new FileInfo(Historia.Diagnostics.PerfLog.LogPath).Length
                : 0;

            await ctx.WithRepositoryAsync(r => { Thread.Sleep(15); return 0; });
            await ctx.WithRepositoryBackgroundAsync(r => { Thread.Sleep(15); return 0; });
            await ctx.WithRepositorySpillAsync(r => { Thread.Sleep(15); return 0; });

            var endCount = Volatile.Read(ref RepositoryContext.FormattedGateMarksCount);
            var endLogSize = File.Exists(Historia.Diagnostics.PerfLog.LogPath)
                ? new FileInfo(Historia.Diagnostics.PerfLog.LogPath).Length
                : 0;

            Assert.Equal(startCount, endCount);
            Assert.Equal(startLogSize, endLogSize);
        }
        finally
        {
            Historia.Diagnostics.PerfLog.Enabled = prevEnabled;
        }
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
