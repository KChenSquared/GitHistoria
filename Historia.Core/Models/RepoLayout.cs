namespace Historia.Models;

/// <summary>
/// Per-repository graph-panel layout (§V3-B). Stored keyed by repo path in
/// <c>repo-columns.json</c>, not in <see cref="AppSettings"/> — column widths, order,
/// visibility and the graph-width pin are a per-repo concern.
/// </summary>
public sealed record RepoLayout
{
    public IReadOnlyList<GraphColumn> Columns { get; init; } = GraphColumnDefaults.All;

    /// <summary>True once the user has dragged the Graph column divider. Cleared every launch.</summary>
    public bool GraphWidthPinned { get; init; }

    /// <summary>
    /// §13: when true, fetch / push / pull are forced over SSH (converting a non-SSH remote URL
    /// per-invocation) and fail closed with no HTTPS fallback. Default false. Persists across
    /// launches.
    /// </summary>
    public bool SshOnly { get; init; }
}
