namespace Historia.ViewModels;

/// <summary>
/// One remedy button on a notification. A remedy that throws is reported as an error notification
/// instead of escaping into the UI.
/// </summary>
public sealed partial class NotificationActionViewModel(NotificationAction action, INotificationService notifications)
{
    public string Label => action.Label;

    // Concurrent runs stay allowed so a slow remedy never greys its own button out.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunAsync()
    {
        try
        {
            await action.Invoke();
        }
        catch (Exception ex)
        {
            notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("The action"), ex.NotificationDetails());
        }
    }
}
