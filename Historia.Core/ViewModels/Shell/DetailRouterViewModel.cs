namespace Historia.ViewModels;

/// <summary>The pages the detail pane can show.</summary>
public enum DetailPage
{
    Home,
    Repo,
    Settings,
    Themes,
    RepoSettings,
    Rebase,
    Developer,
    Profiles,
}

/// <summary>A parsed detail route. <paramref name="Path"/> is the normalized route, query included.</summary>
/// <param name="Intent">The route's <c>intent</c> query value, or empty.</param>
/// <param name="Onto">The route's <c>onto</c> query value, or empty.</param>
public sealed record DetailRoute(DetailPage Page, string Path, string Intent, string Onto);

/// <summary>
/// The detail-pane router: maps the navigation bus's route strings to a <see cref="DetailRoute"/>
/// and reports the normalized route back to the bus. The keyboard shortcuts Ctrl+, (settings) and
/// Ctrl+B (home) navigate from here. A route to the page already shown still raises
/// <see cref="Navigated"/>, so the page starts afresh.
/// </summary>
public sealed class DetailRouterViewModel : ViewModelBase
{
    private readonly INavigationBus _bus;
    private readonly IUiDispatcher _ui;

    public DetailRouterViewModel(INavigationBus bus, IKeyboardCommandRouter keyboard, IUiDispatcher ui)
    {
        _bus = bus;
        _ui = ui;

        Subscribe(() => _bus.NavigationRequested += OnNavigationRequested, () => _bus.NavigationRequested -= OnNavigationRequested);
        Track(keyboard.Register("Ctrl+,", () => { Navigate("settings"); return Task.CompletedTask; }));
        Track(keyboard.Register("Ctrl+B", () => { Navigate(""); return Task.CompletedTask; }));

        Current = Parse(_bus.CurrentRoute);
        _bus.ReportRoute(Current.Path);
    }

    /// <summary>Raised after every navigation, with the route now shown.</summary>
    public event EventHandler<DetailRoute>? Navigated;

    /// <summary>The route the detail pane shows.</summary>
    public DetailRoute Current { get; private set; }

    /// <summary>Shows the page <paramref name="route"/> names and reports the route to the bus.</summary>
    public void Navigate(string route)
    {
        if (IsDisposed)
            return;

        Current = Parse(route);
        _bus.ReportRoute(Current.Path);
        Navigated?.Invoke(this, Current);
    }

    /// <summary>Parses a route such as <c>repo</c> or <c>profiles?intent=add</c>. A leading slash is
    /// ignored and an unknown or empty head is the home page.</summary>
    public static DetailRoute Parse(string? route)
    {
        var normalized = (route ?? "").TrimStart('/');
        var q = normalized.IndexOf('?');
        var head = q >= 0 ? normalized[..q] : normalized;

        var page = head switch
        {
            "repo" => DetailPage.Repo,
            "settings" => DetailPage.Settings,
            "themes" => DetailPage.Themes,
            "repo-settings" => DetailPage.RepoSettings,
            "rebase" => DetailPage.Rebase,
            "dev" => DetailPage.Developer,
            "profiles" => DetailPage.Profiles,
            _ => DetailPage.Home,
        };
        return new DetailRoute(page, normalized, QueryValue(normalized, "intent"), QueryValue(normalized, "onto"));
    }

    /// <summary>The unescaped value of <paramref name="key"/> in the route's query, or empty.</summary>
    public static string QueryValue(string route, string key)
    {
        var q = route.IndexOf('?');
        if (q < 0)
            return "";

        foreach (var pair in route[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == key)
                return Uri.UnescapeDataString(kv[1]);
        }

        return "";
    }

    // The bus raises on the caller's thread; the page is built on the UI thread.
    private void OnNavigationRequested(string uri) => _ui.Post(() => Navigate(uri));
}
