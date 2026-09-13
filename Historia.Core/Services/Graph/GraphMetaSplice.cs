namespace Historia.Services.Graph;

/// <summary>
/// Phase 6 (v10 graph memory footprint plan, optional): builds a new row order from
/// "added commits on top, then the old order minus removed commits" — without a full re-walk —
/// when the guards below say it's safe and worth it. An existing commit's parents are fixed when
/// it is created, so no existing commit can ever have a NEW commit as a parent; that is what makes
/// prepending added rows to the (unchanged) old order always a VALID topological order over the
/// resulting commit set, regardless of the guards. What the guards protect against is quality
/// (lane/line blowup when many branches moved, not just the trunk) and cost (the delta queries
/// this need can themselves be expensive on a repo with many tips) — both measured in the plan,
/// not assumed.
/// <para>
/// Deliberately has no file or git access, the same design as <see cref="GraphMetaReorder"/>:
/// the caller resolves added/removed commits (via <c>git rev-list &lt;new tips&gt; --not
/// &lt;old tips&gt;</c> and the reverse) and times its own queries; this class only decides
/// whether to trust the result and, if so, computes the resulting order.
/// </para>
/// </summary>
internal static class GraphMetaSplice
{
    /// <summary>Guard 3 (the plan's numbering): splicing only pays off on a large history, where
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
    /// <param name="repoIsShallow">Guard 2 (simplified from the plan's exact recipe — see the
    /// plan doc's Phase 6 status note): the app doesn't currently persist a snapshot of
    /// <c>.git/shallow</c>/<c>info/grafts</c> at splice time to compare against, so this only
    /// checks the CURRENT shallow state, not "unchanged since the order was built".</param>
    /// <param name="oldRowCount">Guard 3: the OLD order's row count.</param>
    /// <param name="addedQueryElapsed">Wall time of the "added" delta query.</param>
    /// <param name="removedQueryElapsed">Wall time of the "removed" delta query.</param>
    /// <param name="lastFullWalkElapsed">Guard 4: how long a full walk of this repo last took —
    /// supplied by the caller (Phase 5 (M5.1): read from the header's <c>LastFullWalkMs</c>,
    /// recorded whenever a walk from row 0 reaches exhaustion). <c>&lt;= TimeSpan.Zero</c> (never
    /// recorded, e.g. a store from before this field existed, or a repo whose disk cache was
    /// wiped) FAILS CLOSED — finding #16 flagged the opposite (skipping the check entirely) as a
    /// bug: an unknown baseline must never be treated as "no budget", since that would let an
    /// arbitrarily slow delta query through unchecked.</param>
    /// <param name="addedCommits">The new commits, in the new walk's topo order (nearest tips
    /// first).</param>
    /// <param name="existingRecordRow">SHA → the OLD order's row for every commit already
    /// recorded (the inverse of the old order, restricted to SHAs actually referenced by it).</param>
    /// <param name="minRowsToConsider">Guard 3's threshold — defaults to <see cref="MinRowsToConsider"/>.
    /// Overridable (Phase 5 (M5.1) step 6) so an automated fixture test can drive the wiring at a
    /// small scale a real 200,000-row history would make impractical to build in a test.</param>
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
        IReadOnlyDictionary<string, int> existingRecordRow,
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
                if (existingRecordRow.TryGetValue(parent.Sha, out var oldRow) && oldRow >= oldParentLimit)
                    return GuardResult.Reject($"an added commit's old parent sits at row {oldRow} >= {oldParentLimit}");

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
