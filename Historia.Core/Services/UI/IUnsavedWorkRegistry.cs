namespace Historia.Services.UI;

/// <summary>
/// Represents a piece of unsaved state (e.g. an active conflict resolution or file edit)
/// tracked by <see cref="IUnsavedWorkRegistry"/> across tab close and application exit.
/// </summary>
public interface IUnsavedWork
{
    /// <summary>The repository context owning this unsaved work.</summary>
    RepositoryContext Context { get; }

    /// <summary>Human-readable description of the unsaved work (e.g. file path).</summary>
    string Description { get; }

    /// <summary>
    /// Attempts to save the work to disk. Returns <c>true</c> if successfully saved,
    /// or <c>false</c> if the save was refused, failed, or cancelled by the user.
    /// </summary>
    Task<bool> SaveAsync();

    /// <summary>
    /// Synchronously and without I/O, drops the dirty state and unregisters.
    /// Runs on the tab-close and exit paths where the owner is about to be disposed,
    /// so reloading from disk there would be wasted, blocking work. An owner that stays
    /// alive reloads on its own, later and off the UI thread.
    /// </summary>
    void Discard();
}

/// <summary>
/// Thread-safe registry tracking active unsaved work across open repositories.
/// Singleton.
/// </summary>
public interface IUnsavedWorkRegistry
{
    /// <summary>
    /// Registers an active unsaved work item. Disposing the returned handle unregisters it.
    /// </summary>
    IDisposable Register(IUnsavedWork work);

    /// <summary>
    /// Returns all registered unsaved work items for the specified repository context,
    /// or all registered items across all repositories if <paramref name="ctx"/> is <c>null</c>.
    /// </summary>
    IReadOnlyList<IUnsavedWork> For(RepositoryContext? ctx);
}
