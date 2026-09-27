
namespace Historia.Services.Settings;

public sealed record RepoLayoutChangedEventArgs(string RepoPath, RepoLayout Layout);

/// <summary>
/// Per-repository graph-panel layout store. Keyed by normalised repository path, backed
/// by <c>repo-columns.json</c>. Column order/widths and the width pin reset to defaults on every
/// launch. Singleton.
/// </summary>
public interface IRepoLayoutService
{
    /// <summary><see cref="RepoLayoutChangedEventArgs.RepoPath"/> value for a change to the
    /// application-wide column order / visibility rather than to one repo's widths.</summary>
    const string GlobalKey = "\0global";

    /// <summary>The layout for <paramref name="repoPath"/>: the global column order + visibility
    /// merged with that repo's per-session widths and pin. Never null — defaults when unseen.</summary>
    RepoLayout Get(string repoPath);

    /// <summary>the application-wide column order + visibility + flex column. Order resets
    /// to canonical at launch; visibility persists.</summary>
    IReadOnlyList<GraphColumn> GlobalColumns { get; }

    /// <summary>Atomically mutates and persists the layout for <paramref name="repoPath"/>, then
    /// raises <see cref="Changed"/>. Column order / visibility in the result are split off into
    /// <see cref="GlobalColumns"/>; widths, the pin and <c>SshOnly</c> stay per repo.</summary>
    Task UpdateAsync(string repoPath, Func<RepoLayout, RepoLayout> mutate);

    /// <summary>Atomically mutates and persists <see cref="GlobalColumns"/>, then raises
    /// <see cref="Changed"/> with <see cref="GlobalKey"/>. A no-op when the shape is unchanged.</summary>
    Task UpdateGlobalAsync(Func<IReadOnlyList<GraphColumn>, IReadOnlyList<GraphColumn>> mutate);

    /// <summary>Raised (synchronously, on the caller's thread) after a successful <see cref="UpdateAsync"/>.</summary>
    event EventHandler<RepoLayoutChangedEventArgs>? Changed;

    /// <summary>Forces any debounced write to disk to complete.</summary>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>Normalises a repository path into the storage key.</summary>
    static string KeyFor(string repoPath)
    {
        try { return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(repoPath)).ToLowerInvariant(); }
        catch { return repoPath.ToLowerInvariant(); }
    }
}
