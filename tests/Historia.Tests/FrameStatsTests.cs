namespace Historia.Tests;

/// <summary>
/// <see cref="FrameStats"/> turns captured frame intervals, render times and GC counters into the
/// numbers that decide whether a candidate build is worth adopting. Every threshold, percentile
/// method and zero-median convention it documents is pinned here, because a wrong verdict would
/// come out of these numbers with no error anywhere.
/// </summary>
[Trait("tier", "unit")]
public sealed class FrameStatsTests
{
    private static readonly GcDelta NoGc = new(default, default, TimeSpan.Zero);

    // ---- Percentiles ----------------------------------------------------------------------

    [Fact]
    public void Nearest_rank_percentiles_on_one_to_a_hundred_pick_the_rank_itself()
    {
        var values = Enumerable.Range(1, 100).Select(i => (double)i).Reverse().ToArray();

        Assert.Equal(50, FrameStats.Percentile(values, 50));
        Assert.Equal(95, FrameStats.Percentile(values, 95));
        Assert.Equal(99, FrameStats.Percentile(values, 99));
        Assert.Equal(100, FrameStats.Percentile(values, 100));
        Assert.Equal(1, FrameStats.Percentile(values, 0));
    }

    [Fact]
    public void Nearest_rank_rounds_the_rank_up_and_never_interpolates()
    {
        double[] values = [40, 10, 30, 20, 50, 60, 70, 80, 90, 100];

        Assert.Equal(50, FrameStats.Percentile(values, 50));   // ceil(5.0)  = rank 5
        Assert.Equal(60, FrameStats.Percentile(values, 51));   // ceil(5.1)  = rank 6
        Assert.Equal(100, FrameStats.Percentile(values, 95));  // ceil(9.5)  = rank 10
        Assert.Equal(100, FrameStats.Percentile(values, 99));  // ceil(9.9)  = rank 10
    }

    [Fact]
    public void Percentiles_of_an_empty_span_are_zero()
    {
        Assert.Equal(0, FrameStats.Percentile([], 50));
        Assert.Equal(0, FrameStats.Percentile([], 99));
        Assert.Equal(0, FrameStats.Median([]));
    }

    [Fact]
    public void Every_percentile_of_a_single_sample_is_that_sample()
    {
        foreach (var p in new double[] { 0, 1, 50, 95, 99, 100 })
            Assert.Equal(16.7, FrameStats.Percentile([16.7], p));
    }

    [Fact]
    public void Median_takes_the_middle_of_an_odd_count_and_the_mean_of_an_even_count()
    {
        Assert.Equal(3, FrameStats.Median([5, 1, 3, 4, 2]));
        Assert.Equal(2.5, FrameStats.Median([4, 1, 3, 2]));
    }

    // ---- Summarize ------------------------------------------------------------------------

    [Fact]
    public void Summarize_fills_every_interval_and_render_statistic()
    {
        var intervals = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();
        double[] render = [2, 4, 6, 8];

        var s = FrameStats.Summarize(intervals, render, NoGc, seconds: 5);

        Assert.Equal(100, s.Frames);
        Assert.Equal(5, s.Seconds);
        Assert.Equal(20, s.AverageFps);
        Assert.Equal(50, s.IntervalP50Ms);
        Assert.Equal(95, s.IntervalP95Ms);
        Assert.Equal(99, s.IntervalP99Ms);
        Assert.Equal(100, s.IntervalMaxMs);
        Assert.Equal(100 - 22, s.FramesOverCeiling); // 23..100
        Assert.Equal(4, s.RenderP50Ms);
        Assert.Equal(8, s.RenderP99Ms);
        Assert.Equal(8, s.RenderMaxMs);
        Assert.Equal(FrameStats.FrameCeilingMs - 99, s.HeadroomMs, 9);
    }

    [Fact]
    public void Over_ceiling_count_is_strictly_greater_than_22_2_ms()
    {
        double[] intervals = [22.2, FrameStats.FrameCeilingMs, 22.19, 22.2000001, 30, 16.7];

        var s = FrameStats.Summarize(intervals, [], NoGc, seconds: 1);

        Assert.Equal(2, s.FramesOverCeiling);
    }

    [Fact]
    public void Average_fps_is_the_frame_count_over_the_duration()
    {
        var intervals = Enumerable.Repeat(1000.0 / 60, 1200).ToArray();

        var s = FrameStats.Summarize(intervals, [], NoGc, seconds: 20);

        Assert.Equal(60, s.AverageFps, 9);
        Assert.Equal(16.67, s.IntervalP99Ms, 2);
        Assert.Equal(0, s.FramesOverCeiling);
    }

    [Fact]
    public void Summarize_of_nothing_is_all_zero_with_full_headroom_and_no_nan()
    {
        var s = FrameStats.Summarize([], [], NoGc, seconds: 0);

        Assert.Equal(0, s.Frames);
        Assert.Equal(0, s.AverageFps);
        Assert.Equal(0, s.IntervalP99Ms);
        Assert.Equal(0, s.IntervalMaxMs);
        Assert.Equal(0, s.RenderMaxMs);
        Assert.Equal(0, s.AllocatedMbPerSecond);
        Assert.Equal(FrameStats.FrameCeilingMs, s.HeadroomMs);

        var nan = FrameStats.Summarize([16, 17], [], NoGc, seconds: double.NaN);
        Assert.Equal(0, nan.Seconds);
        Assert.Equal(0, nan.AverageFps);
    }

    [Fact]
    public void Summarize_takes_gc_deltas_pause_totals_and_allocation_rate()
    {
        var before = new GcSnapshot(10, 4, 1, TimeSpan.FromMilliseconds(100), 1_000_000);
        var after = new GcSnapshot(25, 7, 2, TimeSpan.FromMilliseconds(160), 1_000_000 + 40L * 1024 * 1024);
        var gc = new GcDelta(before, after, TimeSpan.FromMilliseconds(12.5));

        var s = FrameStats.Summarize([16.7], [], gc, seconds: 10);

        Assert.Equal(15, s.Gen0Collections);
        Assert.Equal(3, s.Gen1Collections);
        Assert.Equal(1, s.Gen2Collections);
        Assert.Equal(60, s.GcPauseTotalMs, 9);
        Assert.Equal(12.5, s.GcPauseMaxMs, 9);
        Assert.Equal(4, s.AllocatedMbPerSecond, 9);
    }

    [Fact]
    public void Max_pause_never_exceeds_the_total_pause_inside_the_window()
    {
        var snap = new GcSnapshot(3, 1, 0, TimeSpan.FromMilliseconds(50), 0);
        var gc = new GcDelta(snap, snap, TimeSpan.FromMilliseconds(8)); // no GC in the window

        var s = FrameStats.Summarize([16.7], [], gc, seconds: 1);

        Assert.Equal(0, s.GcPauseTotalMs);
        Assert.Equal(0, s.GcPauseMaxMs);
    }

    // ---- Compare --------------------------------------------------------------------------

    [Fact]
    public void Compare_picks_the_median_of_each_side_not_the_mean()
    {
        var a = Runs(p99: [10, 50, 12, 11, 13]);   // median 12, mean 19.2
        var b = Runs(p99: [9, 8, 100, 10, 7]);     // median 9,  mean 26.8

        var c = FrameStats.Compare(a, b)[FrameMetric.IntervalP99];

        Assert.Equal(12, c.MedianA);
        Assert.Equal(9, c.MedianB);
        Assert.Equal(-0.25, c.Change, 9);
        Assert.Equal(0.25, c.Improvement, 9);
    }

    [Fact]
    public void Compare_reports_every_metric_and_the_run_counts()
    {
        var comparison = FrameStats.Compare(Runs(p99: [16, 17, 18]), Runs(p99: [16, 17]));

        Assert.Equal(3, comparison.RunsA);
        Assert.Equal(2, comparison.RunsB);
        Assert.Equal(Enum.GetValues<FrameMetric>(), comparison.Metrics.Select(m => m.Metric));
    }

    [Fact]
    public void Compare_rejects_a_side_with_no_runs()
    {
        Assert.Throws<ArgumentException>(() => FrameStats.Compare([], Runs(p99: [16])));
        Assert.Throws<ArgumentException>(() => FrameStats.Compare(Runs(p99: [16]), []));
    }

    [Fact]
    public void Noise_band_has_a_five_percent_floor()
    {
        var c = FrameStats.Compare(Runs(p99: [16, 16, 16]), Runs(p99: [17, 17, 17]))[FrameMetric.IntervalP99];

        Assert.Equal(FrameStats.NoiseFloor, c.NoiseBand);
    }

    [Fact]
    public void Noise_band_is_the_wider_of_the_two_relative_spreads()
    {
        var a = Runs(p99: [9, 10, 11]);    // (11 − 9) / 10 = 20%
        var b = Runs(p99: [19, 20, 21]);   // (21 − 19) / 20 = 10%

        var c = FrameStats.Compare(a, b)[FrameMetric.IntervalP99];

        Assert.Equal(0.20, c.NoiseBand, 9);
        Assert.Equal(1.0, c.Change, 9);
    }

    [Fact]
    public void A_change_inside_the_noise_band_is_neither_a_win_nor_a_regression()
    {
        var a = Runs(p99: [16, 20, 24]);  // spread 8/20 = 40%
        var b = Runs(p99: [20, 26, 28]);  // +30%, inside the 40% band

        var c = FrameStats.Compare(a, b)[FrameMetric.IntervalP99];

        Assert.False(c.BeyondNoise);
        Assert.False(c.WorsensBy(FrameStats.RegressionThreshold));
        Assert.False(c.ImprovesBy(FrameStats.FrameWinThreshold));
    }

    [Fact]
    public void Exactly_ten_percent_meets_the_threshold_despite_floating_point()
    {
        var c = FrameStats.Compare(Runs(p99: [10, 10, 10]), Runs(p99: [11, 11, 11]))[FrameMetric.IntervalP99];

        Assert.True(c.WorsensBy(FrameStats.RegressionThreshold));
    }

    [Fact]
    public void Zero_against_zero_is_no_change()
    {
        var c = FrameStats.Compare(Runs(over: [0, 0, 0]), Runs(over: [0, 0, 0]))[FrameMetric.FramesOverCeiling];

        Assert.Equal(0, c.Change);
        Assert.Equal(FrameStats.NoiseFloor, c.NoiseBand);
        Assert.False(c.WorsensBy(FrameStats.RegressionThreshold));
    }

    [Fact]
    public void Zero_to_n_reads_as_plus_one_hundred_percent_and_regresses_when_it_clears_the_noise()
    {
        var a = Runs(over: [0, 0, 0, 1, 0]);   // spread 1 against B's median 4 = 25%
        var b = Runs(over: [3, 4, 5, 3, 4]);   // spread 2 / 4 = 50%

        var c = FrameStats.Compare(a, b)[FrameMetric.FramesOverCeiling];

        Assert.Equal(1.0, c.Change, 9);
        Assert.Equal(0.5, c.NoiseBand, 9);
        Assert.True(c.WorsensBy(FrameStats.RegressionThreshold));
    }

    [Fact]
    public void Zero_to_n_inside_a_spiky_baseline_is_not_a_regression()
    {
        var a = Runs(over: [0, 0, 0, 9, 0]);   // spread 9 against B's median 1 = 900%
        var b = Runs(over: [0, 1, 1, 1, 2]);

        var c = FrameStats.Compare(a, b)[FrameMetric.FramesOverCeiling];

        Assert.Equal(1.0, c.Change, 9);
        Assert.False(c.WorsensBy(FrameStats.RegressionThreshold));
    }

    [Fact]
    public void N_to_zero_reads_as_minus_one_hundred_percent()
    {
        var c = FrameStats.Compare(Runs(over: [4, 4, 4]), Runs(over: [0, 0, 0]))[FrameMetric.FramesOverCeiling];

        Assert.Equal(-1.0, c.Change, 9);
        Assert.True(c.ImprovesBy(FrameStats.FrameWinThreshold));
    }

    [Fact]
    public void Higher_fps_is_an_improvement()
    {
        var c = FrameStats.Compare(Runs(fps: [50, 50, 50]), Runs(fps: [60, 60, 60]))[FrameMetric.AverageFps];

        Assert.True(c.Improvement > 0);
        Assert.True(c.ImprovesBy(FrameStats.FrameWinThreshold));
    }

    [Fact]
    public void Headroom_and_ceiling_breaks_come_from_the_medians()
    {
        var comparison = FrameStats.Compare(Runs(p99: [17, 18, 19], over: [0, 0, 1]), Runs(p99: [23, 24, 25], over: [1, 2, 3]));

        Assert.Equal(FrameStats.FrameCeilingMs - 18, comparison.HeadroomA, 9);
        Assert.Equal(FrameStats.FrameCeilingMs - 24, comparison.HeadroomB, 9);
        Assert.False(comparison.CeilingBrokenA);
        Assert.True(comparison.CeilingBrokenB);
    }

    // ---- Verdict --------------------------------------------------------------------------

    [Fact]
    public void A_frame_win_on_both_repos_with_no_regression_proceeds()
    {
        var verdict = FrameStats.Verdict(
        [
            Cell("linux", FrameStats.GraphScrollWorkload, Better(p99: 14)),
            Cell("linux", FrameStats.GraphJumpWorkload, Same()),
            Cell("linux", "diff-scroll", Same()),
            Cell("chromium", FrameStats.GraphScrollWorkload, Same()),
            Cell("chromium", FrameStats.GraphJumpWorkload, Better(renderP99: 3)),
            Cell("chromium", "diff-scroll", Same()),
        ]);

        Assert.Equal(VerdictOutcome.Proceed, verdict.Outcome);
        Assert.Equal(["linux", "chromium"], verdict.ReposWithWin);
        Assert.Empty(verdict.ReposWithoutWin);
        Assert.Contains(verdict.Reasons, r => r.StartsWith("linux graph-scroll: win, frame-interval p99", StringComparison.Ordinal));
        Assert.Contains(verdict.Reasons, r => r.StartsWith("chromium graph-jump: win, graph render p99", StringComparison.Ordinal));
    }

    [Fact]
    public void A_win_on_one_repo_only_is_inconclusive_and_names_the_losing_repo()
    {
        var verdict = FrameStats.Verdict(
        [
            Cell("linux", FrameStats.GraphScrollWorkload, Better(p99: 14)),
            Cell("chromium", FrameStats.GraphScrollWorkload, Same()),
            Cell("chromium", FrameStats.GraphJumpWorkload, Same()),
        ]);

        Assert.Equal(VerdictOutcome.Inconclusive, verdict.Outcome);
        Assert.Equal(["linux"], verdict.ReposWithWin);
        Assert.Equal(["chromium"], verdict.ReposWithoutWin);
    }

    [Fact]
    public void A_win_plus_any_regression_skips()
    {
        var verdict = FrameStats.Verdict(
        [
            Cell("linux", FrameStats.GraphScrollWorkload, Better(p99: 14)),
            Cell("linux", "diff-scroll", Worse(max: 40)),
            Cell("chromium", FrameStats.GraphScrollWorkload, Better(p99: 14)),
        ]);

        Assert.Equal(VerdictOutcome.Skip, verdict.Outcome);
        Assert.Equal("1 regression.", verdict.Reasons[0]);
        Assert.Contains(verdict.Reasons, r => r.StartsWith("linux diff-scroll: regression, max frame", StringComparison.Ordinal));
    }

    [Fact]
    public void A_terminal_regression_counts_like_any_other_workload()
    {
        var verdict = FrameStats.Verdict(
        [
            Cell("linux", FrameStats.GraphScrollWorkload, Better(p99: 14)),
            Cell("chromium", FrameStats.GraphScrollWorkload, Better(p99: 14)),
            Cell("linux", "terminal", Worse(over: 5)),
        ]);

        Assert.Equal(VerdictOutcome.Skip, verdict.Outcome);
    }

    [Fact]
    public void No_change_anywhere_skips()
    {
        var verdict = FrameStats.Verdict(
        [
            Cell("linux", FrameStats.GraphScrollWorkload, Same()),
            Cell("linux", FrameStats.GraphJumpWorkload, Same()),
            Cell("chromium", FrameStats.GraphScrollWorkload, Same()),
            Cell("chromium", FrameStats.GraphJumpWorkload, Same()),
        ]);

        Assert.Equal(VerdictOutcome.Skip, verdict.Outcome);
        Assert.Equal("No win on any repo.", verdict.Reasons[0]);
        Assert.Single(verdict.Reasons);
    }

    [Fact]
    public void A_gc_only_win_on_both_repos_proceeds()
    {
        var verdict = FrameStats.Verdict(
        [
            Cell("linux", FrameStats.GraphScrollWorkload, Better(gcTotal: 70)),
            Cell("chromium", FrameStats.GraphJumpWorkload, Better(gcMax: 7)),
        ]);

        Assert.Equal(VerdictOutcome.Proceed, verdict.Outcome);
        Assert.Contains(verdict.Reasons, r => r.StartsWith("linux graph-scroll: win, total GC pause", StringComparison.Ordinal));
        Assert.Contains(verdict.Reasons, r => r.StartsWith("chromium graph-jump: win, max GC pause", StringComparison.Ordinal));
    }

    [Fact]
    public void A_gc_drop_under_twenty_five_percent_is_not_a_win()
    {
        var verdict = FrameStats.Verdict(
        [
            Cell("linux", FrameStats.GraphScrollWorkload, Better(gcTotal: 80)),     // −20%
            Cell("chromium", FrameStats.GraphScrollWorkload, Better(gcTotal: 80)),
        ]);

        Assert.Equal(VerdictOutcome.Skip, verdict.Outcome);
    }

    [Fact]
    public void A_win_outside_the_graph_workloads_does_not_count()
    {
        var verdict = FrameStats.Verdict(
        [
            Cell("linux", "diff-scroll", Better(p99: 14)),
            Cell("chromium", "diff-scroll", Better(p99: 14)),
        ]);

        Assert.Equal(VerdictOutcome.Skip, verdict.Outcome);
    }

    [Fact]
    public void A_win_on_a_single_repo_input_cannot_proceed()
    {
        var verdict = FrameStats.Verdict([Cell("linux", FrameStats.GraphScrollWorkload, Better(p99: 14))]);

        Assert.Equal(VerdictOutcome.Inconclusive, verdict.Outcome);
    }

    [Fact]
    public void An_invalid_cell_keeps_its_repo_in_play_but_cannot_win()
    {
        var verdict = FrameStats.Verdict(
        [
            Cell("linux", FrameStats.GraphScrollWorkload, Better(p99: 14)),
            new VerdictCell("chromium", FrameStats.GraphScrollWorkload, null),
        ]);

        Assert.Equal(VerdictOutcome.Inconclusive, verdict.Outcome);
        Assert.Equal(["chromium"], verdict.ReposWithoutWin);
        Assert.Contains("chromium graph-scroll: invalid cell, not counted", verdict.Reasons);
    }

    [Fact]
    public void No_cells_skips()
    {
        Assert.Equal(VerdictOutcome.Skip, FrameStats.Verdict([]).Outcome);
    }

    // ---- JSON -----------------------------------------------------------------------------

    [Fact]
    public void Frame_summary_round_trips_through_json()
    {
        var before = new GcSnapshot(1, 0, 0, TimeSpan.FromMilliseconds(3), 1234);
        var after = new GcSnapshot(9, 2, 1, TimeSpan.FromMilliseconds(41.75), 98_765_432);
        var intervals = Enumerable.Range(0, 600).Select(i => 16.6 + (i % 7) * 0.9).ToArray();
        var original = FrameStats.Summarize(intervals, [1.1, 2.3, 0.7], new GcDelta(before, after, TimeSpan.FromMilliseconds(9.3)), 10.25);

        var json = JsonSerializer.Serialize(original, HistoriaJson.Options);
        var restored = JsonSerializer.Deserialize<FrameSummary>(json, HistoriaJson.Options);

        Assert.Equal(original, restored);
        Assert.Contains("\"intervalP99Ms\"", json, StringComparison.Ordinal);
        Assert.Contains("\"headroomMs\"", json, StringComparison.Ordinal);
    }

    // ---- Helpers --------------------------------------------------------------------------

    private static readonly FrameSummary Baseline = new(
        Frames: 1200, Seconds: 20, AverageFps: 60,
        IntervalP50Ms: 16.7, IntervalP95Ms: 17.5, IntervalP99Ms: 20, IntervalMaxMs: 30,
        FramesOverCeiling: 4,
        RenderP50Ms: 2, RenderP99Ms: 5, RenderMaxMs: 8,
        Gen0Collections: 10, Gen1Collections: 2, Gen2Collections: 0,
        GcPauseTotalMs: 100, GcPauseMaxMs: 10, AllocatedMbPerSecond: 50);

    private static FrameSummary[] Runs(double[]? p99 = null, int[]? over = null, double[]? fps = null)
    {
        var count = p99?.Length ?? over?.Length ?? fps?.Length ?? 1;
        return Enumerable.Range(0, count).Select(i => Baseline with
        {
            IntervalP99Ms = p99?[i] ?? Baseline.IntervalP99Ms,
            FramesOverCeiling = over?[i] ?? Baseline.FramesOverCeiling,
            AverageFps = fps?[i] ?? Baseline.AverageFps,
        }).ToArray();
    }

    /// <summary>Five identical baseline runs against five identical candidate runs, so every
    /// noise band sits at the floor.</summary>
    private static FrameComparison Versus(FrameSummary candidate)
        => FrameStats.Compare(Enumerable.Repeat(Baseline, 5).ToArray(), Enumerable.Repeat(candidate, 5).ToArray());

    private static FrameComparison Same() => Versus(Baseline);

    private static FrameComparison Better(double? p99 = null, double? renderP99 = null, double? gcTotal = null, double? gcMax = null)
        => Versus(Baseline with
        {
            IntervalP99Ms = p99 ?? Baseline.IntervalP99Ms,
            RenderP99Ms = renderP99 ?? Baseline.RenderP99Ms,
            GcPauseTotalMs = gcTotal ?? Baseline.GcPauseTotalMs,
            GcPauseMaxMs = gcMax ?? Baseline.GcPauseMaxMs,
        });

    private static FrameComparison Worse(double? max = null, int? over = null)
        => Versus(Baseline with
        {
            IntervalMaxMs = max ?? Baseline.IntervalMaxMs,
            FramesOverCeiling = over ?? Baseline.FramesOverCeiling,
        });

    private static VerdictCell Cell(string repo, string workload, FrameComparison comparison) => new(repo, workload, comparison);
}
