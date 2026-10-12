namespace Historia.Views;

/// <summary>Toast stack for <see cref="INotificationService"/>. The view is markup bound to a
/// <see cref="NotificationHostViewModel"/>; the only code here is the dispatcher-backed timer the
/// view model schedules auto-hide on.</summary>
public sealed partial class NotificationHostView : UserControl, IDisposable
{
    public NotificationHostView(INotificationService notifications, ISettingsService settings)
        : this(notifications, settings, ScheduleOnDispatcher)
    {
    }

    /// <summary><paramref name="schedule"/> runs an action once after a delay and returns a handle
    /// that cancels it; tests pass a manual one.</summary>
    internal NotificationHostView(INotificationService notifications, ISettingsService settings, Func<TimeSpan, Action, IDisposable> schedule)
    {
        InitializeComponent();
        ViewModel = new NotificationHostViewModel(notifications, settings, new UiDispatcher(), schedule, new ClipboardService());
        DataContext = ViewModel;
    }

    internal NotificationHostViewModel ViewModel { get; }

    /// <summary>How long a notice stays before closing itself, or null when it stays until dismissed.</summary>
    internal static TimeSpan? AutoHideDelay(AppSettings settings) => NotificationHostViewModel.AutoHideDelay(settings);

    private static IDisposable ScheduleOnDispatcher(TimeSpan delay, Action action) => DispatcherTimer.RunOnce(action, delay);

    public void Dispose() => ViewModel.Dispose();
}
