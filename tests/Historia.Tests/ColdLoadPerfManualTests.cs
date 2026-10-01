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
/// Cold-load and tab-switch latency measurements of the shipped code: logging on the calling
/// thread, repository discovery, the watcher's size probe, opening a repository and restoring a
/// whole session. <c>HISTORIA_PERF_REPOS</c> is a <c>;</c>-separated list of real repositories;
/// every repo operation here is read-only. Results also go to <c>HISTORIA_PERF_OUT</c> when set.
/// <code>
/// $env:HISTORIA_COLDLOAD_PERF=1; $env:HISTORIA_PERF_REPOS="C:\big\repo;D:\other\repo"
/// dotnet test tests/Historia.Tests --filter "FullyQualifiedName~ColdLoadPerfManualTests"
/// </code>
/// </summary>
[Trait("tier", "perf")]
[Collection("serial")]
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

    // ── Logging ──

    [ColdLoadPerfFact]
    public void A_perf_mark_does_not_wait_on_a_stalled_write()
    {
        PerfLog.OverrideEnabledForTesting(true);
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var stalled = QueuedLineWriter.OpenPerBatch("perf-test-writer",
            p => new QueuedLineWriterTests.StallingStream(QueuedLineWriterTests.Append(p), release, entered));
        var previous = PerfLog.SwapWriterForTesting(stalled);
        double ms;
        try
        {
            PerfLog.Mark("cold-load perf: first");
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var sw = Stopwatch.StartNew();
            PerfLog.Mark("cold-load perf: behind a stalled write");
            ms = sw.Elapsed.TotalMilliseconds;
        }
        finally
        {
            release.Set();
            PerfLog.SwapWriterForTesting(previous);
            stalled.Dispose();
            PerfLog.OverrideEnabledForTesting(null);
        }

        Log($"[perflog-stall] a mark behind a stalled write returned in {ms:F3} ms");
        Assert.True(ms < 50);
    }

    [ColdLoadPerfFact]
    public void Per_line_logging_cost_on_the_calling_thread()
    {
        const int lines = 2000;
        PerfLog.OverrideEnabledForTesting(true);
        try
        {
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < lines; i++)
                PerfLog.Mark($"cold-load perf: line {i}");
            var perfMs = sw.Elapsed.TotalMilliseconds;
            Assert.True(PerfLog.Flush(TimeSpan.FromSeconds(30)));
            var perfDrained = sw.Elapsed.TotalMilliseconds;
            Assert.Equal(lines, File.ReadAllLines(PerfLog.LogPath).Length);

            using var session = AppLog.StartSessionForTesting(TestScratch.NewDirectory("applog-perf-"));
            sw.Restart();
            for (var i = 0; i < lines; i++)
                AppLog.Write(AppLogLevel.Info, "perf", $"cold-load perf: line {i}");
            var appMs = sw.Elapsed.TotalMilliseconds;
            Assert.True(AppLog.Flush(TimeSpan.FromSeconds(30)));

            Log($"[log-cost] {lines} lines on the callers: PerfLog {perfMs:F1} ms ({perfMs * 1000 / lines:F1} µs/line, " +
                $"on disk after {perfDrained:F0} ms); AppLog {appMs:F1} ms ({appMs * 1000 / lines:F1} µs/line)");
        }
        finally
        {
            AppLog.ResetForTesting();
            PerfLog.OverrideEnabledForTesting(null);
        }
    }

    // ── Opening repositories ──

    [ColdLoadPerfFact]
    public async Task Discovery_runs_one_git_process_per_working_tree()
    {
        Assert.NotEmpty(Repos);
        await using var host = new MiniHost();
        var total = 0.0;
        foreach (var repo in Repos)
        {
            var cli = new InterceptingGitCliService(host.Cli);
            await RepositoryDiscovery.DiscoverAsync(repo, cli);
            Assert.Single(cli.Requests);

            var times = new List<double>();
            for (var round = 0; round < 7; round++)
            {
                var sw = Stopwatch.StartNew();
                await RepositoryDiscovery.DiscoverAsync(repo, host.Cli);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            total += Median(times);
            Log($"[discovery] {Path.GetFileName(repo)}: {Median(times):F0} ms (median of 7), 1 git process");
        }
        Log($"[discovery] all repos, serial: {total:F0} ms");
    }

    [ColdLoadPerfFact]
    public async Task Opening_each_repository()
    {
        Assert.NotEmpty(Repos);
        await using var host = new MiniHost(); // sets its own isolated state dir — copy into that one
        CopyUserRepoStateCache();
        var sp = BuildProvider(host);

        foreach (var repo in Repos)
        {
            var probe = new List<double>();
            var open = new List<double>();
            var tracked = 0;
            for (var round = 0; round < 3; round++)
            {
                var sw = Stopwatch.StartNew();
                tracked = Historia.Services.Watching.FileWatcherService.TrackedFileCount(Path.Combine(repo, ".git"));
                probe.Add(sw.Elapsed.TotalMilliseconds);

                sw.Restart();
                await using (await RepositoryContext.OpenAsync(repo, sp))
                    open.Add(sw.Elapsed.TotalMilliseconds);
            }
            Log($"[open] {Path.GetFileName(repo)}: OpenAsync {Median(open):F0} ms (first {open[0]:F0}); " +
                $"watcher size probe {Median(probe):F2} ms ({tracked} tracked files)");
        }
    }

    [ColdLoadPerfFact]
    public async Task Restoring_the_session_opens_every_repository_concurrently()
    {
        Assert.NotEmpty(Repos);
        await using var host = new MiniHost();
        CopyUserRepoStateCache();
        var sp = BuildProvider(host);

        var variants = new (string Name, Func<string, Task<RepositoryContext>> Open)[]
        {
            ("OpenAsync (the app's path)", p => Task.Run(() => RepositoryContext.OpenAsync(p, sp))),
            ("blocking Open on the thread pool", p => Task.Run(() => RepositoryContext.Open(p, sp))),
        };

        var results = variants.ToDictionary(v => v.Name, _ => new List<(double All, int PeakQueue)>());
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
                var opened = await Task.WhenAll(Repos.Select(open));
                var all = sw.Elapsed.TotalMilliseconds;
                stop.Cancel();
                await sampler;

                results[name].Add((all, peak));
                foreach (var ctx in opened)
                    await ctx.DisposeAsync();
            }
        }

        foreach (var (name, runs) in results)
        {
            Log($"[restore-open] {name}: all {Repos.Count} open in {Median(runs.Select(r => r.All).ToList()):F0} ms " +
                $"(rounds: {string.Join(", ", runs.Select(r => $"{r.All:F0}"))}); peak thread-pool queue {runs.Max(r => r.PeakQueue)}");
        }
    }

    // ── helpers ──

    /// <summary>Copies the developer's real repo-state cache (<c>repo-*.json.gz</c>, read only)
    /// into this test's isolated cache directory, so the seed reads what the app reads.</summary>
    private static void CopyUserRepoStateCache()
    {
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Historia", "cache");
        Directory.CreateDirectory(AppPaths.CacheDirectory);
        if (!Directory.Exists(real))
            return;
        foreach (var f in Directory.EnumerateFiles(real, "repo-*.json.gz"))
            File.Copy(f, Path.Combine(AppPaths.CacheDirectory, Path.GetFileName(f)), overwrite: true);
    }

    private static ServiceProvider BuildProvider(MiniHost host)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService>(host.Settings);
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton(host.Locator);
        sc.AddSingleton(host.Cli);
        sc.AddSingleton<ICredentialVaultService, FakeVault>();
        sc.AddSingleton<IRepoStateCache>(new RepoStateCache(host.Settings));
        return sc.BuildServiceProvider();
    }
}
