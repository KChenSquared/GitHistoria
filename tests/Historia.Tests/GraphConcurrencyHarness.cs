namespace Historia.Tests;

/// <summary>
/// The invariants of the graph harness, kept apart from the actors so a test can prove the checker
/// itself is able to fail. References are the git-ordered histories the repo has had; a painted
/// layout is valid when its rows are a prefix of one of them. A stub row (a zero id: the store
/// could not decode it for one frame) stands for "unknown" and matches anything.
/// </summary>
internal sealed class GraphInvariantChecker
{
    private readonly object _sync = new();
    private readonly List<string[]> _references = [];
    private readonly HashSet<string> _allIds = new(StringComparer.Ordinal);
    private readonly List<(string Where, string[] Ids)> _pending = [];
    private readonly List<string> _violations = [];

    public static readonly string StubId = GitObjectId.Zero.ToString();

    public IReadOnlyList<string> Violations
    {
        get { lock (_sync) return [.. _violations]; }
    }

    public int LayoutsChecked { get; private set; }

    public void AddReference(string[] ids)
    {
        lock (_sync)
        {
            _references.Add(ids);
            foreach (var id in ids)
                _allIds.Add(id);
        }
    }

    public void Violation(string invariant, string detail)
    {
        lock (_sync)
            if (_violations.Count < 20)
                _violations.Add($"{invariant}: {detail}");
    }

    private bool MatchesAReference(string[] ids)
    {
        lock (_sync)
            foreach (var reference in _references)
            {
                if (ids.Length > reference.Length)
                    continue;
                var ok = true;
                for (var i = 0; i < ids.Length && ok; i++)
                    ok = ids[i] == StubId || ids[i] == reference[i];
                if (ok)
                    return true;
            }
        return false;
    }

    /// <summary>I1: the painted rows are a prefix of a recorded reference, with no row twice. A layout
    /// that matches nothing yet may have been built from a ref state that has not been recorded
    /// (the change is applied, then recorded); it is held and judged by <see cref="Settle"/>.</summary>
    public void CheckLayout(string where, string[] ids)
    {
        lock (_sync)
            LayoutsChecked++;
        if (ids.Length == 0)
            return;
        var real = ids.Where(id => id != StubId).ToArray();
        if (real.Length != real.Distinct(StringComparer.Ordinal).Count())
        {
            var dup = real.GroupBy(x => x).First(g => g.Count() > 1).Key;
            Violation("I1", $"{where}: a painted layout of {ids.Length} rows holds {dup[..8]} twice");
            return;
        }
        if (!MatchesAReference(ids))
            lock (_sync)
                _pending.Add((where, ids));
    }

    /// <summary>Judges held layouts. While a ref change is in flight they stay held; at quiescence
    /// every one must match a recorded reference.</summary>
    public void Settle(bool refChangesInFlight)
    {
        List<(string Where, string[] Ids)> held;
        lock (_sync)
        {
            held = [.. _pending];
            _pending.Clear();
        }
        foreach (var (where, ids) in held)
        {
            if (MatchesAReference(ids))
                continue;
            if (refChangesInFlight)
                lock (_sync)
                    _pending.Add((where, ids));
            else
                Violation("I1", $"{where}: a painted layout of {ids.Length} rows is not a prefix of any recorded history ({Divergence(ids)})");
        }
    }

    /// <summary>Where the layout leaves its closest recorded history, for the failure message.</summary>
    private string Divergence(string[] ids)
    {
        lock (_sync)
        {
            var bestLen = -1;
            var bestRef = -1;
            for (var r = 0; r < _references.Count; r++)
            {
                var reference = _references[r];
                var n = 0;
                while (n < ids.Length && n < reference.Length && (ids[n] == StubId || ids[n] == reference[n]))
                    n++;
                if (n > bestLen)
                {
                    bestLen = n;
                    bestRef = r;
                }
            }
            var got = bestLen < ids.Length ? ids[bestLen][..8] : "-";
            var want = bestRef >= 0 && bestLen < _references[bestRef].Length ? _references[bestRef][bestLen][..8] : "-";
            var stubs = ids.Count(id => id == StubId);
            var inAnyOrder = ids.Where(id => id != StubId).All(_allIds.Contains);
            return $"first divergence at row {bestLen}: got {got}, expected {want} in history #{bestRef} of {_references.Count}; {stubs} stub rows; every row is a known commit: {inAnyOrder}";
        }
    }

    /// <summary>I4: a row read from a store is the stub or a commit some recorded history contains.</summary>
    public bool IsKnownRow(string id)
    {
        lock (_sync)
            return id == StubId || _allIds.Contains(id);
    }

    /// <summary>I3: an exhausted store holds exactly one recorded history.</summary>
    public bool IsWholeReference(string[] ids)
    {
        lock (_sync)
            return _references.Any(r => r.SequenceEqual(ids, StringComparer.Ordinal));
    }
}

/// <summary>
/// A seeded, randomized run of every concurrent actor of the graph cache and store against one
/// repository — the foreground load and its background spill, the headless job, ref changes, tab
/// switches, readers and a foreign writer — checking <see cref="GraphInvariantChecker"/>'s
/// invariants throughout. Every choice (actor, delay, cancel point, ref change) comes from a
/// <see cref="Random"/> derived from the seed, so a seed reproduces the schedule of choices;
/// thread timing still varies, which is what the soak run is for.
/// </summary>
internal sealed class GraphConcurrencyHarness : IAsyncDisposable
{
    private static readonly GraphQuery Q = new() { RefScope = GraphRefScope.AllRefs };
    private const long ForeignKeyBase = 0x0F0F_0000_0000_0000;
    private static readonly TimeSpan LivenessBound = TimeSpan.FromSeconds(30);

    private readonly int _seed;
    private readonly TimeSpan _activity;
    private readonly ITestOutputHelper? _output;
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo = TestRepositoryBuilder.FromTemplate("concurrency-harness-v1", BuildFixture);
    private readonly GraphInvariantChecker _checker = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConcurrentQueue<string> _log = new();
    private readonly ConcurrentQueue<Exception> _unobserved = new();
    private readonly object _gitLock = new();
    private readonly object _currentLock = new();
    private readonly List<RawCommit[]> _referenceRows = [];   // guarded by _gitLock
    private readonly List<string> _madeBranches = [];         // guarded by _gitLock
    private GraphLayoutService _svc = null!;
    private RepositoryContext _ctx = null!;
    private RepositoryContext _readerCtx = null!;
    private GraphLayoutResult? _current;
    private volatile bool _stopping;
    private int _refChangesInFlight;
    private int _branchCounter;
    private int _commitCounter;
    private readonly int[] _actionCounts = new int[6];

    private GraphConcurrencyHarness(int seed, TimeSpan activity, ITestOutputHelper? output)
    {
        _seed = seed;
        _activity = activity;
        _output = output;
    }

    public static async Task<GraphConcurrencyHarness> CreateAsync(int seed, TimeSpan activity, ITestOutputHelper? output)
    {
        var h = new GraphConcurrencyHarness(seed, activity, output);
        await h._host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        h._svc = new GraphLayoutService(h._host.Settings, h._host.Locator, h._host.Provider.GetRequiredService<IGitCliService>());
        h._ctx = h._host.Open(h._repo.Path);
        h._ctx.MarkFirstGraphPaintDone();
        h._ctx.MarkFirstFullLoadDone();
        h._readerCtx = h._host.Open(h._repo.Path);
        lock (h._gitLock)
            h.RecordReference();
        return h;
    }

    public async ValueTask DisposeAsync()
    {
        try { await _svc.Hibernate(_ctx); } catch { /* best effort */ }
        await _ctx.DisposeAsync();
        await _readerCtx.DisposeAsync();
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    // ── fixture and oracle ──

    /// <summary>A 300-commit trunk with three side branches (40, 30 and 20 commits) whose commit
    /// times interleave with the trunk's, and an annotated tag on the trunk.</summary>
    private static void BuildFixture(TestRepositoryBuilder b)
    {
        b.LinearHistory(300);
        var trunk = b.Git("rev-list", "--first-parent", "main").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        void Side(string branch, int attachBack, int count)
        {
            var attachIndex = trunk.Length - 1 - attachBack;
            var sb = new StringBuilder();
            for (var j = 0; j < count; j++)
            {
                var epoch = baseTime.AddMinutes(attachIndex + 1 + 2 * j).ToUnixTimeSeconds();
                var msg = $"{branch} commit {j}\n";
                var content = $"{branch} {j}\n";
                sb.Append($"commit refs/heads/{branch}\n");
                sb.Append($"author Test User <test@example.com> {epoch} +0000\n");
                sb.Append($"committer Test User <test@example.com> {epoch} +0000\n");
                sb.Append($"data {Encoding.UTF8.GetByteCount(msg)}\n{msg}");
                if (j == 0)
                    sb.Append($"from {trunk[attachBack]}\n");
                sb.Append($"M 100644 inline side-{branch}.txt\ndata {Encoding.UTF8.GetByteCount(content)}\n{content}\n");
            }
            b.FastImport(sb.ToString());
        }

        Side("b1", 150, 40);
        Side("b2", 80, 30);
        Side("b3", 20, 20);
        b.Git("tag", "-a", "v1", "-m", "v1", trunk[50]);
    }

    private static string RunGit(string repoPath, string stdin, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        p.WaitForExit(30_000);
        Task.WaitAll(stdout, stderr);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {stderr.Result}");
        return stdout.Result;
    }

    /// <summary>Records the history the repo has now, resolved as the service resolves it: every
    /// branch, remote and tag tip (an annotated tag through its peeled commit), sorted ordinally,
    /// walked by <c>git log --date-order</c>. Caller holds <see cref="_gitLock"/>.</summary>
    private void RecordReference()
    {
        var refs = RunGit(_repo.Path, "", "for-each-ref", "--format=%(objectname) %(*objectname)",
            "refs/heads", "refs/remotes", "refs/tags");
        var tips = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in refs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            tips.Add(parts.Length > 1 ? parts[1] : parts[0]);
        }
        var log = RunGit(_repo.Path, string.Join('\n', tips) + "\n",
            "log", "--date-order", "--no-mailmap", "--no-show-signature", "--stdin",
            "--format=%H%x1f%P%x1f%aI%x1f%cI%x1f%an%x1f%ae%x1f%s");
        var rows = new List<RawCommit>();
        foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('\x1f');
            var id = new GitObjectId(f[0]);
            var parents = f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => new GitObjectId(p)).ToArray();
            rows.Add(new RawCommit(id, parents, new CommitMetadata
            {
                Id = id,
                MessageShort = f.Length > 6 ? f[6] : "",
                AuthorName = f[4],
                AuthorEmail = f[5],
                AuthorWhen = DateTimeOffset.Parse(f[2]),
                CommitWhen = DateTimeOffset.Parse(f[3]),
                ParentCount = parents.Length,
            }));
        }
        _referenceRows.Add([.. rows]);
        _checker.AddReference([.. rows.Select(r => r.Id.ToString())]);
    }

    // ── actors ──

    private bool Stopping => _stopping;

    private void Log(string actor, string action)
    {
        _log.Enqueue($"{_clock.ElapsedMilliseconds,6} ms  {actor,-10} {action}");
        while (_log.Count > 400)
            _log.TryDequeue(out _);
    }

    private static Task Pause(Random rng) => Task.Delay(rng.Next(0, 31));

    private void Publish(GraphLayoutResult next, GraphLayoutResult? replacing, bool force = false)
    {
        lock (_currentLock)
            if (force || ReferenceEquals(_current, replacing) || _current is null)
                _current = next;
    }

    private GraphLayoutResult? Current
    {
        get { lock (_currentLock) return _current; }
    }

    /// <summary>Reads a layout's ids under a lease of its store, as the painted graph does. Null when
    /// the store has already been closed by a tab switch: nothing left to verify.</summary>
    private static string[]? ReadIds(GraphLayoutResult r)
    {
        var lease = r.Layout.Commits.AddRef();
        if (lease is null)
            return null;
        try
        {
            // A frame the store could not cover keeps its earlier (shorter) store for one frame;
            // rows past its end read as the stub, as they do on screen.
            var ids = new string[r.Layout.RowCount];
            var covered = lease.Count;
            for (var i = 0; i < ids.Length; i++)
                ids[i] = i < covered ? lease[i].Id.ToString() : GraphInvariantChecker.StubId;
            return ids;
        }
        finally
        {
            lease.Dispose();
        }
    }

    private void Verify(string where, GraphLayoutResult r)
    {
        if (ReadIds(r) is { } ids)
            _checker.CheckLayout(where, ids);
    }

    private static string Describe(Exception ex)
    {
        var frames = (ex.StackTrace ?? "").Split('\n').Take(6).Select(f => f.Trim());
        return $"{ex.GetType().Name}: {ex.Message} at {string.Join(" <- ", frames)}";
    }

    /// <summary>An exception a racing tab switch or ref change legitimately causes in a call that
    /// was already running when its cache was torn down.</summary>
    private static bool IsTeardownRace(Exception ex) => ex is ObjectDisposedException or OperationCanceledException;

    private async Task ForegroundAsync(Random rng)
    {
        while (!Stopping)
        {
            var cur = Current;
            try
            {
                GraphLayoutResult next;
                if (cur is null || rng.Next(12) == 0)
                {
                    Log("foreground", "compute");
                    next = await _svc.ComputeLayoutAsync(_ctx, Q, 30 + rng.Next(60));
                }
                else
                {
                    Log("foreground", "extend");
                    next = await _svc.ExtendLayoutAsync(_ctx, cur, 20 + rng.Next(80));
                }
                Publish(next, cur);
                Verify("foreground", next);
                CheckWalkPosition();
            }
            catch (Exception ex) when (IsTeardownRace(ex)) { Log("foreground", $"race: {ex.GetType().Name}"); }
            catch (Exception ex) { _checker.Violation("I4", $"foreground call threw {Describe(ex)}"); }
            Interlocked.Increment(ref _actionCounts[0]);
            await Pause(rng);
        }
    }

    private async Task HeadlessAsync(Random rng)
    {
        while (!Stopping)
        {
            try
            {
                switch (rng.Next(3))
                {
                    case 0:
                        Log("headless", "run");
                        using (var cts = new CancellationTokenSource())
                        {
                            if (rng.Next(3) != 0)
                                cts.CancelAfter(rng.Next(0, 300));
                            try { await _svc.RunHeadlessCacheAsync(_ctx, Q, cts.Token); }
                            catch (OperationCanceledException) { }
                        }
                        break;
                    case 1:
                        Log("headless", "enqueue");
                        _svc.EnqueueInactiveTabCaching(_ctx, Q);
                        break;
                    default:
                        Log("headless", "cancel queued");
                        await _svc.CancelInactiveTabCachingAsync(_ctx);
                        break;
                }
            }
            catch (Exception ex) { Log("headless", $"ended with {ex.GetType().Name}"); }
            Interlocked.Increment(ref _actionCounts[1]);
            await Pause(rng);
        }
    }

    private async Task RefChangesAsync(Random rng)
    {
        while (!Stopping)
        {
            try
            {
                Interlocked.Increment(ref _refChangesInFlight);
                try
                {
                    lock (_gitLock)
                    {
                        MutateRefs(rng);
                        RecordReference();
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _refChangesInFlight);
                }
                _checker.Settle(refChangesInFlight: _refChangesInFlight > 0);

                if (Current is { } previous)
                {
                    Log("refs", "recompute");
                    var next = await _svc.RecomputeAsync(_ctx, Q, previous, InvalidationReason.RefsChanged, 30 + rng.Next(60));
                    Publish(next, previous, force: true);
                    Verify("recompute", next);
                }
            }
            catch (Exception ex) when (IsTeardownRace(ex)) { Log("refs", $"race: {ex.GetType().Name}"); }
            catch (Exception ex) { _checker.Violation("I4", $"ref change or recompute threw {Describe(ex)}"); }
            Interlocked.Increment(ref _actionCounts[2]);
            await Task.Delay(rng.Next(20, 120));
        }
    }

    /// <summary>One change to the repo's refs, never touching the checked-out branch or its work tree:
    /// a commit on a side branch, a new branch, or the deletion of a branch this run made. Caller
    /// holds <see cref="_gitLock"/>.</summary>
    private void MutateRefs(Random rng)
    {
        var sides = new List<string> { "b1", "b2", "b3" };
        sides.AddRange(_madeBranches);
        switch (rng.Next(3))
        {
            case 0:
            {
                var branch = sides[rng.Next(sides.Count)];
                var tip = _repo.Git("rev-parse", $"refs/heads/{branch}").Trim();
                var tree = _repo.Git("rev-parse", $"{tip}^{{tree}}").Trim();
                var when = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(++_commitCounter).ToString("O");
                var sha = _repo.Git(new Dictionary<string, string> { ["GIT_AUTHOR_DATE"] = when, ["GIT_COMMITTER_DATE"] = when },
                    "commit-tree", tree, "-p", tip, "-m", $"harness {_commitCounter}").Trim();
                _repo.Git("update-ref", $"refs/heads/{branch}", sha, tip);
                Log("refs", $"commit on {branch}");
                break;
            }
            case 1:
            {
                var name = $"h{++_branchCounter}";
                var back = rng.Next(0, 250);
                var sha = _repo.Git("rev-parse", $"main~{back}").Trim();
                _repo.Git("update-ref", $"refs/heads/{name}", sha);
                _madeBranches.Add(name);
                Log("refs", $"create {name} at main~{back}");
                break;
            }
            default:
            {
                if (_madeBranches.Count == 0)
                {
                    goto case 1;
                }
                var name = _madeBranches[rng.Next(_madeBranches.Count)];
                _repo.Git("update-ref", "-d", $"refs/heads/{name}");
                _madeBranches.Remove(name);
                Log("refs", $"delete {name}");
                break;
            }
        }
    }

    private async Task TabSwitchAsync(Random rng)
    {
        while (!Stopping)
        {
            try
            {
                Log("tabs", "hibernate");
                await _svc.Hibernate(_ctx);
                Log("tabs", "compute");
                var next = await _svc.ComputeLayoutAsync(_ctx, Q, 30 + rng.Next(60));
                Publish(next, null, force: true);
                Verify("tab switch", next);
            }
            catch (Exception ex) when (IsTeardownRace(ex)) { Log("tabs", $"race: {ex.GetType().Name}"); }
            catch (Exception ex) { _checker.Violation("I4", $"tab switch threw {Describe(ex)}"); }
            Interlocked.Increment(ref _actionCounts[3]);
            await Task.Delay(rng.Next(40, 200));
        }
    }

    private async Task ReadersAsync(Random rng)
    {
        while (!Stopping)
        {
            try
            {
                if (rng.Next(3) == 0)
                {
                    Log("readers", "optimistic snapshot");
                    if (_svc.TryOptimisticSnapshot(_readerCtx, Q, 100) is { } snap)
                        Verify("optimistic snapshot", snap);
                }
                else if (Current is { } layout && layout.Layout.Commits.AddRef() is { } lease)
                {
                    try
                    {
                        Log("readers", "store reads");
                        if (lease is DiskCommitStore store)
                        {
                            for (var i = 0; i < 12; i++)
                            {
                                var row = rng.Next(Math.Max(1, store.Count));
                                var ids = new[] { store[row].Id.ToString(), store.ReadRaw(row).Id.ToString() };
                                foreach (var id in ids.Concat(store.ReadRawRange(row, 5).Select(c => c.Id.ToString())))
                                    if (!_checker.IsKnownRow(id) && _refChangesInFlight == 0)
                                        _checker.Violation("I4", $"a store row {id[..8]} is in no recorded history");
                            }
                            foreach (var hit in store.Search(rng.Next(2) == 0 ? "commit" : "harness"))
                                if (hit < 0 || hit >= store.RecordCount)
                                    _checker.Violation("I4", $"a search hit {hit} is outside the store's {store.RecordCount} records");
                        }
                    }
                    finally
                    {
                        lease.Dispose();
                    }
                }
            }
            catch (Exception ex) { _checker.Violation("I4", $"a store read threw {Describe(ex)}"); }
            Interlocked.Increment(ref _actionCounts[4]);
            await Pause(rng);
        }
    }

    private async Task ForeignWriterAsync(Random rng)
    {
        while (!Stopping)
        {
            try
            {
                RawCommit[] rows;
                lock (_gitLock)
                    rows = _referenceRows[rng.Next(_referenceRows.Count)];
                var n = rng.Next(1, Math.Min(100, rows.Length));
                var key = ForeignKeyBase + rng.Next(3);
                Log("foreign", $"reset store to key {key & 0xF} with {n} rows");
                new GraphMetaWriter(_repo.Path, key, includeRemoteBranches: true)
                    .AppendSpillBatch(rows.AsSpan(0, n).ToArray(), 0, n, exhausted: false);
            }
            catch (Exception ex) { _checker.Violation("I4", $"the foreign writer threw {Describe(ex)}"); }
            Interlocked.Increment(ref _actionCounts[5]);
            await Task.Delay(rng.Next(30, 150));
        }
    }

    /// <summary>I2: what the foreground cache reports agrees with itself. The live walk's position
    /// never trails the rows held, and the rows held exceed it only by rows another writer already
    /// put on disk (an adoption); resident rows beyond the stream would be rows read twice. Skipped
    /// when a lock is busy.</summary>
    private void CheckWalkPosition()
    {
        if (_svc.GetCacheStateForTesting(_ctx) is not { HasLiveWalk: true } s)
            return;
        if (s.TotalWalked < s.LiveWalkPos)
            _checker.Violation("I2", $"the cache holds {s.TotalWalked} rows but the stream has produced {s.LiveWalkPos}");
        else if (s.TotalWalked > s.LiveWalkPos && s.SpilledRows <= s.LiveWalkPos)
            _checker.Violation("I2", $"{s.TotalWalked - s.LiveWalkPos} resident rows lie beyond the stream position {s.LiveWalkPos} (spilled {s.SpilledRows})");
    }

    // ── run ──

    /// <summary>The oracle's own check: one foreground actor, nothing else, walks the whole history
    /// and every layout must match the recorded history. A failure here is the oracle's, not the
    /// service's.</summary>
    public async Task RunCleanForegroundAsync()
    {
        var layout = await _svc.ComputeLayoutAsync(_ctx, Q, 50);
        Verify("clean compute", layout);
        for (var i = 0; i < 60 && layout.Layout.RowCount < _referenceRows[0].Length; i++)
        {
            layout = await _svc.ExtendLayoutAsync(_ctx, layout, 40);
            Verify("clean extend", layout);
        }
        if (layout.Layout.RowCount != _referenceRows[0].Length)
            _checker.Violation("I1", $"a clean walk painted {layout.Layout.RowCount} of {_referenceRows[0].Length} rows");
        _checker.Settle(refChangesInFlight: false);
        var violations = _checker.Violations;
        if (violations.Count > 0)
            throw new Xunit.Sdk.XunitException(string.Join(Environment.NewLine, violations));
    }

    public async Task RunAsync()
    {
        var master = new Random(_seed);
        var rngs = Enumerable.Range(0, 6).Select(_ => new Random(master.Next())).ToArray();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) { _unobserved.Enqueue(e.Exception); e.SetObserved(); }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        var previousBatch = GraphLayoutService.TestSpillBatchRowsOverride;
        GraphLayoutService.TestSpillBatchRowsOverride = 20;
        try
        {
            var actors = new[]
            {
                Task.Run(() => ForegroundAsync(rngs[0])),
                Task.Run(() => HeadlessAsync(rngs[1])),
                Task.Run(() => RefChangesAsync(rngs[2])),
                Task.Run(() => TabSwitchAsync(rngs[3])),
                Task.Run(() => ReadersAsync(rngs[4])),
                Task.Run(() => ForeignWriterAsync(rngs[5])),
            };
            await Task.Delay(_activity);
            _stopping = true;

            var all = Task.WhenAll(actors);
            if (await Task.WhenAny(all, Task.Delay(LivenessBound)) != all)
                _checker.Violation("I5", $"an actor did not finish within {LivenessBound.TotalSeconds:0} s of the stop signal");
            else if (all.IsFaulted)
                _checker.Violation("I5", $"an actor faulted: {all.Exception!.GetBaseException().Message}");

            await Quiesce();
        }
        finally
        {
            GraphLayoutService.TestSpillBatchRowsOverride = previousBatch;
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        foreach (var ex in _unobserved)
            _checker.Violation("I5", $"an unobserved task exception: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}");

        _output?.WriteLine(
            $"seed={_seed} activity={_activity.TotalMilliseconds:0} ms actions foreground/headless/refs/tabs/readers/foreign="
            + string.Join('/', _actionCounts) + $" layouts checked={_checker.LayoutsChecked} references={_referenceRows.Count}");

        var violations = _checker.Violations;
        if (violations.Count > 0)
            throw new Xunit.Sdk.XunitException(
                $"seed={_seed}\n{string.Join('\n', violations)}\n--- last 50 actions ---\n{string.Join('\n', _log.TakeLast(50))}");
    }

    /// <summary>Everything stops writing, then the store is read back once (I3) and held layouts
    /// are judged.</summary>
    private async Task Quiesce()
    {
        try { await _svc.CancelInactiveTabCachingAsync(_ctx).WaitAsync(LivenessBound); }
        catch (TimeoutException) { _checker.Violation("I5", "a queued headless job did not exit"); }
        await _svc.Hibernate(_ctx);
        _checker.Settle(refChangesInFlight: false);

        var header = GraphMetaStore.ProbeExisting(_repo.Path, includeRemoteBranches: true);
        if (!header.Ok || !header.Exhausted)
            return;
        if (header.OrderVersion == 0 && header.RowCount != header.RecordCount)
            _checker.Violation("I3", $"an identity-order store has {header.RowCount} rows but {header.RecordCount} records");
        using var store = DiskCommitStore.Open(_repo.Path, header.RowCount, header.RecordCount, header.OrderVersion);
        var ids = Enumerable.Range(0, store.Count).Select(i => store.ReadRaw(i).Id.ToString()).ToArray();
        if (!_checker.IsWholeReference(ids))
            _checker.Violation("I3", $"an exhausted store of {ids.Length} rows (order {header.OrderVersion}) is not any recorded history");
    }
}
