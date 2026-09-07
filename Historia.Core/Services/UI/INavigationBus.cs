namespace Historia.Services.UI;

/// <summary>
/// Cross-WebView navigation channel (§6.2.1). Phase 2 hosts the sidebar and the detail area in
/// two separate <c>BlazorWebView</c> instances with the native commit graph between them; the
/// sidebar publishes navigation intents here and the detail root consumes them. Singleton.
/// </summary>
public interface INavigationBus
{
    /// <summary>Raised (on the detail WebView's dispatcher) when the sidebar requests navigation.</summary>
    event Action<string>? NavigationRequested;

    void RequestNavigate(string relativeUri);

    /// <summary>The detail WebView's current relative route ("" = Home). Reported by Routes.razor (§V3-10.2).</summary>
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
