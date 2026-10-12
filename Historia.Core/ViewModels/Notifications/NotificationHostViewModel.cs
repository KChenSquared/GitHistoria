namespace Historia.ViewModels;

/// <summary>
/// The toast stack for <see cref="INotificationService"/>. <see cref="Items"/> mirrors the
/// service's current notifications. With <see cref="AppSettings.AutoHideNotices"/> on, every
/// notice closes itself <see cref="AutoHideAfter"/> after it is first shown (or after the setting
/// is turned on); with it off, notices stay until dismissed.
/// </summary>
public sealed class NotificationHostViewModel : ViewModelBase
{
    public static readonly TimeSpan AutoHideAfter = TimeSpan.FromSeconds(10);

    private readonly INotificationService _notifications;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _ui;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly IClipboardService _clipboard;

    // One armed auto-hide timer per shown notice, so a refresh never stacks timers and turning
    // the setting off can stop them all.
    private readonly Dictionary<Guid, IDisposable> _autoHideTimers = [];

    /// <param name="schedule">Runs an action once after a delay and returns a handle that cancels
    /// it. The real one is a UI timer, so the view supplies it; tests pass a manual one.</param>
    public NotificationHostViewModel(
        INotificationService notifications,
        ISettingsService settings,
        IUiDispatcher ui,
        Func<TimeSpan, Action, IDisposable> schedule,
        IClipboardService? clipboard = null)
    {
        _notifications = notifications;
        _settings = settings;
        _ui = ui;
        _schedule = schedule;
        _clipboard = clipboard ?? NullClipboardService.Instance;

        Refresh(_notifications.Current);
        Subscribe(() => _notifications.Changed += OnNotificationsChanged, () => _notifications.Changed -= OnNotificationsChanged);
        Subscribe(() => _settings.Changed += OnSettingsChanged, () => _settings.Changed -= OnSettingsChanged);
    }

    /// <summary>The toasts on screen, oldest first. At most a handful exist, and an update keeps a
    /// toast's view model, so the list is synced in place rather than replaced.</summary>
    public System.Collections.ObjectModel.ObservableCollection<NotificationItemViewModel> Items { get; } = [];

    /// <summary>Notices with an armed auto-hide timer.</summary>
    public int ArmedAutoHideCount => _autoHideTimers.Count;

    /// <summary>How long a notice stays before closing itself, or null when it stays until dismissed.</summary>
    public static TimeSpan? AutoHideDelay(AppSettings settings) => settings.AutoHideNotices ? AutoHideAfter : null;

    // Both services raise on the thread that changed them; the work hops to the UI thread because
    // it touches bound state.
    private void OnNotificationsChanged(object? sender, NotificationsChangedEventArgs e)
        => _ui.Post(() => Refresh(e.Current));

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.OldSettings.AutoHideNotices != e.NewSettings.AutoHideNotices)
            _ui.Post(() => SyncAutoHide(_notifications.Current));
    }

    private void Refresh(IReadOnlyList<Notification> current)
    {
        if (IsDisposed)
            return;

        SyncItems(current);
        SyncAutoHide(current);
    }

    // Order never changes: the service appends, updates in place and removes. Gone notices drop
    // out, a known one takes its new count, an unknown one is inserted at its position.
    private void SyncItems(IReadOnlyList<Notification> current)
    {
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            var id = Items[i].Id;
            if (current.All(n => n.Id != id))
                Items.RemoveAt(i);
        }

        for (var i = 0; i < current.Count; i++)
        {
            if (i < Items.Count && Items[i].Id == current[i].Id)
                Items[i].Update(current[i]);
            else
                Items.Insert(i, new NotificationItemViewModel(current[i], _notifications, _clipboard));
        }
    }

    private void SyncAutoHide(IReadOnlyList<Notification> current)
    {
        if (IsDisposed)
            return;

        foreach (var id in _autoHideTimers.Keys.Where(id => current.All(n => n.Id != id)).ToList())
        {
            _autoHideTimers[id].Dispose();
            _autoHideTimers.Remove(id);
        }

        if (AutoHideDelay(_settings.Current) is not { } delay)
        {
            foreach (var timer in _autoHideTimers.Values)
                timer.Dispose();
            _autoHideTimers.Clear();
            return;
        }

        foreach (var n in current)
        {
            if (_autoHideTimers.ContainsKey(n.Id))
                continue;
            var id = n.Id;
            _autoHideTimers[id] = _schedule(delay, () =>
            {
                _autoHideTimers.Remove(id);
                _notifications.Dismiss(id);
            });
        }
    }

    protected override void OnDisposed()
    {
        foreach (var timer in _autoHideTimers.Values)
            timer.Dispose();
        _autoHideTimers.Clear();
    }
}
