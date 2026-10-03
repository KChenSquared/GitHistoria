namespace Historia.Diagnostics;

/// <summary>
/// The process-wide switch for the scripted frame benchmark (<c>--frame-bench</c>). Every probe
/// in non-benchmark code checks <see cref="Active"/> first, so with the switch off each probe is
/// one static <c>bool</c> read and nothing else.
/// </summary>
internal static class FrameBench
{
    /// <summary>The verb that runs a benchmark launch.</summary>
    public const string RunVerb = "--frame-bench";

    /// <summary>The verb that summarises a folder of benchmark results.</summary>
    public const string SummarizeVerb = "--frame-bench-summarize";

    /// <summary>True only inside a <c>--frame-bench</c> launch. Set once, before Avalonia starts.</summary>
    public static bool Active { get; private set; }

    /// <summary>The parsed command line of the running benchmark; null when <see cref="Active"/> is false.</summary>
    public static FrameBenchOptions? Options { get; private set; }

    private static long _graphRenderTicks;
    private static int _graphRenderCount;
    private static int _finishedExitCode = -1;

    public static void Activate(FrameBenchOptions options)
    {
        Options = options;
        Active = true;
    }

    /// <summary>
    /// Adds one <c>CommitGraphControl.Render</c> pass that began at <paramref name="startTimestamp"/>
    /// (<see cref="Stopwatch.GetTimestamp"/>). Render runs on the UI thread, as does the recorder
    /// that drains the sum, so plain fields suffice and nothing is allocated.
    /// </summary>
    public static void RecordGraphRender(long startTimestamp)
    {
        _graphRenderTicks += Stopwatch.GetTimestamp() - startTimestamp;
        _graphRenderCount++;
    }

    /// <summary>Returns and clears the graph render time accumulated since the last call.</summary>
    public static (long Ticks, int Count) TakeGraphRender()
    {
        var taken = (_graphRenderTicks, _graphRenderCount);
        _graphRenderTicks = 0;
        _graphRenderCount = 0;
        return taken;
    }

    /// <summary>Records the exit code the run settled on, so a watchdog that fires during the
    /// shutdown that follows exits with it rather than with the timeout code.</summary>
    public static void Finish(int exitCode) => Interlocked.CompareExchange(ref _finishedExitCode, exitCode, -1);

    /// <summary>The settled exit code, or null while the run is still going.</summary>
    public static int? FinishedExitCode
    {
        get
        {
            var code = Volatile.Read(ref _finishedExitCode);
            return code < 0 ? null : code;
        }
    }
}

/// <summary>Process exit codes of a benchmark launch.</summary>
internal static class FrameBenchExitCodes
{
    public const int Ok = 0;
    public const int Failure = 1;
    public const int Usage = 2;
    public const int AnotherInstance = 3;
    public const int Watchdog = 4;
    public const int HeadMismatch = 5;
    public const int InvalidRun = 6;
}

/// <summary>Which set of workloads a launch runs.</summary>
internal enum FrameBenchMode
{
    /// <summary><c>graph-scroll</c>, <c>graph-jump</c> and <c>diff-scroll</c>, in that order.</summary>
    All,

    /// <summary>The terminal workload on its own.</summary>
    Terminal,
}

/// <summary>
/// <c>--frame-bench &lt;all|terminal&gt; --repo &lt;path&gt; --out &lt;dir&gt; --data &lt;dir&gt; --run &lt;n&gt;
/// [--seconds 20] [--seed 1] [--config &lt;frame-bench.json&gt;]</c>. Paths are made absolute against
/// the working directory.
/// </summary>
internal sealed record FrameBenchOptions(
    FrameBenchMode Mode,
    string Repo,
    string OutDirectory,
    string DataDirectory,
    int Run,
    double Seconds,
    int Seed,
    string? ConfigPath)
{
    public const double DefaultSeconds = 20;
    public const int DefaultSeed = 1;

    /// <summary>The repository's folder name — the key into <c>frame-bench.json</c> and the first
    /// part of every output file name.</summary>
    public string RepoKey => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Repo));

    public string ModeName => Mode == FrameBenchMode.Terminal ? "terminal" : "all";

    /// <summary>Parses the arguments after <see cref="FrameBench.RunVerb"/>'s position; returns
    /// null and an error message when they are incomplete or malformed.</summary>
    public static FrameBenchOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        string? mode = null, repo = null, outDir = null, data = null, config = null;
        int? run = null;
        var seconds = DefaultSeconds;
        var seed = DefaultSeed;

        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string? Next()
            {
                if (i + 1 >= args.Count)
                    return null;
                i++;
                return args[i];
            }

            switch (a)
            {
                case FrameBench.RunVerb: mode = Next(); break;
                case "--repo": repo = Next(); break;
                case "--out": outDir = Next(); break;
                case "--data": data = Next(); break;
                case "--config": config = Next(); break;
                case "--run":
                    if (!int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) || r < 0)
                    {
                        error = "--run needs a non-negative integer.";
                        return null;
                    }
                    run = r;
                    break;
                case "--seconds":
                    if (!double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
                        || !double.IsFinite(s) || s <= 0 || s > 300)
                    {
                        error = "--seconds needs a number above 0 and at most 300.";
                        return null;
                    }
                    seconds = s;
                    break;
                case "--seed":
                    if (!int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sd))
                    {
                        error = "--seed needs an integer.";
                        return null;
                    }
                    seed = sd;
                    break;
                default:
                    error = $"Unknown argument '{a}'.";
                    return null;
            }
        }

        FrameBenchMode parsedMode;
        if (string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase))
            parsedMode = FrameBenchMode.All;
        else if (string.Equals(mode, "terminal", StringComparison.OrdinalIgnoreCase))
            parsedMode = FrameBenchMode.Terminal;
        else
        {
            error = $"{FrameBench.RunVerb} needs 'all' or 'terminal'.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(outDir)
            || string.IsNullOrWhiteSpace(data) || run is null)
        {
            error = "--repo, --out, --data and --run are required.";
            return null;
        }

        return new FrameBenchOptions(
            parsedMode,
            System.IO.Path.GetFullPath(repo),
            System.IO.Path.GetFullPath(outDir),
            System.IO.Path.GetFullPath(data),
            run.Value,
            seconds,
            seed,
            string.IsNullOrWhiteSpace(config) ? null : System.IO.Path.GetFullPath(config));
    }

    /// <summary>The usage line printed with a parse error.</summary>
    public const string Usage =
        "usage: Historia --frame-bench <all|terminal> --repo <path> --out <dir> --data <dir> --run <n> "
        + "[--seconds 20] [--seed 1] [--config <frame-bench.json>]\n"
        + "       Historia --frame-bench-summarize <out> [--runs <n>]";
}

/// <summary>
/// <c>dev/frame-bench.json</c>: per repository folder name, the HEAD the benchmark requires and the
/// commit and file the diff workload opens. Holds no absolute path; the repository arrives
/// through <c>--repo</c>.
/// </summary>
internal sealed record FrameBenchConfig(IReadOnlyDictionary<string, FrameBenchRepoPin> Repos)
{
    public const string FileName = "frame-bench.json";

    /// <summary>
    /// Loads <paramref name="explicitPath"/>, or else the first <c>dev/frame-bench.json</c> found
    /// walking up from the executable's folder and then from the working directory — a build or
    /// publish folder inside the repository finds the tracked file.
    /// </summary>
    public static FrameBenchConfig Load(string? explicitPath, out string resolvedPath)
    {
        resolvedPath = explicitPath ?? Find(AppContext.BaseDirectory) ?? Find(Environment.CurrentDirectory)
            ?? throw new FileNotFoundException($"No dev/{FileName} above {AppContext.BaseDirectory} or {Environment.CurrentDirectory}; pass --config.");
        var config = JsonSerializer.Deserialize<FrameBenchConfig>(File.ReadAllText(resolvedPath), HistoriaJson.Options);
        return config ?? throw new InvalidDataException($"{resolvedPath} is empty.");
    }

    private static string? Find(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "dev", FileName);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}

/// <summary>One repository's pins in <see cref="FrameBenchConfig"/>.</summary>
internal sealed record FrameBenchRepoPin(string Head, FrameBenchDiffTarget Diff);

/// <summary>The commit and repo-relative path the diff workload opens.</summary>
internal sealed record FrameBenchDiffTarget(string Commit, string Path);
