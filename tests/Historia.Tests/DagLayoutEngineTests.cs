using LibGit2Sharp;
using Historia.Models;
using Historia.Services.Graph;
using Xunit;

namespace Historia.Tests;

public class DagLayoutEngineTests
{
    private readonly DagLayoutEngine _engine = new();

    private static ObjectId Sha(int n) => new(n.ToString("x40"));

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
        IReadOnlyDictionary<ObjectId, IReadOnlyList<GraphDecorationInput>>? deco = null)
        => _engine.Compute(commits, deco, maxRows);

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
        // §V3-B: row 0 merges id 5 (first parent) and id 9 (second parent, past the window).
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
        var deco = new Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>>
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
        var deco = new Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>>
        {
            [Sha(2)] = [new(DecorationType.LocalBranch, "feature/auth")],
        };

        var a = Compute(commits, deco: deco);
        var b = Compute(commits, deco: deco);
        Assert.Equal(a.Layout.Rows[0].ColorIndex, b.Layout.Rows[0].ColorIndex);
        var expected = (int)(StableHash.Fnv1a("feature/auth") % LanePalette.Size);
        Assert.Equal(expected, a.Layout.Rows[0].ColorIndex);
    }

    // ── §V3-16.1: every non-OffWindow edge starts in its FromRow's own lane ──

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
        // §V3-3.2: 6 = merge(5, 3) over 5 -> 4 -> 2 -> 1 with 3 -> 2.
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
        // §V6-G7: 6 = merge(5, 3); the merged-in branch is the one holding commit 3.
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
        // §V6-G7: only MergeIn changes. A branch-out first-parent edge still carries the
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
        // must be captured at reservation time inside the resumable walk (§V3-3.5).
        var commits = new[] { C(6, 5, 3), C(5, 4), C(4, 2), C(3, 2), C(2, 1), C(1) };

        var full = Compute(commits);
        var partial = _engine.Compute(commits, null, maxRows: 1); // only the merge row
        var extended = _engine.Extend(partial, commits.Skip(1).ToList(), null, additionalRows: 5);

        Assert.Equal(full.Layout.Edges, extended.Layout.Edges);
        AssertEndpointInvariant(extended);
    }

    // ── §V9-G: "older tip wins the trunk" ──

    [Fact]
    public void Older_branch_tip_holds_the_trunk_lane_and_newer_divergent_work_branches_off()
    {
        // Two branches diverge from a shared ancestor (id 10). v9.1-relogo's tip (id 12) is
        // OLDER than v9.2-fixes2's tip (id 21). Row order is still newest-first, but the older
        // branch must stay collinear with the shared mainline on lane 0.
        var commits = new[] { C(21, 20), C(20, 10), C(12, 10), C(10, 1), C(1) };
        var deco = new Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>>
        {
            [Sha(21)] = [new(DecorationType.LocalBranch, "v9.2-fixes2")],
            [Sha(12)] = [new(DecorationType.LocalBranch, "v9.1-relogo")],
        };

        var r = Compute(commits, deco: deco);

        // Row order is unchanged: the newest commit is still row 0.
        Assert.Equal(0, r.Layout.RowByCommitId[Sha(21)]);
        Assert.Equal(2, r.Layout.RowByCommitId[Sha(12)]);

        // Older tip + the shared history below the fork sit on the trunk (lane 0)…
        Assert.Equal(0, r.Layout.Rows[2].LaneIndex); // v9.1-relogo tip
        Assert.Equal(0, r.Layout.Rows[3].LaneIndex); // shared ancestor
        Assert.Equal(0, r.Layout.Rows[4].LaneIndex); // shared root
        // …and the newer branch is the one that branches off.
        Assert.Equal(1, r.Layout.Rows[0].LaneIndex); // v9.2-fixes2 tip
        Assert.Equal(1, r.Layout.Rows[1].LaneIndex);

        // The trunk lane is born from the older branch (row 2), so its colour is seeded there and
        // is stable across runs and distinct from the branched-off lane.
        var r2 = Compute(commits, deco: deco);
        Assert.Equal(r.Layout.Rows.Select(x => x.ColorIndex), r2.Layout.Rows.Select(x => x.ColorIndex));
        Assert.NotEqual(r.Layout.Rows[0].ColorIndex, r.Layout.Rows[2].ColorIndex);
        Assert.Equal("v9.1-relogo", r.State.LaneKey[r.Layout.Rows[2].LaneIndex]);
    }

    [Fact]
    public void Single_branch_tip_window_is_unaffected_by_the_tip_priority_pass()
    {
        var commits = new[] { C(5, 4), C(4, 3), C(3, 2), C(2, 1), C(1) };
        var deco = new Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>>
        {
            [Sha(5)] = [new(DecorationType.LocalBranch, "main")],
        };

        var r = Compute(commits, deco: deco);

        Assert.All(r.Layout.Rows, row => Assert.Equal(0, row.LaneIndex));
        Assert.Equal(1, r.Layout.TotalLaneCount);
    }

    [Fact]
    public void Tip_priority_pass_keeps_extension_identical_to_a_full_recomputation()
    {
        var commits = new[] { C(21, 20), C(20, 10), C(12, 10), C(10, 1), C(1) };
        var deco = new Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>>
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

    // ── §7.1 (v8 M7): lane colours are a pure function of the walk ──

    /// <summary>Decorates the chain so several lanes carry branch refs and one commit carries
    /// two — the case that used to let ref-enumeration order flip a lane's seed.</summary>
    private static Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> BranchDecorations() =>
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
        Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> Deco(params string[] labels) =>
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
        // is never nudged by the ±2 de-collision pass — a clean assertion point.
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
        Assert.Equal(full.Layout.Segments, s3.Layout.Segments);
        Assert.Equal(full.Layout.Elbows, s3.Layout.Elbows);
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
        // §V3-3.2: 6 = merge(5, 3) over 5 -> 4 -> 2 -> 1 with 3 -> 2. The merged-in parent (3)
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
}
