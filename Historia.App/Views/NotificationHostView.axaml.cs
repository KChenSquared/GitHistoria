namespace Historia.Views;

/// <summary>Toast stack for <see cref="INotificationService"/>. The view is markup bound to a
/// <see cref="NotificationHostViewModel"/>; the only code here is the dispatcher-backed timer the
/// view model schedules auto-hide on.</summary>
public sealed partial class NotificationHostView : UserControl, IDisposable
{
    public NotificationHostView(IServiceProvider sp)
        : this(sp.GetRequiredService<INotificationService>(), sp.GetRequiredService<ISettingsService>(), ScheduleOnDispatcher, sp)
    {
    }

    /// <summary><paramref name="schedule"/> runs an action once after a delay and returns a handle
    /// that cancels it; tests pass a manual one. <paramref name="sp"/> supplies the dispatcher and
    /// clipboard.</summary>
    internal NotificationHostView(INotificationService notifications, ISettingsService settings,
        Func<TimeSpan, Action, IDisposable> schedule, IServiceProvider? sp = null)
    {
        InitializeComponent();
        ViewModel = new NotificationHostViewModel(notifications, settings, ViewServices.Dispatcher(sp), schedule, ViewServices.Clipboard(sp));
        DataContext = ViewModel;
    }

    internal NotificationHostViewModel ViewModel { get; }

    /// <summary>How long a notice stays before closing itself, or null when it stays until dismissed.</summary>
    internal static TimeSpan? AutoHideDelay(AppSettings settings) => NotificationHostViewModel.AutoHideDelay(settings);

    private static IDisposable ScheduleOnDispatcher(TimeSpan delay, Action action) => DispatcherTimer.RunOnce(action, delay);

    public void Dispose() => ViewModel.Dispose();
}
