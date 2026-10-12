namespace Historia.Services.UI;

/// <summary>One network-op progress tick, or (with <c>Progress</c> null) "hide the progress UI" —
/// <c>Repo</c> lets a listener ignore ticks for a repository it isn't currently showing.</summary>
public sealed record RepoNetworkProgress(RepositoryContext Repo, GitProgress? Progress);

/// <summary>
/// Runs the Fetch / Pull / Push flow requested via <see cref="IRepoActionBus"/> — from the
/// shortcuts bar, branch menus and the staging panel's Commit &amp; Push — against the request's
/// repository, or the focused one when the request names none. One operation runs per repository,
/// through the repository's network gate; different repositories run side by side. A request for
/// a repository that is already busy with one of the user's operations (or a lock cleanup) is
/// answered with a notice naming it, not queued; one that finds auto-fetch running waits for it,
/// cancellable, and then runs — auto-fetch is never killed to make room.
/// A DI singleton, eagerly resolved once at startup (<c>MainWindow</c>) so it subscribes to <see cref="IRepoActionBus.NetworkActionRequested"/> for
/// the app's whole lifetime. It does not live in <c>RepositoryView</c>: that view (and any event
/// subscription it holds) is disposed whenever the detail pane shows something else (Home,
/// Settings, …), so Fetch/Pull/Push would silently do nothing there.
/// </summary>
public interface IRepoNetworkActionRunner
{
    event EventHandler<RepoNetworkProgress>? ProgressChanged;

    /// <summary>Cancels the operation running, or waiting to run, on <paramref name="repo"/>, if any.</summary>
    void Cancel(RepositoryContext repo);

    /// <summary>The latest progress of <paramref name="repo"/>'s operation; null when none is running.</summary>
    GitProgress? CurrentProgress(RepositoryContext repo);

    /// <summary>
    /// Runs a network command started outside the Fetch / Pull / Push flow — a card's retry, a
    /// badge's fast-forward — under the same per-repository rules: a notice naming what is running
    /// when <paramref name="repo"/> is busy (and <paramref name="operation"/> does not run), a
    /// cancellable wait behind auto-fetch, and progress plus Cancel in the repository view. A
    /// cancellation ends silently; any other failure propagates to the caller.
    /// </summary>
    Task RunExclusiveAsync(RepositoryContext repo, string label, Func<IProgress<GitProgress>, CancellationToken, Task> operation);
}
