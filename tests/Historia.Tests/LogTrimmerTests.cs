namespace Historia.Tests;

[Trait("tier", "unit")]
[Collection("serial")]
public sealed class LogTrimmerTests : IDisposable
{
    private readonly StateDir _state = new();

    public LogTrimmerTests()
    {
        Environment.SetEnvironmentVariable("HISTORIA_LOG", null);
        LoggingSwitch.OverrideForTesting(null);
        LoggingSwitch.Set(false);
    }

    public void Dispose()
    {
        AppLog.ResetForTesting();
        LoggingSwitch.OverrideForTesting(null);
        LoggingSwitch.Set(false);
        Environment.SetEnvironmentVariable("HISTORIA_LOG", null);
        _state.Dispose();
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public async Task Stale_locked_file_writes_error_and_disables_logging_when_enabled()
    {
        var logsDir = AppPaths.LogsDirectory;
        Directory.CreateDirectory(logsDir);

        LoggingSwitch.Set(true);
        var settings = SettingsService.Load();
        await settings.UpdateAsync(s => s with { LoggingEnabled = true });

        using var session = AppLog.StartSessionForTesting(logsDir);

        var now = DateTimeOffset.UtcNow;
        var fakeTime = new FakeTimeProvider(now);
        var cutoff = now.DateTime.AddHours(-48);

        var stalePath = Path.Combine(logsDir, "dev-stale-locked.log");
        File.WriteAllText(stalePath, "stale content");
        File.SetLastWriteTime(stalePath, cutoff.AddHours(-2));

        using (var lockStream = File.Open(stalePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var trimmer = new LogTrimmer(settings, fakeTime, logsDir);
            await trimmer.TrimAsync();
        }

        Assert.False(settings.Current.LoggingEnabled);

        session.Dispose();
        var logContent = File.ReadAllText(session.SessionPath);
        Assert.Contains("Trimming logs older than 48 hours failed; logging was automatically disabled.", logContent);
    }

    [Fact]
    public async Task With_logging_disabled_failure_writes_nothing_and_setting_unchanged()
    {
        var logsDir = AppPaths.LogsDirectory;
        Directory.CreateDirectory(logsDir);

        LoggingSwitch.Set(false);
        var settings = SettingsService.Load();
        await settings.UpdateAsync(s => s with { LoggingEnabled = false });

        var now = DateTimeOffset.UtcNow;
        var fakeTime = new FakeTimeProvider(now);
        var cutoff = now.DateTime.AddHours(-48);

        var stalePath = Path.Combine(logsDir, "dev-stale-locked.log");
        File.WriteAllText(stalePath, "stale content");
        File.SetLastWriteTime(stalePath, cutoff.AddHours(-2));

        using (var lockStream = File.Open(stalePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var trimmer = new LogTrimmer(settings, fakeTime, logsDir);
            await trimmer.TrimAsync();
        }

        Assert.False(settings.Current.LoggingEnabled);
        Assert.Null(AppLog.CurrentLogPath);
    }

    [Fact]
    public async Task With_nothing_stale_trimmer_does_nothing_and_keeps_setting()
    {
        var logsDir = AppPaths.LogsDirectory;
        Directory.CreateDirectory(logsDir);

        LoggingSwitch.Set(true);
        var settings = SettingsService.Load();
        await settings.UpdateAsync(s => s with { LoggingEnabled = true });

        var now = DateTimeOffset.UtcNow;
        var fakeTime = new FakeTimeProvider(now);

        var freshPath = Path.Combine(logsDir, "dev-fresh.log");
        File.WriteAllText(freshPath, "fresh content");
        File.SetLastWriteTime(freshPath, now.DateTime.AddHours(-1));

        var trimmer = new LogTrimmer(settings, fakeTime, logsDir);
        await trimmer.TrimAsync();

        Assert.True(settings.Current.LoggingEnabled);
        Assert.True(File.Exists(freshPath));
    }
}
