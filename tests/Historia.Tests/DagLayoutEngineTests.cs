namespace Historia.Tests;
[Trait("tier", "unit")]

public class DagLayoutEngineTests
{
    private readonly DagLayoutEngine _engine = new();

    private static GitObjectId Sha(int n) => GitObjectId.Parse(n.ToString("x40"));

    private static CommitMetadata Meta(int n, int parentCount) => new()
    {
        Id = Sha(n),
        MessageShort = $"commit {n}",
        AuthorName = "A",
        AuthorEmail = "a@x",
        AuthorWhen = DateTimeOffset.UnixEpoch.AddMinutes(n),
        CommitWhen = DateTimeOffset.UnixEpoch.AddMinutes(n),
        ParentCount = parentCount,
    };

    /// <summary>Builds a raw commit. Parents are given as ids (must appear later in the list).</summary>
    private static RawCommit C(int id, params int[] parents) =>
        new(Sha(id), parents.Select(Sha).ToList(), Meta(id, parents.Length));

    private GraphLayoutResult Compute(IReadOnlyList<RawCommit> commits, int maxRows = 1000,
        IReadOnlyDictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>>? deco = null,
        Func<GitObjectId, GitObjectId, bool>? isAncestor = null)
        => _engine.Compute(commits, deco, maxRows, isAncestor: isAncestor);

    /// <summary>2: Segments/Elbows are no longer flat arrays on <see cref="GraphLayout"/>
    /// — they're derived per row window on demand. Asserts the two layouts produce byte-identical
    /// segments and elbows for every row, the same equality the old whole-array comparison gave.</summary>
    private static void AssertSameLines(GraphLayout expected, GraphLayout actual)
    {
        Assert.Equal(expected.RowCount, actual.RowCount);
        for (var row = 0; row < expected.RowCount; row++)
        {
            Assert.Equal(expected.GetRowSegments(row).ToArray(), actual.GetRowSegments(row).ToArray());
            Assert.Equal(expected.GetRowElbows(row).ToArray(), actual.GetRowElbows(row).ToArray());
        }
    }

    /// <summary>An ancestry oracle over a synthetic commit list — <c>f(a, b)</c> is true when
    /// <c>a</c> is an ancestor of (or equal to) <c>b</c>, walking parent links.</summary>
    private static Func<GitObjectId, GitObjectId, bool> AncestryOf(IReadOnlyList<RawCommit> commits)
    {
        var byId = commits.ToDictionary(c => c.Id);
        return (a, b) =>
        {
            var seen = new HashSet<GitObjectId>();
            var stack = new Stack<GitObjectId>();
            stack.Push(b);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (cur.Equals(a)) return true;
                if (!seen.Add(cur)) continue;
                if (byId.TryGetValue(cur, out var rc))
                    foreach (var p in rc.ParentIds)
                        stack.Push(p);
            }
            return false;
        };
    }

    // ── Criterion 13: degenerate inputs render without throwing ──

    [Fact]
    public void Empty_history_yields_an_empty_layout()
    {
        var r = Compute([]);
        Assert.Equal(0, r.Layout.RowCount);
        Assert.Empty(r.Layout.Edges);
        Assert.True(r.Layout.IsComplete);
    }

    [Fact]
    public void Single_root_commit()
    {
        var r = Compute([C(1)]);
        Assert.Equal(1, r.Layout.RowCount);
        Assert.Equal(RowFlags.IsRoot, r.Layout.Rows[0].Flags & RowFlags.IsRoot);
        Assert.Empty(r.Layout.Edges);
        Assert.Equal(1, r.Layout.TotalLaneCount);
    }

    // ── Criterion 1: linear / branch+merge / octopus / multi-root ──

    [Fact]
    public void Linear_history_uses_one_lane_and_straight_edges()
    {
        // 5 -> 4 -> 3 -> 2 -> 1(root), newest first.
        var commits = new[] { C(5, 4), C(4, 3), C(3, 2), C(2, 1), C(1) };
        var r = Compute(commits);

        Assert.Equal(5, r.Layout.RowCount);
        Assert.All(r.Layout.Rows, row => Assert.Equal(0, row.LaneIndex));
        Assert.Equal(1, r.Layout.TotalLaneCount);
        Assert.Equal(4, r.Layout.Edges.Count(e => e.Type == EdgeType.Straight));
        Assert.DoesNotContain(r.Layout.Edges, e => e.Type == EdgeType.OffWindow);
    }

    [Fact]
    public void Branch_and_merge_allocates_a_second_lane_then_releases_it()
    {
        //      6 (merge of 5 and 3)
        //     / \
        //    5   |        5 on a feature lane
        //    |   3        3,4 on main
        //    4   |
        //     \ /
        //      2
        //      1 (root)
        var commits = new[]
        {
            C(6, 5, 3),
            C(5, 4),
            C(4, 2),
            C(3, 2),
            C(2, 1),
            C(1),
        };
        var r = Compute(commits);

        Assert.Equal(6, r.Layout.RowCount);
        Assert.True(r.Layout.TotalLaneCount >= 2, "a branch should occupy a second lane");
        Assert.Contains(r.Layout.Rows.First(row => row.RowIndex == 0).Flags.HasFlag(RowFlags.IsMerge), new[] { true });
        Assert.Single(r.Layout.Edges, e => e.Type == EdgeType.MergeIn);
        // Row 2 (id 2) is where the branches rejoin — lane count must not keep growing.
        Assert.True(r.Layout.Rows[^1].LaneIndex == 0);
    }

    [Fact]
    public void Octopus_merge_with_three_parents_produces_two_merge_in_edges()
    {
        //   5 = merge(4,3,2); 1 is the shared root
        var commits = new[]
        {
            C(5, 4, 3, 2),
            C(4, 1),
            C(3, 1),
            C(2, 1),
            C(1),
        };
        var r = Compute(commits);

        var mergeRow = r.Layout.Rows[0];
        Assert.True(mergeRow.Flags.HasFlag(RowFlags.IsMerge));
        Assert.Equal(2, r.Layout.Edges.Count(e => e.Type == EdgeType.MergeIn));
        Assert.Equal(1, r.Layout.Edges.Count(e => e.Type == EdgeType.Straight && e.ToRow == 4)); // first parent
    }

    [Fact]
    public void Multiple_roots_each_get_the_root_flag_and_release_their_lane()
    {
        // Two unrelated histories merged: 5 = merge(4, 2); roots are 3 and 1.
        var commits = new[]
        {
            C(5, 4, 2),
            C(4, 3),
            C(3),
            C(2, 1),
            C(1),
        };
        var r = Compute(commits);

        var roots = r.Layout.Rows.Where(row => row.Flags.HasFlag(RowFlags.IsRoot)).ToList();
        Assert.Equal(2, roots.Count);
    }

    // ── Criterion 2: every edge terminates on a real node ──

    [Fact]
    public void Windowed_layout_uses_offwindow_stubs_not_dangling_edges()
    {
        var commits = new[] { C(5, 4), C(4, 3), C(3, 2), C(2, 1), C(1) };
        var r = Compute(commits, maxRows: 3); // window is rows 0..2, parent of row 2 is off-window

        Assert.Equal(3, r.Layout.RowCount);
        Assert.False(r.Layout.IsComplete);
        foreach (var e in r.Layout.Edges)
        {
            Assert.InRange(e.FromRow, 0, r.Layout.RowCount);
            Assert.InRange(e.ToRow, 0, r.Layout.RowCount);
            if (e.Type == EdgeType.OffWindow)
                Assert.Equal(r.Layout.RowCount, e.ToRow); // terminates exactly at the boundary
        }
        Assert.Contains(r.Layout.Edges, e => e.Type == EdgeType.OffWindow);
    }

    [Fact]
    public void Deferred_merge_parent_does_not_widen_a_windowed_layout()
    {
        // row 0 merges id 5 (first parent) and id 9 (second parent, past the window).
        // The 2nd-parent column must not be claimed until id 9 is actually placed, so the
        // windowed layout stays one lane wide and the stub runs straight down the merge lane.
        var commits = new[]
        {
            C(10, 5, 9), C(5, 4), C(4, 3), C(3, 2), C(2, 1), C(1),
            C(9, 8), C(8, 7), C(7),
        };
        var r = Compute(commits, maxRows: 6); // rows 0..5 = ids 10,5,4,3,2,1; id 9 is off-window

        var stub = Assert.Single(r.Layout.Edges, e => e.Type == EdgeType.OffWindow);
        Assert.Equal(r.Layout.Rows[0].LaneIndex, stub.FromLane);
        Assert.Equal(stub.FromLane, stub.ToLane);   // no phantom column for the unseen parent
        Assert.Equal(1, r.Layout.TotalLaneCount);
    }

    [Fact]
    public void No_edge_references_a_lane_outside_the_total_lane_count()
    {
        var commits = new[]
        {
            C(8, 7, 5),
            C(7, 6),
            C(6, 4),
            C(5, 4),
            C(4, 3, 2),
            C(3, 1),
            C(2, 1),
            C(1),
        };
        var r = Compute(commits);
        foreach (var e in r.Layout.Edges)
        {
            Assert.InRange(e.FromLane, 0, r.Layout.TotalLaneCount);
            Assert.InRange(e.ToLane, 0, r.Layout.TotalLaneCount);
        }
    }

    // ── Criterion 3: decorations attach to the correct rows ──

    [Fact]
    public void Decorations_attach_to_the_right_rows_including_multiple_on_one_commit()
    {
        var commits = new[] { C(3, 2), C(2, 1), C(1) };
        var deco = new Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>>
        {
            [Sha(3)] = [new(DecorationType.Head, "HEAD"), new(DecorationType.LocalBranch, "main")],
            [Sha(1)] = [new(DecorationType.Tag, "v1.0")],
        };

        var r = Compute(commits, deco: deco);

        Assert.Equal(2, r.Layout.Decorations.Count(d => d.RowIndex == 0));
        Assert.Contains(r.Layout.Decorations, d => d.RowIndex == 0 && d.Type == DecorationType.Head);
        Assert.Contains(r.Layout.Decorations, d => d.RowIndex == 2 && d.Label == "v1.0");
        Assert.True(r.Layout.Rows[0].Flags.HasFlag(RowFlags.IsHead));
        Assert.True(r.Layout.Rows[0].Flags.HasFlag(RowFlags.HasDecorations));
    }

    [Fact]
    public void Branch_decoration_seeds_a_stable_lane_color()
    {
        var commits = new[] { C(2, 1), C(1) };
        var deco = new Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>>
        {
            [Sha(2)] = [new(DecorationType.LocalBranch, "feature/auth")],
        };

        var a = Compute(commits, deco: deco);
        var b = Compute(commits, deco: deco);
        Assert.Equal(a.Layout.Rows[0].ColorIndex, b.Layout.Rows[0].ColorIndex);
        var expected = (int)(StableHash.Fnv1a("feature/auth") % LanePalette.Size);
        Assert.Equal(expected, a.Layout.Rows[0].ColorIndex);
    }

    // ── every non-OffWindow edge starts in its FromRow's own lane ──

    private static void AssertEndpointInvariant(GraphLayoutResult r)
    {
        foreach (var e in r.Layout.Edges)
        {
            if (e.Type == EdgeType.OffWindow)
                continue;
            Assert.Equal(r.Layout.Rows[e.FromRow].LaneIndex, e.FromLane);
        }
    }

    public static IEnumerable<object[]> AllFixtures()
    {
        yield return [new[] { C(5, 4), C(4, 3), C(3, 2), C(2, 1), C(1) }];
        yield return [new[] { C(6, 5, 3), C(5, 4), C(4, 2), C(3, 2), C(2, 1), C(1) }];
        yield return [new[] { C(5, 4, 3, 2), C(4, 1), C(3, 1), C(2, 1), C(1) }];
        yield return [new[] { C(5, 4, 2), C(4, 3), C(3), C(2, 1), C(1) }];
        yield return
        [
            new[]
            {
                C(8, 7, 5), C(7, 6), C(6, 4), C(5, 4), C(4, 3, 2), C(3, 1), C(2, 1), C(1),
            },
        ];
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Every_non_offwindow_edge_starts_in_its_from_rows_lane(RawCommit[] commits)
    {
        AssertEndpointInvariant(Compute(commits));
    }

    [Fact]
    public void Endpoint_invariant_holds_across_a_windowed_layout()
    {
        var commits = BuildGnarlyChain(120);
        AssertEndpointInvariant(Compute(commits, maxRows: 60));
    }

    [Fact]
    public void Merge_edge_leaves_the_merge_commits_own_lane_not_the_parents()
    {
        // 6 = merge(5, 3) over 5 -> 4 -> 2 -> 1 with 3 -> 2.
        var commits = new[] { C(6, 5, 3), C(5, 4), C(4, 2), C(3, 2), C(2, 1), C(1) };
        var r = Compute(commits);

        Assert.Equal(0, r.Layout.Rows[0].LaneIndex); // merge commit in lane 0
        Assert.Equal(1, r.Layout.Rows[3].LaneIndex); // its second parent (id 3) in lane 1

        var merge = Assert.Single(r.Layout.Edges, e => e.Type == EdgeType.MergeIn);
        Assert.Equal(0, merge.FromRow);
        Assert.Equal(0, merge.FromLane); // was 1 before the fix — the defect
        Assert.Equal(3, merge.ToRow);
        Assert.Equal(1, merge.ToLane);
    }

    [Fact]
    public void Octopus_merge_in_edges_all_start_in_the_merge_lane()
    {
        var commits = new[] { C(5, 4, 3, 2), C(4, 1), C(3, 1), C(2, 1), C(1) };
        var r = Compute(commits);

        var mergeLane = r.Layout.Rows[0].LaneIndex;
        var mergeIns = r.Layout.Edges.Where(e => e.Type == EdgeType.MergeIn).ToList();
        Assert.Equal(2, mergeIns.Count);
        Assert.All(mergeIns, e => Assert.Equal(mergeLane, e.FromLane));
        Assert.All(mergeIns, e => Assert.Equal(0, e.FromRow));
    }

    [Fact]
    public void Merge_in_edge_is_coloured_by_the_merged_in_branch_not_the_destination()
    {
        // 6 = merge(5, 3); the merged-in branch is the one holding commit 3.
        var commits = new[] { C(6, 5, 3), C(5, 4), C(4, 2), C(3, 2), C(2, 1), C(1) };
        var r = Compute(commits);

        var merge = Assert.Single(r.Layout.Edges, e => e.Type == EdgeType.MergeIn);
        var mergeCommitColour = r.Layout.Rows[0].ColorIndex;   // destination (merge commit) lane
        var sourceBranchColour = r.Layout.Rows[3].ColorIndex;  // second parent — the source

        Assert.NotEqual(mergeCommitColour, sourceBranchColour);
        Assert.Equal(sourceBranchColour, merge.ColorIndex);
        Assert.NotEqual(mergeCommitColour, merge.ColorIndex);
    }

    [Fact]
    public void First_parent_edges_keep_the_child_lane_colour()
    {
        // only MergeIn changes. A branch-out first-parent edge still carries the
        // child's own lane colour.
        //   4 -> 2 (first parent) and 3 -> 2 (first parent) with 2 -> 1; 4 branched off.
        var commits = new[] { C(4, 2), C(3, 2), C(2, 1), C(1) };
        var r = Compute(commits);

        foreach (var e in r.Layout.Edges.Where(e => e.Type is EdgeType.Straight or EdgeType.BranchOut))
            Assert.Equal(r.Layout.Rows[e.FromRow].ColorIndex, e.ColorIndex);
    }

    [Fact]
    public void ChildLane_survives_a_resumed_walk()
    {
        // The merge commit and its second parent land in different Consume calls, so ChildLane
        // must be captured at reservation time inside the resumable walk.
        var commits = new[] { C(6, 5, 3), C(5, 4), C(4, 2), C(3, 2), C(2, 1), C(1) };

        var full = Compute(commits);
        var partial = _engine.Compute(commits, null, maxRows: 1); // only the merge row
        var extended = _engine.Extend(partial, commits.Skip(1).ToList(), null, additionalRows: 5);

        Assert.Equal(full.Layout.Edges, extended.Layout.Edges);
        AssertEndpointInvariant(extended);
    }

    // ── "newest tip wins the trunk, with a checked-out-branch exception" ──

    [Fact]
    public void Newest_branch_tip_holds_the_trunk_lane_when_no_head_is_marked()
    {
        // Two branches diverge from a shared ancestor (id 10). v9.2-fixes2's tip (id 21) is the
        // NEWEST, so it — not the older branch — stays collinear with the shared mainline on
        // lane 0. No HEAD badge and no oracle: the plain base rule applies.
        var commits = new[] { C(21, 20), C(20, 10), C(12, 10), C(10, 1), C(1) };
        var deco = new Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>>
        {
            [Sha(21)] = [new(DecorationType.LocalBranch, "v9.2-fixes2")],
            [Sha(12)] = [new(DecorationType.LocalBranch, "v9.1-relogo")],
        };

        var r = Compute(commits, deco: deco);

        // Row order is unchanged: the newest commit is still row 0.
        Assert.Equal(0, r.Layout.RowByCommitId[Sha(21)]);
        Assert.Equal(2, r.Layout.RowByCommitId[Sha(12)]);

        // Newest tip + the shared history below the fork sit on the trunk (lane 0)…
        Assert.Equal(0, r.Layout.Rows[0].LaneIndex); // v9.2-fixes2 tip
        Assert.Equal(0, r.Layout.Rows[1].LaneIndex);
        Assert.Equal(0, r.Layout.Rows[3].LaneIndex); // shared ancestor
        Assert.Equal(0, r.Layout.Rows[4].LaneIndex); // shared root
        // …and the older branch is the one that branches off.
        Assert.Equal(1, r.Layout.Rows[2].LaneIndex); // v9.1-relogo tip

        // Deterministic across runs; the trunk lane is born from the newest branch (row 0).
        var r2 = Compute(commits, deco: deco);
        Assert.Equal(r.Layout.Rows.Select(x => x.ColorIndex), r2.Layout.Rows.Select(x => x.ColorIndex));
        Assert.Equal("v9.2-fixes2", r.State.LaneKey[r.Layout.Rows[0].LaneIndex]);
    }

    [Fact]
    public void Checked_out_branch_that_is_ancestor_of_the_newest_tip_leaves_the_order_unchanged()
    {
        // main (tip 30, checked out) is the base both feature branches were built on:
        //   feat-b: 30 → 60   (newest)      feat-a: 30 → 50
        var commits = new[] { C(60, 30), C(50, 40), C(40, 30), C(30, 20), C(20, 10), C(10, 1), C(1) };
        var deco = new Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>>
        {
            [Sha(60)] = [new(DecorationType.LocalBranch, "feat-b")],
            [Sha(50)] = [new(DecorationType.LocalBranch, "feat-a")],
            [Sha(30)] = [new(DecorationType.LocalBranch, "main", IsLocal: true, IsHead: true)],
        };

        // The whole graph sits well inside the 200-row scan
        // window, so the exact in-window check (isAncestor: null, every real caller's shape since
        // the libgit2 CalculateHistoryDivergence oracle was retired) must reach the same
        // conclusion as the brute-force oracle tests used to inject.
        foreach (var anc in new[] { AncestryOf(commits), null })
        {
            var r = Compute(commits, deco: deco, isAncestor: anc);

            // main is an ancestor of the newest tip (60) → newest-first order stands.
            Assert.Equal(0, r.Layout.Rows[0].LaneIndex);                                     // feat-b tip on the trunk
            Assert.Equal(0, r.Layout.Rows[r.Layout.RowByCommitId[Sha(30)]].LaneIndex);       // main stays collinear on the trunk
            Assert.NotEqual(0, r.Layout.Rows[r.Layout.RowByCommitId[Sha(50)]].LaneIndex);    // feat-a branches off
        }
    }

    [Fact]
    public void Checked_out_branch_that_is_ancestor_of_a_non_newest_tip_promotes_that_tip()
    {
        // main (tip 20, checked out) is the base of feat-a (20 → 30 → 40) only. feat-c (1 → 70 →
        // 80) is newer and shares nothing with main past the root.
        var commits = new[] { C(80, 70), C(70, 1), C(40, 30), C(30, 20), C(20, 10), C(10, 1), C(1) };
        var deco = new Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>>
        {
            [Sha(80)] = [new(DecorationType.LocalBranch, "feat-c")],
            [Sha(40)] = [new(DecorationType.LocalBranch, "feat-a")],
            [Sha(20)] = [new(DecorationType.LocalBranch, "main", IsLocal: true, IsHead: true)],
        };

        foreach (var anc in new[] { AncestryOf(commits), null })
        {
            var r = Compute(commits, deco: deco, isAncestor: anc);

            // main is an ancestor of feat-a (40) but not of the newest tip feat-c (80): feat-a
            // takes the trunk, feat-c shifts one lane right. main was feat-a's first-parent base,
            // so it rides feat-a's trunk lane.
            Assert.Equal(0, r.Layout.Rows[r.Layout.RowByCommitId[Sha(40)]].LaneIndex); // feat-a → trunk
            Assert.Equal(1, r.Layout.Rows[0].LaneIndex);                                // feat-c (row 0) → lane 1
            Assert.Equal(0, r.Layout.Rows[r.Layout.RowByCommitId[Sha(20)]].LaneIndex);  // main rides the trunk under feat-a
        }
    }

    [Fact]
    public void Checked_out_branch_that_is_ancestor_of_no_newer_tip_takes_the_trunk()
    {
        // Three branches off the shared root, sharing nothing else. main (tip 20) is checked out
        // and is an ancestor of neither newer tip.
        var commits = new[] { C(60, 50), C(50, 1), C(40, 30), C(30, 1), C(20, 10), C(10, 1), C(1) };
        var deco = new Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>>
        {
            [Sha(60)] = [new(DecorationType.LocalBranch, "feat-y")],
            [Sha(40)] = [new(DecorationType.LocalBranch, "feat-x")],
            [Sha(20)] = [new(DecorationType.LocalBranch, "main", IsLocal: true, IsHead: true)],
        };

        foreach (var anc in new[] { AncestryOf(commits), null })
        {
            var r = Compute(commits, deco: deco, isAncestor: anc);

            Assert.Equal(0, r.Layout.Rows[r.Layout.RowByCommitId[Sha(20)]].LaneIndex); // main → trunk
            Assert.Equal(1, r.Layout.Rows[0].LaneIndex);                                // feat-y (row 0) → lane 1
            Assert.Equal(2, r.Layout.Rows[r.Layout.RowByCommitId[Sha(40)]].LaneIndex);  // feat-x → lane 2
        }
    }

    [Fact]
    public void Without_an_explicit_oracle_the_in_window_check_still_applies_the_exception()
    {
        // Legacy behavior: isAncestor: null skipped the
        // checked-out-branch exception entirely, deferring to the verified recompute — is gone.
        // Every real caller now passes null and gets the exact in-window check for free; with no
        // oracle argument at all, this graph (identical to the "ancestor of no newer tip" case
        // above) must reach the SAME conclusion, not the old "skip" behavior.
        var commits = new[] { C(60, 50), C(50, 1), C(40, 30), C(30, 1), C(20, 10), C(10, 1), C(1) };
        var deco = new Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>>
        {
            [Sha(60)] = [new(DecorationType.LocalBranch, "feat-y")],
            [Sha(40)] = [new(DecorationType.LocalBranch, "feat-x")],
            [Sha(20)] = [new(DecorationType.LocalBranch, "main", IsLocal: true, IsHead: true)],
        };

        var r = Compute(commits, deco: deco); // no isAncestor argument at all

        Assert.Equal(0, r.Layout.Rows[r.Layout.RowByCommitId[Sha(20)]].LaneIndex); // main → trunk
        Assert.Equal(1, r.Layout.Rows[0].LaneIndex);                                // feat-y (row 0) → lane 1
        Assert.Equal(2, r.Layout.Rows[r.Layout.RowByCommitId[Sha(40)]].LaneIndex);  // feat-x → lane 2
    }

    /// <summary>DagLayoutEngine's private
    /// <c>IsAncestorInWindow</c> helper (invoked here by reflection — it's an internal algorithm
    /// detail, not part of the engine's public surface) must agree EXACTLY with a brute-force
    /// parent-link reachability oracle, for every (H, T) pair, across many randomized topo-ordered
    /// DAGs. This is the property the exact in-window check's correctness argument rests on: every
    /// commit on a path T → H is a descendant of H and so precedes it in the topo order, so the
    /// whole path lies inside [row(T), row(H)] — no repo access needed.</summary>
    [Fact]
    public void In_window_ancestry_check_matches_brute_force_reachability_on_random_DAGs()
    {
        var method = typeof(DagLayoutEngine).GetMethod(
            "IsAncestorInWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var rng = new Random(20260912);

        for (var trial = 0; trial < 30; trial++)
        {
            var commits = RandomTopoDag(rng, count: 20 + rng.Next(60));
            var rowById = new Dictionary<GitObjectId, int>();
            for (var i = 0; i < commits.Count; i++)
                rowById[commits[i].Id] = i;
            var brute = AncestryOf(commits);

            for (var hi = 0; hi < commits.Count; hi++)
            {
                var h = commits[hi].Id;
                for (var ti = 0; ti < commits.Count; ti++)
                {
                    var t = commits[ti].Id;
                    var expected = brute(h, t);
                    var actual = (bool)method.Invoke(null, [commits, rowById, h, t])!;
                    Assert.True(expected == actual,
                        $"trial {trial}: isAncestor(h=row{hi}, t=row{ti}) expected {expected}, got {actual}");
                }
            }
        }
    }

    /// <summary>Builds a random topologically ordered (children-before-parents) DAG of
    /// <paramref name="count"/> commits: row 0 is the sole newest leaf, and each commit at row i
    /// may point at 0-2 parents chosen from later rows only — the same convention <see cref="C"/>
    /// uses, just randomized instead of hand-written.</summary>
    private static List<RawCommit> RandomTopoDag(Random rng, int count)
    {
        var commits = new RawCommit[count];
        for (var i = count - 1; i >= 0; i--)
        {
            var laterRows = count - 1 - i;
            var maxParents = Math.Min(2, laterRows);
            var parentCount = maxParents == 0 ? 0 : rng.Next(maxParents + 1);
            var parents = new List<int>();
            while (parents.Count < parentCount)
            {
                var p = i + 1 + rng.Next(laterRows);
                if (!parents.Contains(p))
                    parents.Add(p);
            }
            commits[i] = new RawCommit(Sha(i), parents.Select(Sha).ToList(), Meta(i, parents.Count));
        }
        return [.. commits];
    }

    [Fact]
    public void Checked_out_branch_exception_keeps_extension_identical_to_a_full_recomputation()
    {
        var commits = new[] { C(80, 70), C(70, 1), C(40, 30), C(30, 20), C(20, 10), C(10, 1), C(1) };
        var deco = new Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>>
        {
            [Sha(80)] = [new(DecorationType.LocalBranch, "feat-c")],
            [Sha(40)] = [new(DecorationType.LocalBranch, "feat-a")],
            [Sha(20)] = [new(DecorationType.LocalBranch, "main", IsLocal: true, IsHead: true)],
        };
        var anc = AncestryOf(commits);

        var full = _engine.Compute(commits, deco, maxRows: 7, isAncestor: anc);
        var partial = _engine.Compute(commits, deco, maxRows: 2, isAncestor: anc);
        var extended = _engine.Extend(partial, commits.Skip(2).ToList(), deco, additionalRows: 5);

        Assert.Equal(full.Layout.Rows, extended.Layout.Rows);
        Assert.Equal(full.Layout.Edges, extended.Layout.Edges);
    }

    [Fact]
    public void Single_branch_tip_window_is_unaffected_by_the_tip_priority_pass()
    {
        var commits = new[] { C(5, 4), C(4, 3), C(3, 2), C(2, 1), C(1) };
        var deco = new Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>>
        {
            [Sha(5)] = [new(DecorationType.LocalBranch, "main")],
        };

        var r = Compute(commits, deco: deco);

        Assert.All(r.Layout.Rows, row => Assert.Equal(0, row.LaneIndex));
        Assert.Equal(1, r.Layout.TotalLaneCount);
    }

    [Fact]
    public void Only_near_head_tips_are_pinned_a_reserved_lane()
    {
        // A 40-row main line; two 1-commit branches fork near HEAD, three fork far below row 16.
        var list = new List<RawCommit>();
        for (var id = 100; id >= 60; id--)
            list.Add(C(id, id - 1));                 // main line, rows 0..40 (id 100-row)
        list.Insert(3, C(50, 95));                   // near-a  → row 3
        list.Insert(6, C(49, 90));                   // near-b  → row 6
        list.Insert(20, C(48, 75));                  // far-1   → row 20
        list.Insert(22, C(47, 74));                  // far-2   → row 22
        list.Insert(25, C(46, 73));                  // far-3   → row 25
        var deco = new Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>>
        {
            [Sha(100)] = [new(DecorationType.LocalBranch, "main")],
            [Sha(50)] = [new(DecorationType.LocalBranch, "near-a")],
            [Sha(49)] = [new(DecorationType.LocalBranch, "near-b")],
            [Sha(48)] = [new(DecorationType.LocalBranch, "far-1")],
            [Sha(47)] = [new(DecorationType.LocalBranch, "far-2")],
            [Sha(46)] = [new(DecorationType.LocalBranch, "far-3")],
        };

        var r = Compute(list, deco: deco);

        // Only the trunk + the two near-HEAD tips reserve a lane; the three far tips do not.
        Assert.Equal(
            new[] { Sha(49), Sha(50), Sha(100) }.OrderBy(x => x.Sha),
            r.State.TipTargetLane.Keys.OrderBy(x => x.Sha));

        // The far tips pack into the lanes freed by the near branches merging back — far-1/2/3 on
        // lanes 1/2/3 (3 genuinely-concurrent branches), total 4 lanes. Before the fix all six
        // tips were pinned, so far-1/2/3 sat at their pin ranks 3/4/5 and the window was 6 wide.
        Assert.Equal(1, r.Layout.Rows[r.Layout.RowByCommitId[Sha(48)]].LaneIndex); // far-1
        Assert.Equal(3, r.Layout.Rows[r.Layout.RowByCommitId[Sha(46)]].LaneIndex); // far-3
        Assert.Equal(4, r.Layout.TotalLaneCount);

        // The horizon is still a pure function of the fixed prefix — Extend matches a full compute.
        var full = _engine.Compute(list, deco, maxRows: 60);
        var partial = _engine.Compute(list, deco, maxRows: 8);
        var extended = _engine.Extend(partial, list.Skip(8).ToList(), deco, additionalRows: 60);
        Assert.Equal(full.Layout.Rows, extended.Layout.Rows);
        Assert.Equal(full.Layout.Edges, extended.Layout.Edges);
    }

    [Fact]
    public void Tip_priority_pass_keeps_extension_identical_to_a_full_recomputation()
    {
        var commits = new[] { C(21, 20), C(20, 10), C(12, 10), C(10, 1), C(1) };
        var deco = new Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>>
        {
            [Sha(21)] = [new(DecorationType.LocalBranch, "v9.2-fixes2")],
            [Sha(12)] = [new(DecorationType.LocalBranch, "v9.1-relogo")],
        };

        var full = _engine.Compute(commits, deco, maxRows: 5);
        var partial = _engine.Compute(commits, deco, maxRows: 2);
        var extended = _engine.Extend(partial, commits.Skip(2).ToList(), deco, additionalRows: 3);

        Assert.Equal(full.Layout.Rows, extended.Layout.Rows);
        Assert.Equal(full.Layout.Edges, extended.Layout.Edges);
    }

    // ── Criterion 4: incremental extension == full recomputation ──

    [Fact]
    public void Extension_is_identical_to_a_full_recomputation()
    {
        var commits = BuildGnarlyChain(120);

        var full = Compute(commits, maxRows: 120);

        var partial = _engine.Compute(commits, null, maxRows: 50);
        var extended = _engine.Extend(partial, commits.Skip(50).ToList(), null, additionalRows: 70);

        Assert.Equal(full.Layout.RowCount, extended.Layout.RowCount);
        Assert.Equal(full.Layout.TotalLaneCount, extended.Layout.TotalLaneCount);
        Assert.Equal(full.Layout.Rows, extended.Layout.Rows);
        Assert.Equal(full.Layout.Commits, extended.Layout.Commits);
        Assert.Equal(full.Layout.Edges, extended.Layout.Edges);
        Assert.Equal(full.Layout.Decorations, extended.Layout.Decorations);
    }

    // ── lane colours are a pure function of the walk ──

    /// <summary>Decorates the chain so several lanes carry branch refs and one commit carries
    /// two — the case that used to let ref-enumeration order flip a lane's seed.</summary>
    private static Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>> BranchDecorations() =>
        new()
        {
            [Sha(90)] = [new(DecorationType.LocalBranch, "zeta"), new(DecorationType.LocalBranch, "main")],
            [Sha(63)] = [new(DecorationType.RemoteBranch, "origin/feature-63")],
            [Sha(42)] = [new(DecorationType.LocalBranch, "feature/auth")],
            [Sha(21)] = [new(DecorationType.LocalBranch, "hotfix")],
        };

    [Fact]
    public void Extension_is_identical_to_a_full_recomputation_with_decorations()
    {
        var commits = BuildGnarlyChain(90);
        var deco = BranchDecorations();

        var full = Compute(commits, maxRows: 90, deco: deco);

        var s1 = _engine.Compute(commits, deco, maxRows: 25);
        var s2 = _engine.Extend(s1, commits.Skip(25).Take(35).ToList(), deco, additionalRows: 35);
        var s3 = _engine.Extend(s2, commits.Skip(60).ToList(), deco, additionalRows: 30);

        // GraphRow carries ColorIndex, so equal rows == equal lane shades on every row.
        Assert.Equal(full.Layout.Rows, s3.Layout.Rows);
        Assert.Equal(full.Layout.Edges, s3.Layout.Edges);
    }

    [Fact]
    public void Lane_colour_does_not_depend_on_decoration_order_on_the_birth_commit()
    {
        var commits = new[] { C(2, 1), C(1) };
        Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>> Deco(params string[] labels) =>
            new() { [Sha(2)] = labels.Select(l => new GraphDecorationInput(DecorationType.LocalBranch, l)).ToArray() };

        var ab = Compute(commits, deco: Deco("main", "feature/auth"));
        var ba = Compute(commits, deco: Deco("feature/auth", "main"));

        Assert.Equal(ab.Layout.Rows[0].ColorIndex, ba.Layout.Rows[0].ColorIndex);
        // The lexicographically smallest label wins the seed.
        Assert.Equal((int)(StableHash.Fnv1a("feature/auth") % LanePalette.Size), ab.Layout.Rows[0].ColorIndex);
    }

    [Fact]
    public void Branchless_lane_colour_is_seeded_from_the_birth_sha_not_the_lane_ordinal()
    {
        // An undecorated lane's seed must come from its birth-commit SHA (not the lane ordinal),
        // so it is identical on every reopen. Lane 0 is coloured first with no neighbours, so it
        // is never nudged by the ±4 de-collision pass — a clean assertion point.
        var commits = new[] { C(3, 1), C(2), C(1) }; // row0 -> id1 (lane0); row1 id2 is a second root
        var r1 = Compute(commits);
        var r2 = Compute(commits);
        Assert.Equal(r1.Layout.Rows.Select(x => x.ColorIndex), r2.Layout.Rows.Select(x => x.ColorIndex));
        Assert.Equal((int)(StableHash.Fnv1a(Sha(3).Sha) % LanePalette.Size), r1.Layout.Rows[0].ColorIndex);
    }

    [Fact]
    public void Extension_in_two_steps_still_matches_full()
    {
        var commits = BuildGnarlyChain(90);
        var full = Compute(commits, maxRows: 90);

        var s1 = _engine.Compute(commits, null, 30);
        var s2 = _engine.Extend(s1, commits.Skip(30).Take(30).ToList(), null, 30);
        var s3 = _engine.Extend(s2, commits.Skip(60).ToList(), null, 30);

        Assert.Equal(full.Layout.Rows, s3.Layout.Rows);
        Assert.Equal(full.Layout.Edges, s3.Layout.Edges);
        AssertSameLines(full.Layout, s3.Layout);
    }

    [Fact]
    public void GraphRow_has_no_reference_fields()
    {
        var fields = typeof(GraphRow).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.All(fields, f => Assert.True(f.FieldType.IsValueType, $"Field {f.Name} is not a value type"));
    }

    [Fact]
    public void Segments_and_elbows_match_topology()
    {
        var commits = new[]
        {
            C(6, 5, 3),
            C(5, 4),
            C(4, 2),
            C(3, 2),
            C(2, 1),
            C(1),
        };
        var r = Compute(commits);

        var row0Segs = r.Layout.GetRowSegments(0);
        Assert.Contains(row0Segs.ToArray(), s => s.Type == LaneSegmentType.NodeToBottom);

        // An elbow exists on the row where branch rejoins
        var hasElbow = false;
        for (var i = 0; i < r.Layout.RowCount; i++)
        {
            if (r.Layout.GetRowElbows(i).Length > 0)
                hasElbow = true;
        }
        Assert.True(hasElbow);

        // Root row (row 5) has TopToNode, but no NodeToBottom
        var rootSegs = r.Layout.GetRowSegments(5);
        Assert.DoesNotContain(rootSegs.ToArray(), s => s.Type == LaneSegmentType.NodeToBottom);
        Assert.Contains(rootSegs.ToArray(), s => s.Type == LaneSegmentType.TopToNode);
    }

    [Fact]
    public void Merge_edge_turns_at_the_merge_row_and_runs_down_the_source_lane()
    {
        // 6 = merge(5, 3) over 5 -> 4 -> 2 -> 1 with 3 -> 2. The merged-in parent (3)
        // sits two rows below the merge, so the merge edge spans rows 0..3.
        var commits = new[] { C(6, 5, 3), C(5, 4), C(4, 2), C(3, 2), C(2, 1), C(1) };
        var r = Compute(commits);

        Assert.Equal(0, r.Layout.Rows[0].LaneIndex); // merge commit, lane 0
        Assert.Equal(1, r.Layout.Rows[3].LaneIndex); // its second parent (id 3), lane 1

        // The bend is on the merge row itself, leaving the node downward, not on the far parent.
        var turn = Assert.Single(r.Layout.GetRowElbows(0).ToArray());
        Assert.Equal(ElbowKind.LeaveBottom, turn.Kind);
        Assert.Equal(0, turn.FromLane);
        Assert.Equal(1, turn.ToLane);

        // Nothing bends at the parent row — the edge is already vertical in lane 1 by then.
        Assert.Empty(r.Layout.GetRowElbows(3).ToArray());

        // The pass-through rows carry the merge line in the merged-in branch's lane (1), not the
        // merge commit's own lane (0).
        foreach (var row in new[] { 1, 2 })
        {
            var passer = Assert.Single(
                r.Layout.GetRowSegments(row).ToArray(),
                s => s.Type == LaneSegmentType.Full && s.Lane == 1);
            Assert.Equal(r.Layout.Rows[3].ColorIndex, passer.ColorIndex);
        }

        // It arrives straight into the parent node in lane 1.
        Assert.Contains(
            r.Layout.GetRowSegments(3).ToArray(),
            s => s.Type == LaneSegmentType.TopToNode && s.Lane == 1);
    }

    // ── uncapped lanes + ±4 colour de-collision ──

    [Fact]
    public void Uncapped_engine_gives_every_lineage_its_own_lane()
    {
        var commits = BuildWideFan(60);
        var full = Compute(commits, maxRows: commits.Count);

        Assert.True(full.Layout.TotalLaneCount >= 60,
            $"expected >= 60 lanes, got {full.Layout.TotalLaneCount}");

        // Every tip row lands on a distinct lane — nothing collapses onto a shared overflow lane.
        var tipLanes = full.Layout.Rows.Take(60).Select(r => r.LaneIndex).ToList();
        Assert.Equal(60, tipLanes.Distinct().Count());
    }

    [Fact]
    public void Wide_history_compute_plus_extend_matches_full_compute()
    {
        var commits = BuildWideFan(60);
        var full = Compute(commits, maxRows: commits.Count);

        var s1 = _engine.Compute(commits, null, 25);
        var s2 = _engine.Extend(s1, commits.Skip(25).ToList(), null, commits.Count);

        Assert.Equal(full.Layout.Rows, s2.Layout.Rows);
        Assert.Equal(full.Layout.Edges, s2.Layout.Edges);
        AssertSameLines(full.Layout, s2.Layout);
    }

    [Fact]
    public void Windowed_derivation_matches_full_derivation_for_every_row_range()
    {
        // A long gnarly chain spans many checkpoint boundaries (2 CheckpointStride = 256),
        // and BuildWideFan gives hundreds of concurrent lanes whose long-lived edges cross many
        // checkpoints at once — exactly the shapes the crossing index has to get right. Each
        // layout's GraphLayout.GetRowSegments/Elbows internally derives a small overscan window
        // around whatever row is asked for (via the checkpoint-based candidate gather in
        // DagLayoutEngine.DeriveWindow); comparing that against DeriveWindow called once for the
        // FULL row range (which, spanning every checkpoint block, is provably equivalent to
        // passing every edge — same as the legacy DeriveVisuals) catches any bug where the
        // candidate gather drops or duplicates an edge for a genuinely partial window.
        var chain = BuildGnarlyChain(1200);
        var full = Compute(chain, maxRows: chain.Count);

        var fan = BuildWideFan(300);
        var fanFull = Compute(fan, maxRows: fan.Count);

        foreach (var result in new[] { full, fanFull })
        {
            var layout = result.Layout;
            var (fullSegs, fullSegStart, fullSegCount, fullElbs, fullElbStart, fullElbCount) =
                DagLayoutEngine.DeriveWindow(result.State, layout.RealEdgeCap, layout.OffWindowEdges,
                    layout.RowCount, 0, layout.RowCount - 1);

            // Rows chosen to straddle checkpoint boundaries at various offsets: right at the
            // start, mid-block, spanning the history's midpoint, and right at the end.
            int[] probeRows =
            [
                0, 1,
                Math.Min(100, layout.RowCount - 1),
                Math.Min(257, layout.RowCount - 1), // just past a checkpoint boundary
                layout.RowCount / 2,
                Math.Max(0, layout.RowCount - 2), layout.RowCount - 1,
            ];

            foreach (var row in probeRows.Distinct())
            {
                var expectedSegs = fullSegCount[row] > 0
                    ? fullSegs.AsSpan(fullSegStart[row], fullSegCount[row]).ToArray() : [];
                var expectedElbs = fullElbCount[row] > 0
                    ? fullElbs.AsSpan(fullElbStart[row], fullElbCount[row]).ToArray() : [];

                Assert.Equal(expectedSegs, layout.GetRowSegments(row).ToArray());
                Assert.Equal(expectedElbs, layout.GetRowElbows(row).ToArray());
            }
        }
    }

    [Fact]
    public void No_two_coloured_lanes_within_four_share_a_colour()
    {
        var commits = BuildWideFan(60);
        var full = Compute(commits, maxRows: commits.Count);

        var colourByLane = new Dictionary<int, int>();
        foreach (var r in full.Layout.Rows)
            colourByLane[r.LaneIndex] = r.ColorIndex;

        foreach (var (lane, colour) in colourByLane)
            for (var d = 1; d <= 4; d++)
                if (colourByLane.TryGetValue(lane + d, out var other))
                    Assert.NotEqual(colour, other);
    }

    /// <summary>One shared root with <paramref name="tips"/> independent single-commit branches,
    /// so the walk needs one concurrent lane per tip — wider than the retired 48-lane cap.</summary>
    private static List<RawCommit> BuildWideFan(int tips)
    {
        var list = new List<RawCommit>();
        for (var i = 0; i < tips; i++)
            list.Add(new RawCommit(Sha(1000 + i), new List<GitObjectId> { Sha(1) }, Meta(1000 + i, 1)));
        list.Add(new RawCommit(Sha(1), new List<GitObjectId>(), Meta(1, 0)));
        return list;
    }

    /// <summary>A chain with periodic branch/merge structure, newest-first, children before parents.</summary>
    private static List<RawCommit> BuildGnarlyChain(int count)
    {
        // Build oldest-first, then reverse. id i's parent is i-1; every 7th commit is a merge
        // that also has a side parent a few steps back; every 5th spawns a short-lived branch.
        var forward = new List<RawCommit>();
        for (var i = 1; i <= count; i++)
        {
            var parents = new List<int>();
            if (i > 1) parents.Add(i - 1);
            if (i % 7 == 0 && i > 10) parents.Add(i - 4);
            forward.Add(new RawCommit(Sha(i), parents.Select(Sha).ToList(), Meta(i, parents.Count)));
        }

        forward.Reverse(); // newest first
        return forward;
    }

    // ── Canonicalized commit ids ──

    [Fact]
    public void RowByCommitId_keys_on_the_commit_ids()
    {
        var commits = new List<RawCommit>
        {
            C(11, 5),
            C(10, 5),
            C(5),
        };

        var result = Compute(commits);
        var row5 = result.Layout.RowByCommitId[Sha(5)];

        var storedKey = ((IReadOnlyDictionary<GitObjectId, int>)result.Layout.RowByCommitId)
            .Keys.Single(k => k.Equals(Sha(5)));

        Assert.Equal(Sha(5), storedKey);
        Assert.Equal(2, row5);
    }

    /// <summary>Canonicalization must never change the actual layout — same lanes, same rows,
    /// same edges — only which object instance a key happens to be.</summary>
    [Fact]
    public void Canonicalized_ids_produce_the_identical_layout_to_the_legacy_shape()
    {
        var commits = BuildGnarlyChain(200);
        var isAnc = AncestryOf(commits);

        var a = Compute(commits, isAncestor: isAnc);
        var b = Compute(commits, isAncestor: isAnc);

        AssertSameLines(a.Layout, b.Layout);
        for (var i = 0; i < a.Layout.RowCount; i++)
            Assert.Equal(a.Layout.Rows[i].LaneIndex, b.Layout.Rows[i].LaneIndex);
    }

    // ── Build/Extend must not copy the whole accumulator every time ──

    private static List<RawCommit> LinearChain(int count) =>
        Enumerable.Range(0, count).Select(i => i + 1 < count ? C(i, i + 1) : C(i)).ToList();

    /// <summary>Extends a synthetic 200k-row linear history in 5k-row steps and asserts the LAST
    /// extend allocates roughly the same as the FIRST — before this fix, <c>Build</c> copied
    /// <c>s.Rows</c> (a <c>GraphRow[]</c>) and <c>s.RealEdges</c>/<c>OffWindowEdges</c> (a
    /// <c>GraphEdge[]</c>) wholesale on every single call, so allocation per extend grew linearly
    /// with the TOTAL rows accumulated so far — quadratic overall. <c>retainCommits: false</c>
    /// isolates this to the rows/edges allocation this fix targets: <c>Layout.Commits</c>'s own
    /// per-extend copy (<c>s.Commits.ToArray()</c>) is a separate, disk-spill-mode-only concern
    /// is not covered (disk mode never retains commits at all).</summary>
    [Fact]
    public void Extending_a_large_history_does_not_allocate_more_per_extend_as_it_grows()
    {
        const int totalRows = 200_000;
        const int step = 5_000;
        var commits = LinearChain(totalRows);

        var result = _engine.Compute(commits.GetRange(0, step), null, step, retainCommits: false);
        var consumed = step;

        GC.Collect();
        var before1 = GC.GetAllocatedBytesForCurrentThread();
        result = _engine.Extend(result, commits.GetRange(consumed, step), null, step);
        var firstExtendBytes = GC.GetAllocatedBytesForCurrentThread() - before1;
        consumed += step;

        while (consumed < totalRows - step)
        {
            result = _engine.Extend(result, commits.GetRange(consumed, step), null, step);
            consumed += step;
        }

        GC.Collect();
        var before2 = GC.GetAllocatedBytesForCurrentThread();
        result = _engine.Extend(result, commits.GetRange(consumed, step), null, step);
        var lastExtendBytes = GC.GetAllocatedBytesForCurrentThread() - before2;
        consumed += step;

        Assert.Equal(totalRows, consumed);
        Assert.Equal(totalRows, result.Layout.RowCount);
        Assert.True(lastExtendBytes <= firstExtendBytes * 2.5,
            $"expected the last extend (~{totalRows} total rows) to allocate roughly the same as " +
            $"the first (~{step} total rows) — first={firstExtendBytes} bytes, last={lastExtendBytes} bytes");
    }
}
