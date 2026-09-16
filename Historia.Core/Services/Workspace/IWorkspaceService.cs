
namespace Historia.Services.Workspace;

public sealed record FocusedRepositoryChangedEventArgs(RepositoryContext? Old, RepositoryContext? New);
public sealed record OpenRepositoriesChangedEventArgs(IReadOnlyList<RepositoryContext> OpenRepositories);

public sealed record CloneOptions
{
    public bool Recursive { get; init; }
    public int? Depth { get; init; }

    /// <summary>Pass <c>--sparse</c> so the working tree starts as a cone-mode sparse checkout.</summary>
    public bool Sparse { get; init; }
}

/// <summary>
/// Owns the set of open repository tabs (§6.6.3). Singleton. Each tab is fully independent:
/// its own <c>Repository</c>, watcher, cancellation scope, resolved profile, and selection
/// state. Focusing a tab republishes its cached state; it never re-reads from disk.
/// </summary>
public interface IWorkspaceService
{
    IReadOnlyList<RepositoryContext> OpenRepositories { get; }
    RepositoryContext? Focused { get; }

    /// <summary>Opens an existing repository discovered from <paramref name="path"/> (§6.6.1).</summary>
    Task<RepositoryContext> OpenAsync(string path, CancellationToken ct = default);

    /// <summary>Initializes a new repository and opens it (§6.6.1).</summary>
    Task<RepositoryContext> InitAsync(string path, string? initialBranch, CancellationToken ct = default);

    /// <summary>Clones <paramref name="url"/> into <paramref name="targetDirectory"/>, then opens it (§6.6.1).</summary>
    Task<RepositoryContext> CloneAsync(
        string url,
        string targetDirectory,
        CloneOptions options,
        IProgress<GitProgress>? progress,
        CancellationToken ct = default);

    Task CloseAsync(RepositoryContext context);
    void Focus(RepositoryContext context);

    /// <summary>§12.1: force a deferred session-restore tab through its initialize path now
    /// rather than on first focus. No-op if it is already initializing or fully loaded.</summary>
    void EnsureInitialized(RepositoryContext context);

    /// <summary>
    /// Enqueues a refresh for a background tab, draining on the serial background pump behind the
    /// focused tab's first full load in app-band tab order (§F4, §F5). If <paramref name="context"/>
    /// is currently focused, runs immediately.
    /// </summary>
    Task RequestBackgroundRefreshAsync(RepositoryContext context, bool full = false, CancellationToken ct = default)
        => full ? context.State.RefreshAllAsync(ct) : context.State.RefreshStatusAsync(ct);

    /// <summary>
    /// Moves an open tab to <paramref name="newIndex"/> in the strip (drag-and-drop reorder,
    /// §V3-B/19.21). No-op when the context is not open or is already there. Raises
    /// <see cref="OpenRepositoriesChanged"/> and re-persists the session so the order survives a
    /// restart. Focus is unaffected.
    /// </summary>
    void Move(RepositoryContext context, int newIndex);

    /// <summary>Reopens the repositories that were open when the app last closed (§17 Q4; opt-in).</summary>
    Task RestoreSessionAsync(CancellationToken ct = default);

    event EventHandler<FocusedRepositoryChangedEventArgs>? FocusedRepositoryChanged;
    event EventHandler<OpenRepositoriesChangedEventArgs>? OpenRepositoriesChanged;
}
