namespace Historia.Tests;

/// <summary>The render pass merges per-row lane pieces into maximal runs. The merge must
/// show the same colour per lane per half-row as drawing the pieces in list order, and
/// must not allocate once warm.</summary>
[Trait("tier", "unit")]
public class LaneRunBuilderTests
{
    private static RowLaneSegment Seg(int lane, int color, LaneSegmentType type = LaneSegmentType.Full)
        => new() { Lane = lane, ColorIndex = color, Type = type };

    private static LaneRun[] Build(LaneRunBuilder builder, IReadOnlyList<RowLaneSegment[]> rows, int hint = 0)
    {
        builder.Begin(hint);
        foreach (var row in rows)
            builder.AddRow(row);
        return builder.Finish().ToArray();
    }

    private static LaneRun[] Build(IReadOnlyList<RowLaneSegment[]> rows)
        => Build(new LaneRunBuilder(), rows);

    private static LaneRun[] Sorted(LaneRun[] runs)
        => [.. runs.OrderBy(r => r.Lane).ThenBy(r => r.StartHalf)];

    private static RowLaneSegment[][] Rows(int count, Func<int, RowLaneSegment[]> row)
        => [.. Enumerable.Range(0, count).Select(row)];

    [Fact]
    public void Straight_lane_across_rows_is_one_run()
    {
        var runs = Build(Rows(40, _ => [Seg(3, 7)]));

        Assert.Equal([new LaneRun(3, 7, 0, 80)], runs);
    }

    [Fact]
    public void Fifty_straight_lanes_emit_fifty_runs()
    {
        var rows = Rows(40, _ => [.. Enumerable.Range(0, 50).Select(l => Seg(l, l % 16))]);
        Assert.Equal(2000, rows.Sum(r => r.Length));

        var runs = Sorted(Build(rows));

        Assert.Equal(50, runs.Length);
        for (var l = 0; l < 50; l++)
            Assert.Equal(new LaneRun(l, l % 16, 0, 80), runs[l]);
    }

    [Fact]
    public void Same_colour_TopToNode_and_NodeToBottom_merge()
    {
        var runs = Build(
        [
            [Seg(2, 4)],
            [Seg(2, 4, LaneSegmentType.TopToNode), Seg(2, 4, LaneSegmentType.NodeToBottom)],
            [Seg(2, 4)],
        ]);

        Assert.Equal([new LaneRun(2, 4, 0, 6)], runs);
    }

    [Fact]
    public void Different_colours_split_at_the_node()
    {
        var runs = Sorted(Build(
        [
            [Seg(1, 1, LaneSegmentType.TopToNode), Seg(1, 2, LaneSegmentType.NodeToBottom)],
        ]));

        Assert.Equal([new LaneRun(1, 1, 0, 1), new LaneRun(1, 2, 1, 2)], runs);
    }

    [Fact]
    public void Order_within_a_row_does_not_change_non_overlapping_output()
    {
        RowLaneSegment[] before = [Seg(0, 5, LaneSegmentType.TopToNode), Seg(0, 6, LaneSegmentType.NodeToBottom)];
        RowLaneSegment[] after = [Seg(0, 6, LaneSegmentType.NodeToBottom), Seg(0, 5, LaneSegmentType.TopToNode)];

        var a = Sorted(Build([[Seg(0, 5)], before, [Seg(0, 6)]]));
        var b = Sorted(Build([[Seg(0, 5)], after, [Seg(0, 6)]]));

        Assert.Equal(a, b);
        Assert.Equal([new LaneRun(0, 5, 0, 3), new LaneRun(0, 6, 3, 6)], a);
    }

    [Fact]
    public void Overlapping_pieces_last_writer_wins()
    {
        Assert.Equal([new LaneRun(0, 2, 0, 2)], Build([[Seg(0, 1), Seg(0, 2)]]));
        Assert.Equal([new LaneRun(0, 1, 0, 2)], Build([[Seg(0, 2), Seg(0, 1)]]));

        var runs = Sorted(Build(
        [
            [Seg(0, 1)],
            [Seg(0, 1), Seg(0, 2)],
            [Seg(0, 1)],
        ]));

        Assert.Equal([new LaneRun(0, 1, 0, 2), new LaneRun(0, 2, 2, 4), new LaneRun(0, 1, 4, 6)], runs);
    }

    [Fact]
    public void Duplicate_same_colour_pieces_do_not_fragment_runs()
    {
        var runs = Build(Rows(40, _ => [Seg(5, 3), Seg(5, 3)]));

        Assert.Equal([new LaneRun(5, 3, 0, 80)], runs);
    }

    [Fact]
    public void Gap_row_splits_the_run()
    {
        var runs = Sorted(Build(Rows(10, i => i == 4 ? [] : [Seg(0, 0)])));

        Assert.Equal([new LaneRun(0, 0, 0, 8), new LaneRun(0, 0, 10, 20)], runs);
    }

    [Fact]
    public void Negative_lane_is_ignored()
    {
        var runs = Build([[Seg(-1, 3), Seg(0, 1)], [Seg(-5, 3)]]);

        Assert.Equal([new LaneRun(0, 1, 0, 2)], runs);
    }

    [Fact]
    public void Randomised_equivalence()
    {
        var rng = new Random(20260925);
        var builder = new LaneRunBuilder(laneCapacity: 4, runCapacity: 4);
        var types = new[] { LaneSegmentType.Full, LaneSegmentType.TopToNode, LaneSegmentType.NodeToBottom };

        for (var frame = 0; frame < 400; frame++)
        {
            var rowCount = rng.Next(1, 50);
            var rows = new RowLaneSegment[rowCount][];
            var expected = new Dictionary<(int Lane, int Half), int>();
            for (var i = 0; i < rowCount; i++)
            {
                var list = new List<RowLaneSegment>();
                var n = rng.Next(0, 30);
                for (var k = 0; k < n; k++)
                {
                    var seg = Seg(rng.Next(-1, 61), rng.Next(0, 16), types[rng.Next(3)]);
                    list.Add(seg);
                    if (rng.Next(4) == 0)
                        list.Add(seg);
                }
                rows[i] = [.. list];

                foreach (var seg in rows[i])
                {
                    if (seg.Lane < 0) continue;
                    if (seg.Type != LaneSegmentType.NodeToBottom) expected[(seg.Lane, 2 * i)] = seg.ColorIndex;
                    if (seg.Type != LaneSegmentType.TopToNode) expected[(seg.Lane, 2 * i + 1)] = seg.ColorIndex;
                }
            }

            var runs = Build(builder, rows, hint: rng.Next(0, 70));

            var actual = new Dictionary<(int Lane, int Half), int>();
            foreach (var run in runs)
            {
                if (run.StartHalf >= run.EndHalf)
                    Assert.Fail($"frame {frame}: empty run {run}");
                for (var h = run.StartHalf; h < run.EndHalf; h++)
                    if (!actual.TryAdd((run.Lane, h), run.ColorIndex))
                        Assert.Fail($"frame {frame}: overlapping runs at lane {run.Lane} half {h}");
            }
            Assert.Equal(expected.OrderBy(kv => kv.Key), actual.OrderBy(kv => kv.Key));

            // Failure messages are built only on failure: formatting one per pair of runs is what
            // made this test take half a minute.
            foreach (var a in runs)
                foreach (var b in runs)
                    if (a.Lane == b.Lane && a.ColorIndex == b.ColorIndex && a.EndHalf == b.StartHalf)
                        Assert.Fail($"frame {frame}: runs {a} and {b} touch but were not merged");
        }
    }

    [Fact]
    public void Second_identical_frame_records_zero_growths()
    {
        var rows = Rows(40, i => [.. Enumerable.Range(0, 50).Select(l => Seg(l, (l + i / 7) % 16))]);
        var builder = new LaneRunBuilder(laneCapacity: 4, runCapacity: 4);

        var first = Build(builder, rows, hint: 4);
        Assert.True(builder.Growths > 0, "a lane past the hint must grow the lane arrays");

        builder.ResetCounters();
        var second = Build(builder, rows, hint: 4);

        Assert.Equal(0, builder.Growths);
        Assert.Equal(first, second);
    }
}
