namespace Historia.Services.Git;

/// <summary>
/// One per open repository (<see cref="RepositoryContext.RefSnapshots"/>): hands out the current
/// <see cref="RefSnapshot"/>, re-reading refs with one <c>git for-each-ref</c> only when
/// <see cref="RefStateToken"/> says a ref file changed. Never touches the repo gate — the scan is
/// a separate process — so the graph's walk steps and the tag list stop queueing status and diff
/// work behind a multi-second LibGit2Sharp ref enumeration.
/// <para>One scan at a time: callers that miss on the same token share one scan, and a caller
/// whose token differs from the scan in flight waits for it to finish before looking again, so a
/// burst of ref writes (a fetch) never stacks up overlapping scans of every ref. The scan runs on
/// the repository's lifetime token, not a caller's, and each caller waits with its own token — one
/// caller giving up never cancels the scan the others are waiting on.</para>
/// <para>A ref that points at a missing object (a half-deleted branch, a corrupt
/// <c>packed-refs</c>) is left out, as LibGit2Sharp-based reads always did — the full format
/// makes git stop at the first one, so the scan then reads refs without their objects and asks
/// <c>cat-file</c> about each object instead. Any other failure is a
/// <see cref="HistoriaGitException"/> (Rule 11).</para>
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
    // the current one — an older scan finishing late never steps the cache back.
    private long _nextSeq;
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
    /// miss runs one <c>for-each-ref</c> (≈0.75 s at 39k refs). Throws only
    /// <see cref="HistoriaGitException"/> (the refs can't be read — never cached) or
    /// <see cref="OperationCanceledException"/>.</summary>
    public async Task<RefSnapshot> GetAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var state = RefStateToken.Compute(_gitDir);
            Task<RefSnapshot> scan;
            var join = true;
            lock (_sync)
            {
                if (_current is { } cur && cur.Token == state.Token)
                    return cur;

                if (_inflight is { } f)
                {
                    scan = f.Scan;
                    join = f.Token == state.Token;
                }
                else
                {
                    var seq = ++_nextSeq;
                    scan = ScanAsync(state, seq);
                    _inflight = (state.Token, seq, scan);
                }
            }

            if (join)
                return await scan.WaitAsync(ct).ConfigureAwait(false);

            // A scan of older refs is running: let it finish rather than start a second one over
            // every ref, then look again — its result may already be what is on disk now.
            try { await scan.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* its failure is its own callers'; this caller rescans */ }
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
            var result = await RunAsync(ForEachRefParser.Arguments).ConfigureAwait(false);

            var refs = result.ExitCode == 0
                ? Parse(() => ForEachRefParser.Parse(result.StandardOutput))
                : await ScanPastMissingObjectsAsync(result).ConfigureAwait(false);
            await PeelNestedTagsAsync(refs).ConfigureAwait(false);
            var (headSymref, headTarget) = state.IsReftable
                ? await ResolveHeadWithCliAsync().ConfigureAwait(false)
                : ResolveHeadFromFile(state, refs);

            var snapshot = new RefSnapshot(refs, state.Token, headSymref, headTarget);
            Diagnostics.PerfLog.Mark($"ref snapshot: scanned {refs.Count} refs in {sw.Elapsed.TotalMilliseconds:F0}ms");

            lock (_sync)
            {
                ClearInflight(seq);
                if (seq > _currentSeq)
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

    private Task<GitCliResult> RunAsync(IReadOnlyList<string> arguments, string? stdin = null) =>
        _cli.RunAsync(new GitCliRequest
        {
            Arguments = arguments,
            WorkingDirectory = _workingDirectory,
            StandardInput = stdin,
            Timeout = ScanTimeout,
            RawStandardOutput = true,
        }, _lifetime);

    /// <summary>A failed read of refs: cancelled when the repo is closing, otherwise the
    /// classified git failure.</summary>
    private HistoriaGitException Failure(GitCliResult result, string operation)
    {
        _lifetime.ThrowIfCancellationRequested();
        return HistoriaGitException.FromCli(result, operation);
    }

    /// <summary>Output git produced but this parser can't read is a failure to read refs, never
    /// a partial list.</summary>
    private static T Parse<T>(Func<T> parse)
    {
        try { return parse(); }
        catch (FormatException ex)
        {
            throw new HistoriaGitException(GitErrorKind.Unknown, "Reading refs failed (unreadable output).", ex.Message);
        }
    }

    /// <summary>
    /// The full scan failed. If that is because refs point at missing objects — git stops at the
    /// first one (<c>fatal: missing object … for refs/…</c>) — rebuild the same entries without
    /// them: list refs without reading objects, type every object with one
    /// <c>cat-file --batch-check</c>, and read tag data with one <c>cat-file --batch</c>. When no
    /// object is missing, the original failure is unexplained and is thrown as is (Rule 11).
    /// </summary>
    private async Task<List<RefEntry>> ScanPastMissingObjectsAsync(GitCliResult failed)
    {
        if (failed.ErrorKind is GitErrorKind.Cancelled or GitErrorKind.TimedOut)
            throw Failure(failed, "Reading refs");

        var listed = await RunAsync(ForEachRefParser.ListArguments).ConfigureAwait(false);
        if (listed.ExitCode != 0)
            throw Failure(failed, "Reading refs");
        var list = Parse(() => ForEachRefParser.ParseList(listed.StandardOutput));

        var objects = list.Select(r => r.ObjectName).Distinct(StringComparer.Ordinal).ToList();
        var types = await BatchCheckAsync(objects).ConfigureAwait(false);
        var typeOf = new Dictionary<string, (ObjectId? Id, string? Type)>(StringComparer.Ordinal);
        for (var i = 0; i < objects.Count; i++)
            typeOf[objects[i]] = types[i];

        var dropped = list.Where(r => typeOf[r.ObjectName].Type is null).Select(r => r.Name).ToList();
        if (dropped.Count == 0)
            throw Failure(failed, "Reading refs");

        // Annotated tags: peel all the way (as %(*objectname) does) and read tagger + message.
        var tagObjects = objects.Where(o => typeOf[o].Type == "tag").ToList();
        var peeled = await BatchCheckAsync([.. tagObjects.Select(o => o + "^{}")]).ConfigureAwait(false);
        var peelOf = new Dictionary<string, (ObjectId? Id, string? Type)>(StringComparer.Ordinal);
        for (var i = 0; i < tagObjects.Count; i++)
            peelOf[tagObjects[i]] = peeled[i];
        var tagData = await ReadTagObjectsAsync(tagObjects).ConfigureAwait(false);

        var refs = new List<RefEntry>(list.Count - dropped.Count);
        foreach (var (name, objectName, symref) in list)
        {
            var (id, type) = typeOf[objectName];
            if (id is null || type is null)
                continue;
            var annotated = type == "tag";
            var peel = annotated ? peelOf[objectName] : default;
            var tag = annotated && tagData.TryGetValue(id, out var t) ? t : default;
            refs.Add(new RefEntry(
                CanonicalName: name,
                Target: id,
                ObjectType: type,
                Peeled: peel.Id,
                PeeledType: peel.Type,
                SymrefTarget: symref,
                TaggerName: annotated ? tag.TaggerName : null,
                TaggerWhen: annotated ? tag.TaggerWhen : null,
                Message: annotated ? tag.Message ?? "" : null));
        }
        refs.Sort((a, b) => StringComparer.Ordinal.Compare(a.CanonicalName, b.CanonicalName));

        var names = string.Join(", ", dropped.Take(5)) + (dropped.Count > 5 ? $" and {dropped.Count - 5} more" : "");
        AppLog.Write(AppLogLevel.Warn, "git", $"Ignoring {dropped.Count} ref(s) whose objects are missing in {_workingDirectory}: {names}");
        Diagnostics.PerfLog.Mark($"ref snapshot: degraded scan, dropped {dropped.Count} refs with missing objects");
        return refs;
    }

    /// <summary>One <c>cat-file --batch-check</c> over <paramref name="inputs"/>; answers in
    /// input order, a missing object as a null id and type.</summary>
    private async Task<List<(ObjectId? Id, string? Type)>> BatchCheckAsync(IReadOnlyList<string> inputs)
    {
        if (inputs.Count == 0)
            return [];
        var result = await RunAsync(
            ["cat-file", "--batch-check=" + CatFileParser.BatchCheckFormat],
            string.Join("\n", inputs) + "\n").ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw Failure(result, "Reading refs");
        return Parse(() => CatFileParser.ParseBatchCheck(result.StandardOutput, inputs.Count));
    }

    private async Task<Dictionary<ObjectId, CatFileParser.TagData>> ReadTagObjectsAsync(IReadOnlyList<string> tagObjects)
    {
        if (tagObjects.Count == 0)
            return [];
        var result = await RunAsync(["cat-file", "--batch"], string.Join("\n", tagObjects) + "\n").ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw Failure(result, "Reading tags");
        return Parse(() => CatFileParser.ParseTagObjects(result.StandardOutputBytes ?? []));
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

        var peeled = await BatchCheckAsync([.. nested.Select(i => refs[i].Target!.Sha + "^{}")]).ConfigureAwait(false);
        for (var k = 0; k < nested.Count; k++)
        {
            var i = nested[k];
            refs[i] = refs[i] with { Peeled = peeled[k].Id, PeeledType = peeled[k].Type };
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
