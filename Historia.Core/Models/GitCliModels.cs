namespace Historia.Models;

/// <summary>
/// Result of a single Git CLI invocation. Raw stderr is carried but never surfaced
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

    /// <summary>Stdout exactly as git wrote it — set only for a
    /// <see cref="GitCliRequest.RawStandardOutput"/> request, null otherwise.</summary>
    public byte[]? StandardOutputBytes { get; init; }

    /// <summary>True when stdout exceeded <see cref="GitCliRequest.MaxStandardOutputBytes"/>,
    /// stopping the read and terminating the process tree early.</summary>
    public bool OutputTruncated { get; init; }
}

/// <summary>
/// One Git CLI invocation request. Arguments are passed through
/// <c>ProcessStartInfo.ArgumentList</c> — never a concatenated string.
/// </summary>
public sealed record GitCliRequest
{
    /// <summary>Arguments after the deterministic <c>-c</c> prefix. Each element is one argv slot.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Working directory (repo working dir, or parent dir for <c>clone</c>).</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Active profile whose SSH key / identity scopes this invocation's environment.</summary>
    public GitProfile? Profile { get; init; }

    /// <summary>Extra per-invocation environment entries. Applied on top of the deterministic base.</summary>
    public IReadOnlyDictionary<string, string>? ExtraEnvironment { get; init; }

    /// <summary>Local-operation wall-clock timeout. Network operations pass <see cref="Timeout.InfiniteTimeSpan"/>.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>When true, stdout/stderr line callbacks fire as output streams in.</summary>
    public bool StreamOutput { get; init; }

    /// <summary>Capture stdout as bytes, with no line splitting — for machine-parsed output whose
    /// line breaks, lone CRs or NULs must survive exactly (<see cref="GitCliResult.StandardOutputBytes"/>;
    /// <see cref="GitCliResult.StandardOutput"/> is those bytes decoded as UTF-8). Line mode
    /// splits on CR as well as LF and rejoins lines with CRLF. Can't be combined with
    /// <see cref="StreamOutput"/> or a stdout line callback.</summary>
    public bool RawStandardOutput { get; init; }

    /// <summary>Maximum bytes of stdout to buffer before stopping the read, killing the process
    /// tree, and flagging <see cref="GitCliResult.OutputTruncated"/>. Null means unlimited.</summary>
    public long? MaxStandardOutputBytes { get; init; }

    /// <summary>When set, written to the process's stdin and then closed (e.g. a patch for <c>git apply -</c>).</summary>
    public string? StandardInput { get; init; }

    /// <summary>Run the process at
    /// <see cref="System.Diagnostics.ProcessPriorityClass.BelowNormal"/> — for background,
    /// self-contained git operations (a commit-graph write) that should never contend with a
    /// user-driven git invocation for CPU. Never killed once started regardless of this flag
    /// (git clears its lock files only from its own exit and signal handlers, which
    /// <see cref="System.Diagnostics.Process.Kill()"/>/<c>TerminateProcess</c> bypass).</summary>
    public bool LowerPriority { get; init; }
}

/// <summary>Phase-aware progress for a streamed network operation.</summary>
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

/// <summary>Discovered <c>git.exe</c> location and version.</summary>
public sealed record GitExecutableInfo
{
    public required bool Found { get; init; }
    public string? Path { get; init; }
    public Version? Version { get; init; }
    public string? NotFoundReason { get; init; }

    /// <summary>
    /// Minimum supported version is 2.35, the release that introduced <c>stash push --staged</c>
    /// and <c>checkout --conflict=zdiff3</c>.
    /// </summary>
    public static readonly Version MinimumVersion = new(2, 35);
    public bool MeetsMinimumVersion => Version is not null && Version >= MinimumVersion;

    public static GitExecutableInfo NotFound { get; } = new() { Found = false };
}
