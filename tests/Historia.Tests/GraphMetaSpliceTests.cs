namespace Historia.Tests;

/// <summary>
/// <see cref="GraphMetaSplice"/> is the pure
/// planning half (no file/git access — see its own doc comment) of "splice instead of re-walk
/// after a ref change". These tests exercise the guards individually (each one rejects its own
/// case, matching the plan's own guard table) and the splice construction itself, using small
/// synthetic histories — the plan's own real-repo numbers (linux/vscode/openclaw/workerd) aren't
/// reproducible from this environment, so the guard THRESHOLDS are exercised with explicit
/// parameters rather than an actual 200k-row fixture.
/// </summary>
public sealed class GraphMetaSpliceTests
{
    private static ObjectId Oid(int i) => new(i.ToString("x40"));

    private static RawCommit Commit(int i, params int[] parents) => new(
        Oid(i),
        [.. parents.Select(Oid)],
        new CommitMetadata
        {
            Id = Oid(i),
            MessageShort = $"commit {i}",
            AuthorName = "A",
            AuthorEmail = "a@x",
            AuthorWhen = DateTimeOffset.UnixEpoch.AddMinutes(i),
            CommitWhen = DateTimeOffset.UnixEpoch.AddMinutes(i),
            ParentCount = parents.Length,
        });

    private static readonly TimeSpan FastQuery = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan FullWalkTime = TimeSpan.FromSeconds(20);

    private static GraphMetaSplice.GuardResult EvaluateDefaults(
        bool storeExhausted = true,
        bool oldTipsMatch = true,
        bool repoIsShallow = false,
        int oldRowCount = 300_000,
        TimeSpan? addedElapsed = null,
        TimeSpan? removedElapsed = null,
        TimeSpan? fullWalkElapsed = null,
        IReadOnlyList<RawCommit>? addedCommits = null,
        IReadOnlyDictionary<string, int>? existingRecordRow = null) =>
        GraphMetaSplice.Evaluate(
            storeExhausted, oldTipsMatch, repoIsShallow, oldRowCount,
            addedElapsed ?? FastQuery, removedElapsed ?? FastQuery, fullWalkElapsed ?? FullWalkTime,
            addedCommits ?? [], existingRecordRow ?? new Dictionary<string, int>());

    [Fact]
    public void Every_guard_passing_allows_the_splice()
    {
        var result = EvaluateDefaults();
        Assert.True(result.Allowed);
    }

    [Fact]
    public void Guard1_rejects_a_partial_non_exhausted_store()
    {
        var result = EvaluateDefaults(storeExhausted: false);
        Assert.False(result.Allowed);
    }

    [Fact]
    public void Guard1_rejects_when_the_old_tip_set_does_not_match_meta_tips()
    {
        var result = EvaluateDefaults(oldTipsMatch: false);
        Assert.False(result.Allowed);
    }

    [Fact]
    public void Guard2_rejects_a_shallow_repo()
    {
        var result = EvaluateDefaults(repoIsShallow: true);
        Assert.False(result.Allowed);
    }

    [Fact]
    public void Guard3_rejects_a_history_below_the_row_threshold()
    {
        var result = EvaluateDefaults(oldRowCount: GraphMetaSplice.MinRowsToConsider - 1);
        Assert.False(result.Allowed);
    }

    [Fact]
    public void Guard3_allows_a_history_exactly_at_the_row_threshold()
    {
        var result = EvaluateDefaults(oldRowCount: GraphMetaSplice.MinRowsToConsider);
        Assert.True(result.Allowed);
    }

    [Fact]
    public void Guard4_rejects_a_delta_query_that_exceeds_the_time_budget()
    {
        // Budget is 25% of the full-walk time; 20s * 0.25 = 5s — a 6s query blows it.
        var result = EvaluateDefaults(addedElapsed: TimeSpan.FromSeconds(6));
        Assert.False(result.Allowed);
    }

    [Fact]
    public void Guard4_allows_a_delta_query_within_the_time_budget()
    {
        var result = EvaluateDefaults(addedElapsed: TimeSpan.FromSeconds(4));
        Assert.True(result.Allowed);
    }

    [Fact]
    public void Guard4_fails_closed_when_no_full_walk_time_is_known()
    {
        // An unknown baseline (no recorded
        // LastFullWalkMs — a store from before this field existed, or a wiped disk cache) used to
        // let a delta query of ANY length through unchecked ("skip the check, allow the splice").
        // Fixed to reject instead — even a fast delta query must not pass without a real budget to
        // compare against.
        var result = EvaluateDefaults(addedElapsed: TimeSpan.FromMilliseconds(1), fullWalkElapsed: TimeSpan.Zero);
        Assert.False(result.Allowed);
    }

    [Fact]
    public void Guard4_also_fails_closed_on_a_negative_full_walk_time()
    {
        var result = EvaluateDefaults(fullWalkElapsed: TimeSpan.FromMilliseconds(-1));
        Assert.False(result.Allowed);
    }

    [Fact]
    public void Guard5_rejects_when_an_added_commits_old_parent_sits_beyond_the_row_cap()
    {
        // Commit 100 is an added commit whose parent (commit 5, an OLD commit) sits at old row
        // 250 — beyond MaxOldParentRow (200).
        var added = new List<RawCommit> { Commit(100, 5) };
        var existingRow = new Dictionary<string, int> { [Oid(5).Sha] = 250 };

        var result = EvaluateDefaults(addedCommits: added, existingRecordRow: existingRow);
        Assert.False(result.Allowed);
    }

    [Fact]
    public void Guard5_allows_when_every_added_commits_old_parent_is_within_the_row_cap()
    {
        var added = new List<RawCommit> { Commit(100, 5) };
        var existingRow = new Dictionary<string, int> { [Oid(5).Sha] = 199 };

        var result = EvaluateDefaults(addedCommits: added, existingRecordRow: existingRow);
        Assert.True(result.Allowed);
    }

    [Fact]
    public void Guard5_ignores_a_parent_that_is_itself_a_newly_added_commit()
    {
        // Commit 101's parent (100) is ALSO newly added — not in existingRecordRow at all — so
        // there's nothing to bound; only OLD parents are checked.
        var added = new List<RawCommit> { Commit(101, 100), Commit(100) };
        var result = EvaluateDefaults(addedCommits: added, existingRecordRow: new Dictionary<string, int>());
        Assert.True(result.Allowed);
    }

    [Fact]
    public void Guard3_threshold_is_overridable_for_small_scale_fixture_tests()
    {
        // A real 200,000-row history isn't practical to build in a test —
        // Evaluate's threshold parameters let a fixture drive the wiring at a small scale instead.
        var result = GraphMetaSplice.Evaluate(
            storeExhausted: true, oldTipsMatch: true, repoIsShallow: false, oldRowCount: 20,
            FastQuery, FastQuery, FullWalkTime, [], new Dictionary<string, int>(),
            minRowsToConsider: 20);
        Assert.True(result.Allowed);
    }

    [Fact]
    public void Guard5_threshold_is_overridable_for_small_scale_fixture_tests()
    {
        var added = new List<RawCommit> { Commit(100, 5) };
        var existingRow = new Dictionary<string, int> { [Oid(5).Sha] = 3 };

        var result = GraphMetaSplice.Evaluate(
            storeExhausted: true, oldTipsMatch: true, repoIsShallow: false, oldRowCount: 300_000,
            FastQuery, FastQuery, FullWalkTime, added, existingRow,
            maxOldParentRow: 2);
        Assert.False(result.Allowed);
    }

    // ── Build: the splice construction itself ──

    [Fact]
    public void Build_prepends_added_records_then_the_old_order_minus_removed()
    {
        var oldOrder = new[] { 4, 3, 2, 1, 0 }; // old rows, referencing records 0..4
        var removed = new HashSet<int> { 2 };   // record 2 (old row index 2) is gone
        var added = new List<RawCommit> { Commit(10, 4), Commit(11, 10) };

        var plan = GraphMetaSplice.Build(oldOrder, removed, added, existingRecordCount: 5);

        // New records get indices 5, 6 (existingRecordCount + i).
        Assert.Equal([5, 6, 4, 3, 1, 0], plan.Order);
        Assert.Equal(added, plan.NewRecords);
    }

    [Fact]
    public void Build_is_a_no_op_when_nothing_was_added_or_removed()
    {
        var oldOrder = new[] { 2, 1, 0 };
        var plan = GraphMetaSplice.Build(oldOrder, new HashSet<int>(), [], existingRecordCount: 3);

        Assert.Same(oldOrder, plan.Order); // literally unchanged — the no-op case
        Assert.Empty(plan.NewRecords);
    }

    [Fact]
    public void Build_handles_removal_only_branch_deleted_with_no_new_commits()
    {
        var oldOrder = new[] { 3, 2, 1, 0 };
        var removed = new HashSet<int> { 3 };

        var plan = GraphMetaSplice.Build(oldOrder, removed, [], existingRecordCount: 4);

        Assert.Equal([2, 1, 0], plan.Order);
        Assert.Empty(plan.NewRecords);
    }

    [Fact]
    public void Build_handles_addition_only_a_fast_forward_fetch()
    {
        var oldOrder = new[] { 1, 0 };
        var added = new List<RawCommit> { Commit(10, 1) };

        var plan = GraphMetaSplice.Build(oldOrder, new HashSet<int>(), added, existingRecordCount: 2);

        Assert.Equal([2, 1, 0], plan.Order);
        Assert.Equal(added, plan.NewRecords);
    }

    [Fact]
    public void A_splice_the_guards_accept_is_always_a_valid_topological_order()
    {
        // Validity check mirroring the plan's own measurement: every added commit's parents are
        // either other added commits (ordered correctly among themselves, since we feed them in
        // topo order) or old commits — which, since an existing commit's parents never change,
        // can never be one of the removed/added commits appearing AFTER it in the spliced order.
        var oldOrder = new[] { 2, 1, 0 }; // main: 2 -> 1 -> 0
        // Newest first: 11 (child) before 10 (its parent) — the same convention Commit()/oldOrder
        // use throughout this file.
        var added = new List<RawCommit> { Commit(11, 10), Commit(10, 2) }; // two new commits on tip
        var plan = GraphMetaSplice.Build(oldOrder, new HashSet<int>(), added, existingRecordCount: 3);

        // Row -> sha, to check parent-before-child (a valid topo order lists a commit before its
        // parents when walking newest-first, which is the convention used throughout this file).
        var recordToSha = new Dictionary<int, string> { [0] = Oid(0).Sha, [1] = Oid(1).Sha, [2] = Oid(2).Sha, [3] = Oid(11).Sha, [4] = Oid(10).Sha };
        var shaToNewRow = new Dictionary<string, int>();
        for (var row = 0; row < plan.Order.Length; row++)
            shaToNewRow[recordToSha[plan.Order[row]]] = row;

        var allCommits = new Dictionary<string, RawCommit>
        {
            [Oid(0).Sha] = Commit(0),
            [Oid(1).Sha] = Commit(1, 0),
            [Oid(2).Sha] = Commit(2, 1),
            [Oid(10).Sha] = Commit(10, 2),
            [Oid(11).Sha] = Commit(11, 10),
        };
        foreach (var (sha, row) in shaToNewRow)
            foreach (var parent in allCommits[sha].ParentIds)
                Assert.True(shaToNewRow[parent.Sha] > row, $"{sha}'s parent {parent.Sha} must come after it (newest-first)");
    }
}
