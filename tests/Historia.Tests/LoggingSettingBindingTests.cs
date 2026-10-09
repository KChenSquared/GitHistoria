namespace Historia.Tests;

[Trait("tier", "unit")]
[Collection("serial")]
public sealed class LoggingSettingBindingTests : IDisposable
{
    private readonly StateDir _dir = new();

    public LoggingSettingBindingTests()
    {
        Environment.SetEnvironmentVariable("HISTORIA_LOG", null);
        LoggingSwitch.OverrideForTesting(null);
        LoggingSwitch.Set(false);
        AppLog.ResetForTesting();
    }

    public void Dispose()
    {
        LoggingSwitch.OverrideForTesting(null);
        LoggingSwitch.Set(false);
        Environment.SetEnvironmentVariable("HISTORIA_LOG", null);
        AppLog.ResetForTesting();
        _dir.Dispose();
    }

    [Fact]
    public async Task UpdateAsync_returns_while_the_switch_handler_is_blocked()
    {
        var settings = SettingsService.Load();
        await settings.UpdateAsync(s => s with { LoggingEnabled = false });
        LoggingSwitch.Set(false);

        using var blockGate = new ManualResetEventSlim(false);
        using var enteredHandler = new ManualResetEventSlim(false);

        void BlockingHandler(bool _)
        {
            enteredHandler.Set();
            blockGate.Wait(TimeSpan.FromSeconds(5));
        }

        LoggingSwitch.Changed += BlockingHandler;
        try
        {
            using var binding = LoggingSettingBinding.Attach(settings);

            var updateTask = settings.UpdateAsync(s => s with { LoggingEnabled = true });
            var completedInTime = await Task.WhenAny(updateTask, Task.Delay(1000)) == updateTask;

            Assert.True(completedInTime, "UpdateAsync should complete off-thread without waiting for the switch handler");
            await updateTask;

            Assert.True(enteredHandler.Wait(TimeSpan.FromSeconds(2)), "Changed handler should be reached");
            blockGate.Set();

            var deadline = Stopwatch.StartNew();
            while (!LoggingSwitch.Enabled && deadline.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(10);

            Assert.True(LoggingSwitch.Enabled);
        }
        finally
        {
            LoggingSwitch.Changed -= BlockingHandler;
            blockGate.Set();
        }
    }

    [Fact]
    public async Task Rapid_toggles_settle_on_the_final_setting()
    {
        var settings = SettingsService.Load();
        await settings.UpdateAsync(s => s with { LoggingEnabled = false });
        LoggingSwitch.Set(false);

        using var binding = LoggingSettingBinding.Attach(settings);

        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            await settings.UpdateAsync(s => s with { LoggingEnabled = !s.LoggingEnabled });
        })).ToArray();

        await Task.WhenAll(tasks);

        var expected = settings.Current.LoggingEnabled;
        var deadline = Stopwatch.StartNew();
        while (LoggingSwitch.Enabled != expected && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(10);
        }

        Assert.Equal(expected, LoggingSwitch.Enabled);
    }

    [Fact]
    public async Task Dispose_stops_following_the_setting()
    {
        var settings = SettingsService.Load();
        await settings.UpdateAsync(s => s with { LoggingEnabled = false });
        LoggingSwitch.Set(false);

        var binding = LoggingSettingBinding.Attach(settings);
        binding.Dispose();

        await settings.UpdateAsync(s => s with { LoggingEnabled = true });
        await Task.Delay(100);

        Assert.False(LoggingSwitch.Enabled);
    }
}
