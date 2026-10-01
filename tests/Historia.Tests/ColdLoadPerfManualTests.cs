namespace Historia.Tests;

/// <summary>
/// Skips a test unless <c>HISTORIA_COLDLOAD_PERF</c> is set. These are wall-clock measurements:
/// they only mean something on an otherwise idle machine, run on their own.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ColdLoadPerfFactAttribute : FactAttribute
{
    public ColdLoadPerfFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HISTORIA_COLDLOAD_PERF")))
            Skip = "Manual-only: set HISTORIA_COLDLOAD_PERF=1 (and HISTORIA_PERF_REPOS for the repo tests) to run.";
    }
}

/// <summary>
/// Cold-load and tab-switch latency measurements. Each test times today's code next to a
/// prototype of its fix (in <see cref="Prototype"/>), so only a fix that measurably works ships. <c>HISTORIA_PERF_REPOS</c> is a <c>;</c>-separated list of real repositories (the session
/// that produced the slow log); every repo operation here is read-only. Results also go to
/// <c>HISTORIA_PERF_OUT</c> when set.
/// <code>
/// $env:HISTORIA_COLDLOAD_PERF=1; $env:HISTORIA_PERF_REPOS="C:\_Testing\chromium;D:\..."
/// dotnet test tests/Historia.Tests --filter "FullyQualifiedName~ColdLoadPerfManualTests"
/// </code>
/// </summary>
[Trait("tier", "perf")]
[Collection("ColdLoadPerf")]
public sealed class ColdLoadPerfManualTests(ITestOutputHelper output) : IDisposable
{
    private readonly StateDir _state = new();

    public void Dispose() => _state.Dispose();

    private static IReadOnlyList<string> Repos =>
        (Environment.GetEnvironmentVariable("HISTORIA_PERF_REPOS") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Directory.Exists)
            .ToList();

    private void Log(string line)
    {
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("HISTORIA_PERF_OUT") is { Length: > 0 } path)
        {
            lock (typeof(ColdLoadPerfManualTests))
                File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    private static double Median(List<double> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s.Count == 0 ? 0 : s[s.Count / 2];
    }

    // ── 1. PerfLog: one slow write stalls every thread that logs ──

    [ColdLoadPerfFact]
    public void PerfLog_mark_waits_behind_a_slow_write_and_a_queued_writer_does_not()
    {
        PerfLog.OverrideEnabledForTesting(true);
        Directory.CreateDirectory(AppPaths.LogsDirectory);
        try
        {
            // Today: Mark takes a process-wide lock and appends to the file inside it. Holding
            // that lock stands in for one File.AppendAllText that the disk or a virus scanner
            // holds up — every other thread's Mark (UI thread included) queues behind it.
            var gate = typeof(PerfLog).GetField("Gate", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            using var held = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                lock (gate)
                {
                    held.Set();
                    Thread.Sleep(1500);
                }
            });
            holder.Start();
            held.Wait();
            var sw = Stopwatch.StartNew();
            PerfLog.Mark("cold-load perf: mark behind a slow write");
            var currentMs = sw.Elapsed.TotalMilliseconds;
            holder.Join();

            // Prototype: Mark only enqueues; one dedicated thread writes. A sink stalled for the
            // same 1.5 s never reaches the caller.
            var written = new List<string>();
            using var sinkEntered = new ManualResetEventSlim();
            var first = true;
            var writer = new Prototype.QueuedLineWriter(batch =>
            {
                if (first)
                {
                    first = false;
                    sinkEntered.Set();
                    Thread.Sleep(1500);
                }
                lock (written) written.AddRange(batch);
            });
            writer.Enqueue("line 1");
            sinkEntered.Wait();
            sw.Restart();
            writer.Enqueue("line 2");
            var prototypeMs = sw.Elapsed.TotalMilliseconds;
            writer.Dispose(); // drains

            Log($"[perflog-stall] current Mark behind a 1.5 s write: {currentMs:F1} ms; queued writer: {prototypeMs:F3} ms");
            Assert.True(currentMs >= 1000, $"expected today's Mark to block (~1500 ms), got {currentMs:F1} ms");
            Assert.True(prototypeMs < 50, $"queued Enqueue should not block, got {prototypeMs:F1} ms");
            Assert.Equal(["line 1", "line 2"], written);
        }
        finally
        {
            PerfLog.OverrideEnabledForTesting(null);
        }
    }

    [ColdLoadPerfFact]
    public void PerfLog_per_line_cost_today_versus_a_held_open_queued_writer()
    {
        const int lines = 2000;
        PerfLog.OverrideEnabledForTesting(true);
        Directory.CreateDirectory(AppPaths.LogsDirectory);
        try
        {
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < lines; i++)
                PerfLog.Mark($"cold-load perf: line {i}");
            var currentMs = sw.Elapsed.TotalMilliseconds;

            var path = Path.Combine(AppPaths.LogsDirectory, "perf-prototype.log");
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            double callerMs, drainedMs;
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var file = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                var writer = new Prototype.QueuedLineWriter(batch =>
                {
                    foreach (var l in batch) file.WriteLine(l);
                    file.Flush();
                });
                sw.Restart();
                for (var i = 0; i < lines; i++)
                    writer.Enqueue($"cold-load perf: line {i}");
                callerMs = sw.Elapsed.TotalMilliseconds;
                writer.Dispose();
                drainedMs = sw.Elapsed.TotalMilliseconds;
            }

            Assert.Equal(lines, File.ReadAllLines(PerfLog.LogPath).Length);
            Assert.Equal(lines, File.ReadAllLines(path).Length);
            Log($"[perflog-cost] {lines} lines: today {currentMs:F0} ms on the calling threads " +
                $"({currentMs * 1000 / lines:F0} µs/line); queued writer {callerMs:F1} ms on the callers, " +
                $"{drainedMs:F0} ms until on disk");
            Assert.True(callerMs < currentMs / 5, "the queued writer should take the file I/O off the callers");
        }
        finally
        {
            PerfLog.OverrideEnabledForTesting(null);
        }
    }

    [ColdLoadPerfFact]
    public void AppLog_per_line_cost_on_the_calling_thread()
    {
        const int lines = 2000;
        var dir = TestScratch.NewDirectory("applog-perf-");
        using var session = AppLog.StartSessionForTesting(dir);
        try
        {
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < lines; i++)
                AppLog.Write(AppLogLevel.Info, "perf", $"cold-load perf: line {i}");
            var ms = sw.Elapsed.TotalMilliseconds;
            Log($"[applog-cost] {lines} lines: {ms:F0} ms on the calling threads ({ms * 1000 / lines:F0} µs/line), " +
                "each a flushed write under one process-wide lock");
        }
        finally
        {
            AppLog.ResetForTesting();
        }
    }

    // ── 2. Discovery: two rev-parse processes per repo → one ──

    [ColdLoadPerfFact]
    public async Task One_rev_parse_discovers_the_same_repository_as_today()
    {
        // Every layout discovery must handle, including the two where --show-toplevel fails.
        using var wt = TestRepositoryBuilder.FromTemplate("disc-wt", b => { b.WriteFile("a.txt", "a\n").Stage(); b.Commit("init"); b.WriteFile("sub/b.txt", "b\n"); });
        var bare = TestScratch.NewDirectory("disc-bare-");
        await RunGitAsync(bare, "init", "-q", "--bare");
        var linked = Path.Combine(TestScratch.NewDirectory("disc-linked-"), "wt2");
        await RunGitAsync(wt.Path, "worktree", "add", "-q", "-b", "side", linked);

        var cases = new List<string>
        {
            wt.Path,
            Path.Combine(wt.Path, "sub"),
            Path.Combine(wt.Path, ".git"),
            Path.Combine(wt.Path, ".git", "refs"),
            bare,
            linked,
        };
        cases.AddRange(Repos);

        await using var host = new MiniHost();
        foreach (var path in cases)
        {
            var today = new InterceptingGitCliService(host.Cli);
            var proto = new InterceptingGitCliService(host.Cli);
            var expected = await RepositoryDiscovery.DiscoverAsync(path, today);
            var actual = await Prototype.DiscoverAsync(path, proto);
            Log($"[discovery-equal] {path}: today {today.Requests.Count} git call(s), prototype {proto.Requests.Count} → {actual}");
            Assert.Equal(expected, actual);
            Assert.True(proto.Requests.Count <= today.Requests.Count, "the prototype must never spawn more git processes");
            if (!expected.IsBare && !path.Contains(".git", StringComparison.OrdinalIgnoreCase))
                Assert.Equal(1, proto.Requests.Count);
        }

        var notRepo = TestScratch.NewDirectory("disc-none-");
        await Assert.ThrowsAsync<HistoriaGitException>(() => RepositoryDiscovery.DiscoverAsync(notRepo, host.Cli));
        await Assert.ThrowsAsync<HistoriaGitException>(() => Prototype.DiscoverAsync(notRepo, host.Cli));
    }

    [ColdLoadPerfFact]
    public async Task One_rev_parse_halves_discovery_time_on_the_session_repos()
    {
        Assert.NotEmpty(Repos);
        await using var host = new MiniHost();
        var totalToday = 0.0;
        var totalProto = 0.0;
        foreach (var repo in Repos)
        {
            var today = new List<double>();
            var proto = new List<double>();
            for (var round = 0; round < 7; round++)
            {
                var sw = Stopwatch.StartNew();
                await RepositoryDiscovery.DiscoverAsync(repo, host.Cli);
                today.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart();
                await Prototype.DiscoverAsync(repo, host.Cli);
                proto.Add(sw.Elapsed.TotalMilliseconds);
            }
            totalToday += Median(today);
            totalProto += Median(proto);
            Log($"[discovery-time] {Path.GetFileName(repo)}: today {Median(today):F0} ms, one call {Median(proto):F0} ms (median of 7)");
        }
        Log($"[discovery-time] all repos, serial: today {totalToday:F0} ms, one call {totalProto:F0} ms");
        Assert.True(totalProto < totalToday * 0.75, "one rev-parse should cut discovery by well over a quarter");
    }

    // ── 3. RepositoryContext construction: the cached-state seed on the open path ──

    [ColdLoadPerfFact]
    public async Task Cached_state_seed_dominates_opening_a_ref_heavy_repo()
    {
        Assert.NotEmpty(Repos);
        await using var host = new MiniHost(); // sets its own isolated state dir — copy into that one
        CopyUserRepoStateCache();
        var withCache = BuildProvider(host, withStateCache: true);
        var withoutCache = BuildProvider(host, withStateCache: false);
        var cache = new RepoStateCache(host.Settings);

        foreach (var repo in Repos)
        {
            var discover = new List<double>();
            var openSeeded = new List<double>();
            var openUnseeded = new List<double>();
            var seed = new List<double>();
            var tags = 0;
            for (var round = 0; round < 3; round++)
            {
                var sw = Stopwatch.StartNew();
                var discovered = await RepositoryDiscovery.DiscoverAsync(repo, host.Cli);
                discover.Add(sw.Elapsed.TotalMilliseconds);

                sw.Restart();
                var snap = cache.TryLoad(discovered.RepositoryPath);
                seed.Add(sw.Elapsed.TotalMilliseconds);
                tags = snap?.Tags?.Count ?? 0;

                sw.Restart();
                await using (var ctx = Prototype.Construct(discovered, withCache))
                    openSeeded.Add(sw.Elapsed.TotalMilliseconds);

                sw.Restart();
                await using (var ctx = Prototype.Construct(discovered, withoutCache))
                    openUnseeded.Add(sw.Elapsed.TotalMilliseconds);
            }

            Log($"[open-breakdown] {Path.GetFileName(repo)} ({tags} cached tags): discovery {Median(discover):F0} ms; " +
                $"construct with seed {Median(openSeeded):F0} ms (first {openSeeded[0]:F0}); " +
                $"construct without seed {Median(openUnseeded):F0} ms (first {openUnseeded[0]:F0}); " +
                $"seed alone {Median(seed):F0} ms (first {seed[0]:F0})");
        }
    }

    // ── 3b. The watcher's working-tree size probe ──

    [ColdLoadPerfFact]
    public void Index_entry_count_replaces_the_working_tree_walk_in_the_watcher_probe()
    {
        Assert.NotEmpty(Repos);
        var countFiles = typeof(Historia.Services.Watching.FileWatcherService).GetMethod("CountFilesCapped", BindingFlags.NonPublic | BindingFlags.Static)!;
        const int threshold = 50_000; // FileWatcherService.LargeTreeFileThreshold

        foreach (var repo in Repos)
        {
            var gitDir = Path.Combine(repo, ".git");
            var walk = new List<double>();
            var header = new List<double>();
            int walked = 0, indexed = 0;
            for (var round = 0; round < 3; round++)
            {
                var sw = Stopwatch.StartNew();
                walked = (int)countFiles.Invoke(null, [repo, threshold])!;
                walk.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart();
                indexed = Prototype.IndexEntryCount(gitDir);
                header.Add(sw.Elapsed.TotalMilliseconds);
            }
            var walkLarge = walked >= threshold;
            var indexLarge = indexed >= threshold;
            Log($"[watcher-probe] {Path.GetFileName(repo)}: tree walk {Median(walk):F0} ms (first {walk[0]:F0}) → {walked} files" +
                $"{(walkLarge ? " (capped → degraded)" : "")}; index header {Median(header):F2} ms → {indexed} tracked" +
                $"{(indexLarge ? " (degraded)" : "")}{(walkLarge == indexLarge ? "" : "  ** decisions differ **")}");
            Assert.True(Median(header) < 5, "reading the index header should be effectively free");
        }
    }

    // ── 4. Session restore: eight concurrent opens ──

    [ColdLoadPerfFact]
    public async Task Concurrent_session_open_today_versus_async_single_call_and_deferred_seed()
    {
        Assert.NotEmpty(Repos);
        await using var host = new MiniHost();
        CopyUserRepoStateCache();
        var withCache = BuildProvider(host, withStateCache: true);
        var withoutCache = BuildProvider(host, withStateCache: false);

        var variants = new (string Name, Func<string, Task<RepositoryContext>> Open)[]
        {
            // RestoreSessionAsync today: Task.Run(RepositoryContext.Open) — sync-over-async
            // discovery (two rev-parses) and the cached-state seed inside the constructor.
            ("today", p => Task.Run(() => RepositoryContext.Open(p, withCache))),
            ("async + one rev-parse", async p =>
                Prototype.Construct(await Prototype.DiscoverAsync(p, host.Cli), withCache)),
            ("async + one rev-parse + seed off the open path", async p =>
                Prototype.Construct(await Prototype.DiscoverAsync(p, host.Cli), withoutCache)),
        };

        var results = variants.ToDictionary(v => v.Name, _ => new List<(double All, double Slowest, int PeakQueue)>());
        for (var round = 0; round < 3; round++)
        {
            foreach (var (name, open) in variants)
            {
                var peak = 0;
                using var stop = new CancellationTokenSource();
                var sampler = Task.Run(async () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        peak = Math.Max(peak, (int)ThreadPool.PendingWorkItemCount);
                        await Task.Delay(5);
                    }
                });

                var sw = Stopwatch.StartNew();
                var each = Repos.Select(async p =>
                {
                    var ctx = await open(p);
                    return (ctx, ms: sw.Elapsed.TotalMilliseconds);
                }).ToList();
                var opened = await Task.WhenAll(each);
                var all = sw.Elapsed.TotalMilliseconds;
                stop.Cancel();
                await sampler;

                results[name].Add((all, opened.Max(o => o.ms), peak));
                foreach (var (ctx, _) in opened)
                    await ctx.DisposeAsync();
            }
        }

        foreach (var (name, runs) in results)
        {
            Log($"[restore-open] {name}: all {Repos.Count} open in {Median(runs.Select(r => r.All).ToList()):F0} ms " +
                $"(rounds: {string.Join(", ", runs.Select(r => $"{r.All:F0}"))}); peak thread-pool queue " +
                $"{runs.Max(r => r.PeakQueue)}");
        }

        var today = Median(results["today"].Select(r => r.All).ToList());
        var best = Median(results[variants[2].Name].Select(r => r.All).ToList());
        Assert.True(best < today, $"expected the combined fix to open the session faster ({best:F0} vs {today:F0} ms)");
    }

    // ── helpers ──

    /// <summary>Copies the developer's real repo-state cache (<c>repo-refs-*.json.gz</c>, read
    /// only) into this test's isolated cache directory, so the seed reads what the app read.</summary>
    private void CopyUserRepoStateCache()
    {
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Historia", "cache");
        Directory.CreateDirectory(AppPaths.CacheDirectory);
        if (!Directory.Exists(real))
            return;
        foreach (var f in Directory.EnumerateFiles(real, "repo-*.json.gz"))
            File.Copy(f, Path.Combine(AppPaths.CacheDirectory, Path.GetFileName(f)), overwrite: true);
    }

    private static ServiceProvider BuildProvider(MiniHost host, bool withStateCache)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService>(host.Settings);
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton(host.Locator);
        sc.AddSingleton(host.Cli);
        sc.AddSingleton<ICredentialVaultService, FakeVault>();
        if (withStateCache)
            sc.AddSingleton<IRepoStateCache>(new RepoStateCache(host.Settings));
        return sc.BuildServiceProvider();
    }

    private static async Task RunGitAsync(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {await p.StandardError.ReadToEndAsync()}");
    }

    /// <summary>Candidate fixes, kept here until the plan moves them into Historia.Core.</summary>
    internal static class Prototype
    {
        /// <summary>One <c>rev-parse</c> answers all three questions for a working tree. In a bare
        /// repo or inside <c>.git</c>, <c>--show-toplevel</c> fails after git has already printed
        /// the first two answers: a bare repo is done from those, and the rare inside-<c>.git</c>
        /// case (and every failure) falls back to today's path, which classifies errors.</summary>
        public static async Task<DiscoveredRepository> DiscoverAsync(string path, IGitCliService cli, CancellationToken ct = default)
        {
            var workingDir = Directory.Exists(path) ? path : (File.Exists(path) ? (Path.GetDirectoryName(path) ?? path) : path);
            var validCwd = Directory.Exists(workingDir)
                ? workingDir
                : (Directory.Exists(Path.GetDirectoryName(workingDir)) ? Path.GetDirectoryName(workingDir)! : AppContext.BaseDirectory);

            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["-C", workingDir, "rev-parse", "--is-bare-repository", "--absolute-git-dir", "--show-toplevel"],
                WorkingDirectory = validCwd,
            }, ct).ConfigureAwait(false);

            var lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length >= 2 && bool.TryParse(lines[0].Trim(), out var isBare))
            {
                var gitDir = Norm(lines[1].Trim());
                if (result.IsSuccess && !isBare && lines.Length >= 3)
                    return new DiscoveredRepository(gitDir, Norm(lines[2].Trim()), IsBare: false);
                if (isBare)
                    return new DiscoveredRepository(gitDir, gitDir, IsBare: true);

                // Inside .git: --show-toplevel needs a work tree, so ask from the git dir's parent.
                if (Path.GetDirectoryName(gitDir) is { Length: > 0 } parentDir)
                {
                    var retry = await cli.RunAsync(new GitCliRequest
                    {
                        Arguments = ["-C", parentDir, "rev-parse", "--show-toplevel"],
                        WorkingDirectory = Directory.Exists(parentDir) ? parentDir : validCwd,
                    }, ct).ConfigureAwait(false);
                    var top = retry.IsSuccess && !string.IsNullOrWhiteSpace(retry.StandardOutput)
                        ? retry.StandardOutput.Trim()
                        : parentDir;
                    return new DiscoveredRepository(gitDir, Norm(top), IsBare: false);
                }
            }

            return await RepositoryDiscovery.DiscoverAsync(path, cli, ct).ConfigureAwait(false);
        }

        private static string Norm(string path)
        {
            try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
            catch { return path; }
        }

        /// <summary>Tracked-file count from the index header (<c>DIRC</c>, version, then a
        /// big-endian entry count) — no tree walk. 0 when there is no readable index.</summary>
        public static int IndexEntryCount(string gitDir)
        {
            try
            {
                using var fs = new FileStream(Path.Combine(gitDir, "index"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Span<byte> h = stackalloc byte[12];
                if (fs.Read(h) != 12 || h[0] != 'D' || h[1] != 'I' || h[2] != 'R' || h[3] != 'C')
                    return 0;
                return (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(h[8..]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }

        /// <summary>The constructor RepositoryContext.Open calls after discovery.</summary>
        public static RepositoryContext Construct(DiscoveredRepository discovered, IServiceProvider services)
        {
            var ctor = typeof(RepositoryContext).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, [typeof(DiscoveredRepository), typeof(IServiceProvider)])!;
            try { return (RepositoryContext)ctor.Invoke([discovered, services]); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
        }

        /// <summary>Callers only enqueue; one dedicated thread hands batches to the sink, so a
        /// stalled write never blocks a caller. Dispose drains what is queued.</summary>
        public sealed class QueuedLineWriter : IDisposable
        {
            private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            private readonly Thread _thread;

            public QueuedLineWriter(Action<IReadOnlyList<string>> sink)
            {
                _thread = new Thread(() =>
                {
                    var batch = new List<string>();
                    while (_lines.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                    {
                        batch.Clear();
                        while (batch.Count < 512 && _lines.Reader.TryRead(out var line))
                            batch.Add(line);
                        try { sink(batch); } catch { /* diagnostics must never throw */ }
                    }
                }) { IsBackground = true, Name = "perf-log-writer" };
                _thread.Start();
            }

            public void Enqueue(string line) => _lines.Writer.TryWrite(line);

            public void Dispose()
            {
                _lines.Writer.TryComplete();
                _thread.Join();
            }
        }
    }
}
