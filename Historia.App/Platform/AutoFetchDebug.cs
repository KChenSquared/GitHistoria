namespace Historia.Platform;

/// <summary>
/// Developer-only toggle: when on, the app band draws a 4&#160;px colour bar down the left
/// edge of every repo tab showing that tab's auto-fetch state — red = the
/// <c>[AutoFetch]</c> loop has not started, yellow = started but this repo has not been fetched
/// this cycle, green = fetched this cycle. Off in release builds (the Developer panel that flips
/// it is only routed to when <see cref="DevMode"/> is enabled). Session-only, like
/// <c>PerfLog.Enabled</c>.
/// </summary>
internal static class AutoFetchDebug
{
    private static bool _enabled;

    public static bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Raised on the thread that set <see cref="Enabled"/>.</summary>
    public static event Action? Changed;
}
