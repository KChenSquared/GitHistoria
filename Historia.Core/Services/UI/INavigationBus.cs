namespace Historia.Services.UI;

/// <summary>
/// Navigation channel for the detail pane. Any view (the app menu, the graph, the shortcut
/// bars) requests a route here; <c>DetailRouter</c> shows it and reports the route back. Singleton.
/// </summary>
public interface INavigationBus
{
    /// <summary>Raised on the caller's thread when a route is requested; <c>DetailRouter</c> marshals to the UI thread.</summary>
    event Action<string>? NavigationRequested;

    void RequestNavigate(string relativeUri);

    /// <summary>The detail pane's current route ("" = Home), as reported by <c>DetailRouter</c>.</summary>
    string CurrentRoute { get; }

    /// <summary>Raised on the caller's thread when the detail route changes; native subscribers marshal.</summary>
    event Action<string>? RouteChanged;

    void ReportRoute(string relativeUri);
}

public sealed class NavigationBus : INavigationBus
{
    public event Action<string>? NavigationRequested;
    public event Action<string>? RouteChanged;

    public string CurrentRoute { get; private set; } = "";

    public void RequestNavigate(string relativeUri) => NavigationRequested?.Invoke(relativeUri);

    public void ReportRoute(string relativeUri)
    {
        var normalized = relativeUri ?? "";
        if (normalized == CurrentRoute)
            return;
        CurrentRoute = normalized;
        RouteChanged?.Invoke(normalized);
    }
}
