namespace Historia.Tests;

/// <summary>§12.1 AF6 — the auto-fetch schedule, over a real <see cref="WorkspaceService"/> with
/// tiny temp repos, a manual delay pump, and a fake host (no real git fetch or graph paint).</summary>
public sealed class AutoFetchServiceTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];
    private WorkspaceService? _ws;
    private AutoFetchService? _svc;

    private WorkspaceService NewWorkspace()
    {
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var recents = new RecentRepositoryService(_host.Settings);
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        var identity = new RepoIdentityService(cli);
        return _ws = new WorkspaceService(_host.Provider, cli, recents, profiles, identity);
    }

    private TestRepositoryBuilder Repo(string file)
    {
        var b = TestRepositoryBuilder.Create();
        b.WriteFile(file, "x\n").Stage();
        b.Commit("c");
        _repos.Add(b);
        return b;
    }

    private static async Task WaitUntil(Func<bool> cond, string what, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return;
            await Task.Delay(20);
        }
        throw new Xunit.Sdk.XunitException($"Timed out waiting for: {what}");
    }

    // ── §12.1.2: positional cursor ──

    [Fact(Timeout = 60000)]
    public async Task Loop_fetches_the_repo_at_each_slot_after_an_initial_fetch_on_the_focused_repo()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);
        var r2 = await ws.OpenAsync(Repo("b.txt").Path);
        var r3 = await ws.OpenAsync(Repo("c.txt").Path); // focused

        var host = new FakeHost();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.Start();

        // [InitialFetch] on the focused repo, before any delay.
        await WaitUntil(() => host.Snapshot().Count == 1, "initial fetch");
        Assert.Same(r3, host.Snapshot()[0]);

        // Cycle: 5-min idle, then slot 0, 1 min, slot 1, 1 min, slot 2.
        await delay.WaitForPending("initial idle");
        Assert.Equal(AutoFetchService.InitialIdle, delay.Requested[^1]);
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == 2, "slot 0 fetch");
        Assert.Same(r1, host.Snapshot()[1]);

        await delay.WaitForPending("step to slot 1");
        Assert.Equal(AutoFetchService.PerRepoStep, delay.Requested[^1]);
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == 3, "slot 1 fetch");
        Assert.Same(r2, host.Snapshot()[2]);

        await delay.WaitForPending("step to slot 2");
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == 4, "slot 2 fetch");
        Assert.Same(r3, host.Snapshot()[3]);
    }

    [Fact(Timeout = 60000)]
    public async Task Reordering_a_just_fetched_tab_forward_refetches_it_and_skips_its_replacement()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);
        var r2 = await ws.OpenAsync(Repo("b.txt").Path);
        var r3 = await ws.OpenAsync(Repo("c.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.Start();
        await WaitUntil(() => host.Snapshot().Count == 1, "initial fetch");
        var afterInitial = host.Snapshot().Count;

        await delay.WaitForPending("initial idle");
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == afterInitial + 1, "slot 0");
        Assert.Same(r1, host.Snapshot()[^1]); // slot 0 = r1

        await delay.WaitForPending("step to slot 1");
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == afterInitial + 2, "slot 1");
        Assert.Same(r2, host.Snapshot()[^1]); // slot 1 = r2

        // The user drags the just-fetched r2 into the last slot: order is now r1, r3, r2.
        // Same count -> no restart. The cursor advances to slot 2, which is now r2 again.
        ws.Move(r2, 2);
        await delay.WaitForPending("step to slot 2");
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == afterInitial + 3, "slot 2");
        Assert.Same(r2, host.Snapshot()[^1]); // r2 fetched again

        var thisCycle = host.Snapshot().Skip(afterInitial).ToArray();
        Assert.Equal(new[] { r1, r2, r2 }, thisCycle);
        Assert.DoesNotContain(r3, thisCycle); // r3 slid into slot 1, already passed
    }

    // ── §12.1.2: add / close restart ──

    [Fact(Timeout = 60000)]
    public async Task Opening_a_tab_restarts_the_in_progress_cycle()
    {
        var ws = NewWorkspace();
        await ws.OpenAsync(Repo("a.txt").Path);
        await ws.OpenAsync(Repo("b.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.Start();
        await WaitUntil(() => host.Snapshot().Count == 1, "initial fetch");

        await delay.WaitForPending("initial idle");
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == 2, "slot 0");
        await delay.WaitForPending("step to slot 1");
        Assert.Equal(1, delay.CountOf(AutoFetchService.InitialIdle));

        // A third tab appears mid-cycle -> restart with a fresh 5-min idle.
        await ws.OpenAsync(Repo("c.txt").Path);
        await WaitUntil(() => delay.CountOf(AutoFetchService.InitialIdle) == 2, "cycle restarts with a fresh idle");
    }

    [Fact(Timeout = 60000)]
    public async Task Closing_a_tab_restarts_the_in_progress_cycle()
    {
        var ws = NewWorkspace();
        await ws.OpenAsync(Repo("a.txt").Path);
        await ws.OpenAsync(Repo("b.txt").Path);
        var r3 = await ws.OpenAsync(Repo("c.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.Start();
        await WaitUntil(() => host.Snapshot().Count == 1, "initial fetch");

        await delay.WaitForPending("initial idle");
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == 2, "slot 0");
        await delay.WaitForPending("step");
        Assert.Equal(1, delay.CountOf(AutoFetchService.InitialIdle));

        await ws.CloseAsync(r3);
        await WaitUntil(() => delay.CountOf(AutoFetchService.InitialIdle) == 2, "cycle restarts on close");
    }

    [Fact(Timeout = 60000)]
    public async Task A_pure_reorder_does_not_restart_the_cycle()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);
        await ws.OpenAsync(Repo("b.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.Start();
        await WaitUntil(() => host.Snapshot().Count == 1, "initial fetch");

        await delay.WaitForPending("initial idle");
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == 2, "slot 0");
        await delay.WaitForPending("step to slot 1");

        ws.Move(r1, 1); // same count
        await Task.Delay(300);
        Assert.Equal(1, delay.CountOf(AutoFetchService.InitialIdle)); // still the same cycle
    }

    // ── gating ──

    [Fact(Timeout = 60000)]
    public async Task Disabling_the_setting_mid_loop_stops_further_fetches()
    {
        var ws = NewWorkspace();
        await ws.OpenAsync(Repo("a.txt").Path);
        await ws.OpenAsync(Repo("b.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        var settings = Settings(true);
        _svc = new AutoFetchService(ws, host, settings, delay.Delay);
        _svc.Start();
        await WaitUntil(() => host.Snapshot().Count == 1, "initial fetch");
        await delay.WaitForPending("initial idle");

        await settings.UpdateAsync(s => s with { AutoFetchEnabled = false });
        Assert.False(_svc.IsEnabled);

        var count = host.Snapshot().Count;
        delay.FireAll();
        await Task.Delay(400);
        Assert.Equal(count, host.Snapshot().Count); // no fetches after disable
    }

    // ── §12.1.1: start conditions with no open repo ──

    [Fact(Timeout = 60000)]
    public async Task Cold_start_then_open_runs_an_initial_fetch()
    {
        var ws = NewWorkspace();
        var host = new FakeHost();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.NotifyRepoEntering(RepoEntrySource.Opened);
        _svc.Start();

        var r1 = await ws.OpenAsync(Repo("a.txt").Path);

        await WaitUntil(() => host.Snapshot().Count == 1, "initial fetch on the opened repo");
        Assert.Same(r1, host.Snapshot()[0]);
        await delay.WaitForPending("loop idle after initial fetch");
        Assert.Equal(AutoFetchService.InitialIdle, delay.Requested[^1]);
    }

    [Fact(Timeout = 60000)]
    public async Task Cold_start_then_clone_or_init_skips_the_initial_fetch()
    {
        var ws = NewWorkspace();
        var host = new FakeHost();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.NotifyRepoEntering(RepoEntrySource.ClonedOrInitialized);
        _svc.Start();

        var r1 = await ws.OpenAsync(Repo("a.txt").Path);

        // No [InitialFetch] — the loop goes straight to its 5-min idle.
        await delay.WaitForPending("loop idle");
        Assert.Equal(AutoFetchService.InitialIdle, delay.Requested[^1]);
        Assert.Empty(host.Snapshot());

        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == 1, "slot 0 fetch");
        Assert.Same(r1, host.Snapshot()[0]);
    }

    [Fact(Timeout = 60000)]
    public async Task With_open_repos_the_loop_waits_for_every_repo_to_fully_load()
    {
        var ws = NewWorkspace();
        await ws.OpenAsync(Repo("a.txt").Path);
        await ws.OpenAsync(Repo("b.txt").Path);

        var host = new FakeHost { LoadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.Start();

        await Task.Delay(300);
        Assert.Empty(host.Snapshot());            // blocked on the load gate
        Assert.Equal(0, delay.PendingCount);

        host.LoadGate!.SetResult();
        await WaitUntil(() => host.Snapshot().Count == 1, "initial fetch once all loaded");
        await delay.WaitForPending("loop idle");
    }

    // ── §21 dev visuals: per-tab state ──

    [Fact(Timeout = 60000)]
    public async Task Tab_state_goes_not_started_then_pending_then_fetched_then_resets_on_restart()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);
        var r2 = await ws.OpenAsync(Repo("b.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);

        Assert.Equal(AutoFetchTabState.NotStarted, _svc.GetTabState(r1)); // loop not started

        _svc.Start();
        await WaitUntil(() => host.Snapshot().Count == 1, "initial fetch");
        await delay.WaitForPending("initial idle"); // loop has entered its first cycle
        await WaitUntil(() => _svc.GetTabState(r1) == AutoFetchTabState.PendingThisCycle, "r1 pending");
        Assert.Equal(AutoFetchTabState.PendingThisCycle, _svc.GetTabState(r2));

        delay.Fire(); // → slot 0 (r1)
        await WaitUntil(() => _svc.GetTabState(r1) == AutoFetchTabState.FetchedThisCycle, "r1 fetched");
        Assert.Equal(AutoFetchTabState.PendingThisCycle, _svc.GetTabState(r2));

        await delay.WaitForPending("step to slot 1");
        delay.Fire(); // → slot 1 (r2)
        await WaitUntil(() => _svc.GetTabState(r2) == AutoFetchTabState.FetchedThisCycle, "r2 fetched");

        // A new tab restarts the cycle → everything back to pending, loop still running.
        await ws.OpenAsync(Repo("c.txt").Path);
        await WaitUntil(() => _svc.GetTabState(r1) == AutoFetchTabState.PendingThisCycle, "r1 reset to pending");
        Assert.Equal(AutoFetchTabState.PendingThisCycle, _svc.GetTabState(r2));
    }

    [Fact(Timeout = 60000)]
    public async Task Disabling_returns_tab_state_to_not_started()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        var settings = Settings(true);
        _svc = new AutoFetchService(ws, host, settings, delay.Delay);
        _svc.Start();
        await delay.WaitForPending("initial idle");
        await WaitUntil(() => _svc.GetTabState(r1) == AutoFetchTabState.PendingThisCycle, "r1 pending");

        await settings.UpdateAsync(s => s with { AutoFetchEnabled = false });
        await WaitUntil(() => _svc.GetTabState(r1) == AutoFetchTabState.NotStarted, "r1 back to not-started");
    }

    private FakeSettingsService Settings(bool enabled) =>
        new(new AppSettings { AutoFetchEnabled = enabled });

    public async ValueTask DisposeAsync()
    {
        if (_svc is not null)
            await _svc.DisposeAsync();
        if (_ws is not null)
            await _ws.DisposeAsync();
        await _host.DisposeAsync();
        foreach (var r in _repos)
            r.Dispose();
    }

    // ── test doubles ──

    private sealed class FakeHost : IAutoFetchHost
    {
        private readonly object _g = new();
        private readonly List<RepositoryContext> _fetched = [];

        public TaskCompletionSource? LoadGate { get; init; }

        public Task WhenFullyLoadedAsync(RepositoryContext context, CancellationToken ct)
            => LoadGate is null ? Task.CompletedTask : LoadGate.Task.WaitAsync(ct);

        public Task FetchAsync(RepositoryContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_g)
                _fetched.Add(context);
            return Task.CompletedTask;
        }

        public List<RepositoryContext> Snapshot()
        {
            lock (_g)
                return [.. _fetched];
        }
    }

    /// <summary>A hand-pumped stand-in for the production <c>Task.Delay</c> seam: records every
    /// requested interval and completes them on command, honouring cancellation.</summary>
    private sealed class ManualDelayer
    {
        private readonly object _g = new();
        private readonly List<TimeSpan> _requested = [];
        private readonly List<(TaskCompletionSource Tcs, CancellationTokenRegistration Reg)> _pending = [];

        public IReadOnlyList<TimeSpan> Requested
        {
            get { lock (_g) return [.. _requested]; }
        }

        public int PendingCount
        {
            get { lock (_g) return _pending.Count(p => !p.Tcs.Task.IsCompleted); }
        }

        public int CountOf(TimeSpan span)
        {
            lock (_g) return _requested.Count(t => t == span);
        }

        public Task Delay(TimeSpan span, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reg = ct.Register(() => tcs.TrySetCanceled());
            lock (_g)
            {
                _requested.Add(span);
                _pending.Add((tcs, reg));
            }
            return tcs.Task;
        }

        public void Fire()
        {
            (TaskCompletionSource Tcs, CancellationTokenRegistration Reg)? item = null;
            lock (_g)
            {
                var idx = _pending.FindIndex(p => !p.Tcs.Task.IsCompleted);
                if (idx >= 0)
                {
                    item = _pending[idx];
                    _pending.RemoveAt(idx);
                }
            }
            if (item is null)
                throw new Xunit.Sdk.XunitException("Fire(): no pending delay");
            item.Value.Reg.Dispose();
            item.Value.Tcs.TrySetResult();
        }

        public void FireAll()
        {
            List<(TaskCompletionSource Tcs, CancellationTokenRegistration Reg)> items;
            lock (_g)
            {
                items = _pending.Where(p => !p.Tcs.Task.IsCompleted).ToList();
                _pending.Clear();
            }
            foreach (var p in items)
            {
                p.Reg.Dispose();
                p.Tcs.TrySetResult();
            }
        }

        public async Task WaitForPending(string what, int timeoutMs = 5000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (PendingCount > 0) return;
                await Task.Delay(20);
            }
            throw new Xunit.Sdk.XunitException($"Timed out waiting for a pending delay: {what}");
        }
    }
}
