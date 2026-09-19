namespace Historia.Tests;

/// <summary>
/// M3.1: Background-tab refresh queue (§F4, §F5).
/// Verifies that background refreshes drain in app-band tab order,
/// tab reordering updates drain order, interrupted tabs retain front priority,
/// background refreshes wait for the focused tab's initial load, and focused tab
/// refreshes run immediately.
/// </summary>
public sealed class BackgroundRefreshQueueTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];
    private RecentRepositoryService? _recents;

    private WorkspaceService NewWorkspace(IRepoIdentityService? identity = null)
    {
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var recents = _recents = new RecentRepositoryService(_host.Settings);
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        identity ??= new RepoIdentityService(cli);
        return new WorkspaceService(_host.Provider, cli, recents, profiles, identity);
    }

    private TestRepositoryBuilder CreateRepo(string name)
    {
        var r = TestRepositoryBuilder.FromTemplate($"brq-{name}", b =>
        {
            b.WriteFile("a.txt", $"{name}\n").Stage();
            b.Commit("init");
        });
        _repos.Add(r);
        return r;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        foreach (var r in _repos)
            r.Dispose();
    }

    [Fact(Timeout = 15000)]
    public async Task Pump_drains_in_app_band_tab_order_not_enqueue_order()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(CreateRepo("r1").Path);
        r1.MarkFirstGraphPaintDone();
        await r1.FirstFullLoad;

        var r2 = await ws.OpenAsync(CreateRepo("r2").Path);
        r2.MarkFirstGraphPaintDone();
        await r2.FirstFullLoad;

        var r3 = await ws.OpenAsync(CreateRepo("r3").Path);
        r3.MarkFirstGraphPaintDone();
        await r3.FirstFullLoad;

        // Open r0 as the focused tab, but do NOT complete its FirstGraphPaint yet so the pump holds.
        var r0 = await ws.OpenAsync(CreateRepo("r0").Path);
        ws.Move(r0, 0); // Put r0 at index 0. Order: [r0 (focused, loading), r1, r2, r3]

        var drainOrder = new List<int>();
        // Enqueue in reverse order: r3, then r2, then r1
        var t3 = ws.RequestBackgroundRefreshAsync(r3).ContinueWith(_ => { lock (drainOrder) drainOrder.Add(3); });
        var t2 = ws.RequestBackgroundRefreshAsync(r2).ContinueWith(_ => { lock (drainOrder) drainOrder.Add(2); });
        var t1 = ws.RequestBackgroundRefreshAsync(r1).ContinueWith(_ => { lock (drainOrder) drainOrder.Add(1); });

        // Release focused tab load so the pump drains:
        r0.MarkFirstGraphPaintDone();
        await r0.FirstFullLoad;

        await Task.WhenAll(t1, t2, t3);

        // App-band order was r1 (idx 1), r2 (idx 2), r3 (idx 3):
        Assert.Equal([1, 2, 3], drainOrder);

        await ws.DisposeAsync();
    }

    [Fact(Timeout = 15000)]
    public async Task Reordering_tabs_changes_the_drain_order()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(CreateRepo("r1").Path);
        r1.MarkFirstGraphPaintDone();
        await r1.FirstFullLoad;

        var r2 = await ws.OpenAsync(CreateRepo("r2").Path);
        r2.MarkFirstGraphPaintDone();
        await r2.FirstFullLoad;

        var r3 = await ws.OpenAsync(CreateRepo("r3").Path);
        r3.MarkFirstGraphPaintDone();
        await r3.FirstFullLoad;

        var r0 = await ws.OpenAsync(CreateRepo("r0").Path);
        ws.Move(r0, 0);

        // Move r3 to index 1: order becomes [r0 (focused, loading), r3 (idx 1), r1 (idx 2), r2 (idx 3)]
        ws.Move(r3, 1);

        var drainOrder = new List<int>();
        var t1 = ws.RequestBackgroundRefreshAsync(r1).ContinueWith(_ => { lock (drainOrder) drainOrder.Add(1); });
        var t2 = ws.RequestBackgroundRefreshAsync(r2).ContinueWith(_ => { lock (drainOrder) drainOrder.Add(2); });
        var t3 = ws.RequestBackgroundRefreshAsync(r3).ContinueWith(_ => { lock (drainOrder) drainOrder.Add(3); });

        // Release focused tab:
        r0.MarkFirstGraphPaintDone();
        await r0.FirstFullLoad;

        await Task.WhenAll(t1, t2, t3);

        // After reordering, r3 is index 1, r1 is index 2, r2 is index 3:
        Assert.Equal([3, 1, 2], drainOrder);

        await ws.DisposeAsync();
    }

    [Fact(Timeout = 15000)]
    public async Task Interrupted_focused_tab_still_jumps_the_queue()
    {
        var ws = NewWorkspace();
        var r0 = await ws.OpenAsync(CreateRepo("r0").Path);
        r0.MarkFirstGraphPaintDone();
        await r0.FirstFullLoad;

        var r1 = await ws.OpenAsync(CreateRepo("r1").Path);
        r1.MarkFirstGraphPaintDone();
        await r1.FirstFullLoad;

        // Open r2 and focus it, but do NOT complete its paint yet:
        var r2 = await ws.OpenAsync(CreateRepo("r2").Path);

        // Now focus r0 again while r2's initial load was in progress.
        // This interrupts r2 and puts it into the background queue with priority.
        ws.Focus(r0);

        var drainOrder = new List<int>();
        var r2Finished = r2.FirstFullLoad.ContinueWith(_ => { lock (drainOrder) drainOrder.Add(2); });

        // Enqueue r1 as well. In _open, r1 is index 1, r2 is index 2.
        var t1 = ws.RequestBackgroundRefreshAsync(r1).ContinueWith(_ => { lock (drainOrder) drainOrder.Add(1); });

        // Mark r2 paint done so when the pump picks r2, it can complete:
        r2.MarkFirstGraphPaintDone();

        await Task.WhenAll(r2Finished, t1);

        // r2 should have drained before r1 even though r1 had a lower index in _open:
        Assert.Equal([2, 1], drainOrder);

        await ws.DisposeAsync();
    }

    [Fact(Timeout = 15000)]
    public async Task Background_watcher_refresh_waits_for_the_focused_tab()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(CreateRepo("r1").Path);
        r1.MarkFirstGraphPaintDone();
        await r1.FirstFullLoad;

        // Open r0 as focused tab, but do not complete its FirstGraphPaint:
        var r0 = await ws.OpenAsync(CreateRepo("r0").Path);

        // Request background refresh for r1:
        var refreshTask = ws.RequestBackgroundRefreshAsync(r1);

        // Background refresh should be waiting on r0:
        await Task.Delay(200);
        Assert.False(refreshTask.IsCompleted);

        // Complete r0:
        r0.MarkFirstGraphPaintDone();
        await r0.FirstFullLoad;

        // Now r1's refresh completes:
        await refreshTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(refreshTask.IsCompletedSuccessfully);

        await ws.DisposeAsync();
    }

    [Fact(Timeout = 15000)]
    public async Task Focused_tab_refresh_is_not_queued()
    {
        var ws = NewWorkspace();
        var r0 = await ws.OpenAsync(CreateRepo("r0").Path);
        r0.MarkFirstGraphPaintDone();
        await r0.FirstFullLoad;

        Assert.True(ReferenceEquals(ws.Focused, r0));

        var refreshTask = ws.RequestBackgroundRefreshAsync(r0);

        // Focused tab refresh executes immediately:
        await refreshTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(refreshTask.IsCompletedSuccessfully);

        await ws.DisposeAsync();
    }
}
