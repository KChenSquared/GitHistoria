namespace Historia.Diagnostics;

/// <summary>
/// The pure arithmetic behind the frame benchmark: one run's frame intervals, graph render times
/// and GC counters become a <see cref="FrameSummary"/>; the summaries of several runs of two builds
/// become a <see cref="FrameComparison"/>; and the comparisons of every (repo, workload) cell
/// become a <see cref="FrameVerdict"/>. Nothing here measures anything — the caller captures the
/// timestamps and GC counters and hands them in — so every number that decides the verdict is
/// reproducible from the inputs alone.
/// <para>
/// Percentiles are <b>nearest-rank</b>: for <c>n</c> sorted samples, percentile <c>p</c> is the
/// sample at 1-based rank <c>ceil(p / 100 × n)</c>, clamped to <c>[1, n]</c>. It always returns an
/// observed sample (no interpolation), so a p99 over a 20 s run at 60 Hz is a real frame.
/// </para>
/// </summary>
public static class FrameStats
{
    /// <summary>The frame-time ceiling: no frame may take longer than this (the 45 FPS budget).</summary>
    public const double FrameCeilingMs = 22.2;

    /// <summary>The smallest noise band a comparison uses, as a fraction (5%).</summary>
    public const double NoiseFloor = 0.05;

    /// <summary>A frame-time metric must improve by at least this fraction to count as a win.</summary>
    public const double FrameWinThreshold = 0.10;

    /// <summary>A GC-pause metric must drop by at least this fraction to count as a win.</summary>
    public const double GcWinThreshold = 0.25;

    /// <summary>A frame-time metric that worsens by at least this fraction is a regression.</summary>
    public const double RegressionThreshold = 0.10;

    /// <summary>The workload that scrolls the graph continuously. A win can only come from this
    /// workload or <see cref="GraphJumpWorkload"/>.</summary>
    public const string GraphScrollWorkload = "graph-scroll";

    /// <summary>The workload that jumps the graph to cold rows. A win can only come from this
    /// workload or <see cref="GraphScrollWorkload"/>.</summary>
    public const string GraphJumpWorkload = "graph-jump";

    /// <summary>Slack on the "at least N%" thresholds, so a change computed as 0.0999999… from
    /// 10 → 11 still counts as 10%.</summary>
    private const double ThresholdEpsilon = 1e-9;

    private const double BytesPerMb = 1024d * 1024d;

    private static readonly FrameMetric[] WinFrameMetrics =
        [FrameMetric.IntervalP99, FrameMetric.RenderP99, FrameMetric.FramesOverCeiling];

    private static readonly FrameMetric[] WinGcMetrics = [FrameMetric.GcPauseTotal, FrameMetric.GcPauseMax];

    private static readonly FrameMetric[] RegressionMetrics =
        [FrameMetric.IntervalP99, FrameMetric.IntervalMax, FrameMetric.FramesOverCeiling];

    /// <summary>
    /// Summarises one run. <paramref name="intervalsMs"/> holds the time between consecutive
    /// frames, one entry per frame, so the frame count is its length and the average FPS is that
    /// count over <paramref name="seconds"/>. <paramref name="renderMs"/> holds the graph render
    /// time of each frame that rendered the graph; it may be shorter than the intervals or empty.
    /// An empty span yields zero for each of its percentiles, and a non-positive or non-finite
    /// duration yields zero FPS and zero allocation rate, so the summary never holds NaN or
    /// infinity and always serialises.
    /// </summary>
    public static FrameSummary Summarize(
        ReadOnlySpan<double> intervalsMs, ReadOnlySpan<double> renderMs, GcDelta gc, double seconds)
    {
        ArgumentNullException.ThrowIfNull(gc);

        var intervals = SortedCopy(intervalsMs);
        var render = SortedCopy(renderMs);

        var overCeiling = 0;
        foreach (var interval in intervals)
        {
            if (interval > FrameCeilingMs)
                overCeiling++;
        }

        var hasDuration = seconds > 0 && double.IsFinite(seconds);
        var pauseTotalMs = Math.Max(0, (gc.After.TotalPause - gc.Before.TotalPause).TotalMilliseconds);
        // One pause cannot outlast every pause in the window together; a larger observed value
        // belongs to a collection that finished before the window opened.
        var pauseMaxMs = Math.Clamp(gc.MaxPause.TotalMilliseconds, 0, pauseTotalMs);
        var allocatedMb = Math.Max(0, gc.After.TotalAllocatedBytes - gc.Before.TotalAllocatedBytes) / BytesPerMb;

        return new FrameSummary(
            Frames: intervals.Length,
            Seconds: hasDuration ? seconds : 0,
            AverageFps: hasDuration ? intervals.Length / seconds : 0,
            IntervalP50Ms: NearestRank(intervals, 50),
            IntervalP95Ms: NearestRank(intervals, 95),
            IntervalP99Ms: NearestRank(intervals, 99),
            IntervalMaxMs: intervals.Length == 0 ? 0 : intervals[^1],
            FramesOverCeiling: overCeiling,
            RenderP50Ms: NearestRank(render, 50),
            RenderP99Ms: NearestRank(render, 99),
            RenderMaxMs: render.Length == 0 ? 0 : render[^1],
            Gen0Collections: Math.Max(0, gc.After.Gen0Collections - gc.Before.Gen0Collections),
            Gen1Collections: Math.Max(0, gc.After.Gen1Collections - gc.Before.Gen1Collections),
            Gen2Collections: Math.Max(0, gc.After.Gen2Collections - gc.Before.Gen2Collections),
            GcPauseTotalMs: pauseTotalMs,
            GcPauseMaxMs: pauseMaxMs,
            AllocatedMbPerSecond: hasDuration ? allocatedMb / seconds : 0);
    }

    /// <summary>The nearest-rank <paramref name="percentile"/> (0–100) of
    /// <paramref name="values"/>, which need not be sorted. Zero for an empty span.</summary>
    public static double Percentile(ReadOnlySpan<double> values, double percentile)
        => NearestRank(SortedCopy(values), percentile);

    /// <summary>The median of <paramref name="values"/>: the middle sample of an odd count, the
    /// mean of the two middle samples of an even count, zero for an empty span.</summary>
    public static double Median(ReadOnlySpan<double> values)
    {
        var sorted = SortedCopy(values);
        if (sorted.Length == 0)
            return 0;
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>
    /// Compares the runs of build <paramref name="a"/> (the baseline) with the runs of build
    /// <paramref name="b"/>, metric by metric: the median of each side, the relative change from
    /// A to B, and the noise band.
    /// <para>
    /// The <b>relative change</b> is <c>(median B − median A) / median A</c>, positive when B's
    /// value is larger. When median A is zero the reference becomes median B, so a change from
    /// 0 to any N reads as +100% rather than infinity, and 0 against 0 is no change.
    /// </para>
    /// <para>
    /// The <b>noise band</b> is the larger of the two sides' relative spreads,
    /// <c>(max − min) / median</c>, never less than <see cref="NoiseFloor"/>. A side whose median
    /// is zero measures its spread against the same reference as the change, so a baseline of
    /// mostly-zero counts with one stray spike still yields a band in the change's units: a
    /// 0 → N move counts only when N clears the noise both sides showed.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">Either side has no runs.</exception>
    public static FrameComparison Compare(IReadOnlyList<FrameSummary> a, IReadOnlyList<FrameSummary> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count == 0)
            throw new ArgumentException("The baseline has no runs to compare.", nameof(a));
        if (b.Count == 0)
            throw new ArgumentException("The candidate has no runs to compare.", nameof(b));

        var metrics = Enum.GetValues<FrameMetric>();
        var comparisons = new MetricComparison[metrics.Length];
        var valuesA = new double[a.Count];
        var valuesB = new double[b.Count];
        for (var m = 0; m < metrics.Length; m++)
        {
            var metric = metrics[m];
            for (var i = 0; i < a.Count; i++)
                valuesA[i] = Value(a[i], metric);
            for (var i = 0; i < b.Count; i++)
                valuesB[i] = Value(b[i], metric);
            comparisons[m] = CompareMetric(metric, valuesA, valuesB);
        }
        return new FrameComparison(a.Count, b.Count, comparisons);
    }

    /// <summary>
    /// Applies the verdict rule across every (repo, workload) cell.
    /// <list type="bullet">
    /// <item><b>Win</b> — a repo wins when one of its <see cref="GraphScrollWorkload"/> or
    /// <see cref="GraphJumpWorkload"/> cells improves the frame-interval p99, the graph render
    /// p99 or the count of frames over the ceiling by at least <see cref="FrameWinThreshold"/>,
    /// or the total or max GC pause by at least <see cref="GcWinThreshold"/> — each beyond the
    /// noise band. The two workloads may differ between repos.</item>
    /// <item><b>Regression</b> — any cell, of any workload, whose frame-interval p99, max frame
    /// or count of frames over the ceiling worsens by at least <see cref="RegressionThreshold"/>
    /// and beyond the noise band.</item>
    /// <item><b>Outcome</b> — any regression is <see cref="VerdictOutcome.Skip"/>; otherwise a
    /// win on every repo, with at least two repos, is <see cref="VerdictOutcome.Proceed"/>; a
    /// win on some repos only is <see cref="VerdictOutcome.Inconclusive"/>; no win is
    /// <see cref="VerdictOutcome.Skip"/>.</item>
    /// </list>
    /// A cell whose <see cref="VerdictCell.Comparison"/> is null failed validity: its repo still
    /// counts toward "every repo", but the cell can neither win nor regress, and it is named in
    /// the reasons. The first reason is the headline; the rest name each win, regression and
    /// invalid cell.
    /// </summary>
    public static FrameVerdict Verdict(IReadOnlyList<VerdictCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        var repos = new List<string>();
        var winners = new HashSet<string>(StringComparer.Ordinal);
        var details = new List<string>();
        var regressions = 0;

        foreach (var cell in cells)
        {
            if (!repos.Contains(cell.Repo))
                repos.Add(cell.Repo);

            if (cell.Comparison is not { } comparison)
            {
                details.Add($"{cell.Repo} {cell.Workload}: invalid cell, not counted");
                continue;
            }

            foreach (var metric in RegressionMetrics)
            {
                var c = comparison[metric];
                if (c.WorsensBy(RegressionThreshold))
                {
                    regressions++;
                    details.Add($"{cell.Repo} {cell.Workload}: regression, {Describe(c, "worse")}");
                }
            }

            if (!IsGraphWorkload(cell.Workload))
                continue;

            foreach (var metric in WinFrameMetrics)
                AddWin(cell, comparison[metric], FrameWinThreshold);
            foreach (var metric in WinGcMetrics)
                AddWin(cell, comparison[metric], GcWinThreshold);
        }

        var withWin = repos.Where(winners.Contains).ToList();
        var withoutWin = repos.Where(r => !winners.Contains(r)).ToList();

        VerdictOutcome outcome;
        string headline;
        if (repos.Count == 0)
        {
            outcome = VerdictOutcome.Skip;
            headline = "No cells to judge.";
        }
        else if (regressions > 0)
        {
            outcome = VerdictOutcome.Skip;
            headline = regressions == 1 ? "1 regression." : $"{regressions} regressions.";
        }
        else if (withWin.Count == repos.Count && repos.Count >= 2)
        {
            outcome = VerdictOutcome.Proceed;
            headline = $"A win on every repo ({string.Join(", ", withWin)}) and no regression.";
        }
        else if (withWin.Count > 0)
        {
            outcome = VerdictOutcome.Inconclusive;
            headline = $"A win on {string.Join(", ", withWin)} only; none on "
                       + (withoutWin.Count == 0 ? "a second repo" : string.Join(", ", withoutWin)) + ".";
        }
        else
        {
            outcome = VerdictOutcome.Skip;
            headline = "No win on any repo.";
        }

        details.Insert(0, headline);
        return new FrameVerdict(outcome, details, withWin, withoutWin);

        void AddWin(VerdictCell cell, MetricComparison c, double threshold)
        {
            if (!c.ImprovesBy(threshold))
                return;
            winners.Add(cell.Repo);
            details.Add($"{cell.Repo} {cell.Workload}: win, {Describe(c, "better")}");
        }
    }

    /// <summary>A short human-readable name for <paramref name="metric"/>, for tables and reasons.</summary>
    public static string Label(FrameMetric metric) => metric switch
    {
        FrameMetric.AverageFps => "average FPS",
        FrameMetric.IntervalP50 => "frame-interval p50",
        FrameMetric.IntervalP95 => "frame-interval p95",
        FrameMetric.IntervalP99 => "frame-interval p99",
        FrameMetric.IntervalMax => "max frame",
        FrameMetric.FramesOverCeiling => "frames over 22.2 ms",
        FrameMetric.RenderP50 => "graph render p50",
        FrameMetric.RenderP99 => "graph render p99",
        FrameMetric.RenderMax => "graph render max",
        FrameMetric.Gen0Collections => "gen0 collections",
        FrameMetric.Gen1Collections => "gen1 collections",
        FrameMetric.Gen2Collections => "gen2 collections",
        FrameMetric.GcPauseTotal => "total GC pause",
        FrameMetric.GcPauseMax => "max GC pause",
        FrameMetric.AllocatedMbPerSecond => "allocated MB/s",
        _ => metric.ToString(),
    };

    /// <summary>Whether a smaller value of <paramref name="metric"/> is the better one. Only
    /// <see cref="FrameMetric.AverageFps"/> is better larger.</summary>
    public static bool LowerIsBetter(FrameMetric metric) => metric != FrameMetric.AverageFps;

    /// <summary>The value of <paramref name="metric"/> in one run's summary.</summary>
    public static double Value(FrameSummary summary, FrameMetric metric)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return metric switch
        {
            FrameMetric.AverageFps => summary.AverageFps,
            FrameMetric.IntervalP50 => summary.IntervalP50Ms,
            FrameMetric.IntervalP95 => summary.IntervalP95Ms,
            FrameMetric.IntervalP99 => summary.IntervalP99Ms,
            FrameMetric.IntervalMax => summary.IntervalMaxMs,
            FrameMetric.FramesOverCeiling => summary.FramesOverCeiling,
            FrameMetric.RenderP50 => summary.RenderP50Ms,
            FrameMetric.RenderP99 => summary.RenderP99Ms,
            FrameMetric.RenderMax => summary.RenderMaxMs,
            FrameMetric.Gen0Collections => summary.Gen0Collections,
            FrameMetric.Gen1Collections => summary.Gen1Collections,
            FrameMetric.Gen2Collections => summary.Gen2Collections,
            FrameMetric.GcPauseTotal => summary.GcPauseTotalMs,
            FrameMetric.GcPauseMax => summary.GcPauseMaxMs,
            FrameMetric.AllocatedMbPerSecond => summary.AllocatedMbPerSecond,
            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null),
        };
    }

    internal static bool MeetsThreshold(double fraction, double threshold)
        => fraction >= threshold - ThresholdEpsilon;

    private static MetricComparison CompareMetric(FrameMetric metric, double[] a, double[] b)
    {
        var medianA = Median(a);
        var medianB = Median(b);
        var reference = medianA > 0 ? medianA : medianB;
        var change = reference > 0 ? (medianB - medianA) / reference : 0;
        var band = Math.Max(NoiseFloor, Math.Max(Spread(a, medianA, reference), Spread(b, medianB, reference)));
        return new MetricComparison(metric, medianA, medianB, change, band);
    }

    private static double Spread(double[] values, double median, double reference)
    {
        var scale = median > 0 ? median : reference;
        if (scale <= 0)
            return 0;
        var min = double.MaxValue;
        var max = double.MinValue;
        foreach (var v in values)
        {
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }
        return (max - min) / scale;
    }

    private static bool IsGraphWorkload(string workload)
        => string.Equals(workload, GraphScrollWorkload, StringComparison.Ordinal)
           || string.Equals(workload, GraphJumpWorkload, StringComparison.Ordinal);

    private static string Describe(MetricComparison c, string direction)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{Label(c.Metric)} {Math.Abs(c.Change) * 100:F1}% {direction} ({c.MedianA:0.##} -> {c.MedianB:0.##}, noise band {c.NoiseBand * 100:F1}%)");

    private static double[] SortedCopy(ReadOnlySpan<double> values)
    {
        var copy = values.ToArray();
        Array.Sort(copy);
        return copy;
    }

    private static double NearestRank(double[] sorted, double percentile)
    {
        var n = sorted.Length;
        if (n == 0)
            return 0;
        // percentile × n is an exact integer for whole percentiles, so the division by 100 lands
        // exactly on a rank when it divides evenly and Ceiling never rounds a whole rank up.
        var rank = (int)Math.Ceiling(percentile * n / 100.0);
        return sorted[Math.Clamp(rank, 1, n) - 1];
    }
}

/// <summary>One GC counter reading: <see cref="GC.CollectionCount"/> for generations 0–2,
/// <see cref="GC.GetTotalPauseDuration"/> and <see cref="GC.GetTotalAllocatedBytes"/> (imprecise).
/// A generation's count also rises when a higher generation is collected, as the runtime reports
/// it.</summary>
public readonly record struct GcSnapshot(
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    TimeSpan TotalPause,
    long TotalAllocatedBytes);

/// <summary>The GC counters at the start and end of a measured window, plus the longest single
/// pause the recorder observed inside it. The caller captures them; <see cref="FrameStats"/> only
/// does the arithmetic.</summary>
public sealed record GcDelta(GcSnapshot Before, GcSnapshot After, TimeSpan MaxPause);

/// <summary>
/// One run's frame and GC statistics. Percentiles are nearest-rank (see <see cref="FrameStats"/>).
/// Generation counts are deltas of <see cref="GC.CollectionCount"/>, so
/// <see cref="Gen0Collections"/> also counts the gen1 and gen2 collections. Megabytes are MiB.
/// </summary>
public sealed record FrameSummary(
    int Frames,
    double Seconds,
    double AverageFps,
    double IntervalP50Ms,
    double IntervalP95Ms,
    double IntervalP99Ms,
    double IntervalMaxMs,
    int FramesOverCeiling,
    double RenderP50Ms,
    double RenderP99Ms,
    double RenderMaxMs,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double GcPauseTotalMs,
    double GcPauseMaxMs,
    double AllocatedMbPerSecond)
{
    /// <summary>How far the frame-interval p99 sits under the frame ceiling; negative when over.</summary>
    public double HeadroomMs => FrameStats.FrameCeilingMs - IntervalP99Ms;
}

/// <summary>A metric <see cref="FrameStats.Compare"/> reports on.</summary>
public enum FrameMetric
{
    AverageFps,
    IntervalP50,
    IntervalP95,
    IntervalP99,
    IntervalMax,
    FramesOverCeiling,
    RenderP50,
    RenderP99,
    RenderMax,
    Gen0Collections,
    Gen1Collections,
    Gen2Collections,
    GcPauseTotal,
    GcPauseMax,
    AllocatedMbPerSecond,
}

/// <summary>
/// One metric across the runs of two builds. <see cref="Change"/> is the signed relative change
/// from A's median to B's (positive = B larger) and <see cref="NoiseBand"/> the fraction it must
/// exceed to count; both are defined on <see cref="FrameStats.Compare"/>.
/// </summary>
public sealed record MetricComparison(
    FrameMetric Metric,
    double MedianA,
    double MedianB,
    double Change,
    double NoiseBand)
{
    /// <summary>The change in the metric's better direction: positive when B is better.</summary>
    public double Improvement => FrameStats.LowerIsBetter(Metric) ? -Change : Change;

    /// <summary>Whether the change, either way, is strictly larger than the noise band.</summary>
    public bool BeyondNoise => Math.Abs(Change) > NoiseBand;

    /// <summary>B is better by at least <paramref name="threshold"/> and beyond the noise band.</summary>
    public bool ImprovesBy(double threshold)
        => Improvement > NoiseBand && FrameStats.MeetsThreshold(Improvement, threshold);

    /// <summary>B is worse by at least <paramref name="threshold"/> and beyond the noise band.</summary>
    public bool WorsensBy(double threshold)
        => -Improvement > NoiseBand && FrameStats.MeetsThreshold(-Improvement, threshold);
}

/// <summary>Every metric for one (repo, workload) cell, from <see cref="FrameStats.Compare"/>.</summary>
public sealed record FrameComparison(int RunsA, int RunsB, IReadOnlyList<MetricComparison> Metrics)
{
    /// <summary>The comparison for <paramref name="metric"/>.</summary>
    public MetricComparison this[FrameMetric metric]
        => Metrics.FirstOrDefault(m => m.Metric == metric)
           ?? throw new KeyNotFoundException($"No comparison for {metric}.");

    /// <summary>The frame ceiling minus A's median frame-interval p99.</summary>
    public double HeadroomA => FrameStats.FrameCeilingMs - this[FrameMetric.IntervalP99].MedianA;

    /// <summary>The frame ceiling minus B's median frame-interval p99.</summary>
    public double HeadroomB => FrameStats.FrameCeilingMs - this[FrameMetric.IntervalP99].MedianB;

    /// <summary>A's median run has at least one frame over the ceiling.</summary>
    public bool CeilingBrokenA => this[FrameMetric.FramesOverCeiling].MedianA > 0;

    /// <summary>B's median run has at least one frame over the ceiling.</summary>
    public bool CeilingBrokenB => this[FrameMetric.FramesOverCeiling].MedianB > 0;
}

/// <summary>One (repo, workload) cell of the benchmark. A null <see cref="Comparison"/> marks a
/// cell that failed validity.</summary>
public sealed record VerdictCell(string Repo, string Workload, FrameComparison? Comparison);

/// <summary>What the benchmark says to do about the candidate build.</summary>
public enum VerdictOutcome
{
    Proceed,
    Skip,
    Inconclusive,
}

/// <summary>The outcome of <see cref="FrameStats.Verdict"/>, with the reasons (headline first)
/// and which repos did and did not win, in first-seen order.</summary>
public sealed record FrameVerdict(
    VerdictOutcome Outcome,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> ReposWithWin,
    IReadOnlyList<string> ReposWithoutWin);
