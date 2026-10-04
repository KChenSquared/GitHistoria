namespace Historia.Diagnostics;

/// <summary>
/// <c>--frame-bench-summarize &lt;out&gt; [--runs &lt;n&gt;]</c>: loads every result JSON under
/// <c>&lt;out&gt;</c>, compares build A with build B per (repo, workload) cell with
/// <see cref="FrameStats.Compare"/>, applies <see cref="FrameStats.Verdict"/>, and writes
/// <c>&lt;out&gt;/summary.md</c>.
/// <para>
/// Build A's runs are <c>&lt;out&gt;/A/*.json</c> and build B's <c>&lt;out&gt;/B/*.json</c>. Without
/// those folders the JSON files directly under <c>&lt;out&gt;</c> are split by Avalonia version, the
/// lower one being A. With only one side present the summary tabulates it and gives no verdict.
/// With <c>--runs</c>, a cell with fewer valid runs than that on either side is invalid.
/// </para>
/// </summary>
internal static class FrameBenchSummarizer
{
    public const string SummaryFileName = "summary.md";

    public static int Run(string[] args)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine(FrameBenchOptions.Usage);
            return FrameBenchExitCodes.Usage;
        }

        int? runs = null;
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] == "--runs" && i + 1 < args.Length
                && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) && r > 0)
            {
                runs = r;
                i++;
            }
            else
            {
                Console.Error.WriteLine($"Unknown argument '{args[i]}'.");
                Console.Error.WriteLine(FrameBenchOptions.Usage);
                return FrameBenchExitCodes.Usage;
            }
        }

        var outDir = System.IO.Path.GetFullPath(args[1]);
        if (!Directory.Exists(outDir))
        {
            Console.Error.WriteLine($"frame-bench: {outDir} does not exist.");
            return FrameBenchExitCodes.Usage;
        }

        var notes = new List<string>();
        var (a, b, labelA, labelB) = LoadSides(outDir, notes);
        if (a.Count == 0 && b.Count == 0)
        {
            Console.Error.WriteLine($"frame-bench: no result files in {outDir}.");
            return FrameBenchExitCodes.Failure;
        }

        var markdown = Render(a, b, labelA, labelB, runs, notes);
        File.WriteAllText(System.IO.Path.Combine(outDir, SummaryFileName), markdown);
        Console.Out.WriteLine(markdown);
        return FrameBenchExitCodes.Ok;
    }

    private static (List<FrameBenchResult> A, List<FrameBenchResult> B, string LabelA, string LabelB) LoadSides(
        string outDir, List<string> notes)
    {
        var dirA = System.IO.Path.Combine(outDir, "A");
        var dirB = System.IO.Path.Combine(outDir, "B");
        if (Directory.Exists(dirA) || Directory.Exists(dirB))
        {
            var a = LoadAll(dirA, notes);
            var b = LoadAll(dirB, notes);
            return (a, b, "A (" + Versions(a) + ")", "B (" + Versions(b) + ")");
        }

        var all = LoadAll(outDir, notes);
        if (all.Count == 0)
            return ([], [], "A (no runs)", "B (no runs)");
        var versions = all.Select(r => r.AvaloniaVersion).Distinct(StringComparer.Ordinal)
            .OrderBy(v => Version.TryParse(v.Split('-')[0], out var parsed) ? parsed : new Version(0, 0))
            .ToList();
        if (versions.Count > 2)
            notes.Add($"More than two Avalonia versions ({string.Join(", ", versions)}); A is {versions[0]}, B is {versions[^1]}, the rest are ignored.");
        var sideA = all.Where(r => r.AvaloniaVersion == versions[0]).ToList();
        var sideB = versions.Count > 1 ? all.Where(r => r.AvaloniaVersion == versions[^1]).ToList() : [];
        return (sideA, sideB, "A (" + Versions(sideA) + ")", "B (" + Versions(sideB) + ")");
    }

    private static string Versions(List<FrameBenchResult> side)
        => side.Count == 0 ? "no runs" : string.Join(", ", side.Select(r => r.AvaloniaVersion).Distinct(StringComparer.Ordinal));

    private static List<FrameBenchResult> LoadAll(string dir, List<string> notes)
    {
        var results = new List<FrameBenchResult>();
        if (!Directory.Exists(dir))
            return results;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            try
            {
                if (JsonSerializer.Deserialize<FrameBenchResult>(File.ReadAllText(file), HistoriaJson.Options) is { } r)
                    results.Add(r);
            }
            catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
            {
                notes.Add($"Skipped {System.IO.Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return results;
    }

    private static string Render(
        List<FrameBenchResult> a, List<FrameBenchResult> b, string labelA, string labelB, int? runs, List<string> notes)
    {
        var inv = CultureInfo.InvariantCulture;
        var md = new System.Text.StringBuilder();
        md.AppendLine("# Frame benchmark summary");
        md.AppendLine();
        md.AppendLine(string.Create(inv, $"Generated {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}. Baseline {labelA}; candidate {labelB}."));
        if (runs is { } expected)
            md.AppendLine(string.Create(inv, $"Each cell needs {expected} valid runs per side."));
        var env = a.Concat(b).Select(r => r.Environment).FirstOrDefault();
        if (env is not null)
        {
            md.AppendLine(string.Create(inv,
                $"Display {env.RefreshRateHz} Hz{(env.RefreshRateFallback ? " (assumed)" : "")}, DPI scale {env.DpiScale:0.###}, window {env.WindowWidthPx}x{env.WindowHeightPx} px; {env.OsDescription}; {env.Runtime}; {env.ProcessorCount} logical CPUs."));
        }
        md.AppendLine();

        var hasB = b.Count > 0;
        var cells = new List<VerdictCell>();
        var repos = a.Concat(b).Select(r => r.Repo).Distinct(StringComparer.Ordinal).ToList();
        var body = new System.Text.StringBuilder();

        foreach (var repo in repos)
        {
            foreach (var workload in OrderedWorkloads(a.Concat(b).Where(r => r.Repo == repo).Select(r => r.Workload)))
            {
                var runsA = a.Where(r => r.Repo == repo && r.Workload == workload).ToList();
                var runsB = b.Where(r => r.Repo == repo && r.Workload == workload).ToList();
                var validA = runsA.Where(r => r.Validity.Valid).Select(r => r.Summary).ToList();
                var validB = runsB.Where(r => r.Validity.Valid).Select(r => r.Summary).ToList();

                body.AppendLine(string.Create(inv, $"## {repo} / {workload}"));
                body.AppendLine();
                var validLine = hasB
                    ? string.Create(inv, $"Valid runs: A {validA.Count} of {runsA.Count}, B {validB.Count} of {runsB.Count}.")
                    : string.Create(inv, $"Valid runs: A {validA.Count} of {runsA.Count}.");
                body.AppendLine(validLine);

                string? invalidWhy = null;
                if (validA.Count == 0 || (hasB && validB.Count == 0))
                    invalidWhy = "a side has no valid run";
                else if (runs is { } need && (validA.Count < need || (hasB && validB.Count < need)))
                    invalidWhy = $"fewer than {need} valid runs on a side";

                if (!hasB)
                {
                    if (validA.Count > 0)
                        AppendSingleSide(body, validA);
                    body.AppendLine();
                    continue;
                }

                if (invalidWhy is not null)
                {
                    body.AppendLine($"**Invalid cell:** {invalidWhy}.");
                    body.AppendLine();
                    cells.Add(new VerdictCell(repo, workload, null));
                    continue;
                }

                var comparison = FrameStats.Compare(validA, validB);
                cells.Add(new VerdictCell(repo, workload, comparison));
                AppendComparison(body, comparison, workload);
            }
        }

        if (hasB)
        {
            var verdict = FrameStats.Verdict(cells);
            md.AppendLine($"**Verdict:** {verdict.Outcome.ToString().ToUpperInvariant()}");
            md.AppendLine();
            foreach (var reason in verdict.Reasons)
                md.AppendLine("- " + reason);
        }
        else
        {
            md.AppendLine("**Verdict:** none; only one build's runs are present, so there is nothing to compare.");
        }
        md.AppendLine();
        md.Append(body);

        var invalid = a.Select(r => ("A", r)).Concat(b.Select(r => ("B", r))).Where(x => !x.r.Validity.Valid).ToList();
        if (invalid.Count > 0)
        {
            md.AppendLine("## Invalid runs");
            md.AppendLine();
            foreach (var (side, r) in invalid)
                md.AppendLine(string.Create(inv, $"- {side} {r.Repo} {r.Workload} r{r.Run}: {string.Join("; ", r.Validity.Reasons)}"));
            md.AppendLine();
        }

        var refused = a.Concat(b).Where(r => r.Validity.RefusedGitRequests > 0).ToList();
        md.AppendLine("## Read-only guard");
        md.AppendLine();
        if (refused.Count == 0)
        {
            md.AppendLine("No git request was refused in any run.");
        }
        else
        {
            md.AppendLine("Refused requests are informational: the guard answered them without running git.");
            md.AppendLine();
            foreach (var command in refused.SelectMany(r => r.Validity.RefusedGitCommands).Distinct(StringComparer.Ordinal))
                md.AppendLine($"- `{command}`");
        }
        md.AppendLine();

        if (notes.Count > 0)
        {
            md.AppendLine("## Notes");
            md.AppendLine();
            foreach (var note in notes)
                md.AppendLine("- " + note);
        }
        return md.ToString();
    }

    private static IEnumerable<string> OrderedWorkloads(IEnumerable<string> present)
    {
        var set = present.Distinct(StringComparer.Ordinal).ToList();
        foreach (var known in FrameWorkloadNames.Ordered)
        {
            if (set.Remove(known))
                yield return known;
        }
        foreach (var other in set.Order(StringComparer.Ordinal))
            yield return other;
    }

    private static void AppendComparison(System.Text.StringBuilder md, FrameComparison c, string workload)
    {
        var inv = CultureInfo.InvariantCulture;
        md.AppendLine(string.Create(inv,
            $"Headroom (22.2 ms - p99 interval): A {c.HeadroomA:0.00} ms, B {c.HeadroomB:0.00} ms. "
            + $"Frames over 22.2 ms in the median run: A {(c.CeilingBrokenA ? "yes" : "none")}, B {(c.CeilingBrokenB ? "yes" : "none")}."));
        md.AppendLine();
        md.AppendLine("| Metric | A median | B median | Change | Noise band | |");
        md.AppendLine("|---|---:|---:|---:|---:|---|");
        var graph = workload is FrameWorkloadNames.GraphScroll or FrameWorkloadNames.GraphJump;
        foreach (var m in c.Metrics)
        {
            var flag = "";
            var isWinFrame = m.Metric is FrameMetric.IntervalP99 or FrameMetric.RenderP99 or FrameMetric.FramesOverCeiling;
            var isWinGc = m.Metric is FrameMetric.GcPauseTotal or FrameMetric.GcPauseMax;
            var isRegression = m.Metric is FrameMetric.IntervalP99 or FrameMetric.IntervalMax or FrameMetric.FramesOverCeiling;
            if (isRegression && m.WorsensBy(FrameStats.RegressionThreshold))
                flag = "regression";
            else if (graph && isWinFrame && m.ImprovesBy(FrameStats.FrameWinThreshold))
                flag = "win";
            else if (graph && isWinGc && m.ImprovesBy(FrameStats.GcWinThreshold))
                flag = "win";
            md.AppendLine(string.Create(inv,
                $"| {FrameStats.Label(m.Metric)} | {m.MedianA:0.###} | {m.MedianB:0.###} | {m.Change * 100:+0.0;-0.0;0.0}% | {m.NoiseBand * 100:0.0}% | {flag} |"));
        }
        md.AppendLine();
    }

    private static void AppendSingleSide(System.Text.StringBuilder md, List<FrameSummary> runs)
    {
        var inv = CultureInfo.InvariantCulture;
        var values = new double[runs.Count];
        var headroom = new double[runs.Count];
        for (var i = 0; i < runs.Count; i++)
            headroom[i] = runs[i].HeadroomMs;
        md.AppendLine(string.Create(inv, $"Headroom (22.2 ms - p99 interval), median: {FrameStats.Median(headroom):0.00} ms."));
        md.AppendLine();
        md.AppendLine("| Metric | A median | A min | A max |");
        md.AppendLine("|---|---:|---:|---:|");
        foreach (var metric in Enum.GetValues<FrameMetric>())
        {
            for (var i = 0; i < runs.Count; i++)
                values[i] = FrameStats.Value(runs[i], metric);
            md.AppendLine(string.Create(inv,
                $"| {FrameStats.Label(metric)} | {FrameStats.Median(values):0.###} | {values.Min():0.###} | {values.Max():0.###} |"));
        }
    }
}
