namespace Historia.Models;

/// <summary>
/// Result of a single Git CLI invocation (§6.5.3). Raw stderr is carried but never surfaced
/// unmodified; <see cref="ErrorKind"/> is what the UI maps to an actionable message.
/// </summary>
public sealed record GitCliResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    GitErrorKind ErrorKind,
    TimeSpan Duration)
{
    public bool IsSuccess => ExitCode == 0 && ErrorKind == GitErrorKind.None;
}

/// <summary>
/// One Git CLI invocation request. Arguments are passed through
/// <c>ProcessStartInfo.ArgumentList</c> — never a concatenated string (§6.5.1).
/// </summary>
public sealed record GitCliRequest
{
    /// <summary>Arguments after the deterministic <c>-c</c> prefix. Each element is one argv slot.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Working directory (repo working dir, or parent dir for <c>clone</c>).</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Active profile whose SSH key / identity scopes this invocation's environment (§6.5.2).</summary>
    public GitProfile? Profile { get; init; }

    /// <summary>Extra per-invocation environment entries. Applied on top of the deterministic base.</summary>
    public IReadOnlyDictionary<string, string>? ExtraEnvironment { get; init; }

    /// <summary>Local-operation wall-clock timeout. Network operations pass <see cref="Timeout.InfiniteTimeSpan"/> (§6.5.1).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>When true, stdout/stderr line callbacks fire as output streams in (§6.5.1).</summary>
    public bool StreamOutput { get; init; }

    /// <summary>When set, written to the process's stdin and then closed (e.g. a patch for <c>git apply -</c>).</summary>
    public string? StandardInput { get; init; }

    /// <summary>v10 graph memory footprint plan, Phase 4: run the process at
    /// <see cref="System.Diagnostics.ProcessPriorityClass.BelowNormal"/> — for background,
    /// self-contained git operations (a commit-graph write) that should never contend with a
    /// user-driven git invocation for CPU. Never killed once started regardless of this flag
    /// (see the plan's documentation check on git's lockfile cleanup and
    /// <see cref="System.Diagnostics.Process.Kill()"/>/<c>TerminateProcess</c>).</summary>
    public bool LowerPriority { get; init; }
}

/// <summary>Phase-aware progress for a streamed network operation (§6.6.2).</summary>
public sealed record GitProgress
{
    public required string Phase { get; init; }
    public double? Percent { get; init; }
    public long? ObjectsDone { get; init; }
    public long? ObjectsTotal { get; init; }
    public double? BytesPerSecond { get; init; }

    /// <summary>Raw progress line, for a details/log view.</summary>
    public string? RawLine { get; init; }
}

/// <summary>Discovered <c>git.exe</c> location and version (§6.2.3).</summary>
public sealed record GitExecutableInfo
{
    public required bool Found { get; init; }
    public string? Path { get; init; }
    public Version? Version { get; init; }

    /// <summary>Minimum supported version is 2.35 (§6.2.3).</summary>
    public bool MeetsMinimumVersion => Version is not null && Version >= new Version(2, 35);

    public static GitExecutableInfo NotFound { get; } = new() { Found = false };
}
