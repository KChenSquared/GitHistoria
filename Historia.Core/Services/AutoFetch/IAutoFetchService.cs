using Historia.Services.Workspace;

namespace Historia.Services.AutoFetch;

/// <summary>How a repository came to be open, for the "app start with no open repo" branch of
/// <see cref="IAutoFetchService"/>'s start conditions (§12.1.1). A freshly cloned / initialized
/// repo is already current, so it gets no <c>[InitialFetch]</c>; an opened one does.</summary>
public enum RepoEntrySource
{
    Opened,
    ClonedOrInitialized,
}

/// <summary>
/// App-supplied bridge the Core <see cref="IAutoFetchService"/> uses to (a) know when a
/// repository tab is <b>fully loaded</b> — branches panel, full graph paint, changes panel
/// (§12.1) — and (b) actually run a background <c>git fetch</c>. Kept out of Core so the
/// schedule logic can be tested with no real repository, watcher, or git process.
/// </summary>
public interface IAutoFetchHost
{
    /// <summary>Completes once <paramref name="context"/> has fully loaded. Must never fault on
    /// its own; a headless context completes it immediately.</summary>
    Task WhenFullyLoadedAsync(RepositoryContext context, CancellationToken ct);

    /// <summary>Runs one background fetch on <paramref name="context"/> (§12.1 — honours SSH
    /// Only, never prompts, quiet on failure). Exceptions other than cancellation are swallowed
    /// by the caller.</summary>
    Task FetchAsync(RepositoryContext context, CancellationToken ct);
}

/// <summary>
/// §12.1: keeps every open repository's remote-tracking refs warm without a manual Fetch. An
/// app-lifetime singleton. Once the app has fully loaded its repo(s) it runs an optional
/// one-shot <c>[InitialFetch]</c> and then an infinite <c>[AutoFetch]</c> loop (5&#160;min idle,
/// then one fetch per open repo 1&#160;min apart, then restart). Gated by
/// <see cref="Historia.Models.AppSettings.AutoFetchEnabled"/> — turning it off stops the loop at
/// once; turning it back on re-enters at the "app start with open repos" path.
/// </summary>
public interface IAutoFetchService : IAsyncDisposable
{
    /// <summary>Mirrors <see cref="Historia.Models.AppSettings.AutoFetchEnabled"/>.</summary>
    bool IsEnabled { get; }

    /// <summary>Idempotent. Begins the start-condition wait, then the loop. A no-op while
    /// disabled or already running.</summary>
    void Start();

    /// <summary>Cancels the loop and any pending <c>[InitialFetch]</c>.</summary>
    void Stop();

    /// <summary>The App calls this immediately before opening / cloning / initializing a
    /// repository from a cold start (no tabs), so the "no open repo" branch knows whether to run
    /// an <c>[InitialFetch]</c>.</summary>
    void NotifyRepoEntering(RepoEntrySource source);
}
