namespace Historia.Services.Graph;

/// <summary>
/// Builds a new row order from
/// "added commits on top, then the old order minus removed commits" — without a full re-walk —
/// when the guards below say it's safe and worth it. Prepending added rows is always a valid
/// topological order, but it equals a fresh <c>--date-order</c> walk only when every added commit
/// is at least as new as the rows it lands above. Guard 6 enforces that. An existing commit's
/// parents are fixed when it is created, so no existing commit can ever have a NEW commit as a
/// parent. What the guards protect against is quality (lane/line blowup when many branches moved,
/// not just the trunk, or date inversions) and cost (the delta queries this needs can themselves
/// be expensive on a repo with many tips) — both measured on real repositories, not assumed.
/// <para>
/// Deliberately has no file or git access, the same design as <see cref="GraphMetaReorder"/>:
/// the caller resolves added/removed commits (via <c>git rev-list &lt;new tips&gt; --not
/// &lt;old tips&gt;</c> and the reverse) and times its own queries; this class only decides
/// whether to trust the result and, if so, computes the resulting order.
/// </para>
/// <para>
/// <see cref="Evaluate"/>'s guards are individually-justified sufficient conditions standing
/// in for a true positive invariant (&quot;output equals a fresh <c>--date-order</c> walk&quot;) that
/// is not cheaply checkable without a full re-walk, defeating the point of splicing. A future
/// correctness gap found in this function should be fixed the way guard 6 was: add a new guard
/// with its own regression test, documented the same way as the existing guards, rather than
/// restructuring the whole function.
/// </para>
/// </summary>
internal static class GraphMetaSplice
{
    /// <summary>Guard 3: splicing only pays off on a large history, where
    /// a full re-walk is itself expensive.</summary>
    internal const int MinRowsToConsider = 200_000;

    /// <summary>Guard 5: every added commit's parents that are already-recorded (old) commits
    /// must lie within this many rows of the top of the OLD order — bounds how far any existing
    /// row's rendered position could be implied to have "moved" relative to git's own placement,
    /// even though the old rows themselves are never renumbered.</summary>
    internal const int MaxOldParentRow = 200;

    /// <summary>Guard 4: a delta query is only trusted if it finishes within this fraction of the
    /// last known full-walk time for this repo.</summary>
    internal const double MaxDeltaFractionOfFullWalk = 0.25;

    internal readonly record struct GuardResult(bool Allowed, string Reason)
    {
        internal static GuardResult Ok() => new(true, "");
        internal static GuardResult Reject(string reason) => new(false, reason);
    }

    /// <summary>
    /// Evaluates every guard. All must pass for a splice to be trusted; otherwise the caller
    /// falls back to <see cref="GraphMetaReorder"/>'s full re-walk reorder.
    /// </summary>
    /// <param name="storeExhausted">Guard 1: the store must be exhausted — a partial store has no
    /// valid order to splice from.</param>
    /// <param name="oldTipsMatch"><c>.meta.tips</c> equals the tip set the old order was built
    /// from (guard 1 continued — the caller resolves this by comparing sorted SHA lists).</param>
    /// <param name="repoIsShallow">Guard 2: checks the CURRENT shallow state.</param>
    /// <param name="oldRowCount">Guard 3: the OLD order's row count.</param>
    /// <param name="addedQueryElapsed">Wall time of the "added" delta query.</param>
    /// <param name="removedQueryElapsed">Wall time of the "removed" delta query.</param>
    /// <param name="lastFullWalkElapsed">Guard 4: how long a full walk of this repo last took —
    /// read from the header's <c>LastFullWalkMs</c> (recorded whenever a walk from row 0 reaches
    /// exhaustion). <c>&lt;= TimeSpan.Zero</c> (never recorded, e.g. a repo whose disk cache was
    /// wiped) FAILS CLOSED: an unknown baseline must never be treated as "no budget", since that
    /// would let an arbitrarily slow delta query through unchecked.</param>
    /// <param name="addedCommits">The new commits, in the new walk's topo order (nearest tips
    /// first).</param>
    /// <param name="existingRecordRow">SHA → the OLD order's row for every commit already
    /// recorded (the inverse of the old order, restricted to SHAs actually referenced by it).</param>
    /// <param name="newestOldTopCommitWhen">Guard 6: the newest commit timestamp among the top
    /// <see cref="MaxOldParentRow"/> rows of the old order. Every added commit must be at least as
    /// new as this timestamp.</param>
    /// <param name="minRowsToConsider">Guard 3's threshold — defaults to <see cref="MinRowsToConsider"/>.
    /// Overridable so an automated fixture test can drive the wiring at a smaller scale.</param>
    /// <param name="maxOldParentRow">Guard 5's threshold — defaults to <see cref="MaxOldParentRow"/>,
    /// overridable for the same reason.</param>
    internal static GuardResult Evaluate(
        bool storeExhausted,
        bool oldTipsMatch,
        bool repoIsShallow,
        int oldRowCount,
        TimeSpan addedQueryElapsed,
        TimeSpan removedQueryElapsed,
        TimeSpan lastFullWalkElapsed,
        IReadOnlyList<RawCommit> addedCommits,
        IReadOnlyDictionary<GitObjectId, int> existingRecordRow,
        DateTimeOffset newestOldTopCommitWhen,
        int? minRowsToConsider = null,
        int? maxOldParentRow = null)
    {
        if (!storeExhausted)
            return GuardResult.Reject("store not exhausted");
        if (!oldTipsMatch)
            return GuardResult.Reject(".meta.tips does not match the old order's tip set");
        if (repoIsShallow)
            return GuardResult.Reject("repo is shallow");
        if (oldRowCount < (minRowsToConsider ?? MinRowsToConsider))
            return GuardResult.Reject($"history too small ({oldRowCount} < {minRowsToConsider ?? MinRowsToConsider})");

        if (lastFullWalkElapsed <= TimeSpan.Zero)
            return GuardResult.Reject("last full-walk time is unknown");
        var budget = lastFullWalkElapsed * MaxDeltaFractionOfFullWalk;
        if (addedQueryElapsed > budget || removedQueryElapsed > budget)
            return GuardResult.Reject("delta query exceeded the time budget");

        var oldParentLimit = maxOldParentRow ?? MaxOldParentRow;
        foreach (var commit in addedCommits)
            foreach (var parent in commit.ParentIds)
                if (existingRecordRow.TryGetValue(parent, out var oldRow) && oldRow >= oldParentLimit)
                    return GuardResult.Reject($"an added commit's old parent sits at row {oldRow} >= {oldParentLimit}");

        foreach (var commit in addedCommits)
            if (commit.Metadata.CommitWhen < newestOldTopCommitWhen)
                return GuardResult.Reject("an added commit is older than the old order's top rows");

        return GuardResult.Ok();
    }

    internal readonly record struct Plan(int[] Order, IReadOnlyList<RawCommit> NewRecords);

    /// <summary>
    /// Builds the spliced order: <paramref name="addedCommits"/> (new records, prepended in their
    /// own topo order) followed by every row of <paramref name="oldOrder"/> whose commit is NOT in
    /// <paramref name="removedRecords"/>. Always a valid topological order over the resulting
    /// commit set when the caller's guards (<see cref="Evaluate"/>) passed — see this class's own
    /// doc comment for why removal can never invalidate it. No-op case (nothing added or removed):
    /// returns <paramref name="oldOrder"/> unchanged with no new records.
    /// </summary>
    internal static Plan Build(
        int[] oldOrder,
        IReadOnlySet<int> removedRecords,
        IReadOnlyList<RawCommit> addedCommits,
        int existingRecordCount)
    {
        if (addedCommits.Count == 0 && removedRecords.Count == 0)
            return new Plan(oldOrder, []);

        var kept = removedRecords.Count == 0
            ? oldOrder
            : [.. oldOrder.Where(rec => !removedRecords.Contains(rec))];

        if (addedCommits.Count == 0)
            return new Plan(kept, []);

        var addedRecordIndices = new int[addedCommits.Count];
        for (var i = 0; i < addedCommits.Count; i++)
            addedRecordIndices[i] = existingRecordCount + i;

        var order = new int[addedRecordIndices.Length + kept.Length];
        addedRecordIndices.CopyTo(order, 0);
        kept.CopyTo(order, addedRecordIndices.Length);

        return new Plan(order, addedCommits);
    }
}
