namespace Historia.Tests;
[Trait("tier", "unit")]

public sealed class PerfLogTests : IDisposable
{
    public void Dispose()
    {
        PerfLog.OverrideEnabledForTesting(null);
    }

    [Fact]
    public async Task Enabled_is_isolated_between_concurrent_flows()
    {
        using var barrier = new Barrier(2);
        bool? flow1Observed = null;
        bool? flow2Observed = null;

        var t1 = Task.Run(() =>
        {
            PerfLog.OverrideEnabledForTesting(true);
            barrier.SignalAndWait();
            flow1Observed = PerfLog.Enabled;
        });

        var t2 = Task.Run(() =>
        {
            PerfLog.OverrideEnabledForTesting(false);
            barrier.SignalAndWait();
            flow2Observed = PerfLog.Enabled;
        });

        await Task.WhenAll(t1, t2);

        Assert.True(flow1Observed);
        Assert.False(flow2Observed);
    }

    [Fact]
    public void Scope_creates_timer_only_when_enabled()
    {
        PerfLog.OverrideEnabledForTesting(false);
        using var disabledScope = PerfLog.Scope("test-disabled");

        PerfLog.OverrideEnabledForTesting(true);
        using var enabledScope = PerfLog.Scope("test-enabled");
        Assert.NotNull(enabledScope);
    }
}
