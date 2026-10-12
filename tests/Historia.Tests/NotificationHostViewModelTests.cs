namespace Historia.Tests;

/// <summary>
/// The toast stack's view model: its items mirror the notification service, auto-hide follows the
/// setting through a scheduler the test controls, and the item commands reach the service and the
/// clipboard.
/// </summary>
[Trait("tier", "unit")]
public class NotificationHostViewModelTests
{
    private sealed class ManualScheduler
    {
        public sealed class Entry(TimeSpan delay, Action action) : IDisposable
        {
            public TimeSpan Delay { get; } = delay;
            public bool Cancelled { get; private set; }
            public bool Fired { get; private set; }

            public void Fire()
            {
                Fired = true;
                action();
            }

            public void Dispose() => Cancelled = true;
        }

        public List<Entry> Entries { get; } = [];

        public IEnumerable<Entry> Armed => Entries.Where(e => !e.Cancelled && !e.Fired);

        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            var entry = new Entry(delay, action);
            Entries.Add(entry);
            return entry;
        }

        public void FireAll()
        {
            foreach (var entry in Armed.ToList())
                entry.Fire();
        }
    }

    private sealed class Rig
    {
        public Rig(AppSettings? settings = null, IUiDispatcher? ui = null, bool seed = false)
        {
            Settings = new FakeSettingsService(settings);
            Notifications = new NotificationService();
            Clipboard = new FakeClipboardService();
            if (seed)
                Notifications.Publish(NotificationSeverity.Info, "already here");
            Host = new NotificationHostViewModel(Notifications, Settings, ui ?? new ImmediateUiDispatcher(), Scheduler.Schedule, Clipboard);
        }

        public FakeSettingsService Settings { get; }
        public NotificationService Notifications { get; }
        public FakeClipboardService Clipboard { get; }
        public ManualScheduler Scheduler { get; } = new();
        public NotificationHostViewModel Host { get; }
    }

    private static Task SetAutoHide(Rig rig, bool on)
        => rig.Settings.UpdateAsync(s => s with { AutoHideNotices = on });

    // ── Items ────────────────────────────────────────────────────────────────

    [Fact]
    public void Items_start_with_the_notifications_already_published()
    {
        var rig = new Rig(seed: true);

        var item = Assert.Single(rig.Host.Items);
        Assert.Equal("already here", item.Message);
    }

    [Fact]
    public void Items_follow_publish_and_dismiss_in_order()
    {
        var rig = new Rig();

        var first = rig.Notifications.Publish(NotificationSeverity.Info, "one");
        var second = rig.Notifications.Publish(NotificationSeverity.Warning, "two");
        var third = rig.Notifications.Publish(NotificationSeverity.Error, "three");
        Assert.Equal([first.Id, second.Id, third.Id], rig.Host.Items.Select(i => i.Id));

        rig.Notifications.Dismiss(second.Id);
        Assert.Equal([first.Id, third.Id], rig.Host.Items.Select(i => i.Id));
    }

    [Fact]
    public void Items_drop_the_oldest_when_the_service_caps_what_it_keeps()
    {
        var rig = new Rig();
        for (var i = 0; i < 8; i++)
            rig.Notifications.Publish(NotificationSeverity.Info, $"notice {i}");

        Assert.Equal(rig.Notifications.Current.Select(n => n.Id), rig.Host.Items.Select(i => i.Id));
        Assert.Equal("notice 2", rig.Host.Items[0].Message);
    }

    [Fact]
    public void A_repeat_inside_the_coalescing_window_updates_the_same_item()
    {
        var rig = new Rig();
        rig.Notifications.Publish(NotificationSeverity.Error, "Fetch failed.", "details");
        var item = Assert.Single(rig.Host.Items);
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        rig.Notifications.Publish(NotificationSeverity.Error, "Fetch failed.", "details");

        Assert.Same(item, Assert.Single(rig.Host.Items));
        Assert.Equal(2, item.Count);
        Assert.Equal("Fetch failed.  ×2", item.DisplayMessage);
        Assert.Contains(nameof(NotificationItemViewModel.Count), changed);
        Assert.Contains(nameof(NotificationItemViewModel.DisplayMessage), changed);
    }

    [Fact]
    public void A_repeat_that_brings_the_first_remedy_actions_replaces_them()
    {
        var rig = new Rig();
        rig.Notifications.Publish(NotificationSeverity.Error, "Push rejected.");
        var item = Assert.Single(rig.Host.Items);
        Assert.False(item.HasActions);
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        rig.Notifications.Publish(NotificationSeverity.Error, "Push rejected.", actions: [new NotificationAction("Pull", () => Task.CompletedTask)]);

        Assert.True(item.HasActions);
        Assert.Equal("Pull", Assert.Single(item.Actions).Label);
        Assert.Contains(nameof(NotificationItemViewModel.HasActions), changed);
    }

    [Fact]
    public void Service_changes_reach_Items_only_through_the_dispatcher()
    {
        var ui = new RecordingUiDispatcher();
        var rig = new Rig(ui: ui);

        rig.Notifications.Publish(NotificationSeverity.Info, "queued");
        Assert.Empty(rig.Host.Items);

        ui.DrainPosted();
        Assert.Single(rig.Host.Items);
    }

    // ── Item shape ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(NotificationSeverity.Error, true, false, false, false, true)]
    [InlineData(NotificationSeverity.Warning, false, true, false, false, true)]
    [InlineData(NotificationSeverity.Success, false, false, true, false, false)]
    [InlineData(NotificationSeverity.Info, false, false, false, true, true)]
    public void Each_item_flags_exactly_its_severity_and_success_has_no_copy(
        NotificationSeverity severity, bool error, bool warning, bool success, bool info, bool showCopy)
    {
        var rig = new Rig();
        rig.Notifications.Publish(severity, "message");

        var item = Assert.Single(rig.Host.Items);
        Assert.Equal((error, warning, success, info, showCopy), (item.IsError, item.IsWarning, item.IsSuccess, item.IsInfo, item.ShowCopy));
    }

    [Fact]
    public void Details_and_actions_are_exposed_when_present()
    {
        var rig = new Rig();
        rig.Notifications.Publish(NotificationSeverity.Error, "Boom.", "stderr text",
            [new NotificationAction("Retry", () => Task.CompletedTask), new NotificationAction("Open", () => Task.CompletedTask)]);
        rig.Notifications.Publish(NotificationSeverity.Info, "Plain.");

        var withDetails = rig.Host.Items[0];
        Assert.True(withDetails.HasDetails);
        Assert.Equal("stderr text", withDetails.Details);
        Assert.Equal(["Retry", "Open"], withDetails.Actions.Select(a => a.Label));

        var plain = rig.Host.Items[1];
        Assert.False(plain.HasDetails);
        Assert.False(plain.HasActions);
        Assert.Equal("Plain.", plain.DisplayMessage);
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    [Fact]
    public void Dismiss_removes_the_notification_from_the_service()
    {
        var rig = new Rig();
        rig.Notifications.Publish(NotificationSeverity.Info, "bye");

        rig.Host.Items[0].DismissCommand.Execute(null);

        Assert.Empty(rig.Notifications.Current);
        Assert.Empty(rig.Host.Items);
    }

    [Fact]
    public async Task CopyDetails_puts_the_message_then_the_details_on_the_clipboard()
    {
        var rig = new Rig();
        rig.Notifications.Publish(NotificationSeverity.Error, "Fetch failed.", "fatal: no remote");

        await rig.Host.Items[0].CopyDetailsCommand.ExecuteAsync(null);

        Assert.Equal(["Fetch failed.\n\nfatal: no remote"], rig.Clipboard.Copied);
    }

    [Fact]
    public async Task CopyDetails_copies_just_the_message_when_there_are_no_details()
    {
        var rig = new Rig();
        rig.Notifications.Publish(NotificationSeverity.Warning, "Careful.");

        await rig.Host.Items[0].CopyDetailsCommand.ExecuteAsync(null);

        Assert.Equal("Careful.", rig.Clipboard.Last);
    }

    [Fact]
    public async Task CopyDetails_without_a_clipboard_service_does_nothing()
    {
        var notifications = new NotificationService();
        notifications.Publish(NotificationSeverity.Info, "x");
        var host = new NotificationHostViewModel(notifications, new FakeSettingsService(), new ImmediateUiDispatcher(), new ManualScheduler().Schedule);

        await host.Items[0].CopyDetailsCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task A_clipboard_that_throws_does_not_escape_the_command()
    {
        var notifications = new NotificationService();
        notifications.Publish(NotificationSeverity.Info, "x");
        var host = new NotificationHostViewModel(notifications, new FakeSettingsService(), new ImmediateUiDispatcher(),
            new ManualScheduler().Schedule, new ThrowingClipboard());

        await host.Items[0].CopyDetailsCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task A_remedy_action_runs_when_its_button_is_pressed()
    {
        var rig = new Rig();
        var runs = 0;
        rig.Notifications.Publish(NotificationSeverity.Error, "Push rejected.", actions: [new NotificationAction("Pull", () => { runs++; return Task.CompletedTask; })]);

        await rig.Host.Items[0].Actions[0].RunCommand.ExecuteAsync(null);

        Assert.Equal(1, runs);
        Assert.Single(rig.Notifications.Current);
    }

    [Fact]
    public async Task A_remedy_action_that_throws_is_reported_as_an_error_notice()
    {
        var rig = new Rig();
        var failure = new InvalidOperationException("no upstream");
        rig.Notifications.Publish(NotificationSeverity.Error, "Push rejected.", actions: [new NotificationAction("Pull", () => throw failure)]);

        await rig.Host.Items[0].Actions[0].RunCommand.ExecuteAsync(null);

        Assert.Equal(2, rig.Host.Items.Count);
        var reported = rig.Host.Items[1];
        Assert.True(reported.IsError);
        Assert.Equal(failure.NotificationMessage("The action"), reported.Message);
        Assert.Equal(failure.NotificationDetails(), reported.Details);
    }

    // ── Auto-hide ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_delay_follows_the_setting(bool on)
    {
        var delay = NotificationHostViewModel.AutoHideDelay(new AppSettings { AutoHideNotices = on });

        Assert.Equal(on ? TimeSpan.FromSeconds(10) : null, delay);
    }

    [Fact]
    public void With_it_on_every_severity_arms_a_ten_second_timer_and_closes_when_it_fires()
    {
        var rig = new Rig();

        rig.Notifications.Publish(NotificationSeverity.Error, "e", "details");
        rig.Notifications.Publish(NotificationSeverity.Warning, "w");
        rig.Notifications.Publish(NotificationSeverity.Info, "i");
        rig.Notifications.Publish(NotificationSeverity.Success, "s");

        Assert.Equal(4, rig.Host.ArmedAutoHideCount);
        Assert.All(rig.Scheduler.Armed, e => Assert.Equal(TimeSpan.FromSeconds(10), e.Delay));

        rig.Scheduler.FireAll();

        Assert.Empty(rig.Notifications.Current);
        Assert.Empty(rig.Host.Items);
        Assert.Equal(0, rig.Host.ArmedAutoHideCount);
    }

    [Fact]
    public void Notices_present_when_the_view_model_is_created_are_armed()
    {
        var rig = new Rig(seed: true);

        Assert.Equal(1, rig.Host.ArmedAutoHideCount);
    }

    [Fact]
    public void With_it_off_nothing_is_armed_and_everything_stays()
    {
        var rig = new Rig(new AppSettings { AutoHideNotices = false });

        rig.Notifications.Publish(NotificationSeverity.Success, "Pushed to origin.");
        rig.Notifications.Publish(NotificationSeverity.Error, "Fetch failed.");

        Assert.Empty(rig.Scheduler.Entries);
        Assert.Equal(2, rig.Host.Items.Count);
    }

    [Fact]
    public async Task Turning_it_off_disarms_and_turning_it_on_arms_notices_on_screen()
    {
        var rig = new Rig();
        rig.Notifications.Publish(NotificationSeverity.Error, "Fetch failed.");
        Assert.Equal(1, rig.Host.ArmedAutoHideCount);

        await SetAutoHide(rig, false);
        Assert.Equal(0, rig.Host.ArmedAutoHideCount);
        Assert.All(rig.Scheduler.Entries, e => Assert.True(e.Cancelled));
        Assert.Single(rig.Host.Items);

        await SetAutoHide(rig, true);
        Assert.Equal(1, rig.Host.ArmedAutoHideCount);

        rig.Scheduler.FireAll();
        Assert.Empty(rig.Host.Items);
    }

    [Fact]
    public async Task A_setting_other_than_auto_hide_leaves_the_timers_alone()
    {
        var rig = new Rig();
        rig.Notifications.Publish(NotificationSeverity.Error, "Fetch failed.");

        await rig.Settings.UpdateAsync(s => s with { ShowAuthorDate = !s.ShowAuthorDate });

        Assert.Single(rig.Scheduler.Entries);
        Assert.Equal(1, rig.Host.ArmedAutoHideCount);
    }

    [Fact]
    public void A_refresh_never_stacks_a_second_timer_on_a_notice()
    {
        var rig = new Rig();

        rig.Notifications.Publish(NotificationSeverity.Error, "Fetch failed.");
        rig.Notifications.Publish(NotificationSeverity.Warning, "Another.");
        rig.Notifications.Publish(NotificationSeverity.Warning, "Another.");

        Assert.Equal(2, rig.Scheduler.Entries.Count);
    }

    [Fact]
    public void A_notice_dismissed_by_hand_releases_its_timer()
    {
        var rig = new Rig();
        var notice = rig.Notifications.Publish(NotificationSeverity.Error, "Fetch failed.");

        rig.Notifications.Dismiss(notice.Id);

        Assert.Equal(0, rig.Host.ArmedAutoHideCount);
        Assert.True(Assert.Single(rig.Scheduler.Entries).Cancelled);
    }

    // ── Dispose ──────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_cancels_every_timer_and_stops_following_the_services()
    {
        var rig = new Rig();
        rig.Notifications.Publish(NotificationSeverity.Error, "Fetch failed.");

        rig.Host.Dispose();

        Assert.Equal(0, rig.Host.ArmedAutoHideCount);
        Assert.True(Assert.Single(rig.Scheduler.Entries).Cancelled);

        rig.Notifications.Publish(NotificationSeverity.Info, "after dispose");
        Assert.Single(rig.Host.Items);
        Assert.Single(rig.Scheduler.Entries);
    }

    [Fact]
    public void A_post_that_lands_after_dispose_does_nothing()
    {
        var ui = new RecordingUiDispatcher();
        var rig = new Rig(ui: ui);
        rig.Notifications.Publish(NotificationSeverity.Info, "queued");

        rig.Host.Dispose();
        ui.DrainPosted();

        Assert.Empty(rig.Host.Items);
        Assert.Empty(rig.Scheduler.Entries);
    }

    private sealed class ThrowingClipboard : IClipboardService
    {
        public Task SetTextAsync(string text) => throw new InvalidOperationException("clipboard busy");
    }
}
