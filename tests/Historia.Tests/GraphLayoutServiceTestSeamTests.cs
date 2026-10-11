namespace Historia.Tests;

[Trait("tier", "unit")]

public sealed class GraphLayoutServiceTestSeamTests : IDisposable
{
    public void Dispose()
    {
        GraphLayoutService.TestSpillBatchRowsOverride = 0;
        GraphLayoutService.TestSpliceMinRowsOverride = null;
        GraphLayoutService.TestSpliceFullWalkElapsedOverride = null;
    }

    [Fact]
    public async Task Spill_batch_override_is_isolated_between_concurrent_flows()
    {
        using var barrier = new Barrier(2);
        int flow1Observed = -1;
        int flow2Observed = -1;

        var t1 = Task.Run(() =>
        {
            GraphLayoutService.TestSpillBatchRowsOverride = 7;
            barrier.SignalAndWait();
            flow1Observed = GraphLayoutService.TestSpillBatchRowsOverride;
        });

        var t2 = Task.Run(() =>
        {
            GraphLayoutService.TestSpillBatchRowsOverride = 19;
            barrier.SignalAndWait();
            flow2Observed = GraphLayoutService.TestSpillBatchRowsOverride;
        });

        await Task.WhenAll(t1, t2);

        Assert.Equal(7, flow1Observed);
        Assert.Equal(19, flow2Observed);
    }

    [Fact]
    public void Splice_overrides_default_to_the_production_values_when_unset()
    {
        // Unset AsyncLocal yields default(T): 0 for int, null for nullable
        Assert.Equal(0, GraphLayoutService.TestSpillBatchRowsOverride);
        Assert.Null(GraphLayoutService.TestSpliceMinRowsOverride);
        Assert.Null(GraphLayoutService.TestSpliceFullWalkElapsedOverride);

        // Resolves to default production constants when unset:
        var effectiveMinRows = GraphLayoutService.TestSpliceMinRowsOverride ?? GraphMetaSplice.MinRowsToConsider;
        Assert.Equal(GraphMetaSplice.MinRowsToConsider, effectiveMinRows);
    }

    [Fact]
    public async Task Override_is_visible_to_a_fire_and_forget_task_started_after_it_is_set()
    {
        GraphLayoutService.TestSpillBatchRowsOverride = 42;
        var tcs = new TaskCompletionSource<int>();

        // Start task that inherits ExecutionContext
        _ = Task.Run(() =>
        {
            tcs.SetResult(GraphLayoutService.TestSpillBatchRowsOverride);
        });

        var observedInTask = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(42, observedInTask);
    }
}
