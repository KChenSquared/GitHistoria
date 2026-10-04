namespace Historia.Diagnostics;

/// <summary>
/// The <see cref="IGitCliService"/> a benchmark launch runs on. The benchmark repositories are
/// read-only: any request whose subcommand writes into the repository or reaches the network is
/// refused without spawning git and answered with a failed result, and the refusal is counted.
/// Everything else passes through to the real service unchanged. Every subcommand that runs is
/// tallied too, so a run's output names exactly what git was asked to do.
/// </summary>
internal sealed class FrameBenchGitGuard(IGitCliService inner) : IGitCliService
{
    private const int MaxRecordedRefusals = 32;

    // Subcommands that always write into the repository, its index or work tree, or talk to a
    // remote. A subcommand whose effect depends on its arguments is classified in RefusalReason.
    private static readonly HashSet<string> AlwaysRefused = new(StringComparer.Ordinal)
    {
        "fetch", "pull", "push", "clone", "ls-remote", "remote-ext", "remote-fd",
        "gc", "maintenance", "repack", "prune", "prune-packed", "pack-refs", "fsmonitor--daemon",
        "commit", "merge", "rebase", "cherry-pick", "revert", "am", "reset", "checkout", "switch",
        "restore", "add", "rm", "mv", "clean", "init", "update-ref", "update-index", "update-server-info",
        "write-tree", "commit-tree", "mktag", "mktree", "read-tree", "checkout-index", "filter-branch",
        "replace", "lfs", "credential", "bundle", "send-email", "request-pull", "submodule--helper",
        "index-pack", "unpack-objects", "merge-file", "merge-index", "sparse-checkout",
    };

    private readonly ConcurrentDictionary<string, int> _subcommands = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _refusals = new();
    private int _refused;

    public bool IsGitAvailable => inner.IsGitAvailable;

    /// <summary>Requests refused so far.</summary>
    public int RefusedCount => Volatile.Read(ref _refused);

    /// <summary>The first refused command lines, for the run's validity block.</summary>
    public IReadOnlyList<string> Refusals => [.. _refusals];

    /// <summary>How many times each subcommand was requested through this service, refused ones
    /// included.</summary>
    public IReadOnlyDictionary<string, int> Subcommands =>
        new SortedDictionary<string, int>(_subcommands, StringComparer.Ordinal);

    public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
        => RunAsync(request, progress: null, onStdoutLine: null, ct);

    public Task<GitCliResult> RunAsync(
        GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (subcommand, reason) = Classify(request.Arguments);
        _subcommands.AddOrUpdate(subcommand ?? "(none)", 1, (_, n) => n + 1);

        if (reason is null)
            return inner.RunAsync(request, progress, onStdoutLine, ct);

        if (Interlocked.Increment(ref _refused) <= MaxRecordedRefusals)
            _refusals.Enqueue("git " + string.Join(' ', request.Arguments));
        AppLog.Write(AppLogLevel.Info, "frame-bench", $"refused git {string.Join(' ', request.Arguments)} ({reason})");
        return Task.FromResult(new GitCliResult(
            128, string.Empty, $"fatal: refused by the frame benchmark's read-only guard ({reason})",
            GitErrorKind.Unknown, TimeSpan.Zero));
    }

    /// <summary>
    /// The subcommand of <paramref name="args"/> (after any leading global options) and, when the
    /// request would write into the repository or reach the network, why it is refused.
    /// </summary>
    internal static (string? Subcommand, string? Reason) Classify(IReadOnlyList<string> args)
    {
        var i = 0;
        while (i < args.Count && args[i].StartsWith('-'))
        {
            // Global options that take their value as the next argument.
            i += args[i] is "-c" or "-C" or "--git-dir" or "--work-tree" or "--namespace" or "--exec-path" ? 2 : 1;
        }
        if (i >= args.Count)
            return (null, null);

        var sub = args[i];
        var rest = new List<string>(args.Count - i - 1);
        for (var j = i + 1; j < args.Count; j++)
            rest.Add(args[j]);
        var verb = FirstPositional(rest);

        if (AlwaysRefused.Contains(sub))
            return (sub, "writes or reaches the network");

        var reason = sub switch
        {
            "commit-graph" when verb == "write" => "writes the commit-graph",
            "multi-pack-index" when verb is "write" or "repack" or "expire" => "writes the multi-pack index",
            "remote" when verb is "add" or "remove" or "rm" or "rename" or "set-url" or "set-head"
                or "set-branches" or "update" or "prune" => "changes or contacts a remote",
            "remote" when verb == "show" && !rest.Contains("-n") => "contacts a remote",
            "worktree" when verb is not (null or "list") => "changes worktrees",
            "submodule" when verb is not ("status" or "summary") => "changes submodules",
            "stash" when verb is not ("list" or "show") => "changes the stash",
            "notes" when verb is not (null or "list" or "show") => "changes notes",
            "reflog" when verb is "expire" or "delete" => "rewrites the reflog",
            "config" when !IsConfigRead(rest, verb) => "writes config",
            "symbolic-ref" when HasAny(rest, "-d", "--delete") || PositionalCount(rest) >= 2 => "writes a ref",
            "tag" when IsTagWrite(rest) => "creates or deletes a tag",
            "branch" when IsBranchWrite(rest) => "changes a branch",
            "apply" when !HasAny(rest, "--check", "--stat", "--numstat", "--summary") => "applies a patch",
            "hash-object" when rest.Contains("-w") => "writes an object",
            "merge-tree" when rest.Contains("--write-tree") => "writes objects",
            "format-patch" when !rest.Contains("--stdout") => "writes files into the work tree",
            "archive" when rest.Any(a => a.StartsWith("--remote", StringComparison.Ordinal)) => "contacts a remote",
            _ => null,
        };
        return (sub, reason);
    }

    private static string? FirstPositional(List<string> rest)
    {
        foreach (var a in rest)
        {
            if (a == "--")
                return null;
            if (!a.StartsWith('-'))
                return a;
        }
        return null;
    }

    private static int PositionalCount(List<string> rest)
    {
        var n = 0;
        foreach (var a in rest)
        {
            if (a == "--")
                break;
            if (!a.StartsWith('-'))
                n++;
        }
        return n;
    }

    private static bool HasAny(List<string> rest, params string[] flags)
    {
        foreach (var a in rest)
        {
            foreach (var f in flags)
            {
                if (a == f || a.StartsWith(f + "=", StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private static bool IsConfigRead(List<string> rest, string? verb)
        => verb is "get" or "list"
           || HasAny(rest, "--get", "--get-all", "--get-regexp", "--get-urlmatch", "--get-color",
               "--get-colorbool", "--list", "-l");

    // A tag command lists unless it is given a name to create or a create/delete flag.
    private static bool IsTagWrite(List<string> rest)
    {
        if (HasAny(rest, "-d", "--delete", "-a", "--annotate", "-s", "--sign", "-u", "--local-user",
                "-m", "--message", "-F", "--file", "-f", "--force"))
            return true;
        if (HasAny(rest, "-l", "--list", "-n", "--contains", "--no-contains", "--merged", "--no-merged", "--points-at"))
            return false;
        return PositionalCount(rest) > 0;
    }

    // A branch command lists unless it is given a name to create or a delete/move/copy/upstream flag.
    private static bool IsBranchWrite(List<string> rest)
    {
        if (HasAny(rest, "-d", "-D", "--delete", "-m", "-M", "--move", "-c", "-C", "--copy", "-f", "--force",
                "-u", "--set-upstream-to", "--unset-upstream", "--edit-description", "-t", "--track", "--no-track"))
            return true;
        if (HasAny(rest, "-l", "--list", "-a", "--all", "-r", "--remotes", "--contains", "--no-contains",
                "--merged", "--no-merged", "--points-at", "--show-current", "-v", "-vv", "--verbose", "--format"))
            return false;
        return PositionalCount(rest) > 0;
    }
}
