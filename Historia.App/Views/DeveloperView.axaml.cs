namespace Historia.Views;

/// <summary>
/// Developer-only tools panel. Only routed to when <see cref="Historia.Platform.DevMode"/>
/// is enabled. The view is markup bound to a <see cref="DeveloperViewModel"/>; the only code here
/// connects the auto-fetch status switch, which lives in the App layer.
/// </summary>
public sealed partial class DeveloperView : UserControl, IDisposable
{
    public DeveloperView(IServiceProvider sp)
    {
        InitializeComponent();
        ViewModel = ActivatorUtilities.CreateInstance<DeveloperViewModel>(
            sp,
            (Func<bool>)(() => AutoFetchDebug.Enabled),
            (Action<bool>)(enabled => AutoFetchDebug.Enabled = enabled));
        DataContext = ViewModel;
    }

    internal DeveloperViewModel ViewModel { get; }

    public void Dispose() => ViewModel.Dispose();
}
