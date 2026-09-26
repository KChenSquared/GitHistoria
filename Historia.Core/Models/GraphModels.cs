namespace Historia.Models;

/// <summary>
/// Immutable per-commit metadata, projected from LibGit2Sharp inside the repository gate
/// (§6.3.1, §7.2). One instance per commit; stored once in <see cref="GraphLayout.Commits"/>
/// and referenced everywhere else by row index.
/// </summary>
public sealed record CommitMetadata
{
    public required ObjectId Id { get; init; }
    public required string MessageShort { get; init; }
    public required string AuthorName { get; init; }
    public required string AuthorEmail { get; init; }
    public required DateTimeOffset AuthorWhen { get; init; }
    public required DateTimeOffset CommitWhen { get; init; }
    public required int ParentCount { get; init; }
}

public enum LaneSegmentType : byte
{
    Full = 0,
    TopToNode = 1,
    NodeToBottom = 2,
}

public readonly record struct RowLaneSegment
{
    public required int Lane { get; init; }
    public required int ColorIndex { get; init; }
    public required LaneSegmentType Type { get; init; }
}

/// <summary>Which way an elbow bends through its row.</summary>
public enum ElbowKind : byte
{
    /// <summary>Enters vertically from the row's top edge in <see cref="RowElbow.FromLane"/>,
    /// turns at node height, leaves horizontally toward <see cref="RowElbow.ToLane"/>. The bend
    /// a branch-out makes at the commit it rejoins.</summary>
    EnterTop = 0,

    /// <summary>Leaves the node (mid-row) in <see cref="RowElbow.FromLane"/>, turns near the
    /// row's bottom edge, exits horizontally toward <see cref="RowElbow.ToLane"/> where a
    /// vertical pass-through segment picks it up on the next row. The bend a merge edge makes at
    /// the merge commit's own row (§V3-3.2) instead of deferring the jog down to its far
    /// parent.</summary>
    LeaveBottom = 1,
}

public readonly record struct RowElbow
{
    public required int FromLane { get; init; }
    public required int ToLane { get; init; }
    public required int ColorIndex { get; init; }
    public required ElbowKind Kind { get; init; }
}

/// <summary>
/// The hot layout record: one per visual row (§7.2). Holds layout position and row-local
/// visual slice indices into flat <see cref="GraphLayout.Segments"/> and <see cref="GraphLayout.Elbows"/>
/// for instant, zero-allocation rendering with zero GC tracing overhead.
/// </summary>
public readonly record struct GraphRow
{
    public required int RowIndex { get; init; }
    public required int LaneIndex { get; init; }
    public required int ColorIndex { get; init; }
    public required int ParentCount { get; init; }
    public required RowFlags Flags { get; init; }
}

[Flags]
public enum RowFlags
{
    None = 0,
    IsHead = 1,
    IsMerge = 2,
    IsRoot = 4,
    HasDecorations = 8,
}

/// <summary>
/// Visual edge connecting two rows (§7.2). Rows, not commit ids: the renderer works purely in
/// row/lane space and never resolves an <see cref="ObjectId"/> during a frame.
/// </summary>
public readonly record struct GraphEdge
{
    public required int FromRow { get; init; }
    public required int FromLane { get; init; }
    public required int ToRow { get; init; }
    public required int ToLane { get; init; }
    public required EdgeType Type { get; init; }

    /// <summary>Colour of the segment/elbow drawn at <see cref="ToRow"/> — the terminal end of
    /// the edge (where it arrives at its parent's node). For a <see cref="EdgeType.MergeIn"/>
    /// edge this is the merged-in branch's own colour (§V6-G7), which can differ from
    /// <see cref="LeadColorIndex"/>.</summary>
    public required int ColorIndex { get; init; }

    /// <summary>Colour of the lead-in portion drawn from <see cref="FromRow"/> down to (but not
    /// including) <see cref="ToRow"/> — i.e. the <c>NodeToBottom</c> and <c>Full</c> segments.
    /// For a first-parent edge (<see cref="EdgeType.Straight"/>/<see cref="EdgeType.BranchOut"/>)
    /// this always equals <see cref="ColorIndex"/>. For <see cref="EdgeType.MergeIn"/> it is the
    /// colour already occupying the merge commit's OWN lane, not the merged-in branch's colour —
    /// that lane keeps carrying its own line for this same row span, so the merge stub must match
    /// it or the two would visibly clash as two differently-coloured lines sharing one lane.</summary>
    public required int LeadColorIndex { get; init; }
}

public enum EdgeType
{
    /// <summary>Parent continues in the same lane — rendered as a straight line.</summary>
    Straight,

    /// <summary>A second-or-later parent joining from another lane into a merge commit.</summary>
    MergeIn,

    /// <summary>A commit's lane diverging to a parent already placed in a different lane.</summary>
    BranchOut,

    /// <summary>
    /// The parent lies outside the currently laid-out row window. Rendered as a short stub
    /// fading at the window boundary — never as a dangling line to nowhere.
    /// </summary>
    OffWindow,
}

/// <summary>Decoration label attached to a commit row (§7.2). Named to avoid a clash with the
/// <see cref="CommitDecoration"/> used by the plain history list.</summary>
public readonly record struct GraphDecoration
{
    public required int RowIndex { get; init; }
    public required DecorationType Type { get; init; }
    public required string Label { get; init; }
    public required int ColorIndex { get; init; }

    /// <summary>§V3-7.2: the badge has a local branch of this name.</summary>
    public bool IsLocal { get; init; }

    /// <summary>§V3-7.2: HEAD points at this badge's branch. Per-badge, unlike <see cref="RowFlags.IsHead"/>.</summary>
    public bool IsHead { get; init; }

    /// <summary>§V3-7.2: the remotes carrying this branch, or null when the badge is local-only.</summary>
    public string[]? RemotesOrNull { get; init; }

    /// <summary>Never null. A struct's default instance bypasses initializers, so a nullable
    /// backing field with a non-null accessor is the only shape correct for both paths (§V3-7.2 Trap).</summary>
    public ReadOnlySpan<string> Remotes => RemotesOrNull ?? [];

    /// <summary>§V3-B: the other refs sitting on this same commit that folded into this badge —
    /// the further branches (default-first, then alphabetical) followed by any tags. Null unless
    /// this badge stands in for a group; shown in the interactive hover list. The <c>+N</c>
    /// counter on the badge is <c>1 + SiblingsOrNull.Length</c>.</summary>
    public GraphDecorationInput[]? SiblingsOrNull { get; init; }

    /// <summary>Never null (default-struct safe, like <see cref="Remotes"/>).</summary>
    public ReadOnlySpan<GraphDecorationInput> Siblings => SiblingsOrNull ?? [];
}

/// <summary>Input decoration before a row index is known. Also used for one entry of a folded
/// badge's <see cref="GraphDecoration.SiblingsOrNull"/> list.</summary>
public readonly record struct GraphDecorationInput(
    DecorationType Type,
    string Label,
    bool IsLocal = false,
    string[]? Remotes = null,
    bool IsHead = false,
    GraphDecorationInput[]? Siblings = null);

/// <summary>One commit fed to the layout engine, in topological (children-before-parents) order.</summary>
public readonly record struct RawCommit(ObjectId Id, IReadOnlyList<ObjectId> ParentIds, CommitMetadata Metadata);

/// <summary>
/// Row-indexed access to the per-commit metadata for a laid-out window (§7.6). The default
/// implementation (<see cref="ArrayCommitStore"/>) is a plain in-memory array; when the
/// "Read commit details from disk" setting is on, <c>DiskCommitStore</c> serves the same
/// shape by paging a random-access cache file, so a huge history keeps only the fixed-size
/// layout arrays resident. Consumers only ever index a bounded visible-row window plus
/// one-off interaction lookups, so a paged read is transparent to them.
/// <para>
/// The store is <b>lease-counted</b> (<see cref="AddRef"/> / <see cref="IDisposable.Dispose"/>).
/// A disk-backed store holds an open file handle; <c>GraphLayoutService</c> keeps one lease for
/// its walk cache, and every consumer that parks a <see cref="GraphLayout.Commits"/> reference it
/// will read from later (the graph control's current layout) must take its own. The handle
/// closes only when the last lease is released — without this, a RefsChanged re-walk that drops
/// the service's cache could close the handle while the control was still rendering from it.
/// A consumer re-taking a lease it had dropped (a control leaving and re-entering the visual
/// tree) must treat a <c>null</c> from <see cref="AddRef"/> as "this layout is dead, recompute".
/// </para>
/// </summary>
public interface ICommitStore : IReadOnlyList<CommitMetadata>, IDisposable
{
    /// <summary>Hint that rows <paramref name="firstRow"/>..<paramref name="lastRow"/>
    /// (inclusive, clamped by the implementation) are about to be read — lets a disk-backed
    /// store fault the block in with one read. A no-op for the in-memory store.</summary>
    void Prefetch(int firstRow, int lastRow);

    /// <summary>§7.5 find: ascending row indices whose message or author contains
    /// <paramref name="term"/> (case-insensitive), or whose SHA starts with it.</summary>
    IReadOnlyList<int> Search(string term);

    /// <summary>Take an extra lease on the store's backing resources. Every successful call must
    /// be balanced by exactly one <see cref="IDisposable.Dispose"/>; the handle a disk-backed
    /// store holds closes only when the final lease is released. Returns <c>this</c> for call
    /// chaining, or <c>null</c> when the store's handle has already been released (a concurrent
    /// final <see cref="IDisposable.Dispose"/> won the race) — the caller must then discard the
    /// layout that pointed at it and recompute rather than render stubs. The in-memory store
    /// never returns null.</summary>
    ICommitStore? AddRef();
}

/// <summary>In-memory <see cref="ICommitStore"/> — the array the layout engine builds.</summary>
public sealed class ArrayCommitStore(CommitMetadata[] items) : ICommitStore
{
    public static ArrayCommitStore Empty { get; } = new([]);

    public CommitMetadata this[int index] => items[index];
    public int Count => items.Length;
    public void Prefetch(int firstRow, int lastRow) { }

    // In-memory: no handle to lease, nothing to release — a lease can never fail.
    public ICommitStore? AddRef() => this;
    public void Dispose() { }

    public IReadOnlyList<int> Search(string term)
    {
        var hits = new List<int>();
        var t = term.Trim();
        if (t.Length == 0)
            return hits;
        for (var i = 0; i < items.Length; i++)
        {
            var m = items[i];
            if (m.MessageShort.Contains(t, StringComparison.OrdinalIgnoreCase)
                || m.AuthorName.Contains(t, StringComparison.OrdinalIgnoreCase)
                || m.Id.Sha.StartsWith(t, StringComparison.OrdinalIgnoreCase))
                hits.Add(i);
        }
        return hits;
    }

    public IEnumerator<CommitMetadata> GetEnumerator() => ((IEnumerable<CommitMetadata>)items).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => items.GetEnumerator();
}

/// <summary>
/// Complete layout for a contiguous row window (§7.2). Arrays are index-aligned:
/// <c>Rows[i]</c> and <c>Commits[i]</c> describe the same commit.
/// </summary>
public sealed class GraphLayout : IDisposable
{
    /// <summary>A <see cref="RowCount"/>-bounded VIEW over <see cref="SourceState"/>'s
    /// own chunked row accumulator — never a fresh array copy. Computed from <see
    /// cref="SourceState"/>/<see cref="RowCount"/> (both set before this is ever read), so it
    /// needs no initializer of its own. Indexer, <c>Count</c> and <c>Length</c> (source
    /// compatibility with the plain array this replaced) are all safe to call at any time; see
    /// <see cref="GraphRowsView"/>'s own doc comment for the concurrency contract.</summary>
    public GraphRowsView Rows => new(SourceState, RowCount);

    /// <summary>The shared, append-only accumulator that <see cref="DagLayoutEngine"/>
    /// derives lines from on demand. Null only for a layout with no engine behind it (<see
    /// cref="Empty"/>) — there are no rows to derive lines for either way.</summary>
    internal GraphLayoutState? SourceState { get; init; }

    /// <summary>How many of <see cref="SourceState"/>'s real edges existed when this snapshot
    /// was built. Later <c>Extend</c> calls on the same state only append — capping reads here
    /// gives an older snapshot the same lines forever, even while the shared accumulator keeps
    /// growing underneath it (snapshot isolation, §Phase 2).</summary>
    internal int RealEdgeCap { get; init; }

    /// <summary>This snapshot's own off-window stub edges, regenerated fresh at build time from
    /// whichever reservations were still open then — never shared with another snapshot.</summary>
    internal GraphEdge[] OffWindowEdges { get; init; } = [];

    /// <summary>Rows pulled either side of a requested row into one cached derivation, so
    /// scrolling a screenful at a time rarely re-derives (§Phase 2). Only scrolling past the
    /// window's edge gets this overscan. A jump (scrollbar drag, search, reveal) derives just the
    /// requested rows ± <see cref="JumpOverscan"/>, because a drag leaves the window on every
    /// frame and a derivation costs window rows × every lane, on screen or not (§7.6).</summary>
    private const int WindowOverscan = 1_000;

    /// <summary>Rows either side of the requested range in the tight window a jump derives.</summary>
    private const int JumpOverscan = 128;

    /// <summary>A request more than this many rows beyond the current window is a jump; within
    /// it, a scroll. A slow drag of a few hundred rows per frame still counts as a scroll and gets
    /// a full window that lasts several frames.</summary>
    private const int JumpDistance = 256;

    private (int A, int B, RowLaneSegment[] Segs, int[] SegStart, int[] SegCount,
        RowElbow[] Elbs, int[] ElbStart, int[] ElbCount)? _window;

    /// <summary>Per-row commit metadata. Usually an <see cref="ArrayCommitStore"/>; a
    /// disk-backed store when the graph-from-disk setting is on (§7.6). Settable within Core
    /// so <c>GraphLayoutService</c> can swap the array for the paged store after the engine
    /// has built the layout — <c>required</c> is dropped only because an internal setter and a
    /// public required member cannot coexist; <see cref="DagLayoutEngine"/> always sets it.</summary>
    public ICommitStore Commits { get; internal set; } = ArrayCommitStore.Empty;

    /// <summary>Materialized lazily on first access, from <see cref="SourceState"/>'s
    /// real-edge accumulator (capped at <see cref="RealEdgeCap"/>, for the same snapshot-isolation
    /// reason <see cref="Rows"/> is capped at <see cref="RowCount"/>) plus this snapshot's own
    /// <see cref="OffWindowEdges"/>. Nothing in App or Core code actually reads this — only tests
    /// do — so <c>DagLayoutEngine.Build</c> no longer pays to build it on every single Build/Extend
    /// call; <see cref="EdgeCount"/> is a plain field computed without ever materializing it.</summary>
    public GraphEdge[] Edges
    {
        get
        {
            if (_edges is { } cached)
                return cached;
            if (SourceState is not { } state)
                return _edges = OffWindowEdges;

            GraphEdge[] real;
            lock (state)
            {
                var n = Math.Min(RealEdgeCap, state.RealEdges.Count);
                real = new GraphEdge[n];
                for (var i = 0; i < n; i++)
                    real[i] = state.RealEdges[i];
            }
            var combined = new GraphEdge[real.Length + OffWindowEdges.Length];
            real.CopyTo(combined, 0);
            OffWindowEdges.CopyTo(combined, real.Length);
            return _edges = combined;
        }
    }

    private GraphEdge[]? _edges;

    public required GraphDecoration[] Decorations { get; init; }

    public required int RowCount { get; init; }
    public required int EdgeCount { get; init; }
    public required int TotalLaneCount { get; init; }
    public required bool IsComplete { get; init; }

    /// <summary>
    /// Maps <see cref="ObjectId"/> → row index. Built once; used for selection sync only.
    /// <see cref="DagLayoutEngine.Build"/> hands this the accumulator's own, still-growing
    /// dictionary rather than a per-snapshot copy (see its comment) — safe for this snapshot's
    /// own <see cref="Consume"/> writer, but not for an unsynchronized reader while a background
    /// <c>ExtendLayoutAsync</c> is inserting into the very same instance under
    /// <c>lock (existing.State)</c> (#3, 2026-09-12 memory review: <c>CommitGraphControl
    /// .ResolveRow</c> / <c>FindSubject</c> read it from the render thread with no lock at all —
    /// an unsynchronized <see cref="Dictionary{TKey,TValue}"/> read concurrent with an insert
    /// that resizes it can return a wrong row or throw). The value stored here is wrapped so
    /// every lookup — through this property's own <c>TryGetValue</c>/indexer, or through <see
    /// cref="TryGetRow"/> (the preferred call for new code) — takes <c>lock (SourceState)</c>
    /// when this snapshot still has one, and ignores any row at or past this snapshot's own
    /// <see cref="RowCount"/> (an older snapshot must not resolve a row a later extend appended).
    /// Existing callers become safe without themselves changing.
    /// </summary>
    public required IReadOnlyDictionary<ObjectId, int> RowByCommitId
    {
        get => _rowByCommitIdView;
        init { _rawRowByCommitId = value; _rowByCommitIdView = new RowByCommitIdView(this); }
    }

    private IReadOnlyDictionary<ObjectId, int> _rawRowByCommitId = EmptyRowMap;
    private IReadOnlyDictionary<ObjectId, int> _rowByCommitIdView = EmptyRowMap;
    private static readonly IReadOnlyDictionary<ObjectId, int> EmptyRowMap = new Dictionary<ObjectId, int>();

    /// <summary>Locked, bounds-checked row lookup (#3, #13 — 2026-09-12 memory review). Takes
    /// <c>lock (SourceState)</c> when this snapshot has one (every layout except <see
    /// cref="Empty"/>), so it never races the accumulator's writer; ignores any row at or past
    /// this snapshot's own <see cref="RowCount"/>. Preferred over <see cref="RowByCommitId"/>'s
    /// <c>TryGetValue</c> for new code — that property routes through this same method.</summary>
    public bool TryGetRow(ObjectId id, out int row)
    {
        bool found;
        int value;
        if (SourceState is { } state)
        {
            lock (state)
                found = _rawRowByCommitId.TryGetValue(id, out value);
        }
        else
        {
            found = _rawRowByCommitId.TryGetValue(id, out value);
        }

        if (found && value < RowCount)
        {
            row = value;
            return true;
        }

        row = -1;
        return false;
    }

    /// <summary>Thin <see cref="IReadOnlyDictionary{TKey,TValue}"/> facade over <see
    /// cref="RowByCommitId"/> that routes every lookup through <see cref="TryGetRow"/> (see that
    /// property's doc comment). Only <see cref="TryGetValue"/> and the indexer sit on any real
    /// call path; <see cref="Count"/>/<see cref="Keys"/>/<see cref="Values"/>/enumeration exist
    /// for interface completeness (exercised by tests, not by App code) and take the same lock
    /// for a single consistent snapshot.</summary>
    private sealed class RowByCommitIdView(GraphLayout owner) : IReadOnlyDictionary<ObjectId, int>
    {
        public int this[ObjectId key] =>
            owner.TryGetRow(key, out var row) ? row : throw new KeyNotFoundException();

        public bool ContainsKey(ObjectId key) => owner.TryGetRow(key, out _);
        public bool TryGetValue(ObjectId key, out int value) => owner.TryGetRow(key, out value);

        public IEnumerable<ObjectId> Keys => Snapshot().Select(kv => kv.Key);
        public IEnumerable<int> Values => Snapshot().Select(kv => kv.Value);
        public int Count => Snapshot().Count;

        public IEnumerator<KeyValuePair<ObjectId, int>> GetEnumerator() => Snapshot().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private List<KeyValuePair<ObjectId, int>> Snapshot()
        {
            if (owner.SourceState is { } state)
                lock (state)
                    return Filter();
            return Filter();
        }

        private List<KeyValuePair<ObjectId, int>> Filter()
        {
            var rowCount = owner.RowCount;
            var list = new List<KeyValuePair<ObjectId, int>>();
            foreach (var kv in owner._rawRowByCommitId)
                if (kv.Value < rowCount)
                    list.Add(kv);
            return list;
        }
    }

    public ReadOnlySpan<RowLaneSegment> GetRowSegments(int row)
    {
        if ((uint)row >= (uint)RowCount) return [];
        EnsureWindow(row);
        var w = _window!.Value;
        var i = row - w.A;
        return w.SegCount[i] > 0 ? w.Segs.AsSpan(w.SegStart[i], w.SegCount[i]) : [];
    }

    public ReadOnlySpan<RowElbow> GetRowElbows(int row)
    {
        if ((uint)row >= (uint)RowCount) return [];
        EnsureWindow(row);
        var w = _window!.Value;
        var i = row - w.A;
        return w.ElbCount[i] > 0 ? w.Elbs.AsSpan(w.ElbStart[i], w.ElbCount[i]) : [];
    }

    private void EnsureWindow(int row) => EnsureRows(row, row);

    /// <summary>
    /// Makes sure rows <paramref name="first"/>..<paramref name="last"/> are inside the cached
    /// derivation window. A renderer calls this once per frame for the rows it will draw, so the
    /// whole block is derived up front instead of from whichever row is read first (§7.6).
    /// Scrolling just past the window's edge (within <see cref="JumpDistance"/> rows), or a
    /// layout with no window yet, derives the full ± <see cref="WindowOverscan"/>. A jump further
    /// than that derives only the range ± <see cref="JumpOverscan"/>, so a scrollbar drag that
    /// leaves the window every frame stays cheap however many lanes the history has. Allocates
    /// nothing when the range is already covered.
    /// </summary>
    public void EnsureRows(int first, int last)
    {
        if (RowCount == 0) return;
        first = Math.Max(0, first);
        last = Math.Min(RowCount - 1, last);
        if (last < first) return;

        var cur = _window;
        if (cur is { } covered && first >= covered.A && last <= covered.B) return;

        if (SourceState is null)
        {
            var len = last - first + 1;
            _window = (first, last, [], new int[len], new int[len], [], new int[len], new int[len]);
            return;
        }

        int a, b;
        var jump = false;
        if (cur is { } w)
        {
            var gap = first > w.B ? first - w.B : w.A - last;
            jump = gap > JumpDistance;
        }

        if (jump)
        {
            a = first - JumpOverscan;
            b = last + JumpOverscan;
        }
        else
        {
            a = first - WindowOverscan;
            b = last + WindowOverscan;
        }
        a = Math.Max(0, a);
        b = Math.Min(RowCount - 1, b);

        var (segs, segStart, segCount, elbs, elbStart, elbCount) =
            DagLayoutEngine.DeriveWindow(SourceState, RealEdgeCap, OffWindowEdges, RowCount, a, b);
        _window = (a, b, segs, segStart, segCount, elbs, elbStart, elbCount);
        WindowDerivations++;
    }

    /// <summary>How many times this snapshot has derived a window. Tests only.</summary>
    internal int WindowDerivations { get; private set; }

    /// <summary>The cached window's row range, or null before first use or after <see
    /// cref="Dispose"/>. Tests only.</summary>
    internal (int A, int B)? WindowRange => _window is { } w ? (w.A, w.B) : null;

    public static GraphLayout Empty { get; } = new()
    {
        Commits = ArrayCommitStore.Empty,
        Decorations = [],
        RowCount = 0,
        EdgeCount = 0,
        TotalLaneCount = 0,
        IsComplete = true,
        RowByCommitId = new Dictionary<ObjectId, int>(),
    };

    /// <summary>Drops this snapshot's cached derivation window. The shared <see
    /// cref="SourceState"/> is owned by the layout service, not this snapshot, so it is never
    /// disposed here.</summary>
    public void Dispose() { _window = null; }
}

/// <summary>
/// A <see cref="GraphLayout.RowCount"/>-bounded, read-only view over a
/// <see cref="GraphLayoutState"/>'s own chunked row accumulator — what
/// <see cref="GraphLayout.Rows"/> hands back instead of a fresh <c>GraphRow[]</c> copy on every
/// Build/Extend call. Deliberately a plain class with no custom <c>Equals</c>/<c>GetHashCode</c>
/// override (and NOT a record type, which would compare its private fields — including the
/// shared, mutable <c>SourceState</c> reference — rather than the rows themselves): callers that
/// compare two layouts' rows for value equality (xUnit's <c>Assert.Equal</c> included) fall
/// through to a sequence comparison via <see cref="IEnumerable{T}"/>, exactly as they did when
/// this property was a plain array — verified against xUnit's <c>AssertEqualityComparer</c>, which
/// only tries <c>IEquatable&lt;T&gt;</c>/<c>IStructuralEquatable</c> before falling back to an
/// element-wise <c>IEnumerable</c> comparison. <c>Length</c> is kept alongside <c>Count</c> for
/// source compatibility with the array this replaced.
/// <para>
/// Every member here is safe to call from any thread at any time with no lock — see
/// <see cref="ChunkedAppendList{T}"/>'s own concurrency contract, which this is a thin,
/// <see cref="GraphLayout.RowCount"/>-bounded wrapper around.
/// </para>
/// </summary>
public sealed class GraphRowsView(GraphLayoutState? source, int count) : IReadOnlyList<GraphRow>
{
    public int Count => count;

    /// <summary>Source compatibility with the plain <c>GraphRow[]</c> this view replaced.</summary>
    public int Length => count;

    public GraphRow this[int index]
    {
        get
        {
            if ((uint)index >= (uint)count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return source!.Rows[index];
        }
    }

    public IEnumerator<GraphRow> GetEnumerator()
    {
        for (var i = 0; i < count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public enum GraphRefScope { AllRefs, CurrentBranch, Selected }

public enum InvalidationReason { CommitsAppended, RefsChanged, HistoryRewritten, FetchCompleted }

/// <summary>Which refs the graph should include, and how deep (§7.8).</summary>
public sealed record GraphQuery
{
    public GraphRefScope RefScope { get; init; } = GraphRefScope.AllRefs;
    public IReadOnlyList<string>? SelectedRefs { get; init; }
    public bool IncludeStashes { get; init; }
    public bool IncludeRemoteBranches { get; init; } = true;
    public string? PathFilter { get; init; }
    /// <summary>§V3.1: lanes are uncapped — every lineage gets its own track. The former
    /// 48-lane cap collapsed anything beyond lane 47 onto a single dashed overflow lane; on a
    /// busy history that obscured genuine topology. Far lanes are reached by the in-graph pan or
    /// by stretching the Graph column, not by a cap.</summary>
    public int MaxLanes { get; init; } = int.MaxValue;
}
