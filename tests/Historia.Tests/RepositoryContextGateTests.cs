namespace Historia.Tests;

/// <summary>
/// §V10.1.2: the repo gate has three priority tiers — foreground (<see cref="RepositoryContext.WithRepositoryAsync{T}"/>),
/// background (<see cref="RepositoryContext.WithRepositoryBackgroundAsync{T}"/>, used by the commit-graph's
/// silent crawl), and spill (<see cref="RepositoryContext.WithRepositorySpillAsync{T}"/>, used only by the
/// §7.6 full-history disk spill). Ensures the spill tier yields to background-tier callers so the background
/// spill does not starve the visible-row crawl.
/// </summary>
public sealed class RepositoryContextGateTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    [Fact(Timeout = 20000)]
    public async Task Spill_tier_yields_to_a_background_tier_caller_even_under_a_tight_reacquire_loop()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("a.txt", "x\n").Stage();
        repo.Commit("c");
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

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
