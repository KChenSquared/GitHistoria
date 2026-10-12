namespace Historia.Views;

/// <summary>
/// The detail-pane router. A <see cref="DetailRouterViewModel"/> maps the
/// <see cref="INavigationBus"/> route string to a page; the code here builds the view for that page,
/// hosts it, and overlays the notification host.
/// </summary>
public sealed partial class DetailRouter : UserControl, IDisposable
{
    private readonly IServiceProvider _sp;
    private readonly NotificationHostView _notifHost;
    private IDisposable? _currentView;

    public DetailRouter(IServiceProvider sp)
    {
        _sp = sp;
        InitializeComponent();

        _notifHost = new NotificationHostView(
            sp.GetRequiredService<Historia.Services.Notifications.INotificationService>(),
            sp.GetRequiredService<ISettingsService>());
        OverlayRoot.Children.Add(_notifHost);
        // FirstRunPrompt is app-modal and hosted at the window root (MainWindow.ModalOverlayHost)
        // so it dims and centres over the whole window, not just this detail pane.

        ViewModel = ActivatorUtilities.CreateInstance<DetailRouterViewModel>(sp, ViewServices.Dispatcher(sp));
        DataContext = ViewModel;
        ViewModel.Navigated += OnNavigated;
        Show(ViewModel.Current);
    }

    internal DetailRouterViewModel ViewModel { get; }

    private void OnNavigated(object? sender, DetailRoute route) => Show(route);

    private void Show(DetailRoute route)
    {
        _currentView?.Dispose();
        Control view = route.Page switch
        {
            DetailPage.Repo => new RepositoryView(_sp),
            DetailPage.Settings => new SettingsView(_sp),
            DetailPage.Themes => new ThemesView(_sp),
            DetailPage.RepoSettings => new RepoSettingsView(_sp),
            DetailPage.Rebase => new RebaseEditorView(_sp, route.Onto),
            DetailPage.Developer => new DeveloperView(_sp),
            DetailPage.Profiles => new ProfilesView(_sp, route.Intent),
            _ => new HomeView(_sp, route.Intent),
        };
        _currentView = view as IDisposable;
        Host.Content = view;
    }

    public void Dispose()
    {
        ViewModel.Navigated -= OnNavigated;
        ViewModel.Dispose();
        _currentView?.Dispose();
        _notifHost.Dispose();
    }
}
