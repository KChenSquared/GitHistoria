namespace Historia.Diagnostics;

/// <summary>
/// One workload's result file, <c>&lt;repo&gt;-&lt;workload&gt;-&lt;avalonia&gt;-r&lt;run&gt;.json</c>. The
/// summariser reads <see cref="Summary"/> and <see cref="Validity"/>; the rest records what was
/// measured and on what.
/// </summary>
internal sealed record FrameBenchResult
{
    public const int CurrentSchema = 1;

    public int Schema { get; init; } = CurrentSchema;
    public required string Repo { get; init; }
    public required string Workload { get; init; }
    public required string Mode { get; init; }
    public required int Run { get; init; }
    public required double PlannedSeconds { get; init; }
    public required int Seed { get; init; }
    public required string AvaloniaVersion { get; init; }
    public required string AvaloniaInformationalVersion { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required FrameBenchEnvironment Environment { get; init; }
    public required FrameBenchStartup Startup { get; init; }
    public required FrameSummary Summary { get; init; }
    public required FrameBenchValidity Validity { get; init; }
    public required IReadOnlyDictionary<string, double> Coverage { get; init; }

    /// <summary>What the frame interval and render times mean, recorded with the numbers.</summary>
    public string Measures { get; init; } =
        "interval = time between consecutive TopLevel.RequestAnimationFrame callbacks (one per UI render pass, "
        + "paced by the compositor's commit while every frame commits); render = CommitGraphControl.Render "
        + "time on the UI thread (recording draw operations, not rasterisation) for the frames that drew the graph.";
}

/// <summary>The machine and window a run measured on.</summary>
internal sealed record FrameBenchEnvironment(
    int RefreshRateHz,
    bool RefreshRateFallback,
    double DpiScale,
    int WindowXPx,
    int WindowYPx,
    int WindowWidthPx,
    int WindowHeightPx,
    double WindowWidthDip,
    double WindowHeightDip,
    string OsDescription,
    string Runtime,
    int ProcessorCount);

/// <summary>How the launch reached a measurable state before its first workload.</summary>
internal sealed record FrameBenchStartup(
    string Head,
    string ConfigPath,
    double OpenToFullLoadMs,
    double OpenToGraphWarmedMs,
    double? OpenToFirstStatusMs,
    double SettleWaitMs,
    bool Settled,
    int RowsLoadedAtStart,
    string? DiffCommit,
    string? DiffPath,
    string? TerminalCommand);

/// <summary>
/// Whether the run counts. Valid needs enough frames (at least half the display's refresh over
/// the recorded time), the workload's range covered, background work settled before recording,
/// and nothing thrown. Refused git requests are informational: the guard answered them without
/// touching the repository.
/// </summary>
internal sealed record FrameBenchValidity(
    bool Valid,
    IReadOnlyList<string> Reasons,
    int Frames,
    int MinimumFrames,
    bool Covered,
    bool Settled,
    double SettleWaitMs,
    bool Overflowed,
    string? Error,
    int RefusedGitRequests,
    IReadOnlyList<string> RefusedGitCommands,
    IReadOnlyDictionary<string, int> GitSubcommands);
