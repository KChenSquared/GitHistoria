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

    [WindowsFact]
    public async Task Third_failure_in_a_row_unticks_the_setting()
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

        var stateFile = AppPaths.LogTrimStateFile;
        var store = new LogTrimFailureStore(stateFile);

        using (var lockStream = File.Open(stalePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var trimmer = new LogTrimmer(settings, fakeTime, logsDir, stateFile);

            // Failure 1
            await trimmer.TrimAsync();
            Assert.True(settings.Current.LoggingEnabled);
            Assert.Equal(1, store.Load());

            // Failure 2
            await trimmer.TrimAsync();
            Assert.True(settings.Current.LoggingEnabled);
            Assert.Equal(2, store.Load());

            // Failure 3
            await trimmer.TrimAsync();
            Assert.False(settings.Current.LoggingEnabled);
            Assert.Equal(0, store.Load());
        }

        session.Dispose();
        var logContent = File.ReadAllText(session.SessionPath);
        Assert.Contains("Trimming logs older than 48 hours failed (1 of 3). After 3 failed rounds in a row, the Enable logging setting is turned off.", logContent);
        Assert.Contains("Trimming logs older than 48 hours failed (2 of 3). After 3 failed rounds in a row, the Enable logging setting is turned off.", logContent);
        Assert.Contains("Trimming logs older than 48 hours failed 3 rounds in a row; the Enable logging setting was turned off.", logContent);
    }

    [WindowsFact]
    public async Task Count_persists_across_instances()
    {
        var logsDir = AppPaths.LogsDirectory;
        Directory.CreateDirectory(logsDir);

        LoggingSwitch.Set(true);
        var settings = SettingsService.Load();
        await settings.UpdateAsync(s => s with { LoggingEnabled = true });

        var now = DateTimeOffset.UtcNow;
        var fakeTime = new FakeTimeProvider(now);
        var cutoff = now.DateTime.AddHours(-48);

        var stalePath = Path.Combine(logsDir, "dev-stale-locked.log");
        File.WriteAllText(stalePath, "stale content");
        File.SetLastWriteTime(stalePath, cutoff.AddHours(-2));

        var stateFile = AppPaths.LogTrimStateFile;

        using (var lockStream = File.Open(stalePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var trimmer1 = new LogTrimmer(settings, fakeTime, logsDir, stateFile);
            await trimmer1.TrimAsync();
            await trimmer1.TrimAsync();
            Assert.True(settings.Current.LoggingEnabled);

            var trimmer2 = new LogTrimmer(settings, fakeTime, logsDir, stateFile);
            await trimmer2.TrimAsync();
            Assert.False(settings.Current.LoggingEnabled);
        }
    }

    [WindowsFact]
    public async Task Success_resets_the_count()
    {
        var logsDir = AppPaths.LogsDirectory;
        Directory.CreateDirectory(logsDir);

        LoggingSwitch.Set(true);
        var settings = SettingsService.Load();
        await settings.UpdateAsync(s => s with { LoggingEnabled = true });

        var now = DateTimeOffset.UtcNow;
        var fakeTime = new FakeTimeProvider(now);
        var cutoff = now.DateTime.AddHours(-48);

        var stalePath = Path.Combine(logsDir, "dev-stale-locked.log");
        File.WriteAllText(stalePath, "stale content");
        File.SetLastWriteTime(stalePath, cutoff.AddHours(-2));

        var stateFile = AppPaths.LogTrimStateFile;
        var store = new LogTrimFailureStore(stateFile);
        var trimmer = new LogTrimmer(settings, fakeTime, logsDir, stateFile);

        using (var lockStream = File.Open(stalePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await trimmer.TrimAsync();
            await trimmer.TrimAsync();
        }
        Assert.Equal(2, store.Load());

        // Unlock and succeed
        await trimmer.TrimAsync();
        Assert.Equal(0, store.Load());
        Assert.True(settings.Current.LoggingEnabled);

        // Fail again
        File.WriteAllText(stalePath, "stale content 2");
        File.SetLastWriteTime(stalePath, cutoff.AddHours(-2));
        using (var lockStream = File.Open(stalePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await trimmer.TrimAsync();
        }

        Assert.True(settings.Current.LoggingEnabled);
        Assert.Equal(1, store.Load());
    }

    [WindowsFact]
    public async Task Env_forced_logging_still_unticks_after_three()
    {
        var logsDir = AppPaths.LogsDirectory;
        Directory.CreateDirectory(logsDir);

        Environment.SetEnvironmentVariable("HISTORIA_LOG", "1");
        LoggingSwitch.Initialize(true);
        var settings = SettingsService.Load();
        await settings.UpdateAsync(s => s with { LoggingEnabled = true });

        using var session = AppLog.StartSessionForTesting(logsDir);

        var now = DateTimeOffset.UtcNow;
        var fakeTime = new FakeTimeProvider(now);
        var cutoff = now.DateTime.AddHours(-48);

        var stalePath = Path.Combine(logsDir, "dev-stale-locked.log");
        File.WriteAllText(stalePath, "stale content");
        File.SetLastWriteTime(stalePath, cutoff.AddHours(-2));

        var stateFile = AppPaths.LogTrimStateFile;
        var trimmer = new LogTrimmer(settings, fakeTime, logsDir, stateFile);

        using (var lockStream = File.Open(stalePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await trimmer.TrimAsync();
            await trimmer.TrimAsync();
            await trimmer.TrimAsync();
        }

        Assert.False(settings.Current.LoggingEnabled);
        Assert.True(LoggingSwitch.Enabled);

        session.Dispose();
        var logContent = File.ReadAllText(session.SessionPath);
        var expectedPhrase = "stays on while HISTORIA_LOG is set";
        var firstIdx = logContent.IndexOf(expectedPhrase, StringComparison.Ordinal);
        var lastIdx = logContent.LastIndexOf(expectedPhrase, StringComparison.Ordinal);
        Assert.True(firstIdx >= 0, "Error entry should contain HISTORIA_LOG message");
        Assert.Equal(firstIdx, lastIdx);
    }

    [Fact]
    public async Task Setting_off_resets_the_count_and_never_changes_the_setting()
    {
        var logsDir = AppPaths.LogsDirectory;
        Directory.CreateDirectory(logsDir);

        var stateFile = AppPaths.LogTrimStateFile;
        var store = new LogTrimFailureStore(stateFile);
        store.Save(2);

        LoggingSwitch.Set(false);
        var settings = SettingsService.Load();
        await settings.UpdateAsync(s => s with { LoggingEnabled = false });

        var changedFired = false;
        settings.Changed += (_, _) => changedFired = true;

        var now = DateTimeOffset.UtcNow;
        var fakeTime = new FakeTimeProvider(now);
        var cutoff = now.DateTime.AddHours(-48);

        var stalePath = Path.Combine(logsDir, "dev-stale-locked.log");
        File.WriteAllText(stalePath, "stale content");
        File.SetLastWriteTime(stalePath, cutoff.AddHours(-2));

        using (var lockStream = File.Open(stalePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var trimmer = new LogTrimmer(settings, fakeTime, logsDir, stateFile);
            await trimmer.TrimAsync();
        }

        Assert.Equal(0, store.Load());
        Assert.False(settings.Current.LoggingEnabled);
        Assert.False(changedFired);
    }

    [WindowsFact]
    public async Task Entry_is_written_before_the_setting_flips()
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

        var stateFile = AppPaths.LogTrimStateFile;
        var store = new LogTrimFailureStore(stateFile);
        store.Save(2); // Pre-seed 2 so next failure is the 3rd

        bool? errorPresentAtSettingFlip = null;

        void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
        {
            if (e.OldSettings.LoggingEnabled && !e.NewSettings.LoggingEnabled)
            {
                AppLog.Flush(TimeSpan.FromSeconds(2));
                using var stream = new FileStream(session.SessionPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var content = reader.ReadToEnd();
                errorPresentAtSettingFlip = content.Contains("Trimming logs older than 48 hours failed 3 rounds in a row; the Enable logging setting was turned off.");
            }
        }

        settings.Changed += OnSettingsChanged;
        try
        {
            using (var lockStream = File.Open(stalePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var trimmer = new LogTrimmer(settings, fakeTime, logsDir, stateFile);
                await trimmer.TrimAsync();
            }

            Assert.True(errorPresentAtSettingFlip);
        }
        finally
        {
            settings.Changed -= OnSettingsChanged;
        }
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

        var stateFile = AppPaths.LogTrimStateFile;
        var trimmer = new LogTrimmer(settings, fakeTime, logsDir, stateFile);
        await trimmer.TrimAsync();

        Assert.True(settings.Current.LoggingEnabled);
        Assert.True(File.Exists(freshPath));
        Assert.False(File.Exists(stateFile));
    }
}
