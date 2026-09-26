namespace Historia.Services.Git;

/// <summary>
/// One per open repository (<see cref="RepositoryContext.RefSnapshots"/>): hands out the current
/// <see cref="RefSnapshot"/>, re-reading refs with one <c>git for-each-ref</c> only when
/// <see cref="RefStateToken"/> says a ref file changed. Never touches the repo gate — the scan is
/// a separate process — so the graph's walk steps and the tag list stop queueing status and diff
/// work behind a multi-second LibGit2Sharp ref enumeration.
/// <para>Single-flight: callers that miss on the same token share one scan. The scan runs on the
/// repository's lifetime token, not a caller's, and each caller waits with its own token — one
/// caller giving up never cancels the scan the others are waiting on.</para>
/// </summary>
public sealed class RefSnapshotProvider
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(60);

    private readonly IGitCliService _cli;
    private readonly string _gitDir;
    private readonly string _workingDirectory;
    private readonly CancellationToken _lifetime;
    private readonly object _sync = new();

    private RefSnapshot? _current;
    private long _currentSeq;
    private (long Token, long Seq, Task<RefSnapshot> Scan)? _inflight;

    // Scans are numbered as they start. A finished scan becomes current only if it started after
    // the current one and after the last Invalidate — an older scan finishing late never steps
    // the cache back, and a scan that began before an Invalidate never repopulates it.
    private long _nextSeq;
    private long _minAcceptedSeq;
    private int _scanCount;

    public RefSnapshotProvider(IGitCliService cli, string gitDir, string workingDirectory, CancellationToken lifetime)
    {
        _cli = cli;
        _gitDir = gitDir;
        _workingDirectory = workingDirectory;
        _lifetime = lifetime;
    }

    /// <summary>Scans started since construction — a test seam for single-flight / cache-hit
    /// assertions.</summary>
    internal int ScanCountForTesting => Volatile.Read(ref _scanCount);

    /// <summary>The snapshot for the refs on disk right now. A token check costs milliseconds; a
    /// miss runs one <c>for-each-ref</c> (≈0.75 s at 39k refs). Throws
    /// <see cref="HistoriaGitException"/> when the scan fails — a failed scan is never cached.</summary>
    public Task<RefSnapshot> GetAsync(CancellationToken ct = default)
    {
        var state = RefStateToken.Compute(_gitDir);
        Task<RefSnapshot> scan;
        lock (_sync)
        {
            if (_current is { } cur && cur.Token == state.Token)
                return Task.FromResult(cur);

            if (_inflight is { } f && f.Token == state.Token)
            {
                scan = f.Scan;
            }
            else
            {
                var seq = ++_nextSeq;
                scan = ScanAsync(state, seq);
                _inflight = (state.Token, seq, scan);
            }
        }

        return scan.WaitAsync(ct);
    }

    /// <summary>Drops the cached snapshot so the next <see cref="GetAsync"/> rescans even if the
    /// token did not move — for a write Historia knows happened but a coarse filesystem clock
    /// might hide.</summary>
    public void Invalidate()
    {
        lock (_sync)
        {
            _current = null;
            _inflight = null;
            _minAcceptedSeq = _nextSeq + 1;
        }
    }

    private async Task<RefSnapshot> ScanAsync(RefStateToken.State state, long seq)
    {
        // GetAsync starts this inside its lock — yield first so none of the scan runs there.
        await Task.Yield();
        Interlocked.Increment(ref _scanCount);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await _cli.RunAsync(new GitCliRequest
            {
                Arguments = ForEachRefParser.Arguments,
                WorkingDirectory = _workingDirectory,
                Timeout = ScanTimeout,
            }, _lifetime).ConfigureAwait(false);

            if (result.ExitCode != 0)
                throw HistoriaGitException.FromCli(result, "Reading refs");

            var refs = ForEachRefParser.Parse(result.StandardOutput);
            await PeelNestedTagsAsync(refs).ConfigureAwait(false);
            var (headSymref, headTarget) = state.IsReftable
                ? await ResolveHeadWithCliAsync().ConfigureAwait(false)
                : ResolveHeadFromFile(state, refs);

            var snapshot = new RefSnapshot(refs, state.Token, headSymref, headTarget);
            Diagnostics.PerfLog.Mark($"ref snapshot: scanned {refs.Count} refs in {sw.Elapsed.TotalMilliseconds:F0}ms");

            lock (_sync)
            {
                ClearInflight(seq);
                if (seq >= _minAcceptedSeq && seq > _currentSeq)
                {
                    _current = snapshot;
                    _currentSeq = seq;
                }
            }
            return snapshot;
        }
        catch
        {
            lock (_sync)
                ClearInflight(seq);
            throw;
        }
    }

    /// <summary>
    /// git 2.51's <c>%(*objectname)</c> peels a tag of a tag all the way down (verified against
    /// loose and packed refs); a git that peels only one level leaves <c>PeeledType == "tag"</c>.
    /// Those few entries are peeled in one <c>cat-file --batch-check</c> call rather than one
    /// process per ref.
    /// </summary>
    private async Task PeelNestedTagsAsync(List<RefEntry> refs)
    {
        var nested = new List<int>();
        for (var i = 0; i < refs.Count; i++)
            if (refs[i].PeeledType == "tag" && refs[i].Target is not null)
                nested.Add(i);
        if (nested.Count == 0)
            return;

        var stdin = string.Join("\n", nested.Select(i => refs[i].Target!.Sha + "^{}")) + "\n";
        var result = await _cli.RunAsync(new GitCliRequest
        {
            Arguments = ["cat-file", "--batch-check=%(objectname) %(objecttype)"],
            WorkingDirectory = _workingDirectory,
            StandardInput = stdin,
            Timeout = ScanTimeout,
        }, _lifetime).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw HistoriaGitException.FromCli(result, "Peeling tags");

        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var k = 0; k < nested.Count && k < lines.Length; k++)
        {
            var parts = lines[k].Split(' ');
            var i = nested[k];
            refs[i] = parts is [var sha, var type] && sha.Length is 40 or 64
                ? refs[i] with { Peeled = new ObjectId(sha), PeeledType = type }
                : refs[i] with { Peeled = null, PeeledType = null }; // "<input> missing"
        }
    }

    /// <summary>Caller holds <see cref="_sync"/>.</summary>
    private void ClearInflight(long seq)
    {
        if (_inflight is { } f && f.Seq == seq)
            _inflight = null;
    }

    /// <summary>Files backend: <c>HEAD</c> is a plain file — a symbolic <c>ref:</c> line resolved
    /// against the snapshot, or a detached SHA.</summary>
    private static (string? Symref, ObjectId? Target) ResolveHeadFromFile(RefStateToken.State state, List<RefEntry> refs)
    {
        if (state.HeadSha is { } sha)
            return (null, new ObjectId(sha));
        if (state.HeadSymref is not { } symref)
            return (null, null);

        var entry = refs.Find(r => string.Equals(r.CanonicalName, symref, StringComparison.Ordinal));
        return (symref, entry?.Target);
    }

    /// <summary>Reftable backend: the <c>HEAD</c> file is a placeholder, so ask git.</summary>
    private async Task<(string? Symref, ObjectId? Target)> ResolveHeadWithCliAsync()
    {
        var symbolic = await _cli.RunAsync(new GitCliRequest
        {
            Arguments = ["symbolic-ref", "-q", "HEAD"],
            WorkingDirectory = _workingDirectory,
        }, _lifetime).ConfigureAwait(false);
        var target = await _cli.RunAsync(new GitCliRequest
        {
            Arguments = ["rev-parse", "-q", "--verify", "HEAD"],
            WorkingDirectory = _workingDirectory,
        }, _lifetime).ConfigureAwait(false);

        var symref = symbolic.ExitCode == 0 ? NullIfEmpty(symbolic.StandardOutput.Trim()) : null;
        var sha = target.ExitCode == 0 ? target.StandardOutput.Trim() : "";
        return (symref, sha.Length is 40 or 64 ? new ObjectId(sha) : null);
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
