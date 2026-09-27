namespace Historia.Tests;

public sealed class GraphPrepareGateTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact(Timeout = 10000)]
    public async Task Foreground_beats_background_crawl_and_spill()
    {
        var gate = new GraphPrepareGate("test");
        var holdTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holdTask = gate.ExecuteAsync(async () =>
        {
            holdEntered.SetResult();
            await holdTcs.Task;
        });
        await holdEntered.Task;

        var executionOrder = new List<string>();

        var spillStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spillTask = Task.Run(async () =>
        {
            spillStarted.SetResult();
            await gate.ExecuteSpillAsync(() =>
            {
                lock (executionOrder) executionOrder.Add("spill");
            });
        });
        await spillStarted.Task;

        var bgStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bgTask = Task.Run(async () =>
        {
            bgStarted.SetResult();
            await gate.ExecuteBackgroundAsync(() =>
            {
                lock (executionOrder) executionOrder.Add("background");
            });
        });
        await bgStarted.Task;

        await Task.Delay(50);

        var fgStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fgTask = Task.Run(async () =>
        {
            fgStarted.SetResult();
            await gate.ExecuteAsync(() =>
            {
                lock (executionOrder) executionOrder.Add("foreground");
            });
        });
        await fgStarted.Task;
        await Task.Delay(50);

        holdTcs.SetResult();
        await Task.WhenAll(holdTask, fgTask, bgTask, spillTask);

        Assert.Equal(["foreground", "background", "spill"], executionOrder);
    }

    [Fact(Timeout = 10000)]
    public async Task Background_crawl_yields_to_queued_foreground()
    {
        var gate = new GraphPrepareGate("test");
        var holdTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holdTask = gate.ExecuteAsync(async () =>
        {
            holdEntered.SetResult();
            await holdTcs.Task;
        });
        await holdEntered.Task;

        var order = new List<string>();

        var bgTask = Task.Run(async () =>
        {
            await gate.ExecuteBackgroundAsync(() =>
            {
                lock (order) order.Add("background");
            });
        });

        await Task.Delay(50);

        var fgTask = Task.Run(async () =>
        {
            await gate.ExecuteAsync(() =>
            {
                lock (order) order.Add("foreground");
            });
        });

        await Task.Delay(50);

        holdTcs.SetResult();
        await Task.WhenAll(holdTask, fgTask, bgTask);

        Assert.Equal(["foreground", "background"], order);
    }

    [Fact(Timeout = 10000)]
    public async Task Spill_yields_to_both_foreground_and_crawl()
    {
        var gate = new GraphPrepareGate("test");
        var holdTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holdTask = gate.ExecuteAsync(async () =>
        {
            holdEntered.SetResult();
            await holdTcs.Task;
        });
        await holdEntered.Task;

        var order = new List<string>();

        var spillTask = Task.Run(async () =>
        {
            await gate.ExecuteSpillAsync(() =>
            {
                lock (order) order.Add("spill");
            });
        });

        await Task.Delay(40);

        var bgTask = Task.Run(async () =>
        {
            await gate.ExecuteBackgroundAsync(() =>
            {
                lock (order) order.Add("background");
            });
        });

        await Task.Delay(40);

        var fgTask = Task.Run(async () =>
        {
            await gate.ExecuteAsync(() =>
            {
                lock (order) order.Add("foreground");
            });
        });

        await Task.Delay(40);

        holdTcs.SetResult();
        await Task.WhenAll(holdTask, fgTask, bgTask, spillTask);

        Assert.Equal(["foreground", "background", "spill"], order);
    }

    [Fact(Timeout = 10000)]
    public async Task Max_yield_bound_does_not_starve_forever()
    {
        var gate = new GraphPrepareGate("test")
        {
            MaxYield = TimeSpan.FromMilliseconds(80)
        };

        var holdTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holdTask = gate.ExecuteAsync(async () =>
        {
            holdEntered.SetResult();
            await holdTcs.Task;
        });
        await holdEntered.Task;

        var spillRan = false;
        var spillTask = Task.Run(async () =>
        {
            await gate.ExecuteSpillAsync(() =>
            {
                spillRan = true;
            });
        });

        await Task.Delay(30);

        var fgHoldTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fgEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fgTask = Task.Run(async () =>
        {
            await gate.ExecuteAsync(async () =>
            {
                fgEntered.SetResult();
                await fgHoldTcs.Task;
            });
        });

        holdTcs.SetResult();
        await fgEntered.Task;

        var fgWaiterCts = new CancellationTokenSource();
        var fg2Task = Task.Run(async () =>
        {
            try
            {
                await gate.ExecuteAsync(async () =>
                {
                    await Task.Delay(1000, fgWaiterCts.Token);
                }, fgWaiterCts.Token);
            }
            catch (OperationCanceledException) { }
        });

        await Task.Delay(150);

        fgHoldTcs.SetResult();
        fgWaiterCts.Cancel();
        await spillTask;
        Assert.True(spillRan);
        await fgTask;
        await fg2Task;
    }

    [Fact(Timeout = 10000)]
    public async Task Cancellation_releases_the_gate_slot()
    {
        var gate = new GraphPrepareGate("test");
        var holdTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holdTask = gate.ExecuteAsync(async () =>
        {
            holdEntered.SetResult();
            await holdTcs.Task;
        });
        await holdEntered.Task;

        using var cts = new CancellationTokenSource();
        var canceledTask = Task.Run(async () =>
        {
            await gate.ExecuteAsync(() => 42, cts.Token);
        });

        await Task.Delay(50);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledTask);

        holdTcs.SetResult();
        await holdTask;

        var result = await gate.ExecuteAsync(() => 123);
        Assert.Equal(123, result);
    }

    [Fact]
    public void Replaced_RepoCache_on_the_same_context_shares_the_gate()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gate-share", b =>
        {
            b.WriteFile("f.txt", "1\n").Stage();
            b.Commit("c1");
        });
        var ctx = _host.Open(repo.Path);

        var gate1 = GraphLayoutService.GetGateFor(ctx);
        var gate2 = GraphLayoutService.GetGateFor(ctx);

        Assert.NotNull(gate1);
        Assert.Same(gate1, gate2);
    }
}
