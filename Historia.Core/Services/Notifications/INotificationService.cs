namespace Historia.Services.Notifications;

public enum NotificationSeverity
{
    /// <summary>Transient success toast, auto-dismiss ~3 s.</summary>
    Success,

    /// <summary>Informational.</summary>
    Info,

    /// <summary>Recoverable warning — non-blocking banner in the repo view.</summary>
    Warning,

    /// <summary>Operation failure — inline error card with remedy actions.</summary>
    Error,
}

/// <summary>An optional remedy the user can invoke from an error card or banner (§6.15.1).</summary>
public sealed record NotificationAction(string Label, Func<Task> Invoke);

public sealed record Notification
{
    public required Guid Id { get; init; }
    public required NotificationSeverity Severity { get; init; }
    public required string Message { get; init; }

    /// <summary>Verbatim stderr / stack detail, shown behind a "Details" expander only.</summary>
    public string? Details { get; init; }
    public IReadOnlyList<NotificationAction> Actions { get; init; } = [];
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>How many times this same (severity, message, details) has been published inside
    /// the coalescing window (§18 / v8 M10). 1 for a normal single toast; the UI shows
    /// <c>×N</c> when it is greater.</summary>
    public int Count { get; init; } = 1;
}

public sealed record NotificationsChangedEventArgs(IReadOnlyList<Notification> Current);

/// <summary>
/// Central surface for toasts, banners, and error cards (§6.15.1). Every unhandled exception
/// is routed here rather than reaching the WPF dispatcher (§6.16 criterion 17). Singleton.
/// </summary>
public interface INotificationService
{
    IReadOnlyList<Notification> Current { get; }

    Notification Publish(
        NotificationSeverity severity,
        string message,
        string? details = null,
        IReadOnlyList<NotificationAction>? actions = null);

    void Dismiss(Guid id);

    event EventHandler<NotificationsChangedEventArgs>? Changed;
}
