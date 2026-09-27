namespace Historia.Services.Notifications;

/// <inheritdoc cref="INotificationService"/>
public sealed class NotificationService : INotificationService
{
    // Toasts are dismissed by hand, so a repeating failure (or the last-chance exception
    // handler firing in a loop) could otherwise stack cards without bound and bury the UI.
    private const int MaxRetained = 6;

    // An identical (severity, message, details) published again within this window
    // does not add a card — it bumps the existing card's timestamp and its occurrence count.
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(4);

    private readonly object _sync = new();
    private readonly Func<DateTimeOffset> _now;
    private ImmutableList<Notification> _current = ImmutableList<Notification>.Empty;

    public NotificationService() : this(null) { }

    /// <param name="clock">Time source for the coalescing window; defaults to
    /// <see cref="DateTimeOffset.Now"/>. Injectable for tests.</param>
    internal NotificationService(Func<DateTimeOffset>? clock) => _now = clock ?? (() => DateTimeOffset.Now);

    public event EventHandler<NotificationsChangedEventArgs>? Changed;

    public IReadOnlyList<Notification> Current
    {
        get { lock (_sync) return _current; }
    }

    public Notification Publish(
        NotificationSeverity severity,
        string message,
        string? details = null,
        IReadOnlyList<NotificationAction>? actions = null)
    {
        var now = _now();
        Notification notification;

        lock (_sync)
        {
            var i = _current.FindLastIndex(n =>
                n.Severity == severity &&
                string.Equals(n.Message, message, StringComparison.Ordinal) &&
                string.Equals(n.Details, details, StringComparison.Ordinal) &&
                now - n.CreatedAt <= CoalesceWindow);

            if (i >= 0)
            {
                var existing = _current[i];
                notification = existing with
                {
                    CreatedAt = now,
                    Count = existing.Count + 1,
                    // Keep the first card's remedy actions unless it had none.
                    Actions = existing.Actions.Count > 0 ? existing.Actions : actions ?? [],
                };
                _current = _current.SetItem(i, notification);
            }
            else
            {
                notification = new Notification
                {
                    Id = Guid.NewGuid(),
                    Severity = severity,
                    Message = message,
                    Details = details,
                    Actions = actions ?? [],
                    CreatedAt = now,
                };
                _current = _current.Add(notification);
                if (_current.Count > MaxRetained)
                    _current = _current.RemoveRange(0, _current.Count - MaxRetained);
            }
        }

        if (severity is NotificationSeverity.Warning or NotificationSeverity.Error)
        {
            AppLog.Write(
                severity == NotificationSeverity.Error ? AppLogLevel.Error : AppLogLevel.Warn,
                "notification",
                string.IsNullOrEmpty(details) ? message : $"{message} — {details}");
        }

        RaiseChanged();
        return notification;
    }

    public void Dismiss(Guid id)
    {
        lock (_sync)
        {
            var next = _current.RemoveAll(n => n.Id == id);
            if (next.Count == _current.Count)
                return;
            _current = next;
        }

        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, new NotificationsChangedEventArgs(Current));
}
