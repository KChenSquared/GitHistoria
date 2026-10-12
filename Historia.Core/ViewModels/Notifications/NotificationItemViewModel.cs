namespace Historia.ViewModels;

/// <summary>
/// One toast. Identity (id, severity, message, details) is fixed for the toast's lifetime; the
/// host updates the occurrence count and the remedy actions in place when the service coalesces a
/// repeat into it.
/// </summary>
public sealed partial class NotificationItemViewModel : ObservableObject
{
    private readonly INotificationService _notifications;
    private readonly IClipboardService _clipboard;
    private IReadOnlyList<NotificationAction> _sourceActions;

    public NotificationItemViewModel(Notification notification, INotificationService notifications, IClipboardService clipboard)
    {
        ArgumentNullException.ThrowIfNull(notification);
        _notifications = notifications;
        _clipboard = clipboard;
        _sourceActions = notification.Actions;
        Id = notification.Id;
        Severity = notification.Severity;
        Message = notification.Message;
        Details = notification.Details;
        Count = notification.Count;
        Actions = CreateActions(notification.Actions);
    }

    public Guid Id { get; }

    public NotificationSeverity Severity { get; }

    public string Message { get; }

    /// <summary>Verbatim detail text, or null when the notification has none.</summary>
    public string? Details { get; }

    /// <summary>How many times the same notice was published inside the coalescing window.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayMessage))]
    public partial int Count { get; private set; }

    /// <summary>The remedy buttons, in the order the publisher supplied them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActions))]
    public partial IReadOnlyList<NotificationActionViewModel> Actions { get; private set; }

    /// <summary>The message with its occurrence count appended once it has repeated.</summary>
    public string DisplayMessage => Count > 1 ? $"{Message}  ×{Count}" : Message;

    public bool HasDetails => !string.IsNullOrEmpty(Details);

    public bool HasActions => Actions.Count > 0;

    // Success notices are informational only and carry no Copy button.
    public bool ShowCopy => Severity != NotificationSeverity.Success;

    // One flag per severity, so markup picks the card colour with a style class.
    public bool IsError => Severity == NotificationSeverity.Error;

    public bool IsWarning => Severity == NotificationSeverity.Warning;

    public bool IsSuccess => Severity == NotificationSeverity.Success;

    public bool IsInfo => Severity == NotificationSeverity.Info;

    /// <summary>What Copy puts on the clipboard: the message, then the details when there are any.</summary>
    public string CopyText => HasDetails ? $"{Message}\n\n{Details}" : Message;

    /// <summary>Takes the mutable parts of a coalesced repeat of this notification.</summary>
    public void Update(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Count = notification.Count;
        if (!ReferenceEquals(_sourceActions, notification.Actions))
        {
            _sourceActions = notification.Actions;
            Actions = CreateActions(notification.Actions);
        }
    }

    [RelayCommand]
    private void Dismiss() => _notifications.Dismiss(Id);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CopyDetailsAsync()
    {
        try
        {
            await _clipboard.SetTextAsync(CopyText);
        }
        catch (Exception ex)
        {
            // A busy system clipboard is routine; losing a copy must not take the app down.
            AppLog.Write(AppLogLevel.Warn, "notification", "Copying a notification to the clipboard failed.", ex);
        }
    }

    private IReadOnlyList<NotificationActionViewModel> CreateActions(IReadOnlyList<NotificationAction> actions)
        => [.. actions.Select(a => new NotificationActionViewModel(a, _notifications))];
}
