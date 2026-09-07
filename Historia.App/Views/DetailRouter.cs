using Avalonia.Threading;
using static Historia.Views.ViewKit;

namespace Historia.Views;

/// <summary>
/// The detail-pane router (V4 port of Routes.razor + MainLayout.razor). Maps the
/// <see cref="INavigationBus"/> route string to a view and overlays the notification host.
/// Keyboard shortcuts Ctrl+, (settings) and Ctrl+B (home) are registered here.
/// </summary>
public sealed class DetailRouter : UserControl, IDisposable
{
    private readonly IServiceProvider _sp;
    private readonly INavigationBus _bus;
    private readonly IKeyboardCommandRouter _keyboard;
    private readonly ContentControl _host = new();
    private readonly Panel _overlayRoot = new();
    private readonly NotificationHostView _notifHost;
    private readonly List<IDisposable> _shortcuts = [];
    private IDisposable? _currentView;

    public DetailRouter(IServiceProvider sp)
    {
        _sp = sp;
        _bus = sp.GetRequiredService<INavigationBus>();
        _keyboard = sp.GetRequiredService<IKeyboardCommandRouter>();

        _overlayRoot.Children.Add(_host);
        _notifHost = new NotificationHostView(sp.GetRequiredService<Historia.Services.Notifications.INotificationService>());
        _overlayRoot.Children.Add(_notifHost);
        // FirstRunPrompt is app-modal and hosted at the window root (MainWindow.ModalOverlayHost)
        // so it dims and centres over the whole window, not just this detail pane.
        Content = _overlayRoot;
        this.Dyn(BackgroundProperty, "ColorChrome");

        _bus.NavigationRequested += OnNavigationRequested;
        _shortcuts.Add(_keyboard.Register("Ctrl+,", () => { Navigate("settings"); return Task.CompletedTask; }));
        _shortcuts.Add(_keyboard.Register("Ctrl+B", () => { Navigate(""); return Task.CompletedTask; }));

        Navigate(_bus.CurrentRoute);
    }

    private void OnNavigationRequested(string uri) => Dispatcher.UIThread.Post(() => Navigate(uri));

    private void Navigate(string route)
    {
        var normalized = (route ?? "").TrimStart('/');
        var q = normalized.IndexOf('?');
        var head = q >= 0 ? normalized[..q] : normalized;

        _bus.ReportRoute(normalized);

        (_currentView as IDisposable)?.Dispose();
        Control view = head switch
        {
            "repo" => new RepositoryView(_sp),
            "settings" => new SettingsView(_sp),
            "themes" => new ThemesView(_sp),
            "repo-settings" => new RepoSettingsView(_sp),
            "rebase" => new RebaseEditorView(_sp, QueryValue(normalized, "onto")),
            "dev" => new DeveloperView(_sp),
            "profiles" => new ProfilesView(_sp, QueryValue(normalized, "intent")),
            _ => new HomeView(_sp, QueryValue(normalized, "intent")),
        };
        _currentView = view as IDisposable;
        _host.Content = view;
    }

    private static string QueryValue(string route, string key)
    {
        var q = route.IndexOf('?');
        if (q < 0) return "";
        foreach (var pair in route[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == key)
                return Uri.UnescapeDataString(kv[1]);
        }
        return "";
    }

    public void Dispose()
    {
        _bus.NavigationRequested -= OnNavigationRequested;
        foreach (var s in _shortcuts) s.Dispose();
        (_currentView as IDisposable)?.Dispose();
        _notifHost.Dispose();
    }
}

/// <summary>A minimal titled page for routes whose full view is not yet ported.</summary>
public sealed class SimplePageView : UserControl
{
    public SimplePageView(string title, string body)
    {
        var stack = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(32) };
        stack.Children.Add(Text(title, "ColorText", 20, Avalonia.Media.FontWeight.SemiBold));
        stack.Children.Add(Text(body, "ColorTextMuted", 13));
        Content = stack;
        this.Dyn(BackgroundProperty, "ColorChrome");
    }
}
