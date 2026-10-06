namespace HistoriaApp.Tests;

/// <summary>Auto hide notices: on, every notice closes itself after 10 s; off, every notice —
/// success included — stays until dismissed; flipping the setting applies to notices on screen.</summary>
[Trait("tier", "ui")]
public sealed class NotificationAutoHideTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_delay_follows_the_setting(bool on)
    {
        var delay = NotificationHostView.AutoHideDelay(new AppSettings { AutoHideNotices = on });
        Assert.Equal(on ? TimeSpan.FromSeconds(10) : null, delay);
    }

    [Fact]
    public void It_defaults_to_on_including_for_settings_saved_before_it_existed()
    {
        Assert.True(new AppSettings().AutoHideNotices);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"schemaVersion\":22}", HistoriaJson.Options);
        Assert.True(loaded!.AutoHideNotices);
    }

    [AvaloniaFact]
    public void With_it_on_every_severity_closes_after_ten_seconds()
    {
        var notifications = new NotificationService();
        var scheduler = new ManualScheduler();
        using var host = new NotificationHostView(notifications, new SettingsStub(new AppSettings()), scheduler.Schedule);

        notifications.Publish(NotificationSeverity.Error, "e", "details");
        notifications.Publish(NotificationSeverity.Warning, "w");
        notifications.Publish(NotificationSeverity.Info, "i");
        notifications.Publish(NotificationSeverity.Success, "s");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(4, host.ArmedAutoHideCount);
        Assert.All(scheduler.Pending, p => Assert.Equal(TimeSpan.FromSeconds(10), p.Delay));

        scheduler.FireAll();
        Assert.Empty(notifications.Current);
        Assert.Equal(0, host.ArmedAutoHideCount);
    }

    [AvaloniaFact]
    public void With_it_off_success_and_errors_stay()
    {
        var notifications = new NotificationService();
        var scheduler = new ManualScheduler();
        using var host = new NotificationHostView(notifications,
            new SettingsStub(new AppSettings { AutoHideNotices = false }), scheduler.Schedule);

        notifications.Publish(NotificationSeverity.Success, "Pushed to origin.");
        notifications.Publish(NotificationSeverity.Error, "Fetch failed.");
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(scheduler.Pending);
        Assert.Equal(2, notifications.Current.Count);
    }

    [AvaloniaFact]
    public void Turning_it_off_disarms_and_turning_it_on_arms_notices_on_screen()
    {
        var notifications = new NotificationService();
        var scheduler = new ManualScheduler();
        var settings = new SettingsStub(new AppSettings());
        using var host = new NotificationHostView(notifications, settings, scheduler.Schedule);

        notifications.Publish(NotificationSeverity.Error, "Fetch failed.");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, host.ArmedAutoHideCount);

        settings.Set(new AppSettings { AutoHideNotices = false });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, host.ArmedAutoHideCount);
        Assert.True(scheduler.Pending.All(p => p.Cancelled));
        Assert.Single(notifications.Current);

        settings.Set(new AppSettings { AutoHideNotices = true });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, host.ArmedAutoHideCount);

        scheduler.FireAll();
        Assert.Empty(notifications.Current);
    }

    [AvaloniaFact]
    public void A_re_render_never_stacks_a_second_timer()
    {
        var notifications = new NotificationService();
        var scheduler = new ManualScheduler();
        using var host = new NotificationHostView(notifications, new SettingsStub(new AppSettings()), scheduler.Schedule);

        notifications.Publish(NotificationSeverity.Error, "Fetch failed.");
        Dispatcher.UIThread.RunJobs();
        notifications.Publish(NotificationSeverity.Warning, "Another.");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, scheduler.Pending.Count);
    }

    private sealed class ManualScheduler
    {
        public sealed class Entry(TimeSpan delay, Action action) : IDisposable
        {
            public TimeSpan Delay { get; } = delay;
            public Action Action { get; } = action;
            public bool Cancelled { get; private set; }
            public bool Fired { get; set; }
            public void Dispose() => Cancelled = true;
        }

        public List<Entry> Pending { get; } = [];

        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            var e = new Entry(delay, action);
            Pending.Add(e);
            return e;
        }

        public void FireAll()
        {
            foreach (var e in Pending.ToList())
            {
                if (e.Cancelled || e.Fired)
                    continue;
                e.Fired = true;
                e.Action();
            }
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed class SettingsStub(AppSettings initial) : ISettingsService
    {
        public AppSettings Current { get; private set; } = initial;
        public event EventHandler<SettingsChangedEventArgs>? Changed;

        public void Set(AppSettings next)
        {
            var old = Current;
            Current = next;
            Changed?.Invoke(this, new SettingsChangedEventArgs(old, next));
        }

        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default)
        {
            Set(mutate(Current));
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
