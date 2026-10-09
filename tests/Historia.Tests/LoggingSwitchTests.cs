namespace Historia.Tests;

[Trait("tier", "unit")]
[Collection("serial")]
public sealed class LoggingSwitchTests : IDisposable
{
    public LoggingSwitchTests()
    {
        Environment.SetEnvironmentVariable("HISTORIA_LOG", null);
        LoggingSwitch.OverrideForTesting(null);
        LoggingSwitch.Set(false);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HISTORIA_LOG", null);
        LoggingSwitch.OverrideForTesting(null);
        LoggingSwitch.Set(false);
    }

    [Fact]
    public void Starts_disabled()
    {
        Assert.False(TestStateIsolation.LoggingSwitchEnabledAtStartup);
        Assert.False(LoggingSwitch.Enabled);
    }

    [Fact]
    public void Initialize_enables()
    {
        LoggingSwitch.Set(false);
        LoggingSwitch.Initialize(true);
        Assert.True(LoggingSwitch.Enabled);
    }

    [Fact]
    public void Env_var_forces_on_when_setting_is_false_and_set_false_does_not_turn_it_off()
    {
        LoggingSwitch.Set(false);
        Environment.SetEnvironmentVariable("HISTORIA_LOG", "1");
        try
        {
            LoggingSwitch.Initialize(false);
            Assert.True(LoggingSwitch.Enabled);

            LoggingSwitch.Set(false);
            Assert.True(LoggingSwitch.Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HISTORIA_LOG", null);
            LoggingSwitch.Set(false);
        }
    }

    [Fact]
    public void Changed_fires_once_per_flip()
    {
        LoggingSwitch.Set(false);
        var history = new List<bool>();
        Action<bool> handler = val => history.Add(val);
        LoggingSwitch.Changed += handler;
        try
        {
            LoggingSwitch.Set(true);
            LoggingSwitch.Set(true);
            LoggingSwitch.Set(false);
            LoggingSwitch.Set(false);
            Assert.Equal([true, false], history);
        }
        finally
        {
            LoggingSwitch.Changed -= handler;
            LoggingSwitch.Set(false);
        }
    }

    [Fact]
    public async Task Flow_local_override_does_not_leak_to_sibling_async_flow()
    {
        using var barrier = new Barrier(2);
        bool? flow1Observed = null;
        bool? flow2Observed = null;

        var t1 = Task.Run(() =>
        {
            LoggingSwitch.OverrideForTesting(true);
            barrier.SignalAndWait();
            flow1Observed = LoggingSwitch.Enabled;
        });

        var t2 = Task.Run(() =>
        {
            LoggingSwitch.OverrideForTesting(false);
            barrier.SignalAndWait();
            flow2Observed = LoggingSwitch.Enabled;
        });

        await Task.WhenAll(t1, t2);

        Assert.True(flow1Observed);
        Assert.False(flow2Observed);
    }
}
