namespace Historia.Services.Graph;

/// <summary>
/// Opaque allocator state carried between layout calls so incremental extension produces the
/// same result as a full recomputation (§7.3, §7.8). Never renumbers lanes already assigned to
/// existing rows.
/// </summary>
public sealed class GraphLayoutState
{
    internal int MaxLanes { get; init; } = int.MaxValue;

    /// <summary>When false, <see cref="Commits"/> is left empty — the owning service is
    /// spilling per-row metadata to a disk-backed store instead of keeping it resident (§7.6).</summary>
    internal bool RetainCommits { get; init; } = true;

    /// <summary>Next sequential row index to assign.</summary>
    internal int NextRowIndex;

    /// <summary>Next never-used raw lane.</summary>
    internal int LaneCeiling;

    /// <summary>Raw lanes released and available for reuse (always the lowest is taken first).</summary>
    internal readonly SortedSet<int> FreeLanes = [];

    /// <summary>parent id → the child reservations expecting it.</summary>
    internal readonly Dictionary<ObjectId, List<Reservation>> Reservations = [];

    /// <summary>Phase 5(a) (v10 graph memory footprint plan): canonicalizes <see cref="ObjectId"/>
    /// instances across <see cref="Reservations"/> and <see cref="RowByCommitId"/> — a commit is
    /// first seen as a reservation key (via its child's own, separately-parsed
    /// <c>ParentIds</c> entry) rows before it is ever seen as a row itself (in a topo walk, a
    /// parent's row always comes after its children's), so without this, <c>RowByCommitId</c>
    /// would key on yet another freshly-parsed instance of the same SHA instead of reusing the one
    /// already resident. See <c>DagLayoutEngine.Intern</c>.</summary>
    internal readonly Dictionary<ObjectId, ObjectId> InternedIds = [];

    /// <summary>
    /// raw lane → how many pending reservations name it as their parent lane (i.e. entries with
    /// <see cref="Reservation.RawLane"/> ≥ 0). Lets <c>ReleaseLane</c> answer "is this lane still
    /// spoken for?" in O(1) instead of scanning every reservation of every parent.
    /// </summary>
    internal readonly Dictionary<int, int> ReservedLaneCount = [];

    /// <summary>raw lane → compacted lane, assigned on first row placement and never changed.</summary>
    internal readonly Dictionary<int, int> CompactionMap = [];
    internal int NextCompactedLane;

    /// <summary>§V9-G ("older tip wins"): branch-tip commit id → the raw lane the pre-pass pinned
    /// it to, so the oldest divergent branch stays collinear with the shared mainline instead of
    /// whichever tip the time-ordered walk emitted onto row 0. Populated once by
    /// <see cref="DagLayoutEngine.Compute"/> (empty for a single-tip window) and carried forward
    /// unchanged by <see cref="DagLayoutEngine.Extend"/>.</summary>
    internal readonly Dictionary<ObjectId, int> TipTargetLane = [];

    /// <summary>compacted lane → color index, seeded once from the lane's topmost commit.</summary>
    internal readonly Dictionary<int, int> LaneColor = [];

    /// <summary>compacted lane → the stable key its colour was seeded from: the lane's branch
    /// name if it has one, else the SHA of the commit that first opened the lane. §7.1 — a pure
    /// function of the walk, so incremental and full layouts colour identically and a repo
    /// reopens in the same shades. Never renumbered.</summary>
    internal readonly Dictionary<int, string> LaneKey = [];

    // Accumulators — real edges only append; OffWindow edges are regenerated each build.
    // Finding #9: Rows is chunked (ChunkedAppendList), not a plain List<T> — see its own doc
    // comment. This is what lets GraphLayout.Rows become a RowCount-bounded VIEW over this same
    // instance instead of a fresh array copy on every Build/Extend.
    internal readonly ChunkedAppendList<GraphRow> Rows = new();
    internal readonly List<CommitMetadata> Commits = [];
    internal readonly List<GraphEdge> RealEdges = [];
    internal readonly List<GraphDecoration> Decorations = [];
    internal readonly Dictionary<ObjectId, int> RowByCommitId = [];

    /// <summary>§Phase 2 crossing index: boundary rows are multiples of this stride. Sized so a
    /// window's checkpoint lookup costs about (concurrent lanes) work, not (total rows) — linux
    /// at 300k rows / 637 lanes gives ~1,172 checkpoints of a few hundred entries each, versus
    /// re-deriving 106M segment entries per <c>Build</c>.</summary>
    internal const int CheckpointStride = 256;

    /// <summary>checkpoint index (boundary row = index × <see cref="CheckpointStride"/>) → the
    /// indices into <see cref="RealEdges"/> of every real edge spanning that boundary
    /// (<c>FromRow &lt; boundary ≤ ToRow</c>). Append-only: populated once, when the edge is
    /// created, for every boundary it will ever cross — a long-lived branch's edge is written
    /// into potentially many checkpoints at creation time, but each checkpoint is then never
    /// touched again.</summary>
    internal readonly Dictionary<int, List<int>> CheckpointCrossing = [];

    /// <summary>(FromRow ÷ <see cref="CheckpointStride"/>) → the indices into <see
    /// cref="RealEdges"/> of every real edge whose <c>FromRow</c> falls in that block. Lets a
    /// window derivation find "edges starting in [checkpoint, b]" without scanning every edge.</summary>
    internal readonly Dictionary<int, List<int>> EdgesByFromBlock = [];

    /// <param name="RawLane">The raw lane reserved for the PARENT.</param>
    /// <param name="ChildLane">The raw lane the CHILD occupies — equal to <paramref name="RawLane"/>
    /// for a first parent; for a second-or-later parent it is the merge commit's own raw lane, so
    /// the emitted edge starts at the merge node rather than floating free of it (§V3-3).</param>
    internal readonly record struct Reservation(int RawLane, int ChildLane, int ChildRow, bool IsFirstParent);
}
