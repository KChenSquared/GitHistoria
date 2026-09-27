
namespace Historia.Services.Terminal;

/// <summary>
/// Owns every live terminal session, keyed by <see cref="TerminalOrigin.Key"/>.
/// Registered as a <b>singleton</b> — never <c>AddScoped</c>. The only
/// terminal surface the views talk to.
/// </summary>
public interface ITerminalSessionService
{
    IReadOnlyList<TerminalSession> Sessions { get; }
    TerminalSession? Focused { get; }

    /// <summary>
    /// Focuses the existing session for this origin, or starts one. Never spawns a second
    /// session for an origin that already has a live one — and is re-entrant-safe: two rapid
    /// clicks produce one process.
    /// </summary>
    Task<TerminalSession> OpenOrFocusAsync(TerminalOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Always starts a fresh session for this origin, even when one is already live — the
    /// "New Terminal" / <c>+</c> path. The new session becomes the focused one.
    /// </summary>
    Task<TerminalSession> OpenNewAsync(TerminalOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Asks the dock to show itself without touching the session set or focus — the
    /// "Open Terminal just reveals the dock" path. No-op if nothing is listening.
    /// </summary>
    void RequestActivation();

    void Focus(TerminalSession session);

    Task CloseAsync(Guid sessionId);

    /// <summary>Called by <c>WorkspaceService</c> when a repository tab closes, so a
    /// closed tab does not leave an orphaned shell holding a directory handle open.
    /// <paramref name="tabId"/> is the closing <c>RepositoryContext.Id</c>; only that tab's
    /// session is killed — a second tab open on the same repository keeps its shell.</summary>
    Task CloseForTabAsync(string tabId);

    event EventHandler? SessionsChanged;
    event EventHandler? FocusedSessionChanged;
    event EventHandler<TerminalSession>? SessionExited;

    /// <summary>Raised when an entry point wants the dock brought into view.</summary>
    event EventHandler? ActivationRequested;
}
