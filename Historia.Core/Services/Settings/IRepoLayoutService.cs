
namespace Historia.Services.Settings;

public sealed record RepoLayoutChangedEventArgs(string RepoPath, RepoLayout Layout);

/// <summary>
/// Per-repository graph-panel layout store (§V3-B). Keyed by normalised repository path, backed
/// by <c>repo-columns.json</c>. Column order/widths and the width pin reset to defaults on every
/// launch. Singleton.
/// </summary>
public interface IRepoLayoutService
{
    /// <summary>The layout for <paramref name="repoPath"/>. Never null — defaults when unseen.</summary>
    RepoLayout Get(string repoPath);

    /// <summary>Atomically mutates and persists the layout for <paramref name="repoPath"/>, then raises <see cref="Changed"/>.</summary>
    Task UpdateAsync(string repoPath, Func<RepoLayout, RepoLayout> mutate);

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
