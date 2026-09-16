namespace Historia.Tests;

/// <summary>
/// Skips a <see cref="Xunit.FactAttribute"/>-based test unless <c>HISTORIA_SPLICE_REPO</c> is set —
/// xUnit 2.9 has no dynamic (runtime-computed) skip, only a static <see cref="Skip"/> string
/// evaluated at discovery time, so the env var is read in this attribute's constructor.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ManualFactAttribute : FactAttribute
{
    public ManualFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HISTORIA_SPLICE_REPO")))
            Skip = "Manual-only: set HISTORIA_SPLICE_REPO to a real large repo's path to run.";
    }
}

/// <summary>
/// <see cref="GraphMetaSplice"/>'s guarded live trigger
/// (<c>GraphLayoutService.TryHeadlessSpliceAsync</c>) can only be exercised at the scale its own
/// guards care about — a real 200,000+ row history, a real fetch that moves a real branch, real
/// pack/disk timings for guard 4's budget, and a real shallow clone for guard 2 — none of which a
/// fixture repo built in-process can reproduce in reasonable time (<see cref="GraphHeadlessCacheTests"/>
/// exercises the wiring itself with lowered thresholds instead). These tests are skipped unless
/// <c>HISTORIA_SPLICE_REPO</c> names a real local clone (linux, vscode, or similar) to run against,
/// so they also double as a documented manual test script:
/// <list type="number">
/// <item>Point <c>HISTORIA_SPLICE_REPO</c> at a real ≥ 200,000-row clone (e.g. linux). Turn on
/// "Splice new commits into the graph cache instead of re-walking" in Settings ▸ Advanced. Open the
/// repo once so its full history is on disk and exhausted (§7.6's background spill), then close it
/// (or switch to another tab) so it becomes an inactive tab.</item>
/// <item>Advance the checked-out branch's remote by a real <c>git fetch</c> — a normal fast-forward
/// fetch of a handful of new commits, no other branch touched. Wait for the inactive-tab headless
/// pipeline to run (perf.log: <c>graph splice committed</c> or <c>graph splice rejected: &lt;reason&gt;</c>,
/// or a plain <c>graph cache write: reorder …</c> mark if it fell back).</item>
/// <item><b>Verify the spliced order equals a fresh walk:</b> run
/// <c>git rev-list --date-order &lt;new tip&gt;</c> and compare its SHA order to the on-disk store's
/// row order (<see cref="GraphMetaStore.ProbeExisting"/> + <see cref="DiskCommitStore.Open"/>, same
/// as <see cref="GraphHeadlessCacheTests.Rows_after_a_headless_reorder_read_back_in_the_new_walks_order"/>
/// does at fixture scale) — they must match exactly.</item>
/// <item><b>Verify lane count and line entries per row</b> at row 5,000 and row 50,000 are within 5%
/// of a from-scratch re-walk of the same history (temporarily turn the setting off, clear the
/// persistent cache from Settings, and reopen to get a genuine re-walk to compare against).</item>
/// <item><b>Guard 5 (multi-branch fetch):</b> fetch two branches at once, each with new commits —
/// perf.log should show <c>graph splice rejected: an added commit's old parent sits at row … &gt;=
/// …</c> (or the fetch simply produces more than <see cref="GraphMetaSplice.MaxOldParentRow"/> rows
/// of movement) and fall back to the ordinary reorder, never a wrong/partial splice.</item>
/// <item><b>Shallow repo:</b> against a shallow clone (<c>git clone --depth 50</c>), the same fetch
/// must show <c>graph splice rejected: repo is shallow</c>.</item>
/// <item><b>Perf.log marks to look for:</b> <c>graph splice committed: +N new, -M, R rows</c> on
/// success; <c>graph splice rejected: &lt;reason&gt;</c> on a guard failure (reason names which
/// guard); a plain reorder mark if the splice path never engaged at all (setting off, or the
/// pre-check on <see cref="GraphMetaSplice.MinRowsToConsider"/> failed before any query ran).</item>
/// </list>
/// </summary>
public sealed class GraphSpliceManualTests
{
    [ManualFact]
    public void Splice_on_a_real_large_repo_matches_a_fresh_walk_within_the_documented_tolerances()
    {
        var repoPath = Environment.GetEnvironmentVariable("HISTORIA_SPLICE_REPO")!;
        throw new Xunit.Sdk.XunitException(
            $"Manual test — follow this class's own doc comment against {repoPath} by hand; " +
            "there is no automated assertion here (see the doc comment for why).");
    }
}
