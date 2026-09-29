namespace Historia.Tests;

/// <summary><see cref="GraphLayout.EnsureRows"/> sizes the derived row window by how the
/// viewport got there. Scrolling past the edge gets the full ±1,000 window, and a jump gets a tight
/// ±128 one, which must still produce exactly the lines a full derivation would.</summary>
[Trait("tier", "unit")]
public class GraphRowWindowTests
{
    private const int Rows = 8_000;
    private const int Full = 1_000;
    private const int Tight = 128;

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

    private GraphLayoutResult Compute(IReadOnlyList<RawCommit> commits)
        => _engine.Compute(commits, null, commits.Count);

    private GraphLayout Chain() => Compute(BuildGnarlyChain(Rows)).Layout;

    /// <summary>A chain with periodic merges, newest-first, children before parents.</summary>
    private static List<RawCommit> BuildGnarlyChain(int count)
    {
        var forward = new List<RawCommit>();
        for (var i = 1; i <= count; i++)
        {
            var parents = new List<int>();
            if (i > 1) parents.Add(i - 1);
            if (i % 7 == 0 && i > 10) parents.Add(i - 4);
            forward.Add(new RawCommit(Sha(i), parents.Select(Sha).ToList(), Meta(i, parents.Count)));
        }

        forward.Reverse();
        return forward;
    }

    /// <summary><paramref name="chains"/> interleaved chains (one lane each), newest-first, with
    /// chain 0 periodically merging its neighbour so there are elbows as well as straight runs.</summary>
    private static List<RawCommit> BuildParallelChains(int chains, int length)
    {
        int Id(int c, int i) => 10 + c * length + i;

        var list = new List<RawCommit>();
        for (var i = length - 1; i >= 0; i--)
        {
            for (var c = 0; c < chains; c++)
            {
                var parents = new List<int>();
                if (i > 0) parents.Add(Id(c, i - 1));
                if (c == 0 && i > 0 && i % 37 == 0) parents.Add(Id(1, i - 1));
                list.Add(new RawCommit(Sha(Id(c, i)), parents.Select(Sha).ToList(),
                    Meta(Id(c, i), parents.Count)));
            }
        }
        return list;
    }

    [Fact]
    public void First_use_derives_the_full_window()
    {
        var layout = Chain();

        layout.EnsureRows(3000, 3040);

        Assert.Equal((3000 - Full, 3040 + Full), layout.WindowRange);
        Assert.Equal(1, layout.WindowDerivations);
    }

    [Fact]
    public void Covered_range_does_not_rederive()
    {
        var layout = Chain();
        layout.EnsureRows(3000, 3040);
        var (a, b) = layout.WindowRange!.Value;

        layout.EnsureRows(a + 10, b - 10);
        for (var r = a; r <= b; r++)
        {
            _ = layout.GetRowSegments(r);
            _ = layout.GetRowElbows(r);
        }

        Assert.Equal(1, layout.WindowDerivations);
    }

    [Fact]
    public void Scroll_just_past_the_edge_derives_the_full_window()
    {
        var layout = Chain();
        layout.EnsureRows(3000, 3040);
        var (_, b) = layout.WindowRange!.Value;

        layout.EnsureRows(b + 1, b + 41);

        Assert.Equal((b + 1 - Full, Math.Min(Rows - 1, b + 41 + Full)), layout.WindowRange);
        Assert.Equal(2, layout.WindowDerivations);
    }

    [Fact]
    public void Jump_derives_a_tight_window()
    {
        var layout = Chain();
        layout.EnsureRows(0, 40);
        var (_, b) = layout.WindowRange!.Value;

        layout.EnsureRows(b + 5000, b + 5040);

        Assert.Equal((b + 5000 - Tight, b + 5040 + Tight), layout.WindowRange);
        Assert.Equal(2, layout.WindowDerivations);
    }

    [Theory]
    [InlineData(256, false)]
    [InlineData(257, true)]
    public void JumpDistance_boundary(int gap, bool isJump)
    {
        var layout = Chain();
        var overscan = isJump ? Tight : Full;

        // Below the window: the range starts `gap` rows past B.
        layout.EnsureRows(4000, 4040);
        var (a, b) = layout.WindowRange!.Value;
        layout.EnsureRows(b + gap, b + gap + 40);
        Assert.Equal((b + gap - overscan, Math.Min(Rows - 1, b + gap + 40 + overscan)), layout.WindowRange);

        // Above the window: the range ends `gap` rows before A.
        layout.Dispose();
        layout.EnsureRows(4000, 4040);
        layout.EnsureRows(a - gap - 40, a - gap);
        Assert.Equal((Math.Max(0, a - gap - 40 - overscan), a - gap + overscan), layout.WindowRange);
    }

    [Fact]
    public void Tight_window_covers_a_tall_viewport()
    {
        var layout = Chain();
        layout.EnsureRows(0, 40);
        const int f = 5000;

        layout.EnsureRows(f, f + 300);
        var before = layout.WindowDerivations;
        for (var r = f; r <= f + 300; r++)
        {
            _ = layout.GetRowSegments(r);
            _ = layout.GetRowElbows(r);
        }

        Assert.Equal(2, before);
        Assert.Equal(before, layout.WindowDerivations);
        Assert.Equal((f - Tight, f + 300 + Tight), layout.WindowRange);
    }

    [Fact]
    public void Windows_clamp_at_history_edges()
    {
        var layout = Chain();
        layout.EnsureRows(4000, 4040);

        layout.EnsureRows(0, 40);
        Assert.Equal((0, 40 + Tight), layout.WindowRange);

        layout.EnsureRows(4000, 4040);
        layout.EnsureRows(Rows - 40, Rows + 100);
        Assert.Equal((Rows - 40 - Tight, Rows - 1), layout.WindowRange);

        layout.EnsureRows(-500, -1);
        layout.EnsureRows(Rows + 10, Rows + 20);
        Assert.Equal((Rows - 40 - Tight, Rows - 1), layout.WindowRange);
    }

    [Fact]
    public void Single_row_callers_get_the_same_classification()
    {
        var layout = Chain();
        layout.EnsureRows(3000, 3040);
        const int far = 7500;

        _ = layout.GetRowSegments(far);

        Assert.Equal((far - Tight, far + Tight), layout.WindowRange);
    }

    [Fact]
    public void Tight_windows_match_full_derivation()
    {
        var chain = Compute(BuildGnarlyChain(Rows));
        var fan = Compute(BuildParallelChains(40, Rows / 40));

        foreach (var result in new[] { chain, fan })
        {
            var layout = result.Layout;
            var n = layout.RowCount;
            var (segs, segStart, segCount, elbs, elbStart, elbCount) =
                DagLayoutEngine.DeriveWindow(result.State, layout.RealEdgeCap, layout.OffWindowEdges,
                    n, 0, n - 1);

            var targets = new SortedSet<int> { 0, n - 1 };
            for (var cp = 0; cp < n; cp += GraphLayoutState.CheckpointStride)
            {
                targets.Add(cp);
                if (cp > 0) targets.Add(cp - 1);
                if (cp + 1 < n) targets.Add(cp + 1);
            }
            var rng = new Random(20260926);
            for (var i = 0; i < 20; i++)
                targets.Add(rng.Next(n));

            foreach (var t in targets)
            {
                // Park the window at the far end so the jump to t is always a jump.
                layout.Dispose();
                if (t < n / 2) layout.EnsureRows(n - 41, n - 1);
                else layout.EnsureRows(0, 40);

                layout.EnsureRows(t, t + 40);
                var (a, b) = layout.WindowRange!.Value;
                Assert.Equal((Math.Max(0, t - Tight), Math.Min(n - 1, t + 40 + Tight)), (a, b));

                for (var r = a; r <= b; r++)
                {
                    var expectedSegs = segCount[r] > 0 ? segs.AsSpan(segStart[r], segCount[r]).ToArray() : [];
                    var expectedElbs = elbCount[r] > 0 ? elbs.AsSpan(elbStart[r], elbCount[r]).ToArray() : [];
                    Assert.Equal(expectedSegs, layout.GetRowSegments(r).ToArray());
                    Assert.Equal(expectedElbs, layout.GetRowElbows(r).ToArray());
                }
                Assert.Equal((a, b), layout.WindowRange);
            }
        }
    }

    [Fact]
    public void Empty_layout_is_a_no_op()
    {
        GraphLayout.Empty.EnsureRows(0, 10);

        Assert.Equal(0, GraphLayout.Empty.WindowDerivations);
    }
}
