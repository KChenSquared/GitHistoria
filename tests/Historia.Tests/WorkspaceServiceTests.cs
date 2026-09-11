namespace Historia.Tests;

public class WorkspaceServiceTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private RecentRepositoryService? _recents;

    private WorkspaceService NewWorkspace(IRepoIdentityService? identity = null)
    {
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var recents = _recents = new RecentRepositoryService(_host.Settings);
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        identity ??= new RepoIdentityService(cli);
        return new WorkspaceService(_host.Provider, cli, recents, profiles, identity);
    }

    private static string Norm(string p) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)).ToLowerInvariant();

    private static TestRepositoryBuilder Repo(string file)
    {
        var b = TestRepositoryBuilder.Create();
        b.WriteFile(file, "x\n").Stage();
        b.Commit("c");
        return b;
    }

    /// <summary>
    /// Regression: re-opening an already-open repo used to call FocusInternal (which raises
    /// FocusedRepositoryChanged) from inside `lock (_sync)`. A handler marshalling to the UI
    /// thread then deadlocked against any other thread reading OpenRepositories / Focused.
    /// The event must be raised with the lock released.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Reopening_an_open_repo_does_not_raise_focus_event_under_the_lock()
    {
        var ws = NewWorkspace();
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        await ws.OpenAsync(r1.Path);
        await ws.OpenAsync(r2.Path);          // focus is now r2

        var otherThreadCouldRead = false;
        ws.FocusedRepositoryChanged += (_, _) =>
        {
            // A different thread reads state guarded by _sync. If the event fires while the
            // caller still holds _sync, this Wait times out — the deadlock, reproduced.
            var read = Task.Run(() => { _ = ws.OpenRepositories; _ = ws.Focused; });
            otherThreadCouldRead = read.Wait(TimeSpan.FromSeconds(5));
        };

        await ws.OpenAsync(r1.Path);          // already open → dup-guard → FocusInternal

        Assert.True(otherThreadCouldRead, "FocusedRepositoryChanged was raised while _sync was held");
        await ws.DisposeAsync();
    }

    // ── security review 2026-09-07, finding 3: clone URL validation ──

    [Theory]
    [InlineData("--upload-pack=calc.exe")]
    [InlineData("-oProxyCommand=calc.exe")]
    [InlineData("ext::sh -c calc.exe")]
    [InlineData("fd::1")]
    public async Task CloneAsync_rejects_urls_that_are_not_a_repository_address(string maliciousUrl)
    {
        var ws = NewWorkspace();
        var target = Path.Combine(Path.GetTempPath(), $"historia-clone-test-{Guid.NewGuid():N}");

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(
            () => ws.CloneAsync(maliciousUrl, target, new Historia.Services.Workspace.CloneOptions(), progress: null));

        Assert.False(Directory.Exists(target), "a rejected clone must never touch the filesystem");
        Assert.NotNull(ex);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task CloneAsync_still_clones_a_plain_local_path()
    {
        var ws = NewWorkspace();
        using var source = Repo("a.txt");
        var target = Path.Combine(Path.GetTempPath(), $"historia-clone-test-{Guid.NewGuid():N}");

        try
        {
            var ctx = await ws.CloneAsync(source.Path, target, new Historia.Services.Workspace.CloneOptions(), progress: null);
            Assert.True(Directory.Exists(Path.Combine(target, ".git")));
            // ws.DisposeAsync() below disposes every still-open context, ctx included — don't
            // dispose it a second time here.
        }
        finally
        {
            await ws.DisposeAsync();
            try { if (Directory.Exists(target)) Directory.Delete(target, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── §V3-B/19.21: tab drag-and-drop reorder ──

    [Fact(Timeout = 30000)]
    public async Task Move_reorders_open_tabs_and_raises_open_changed()
    {
        var ws = NewWorkspace();
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");
        using var r3 = Repo("c.txt");

        var a = await ws.OpenAsync(r1.Path);
        await ws.OpenAsync(r2.Path);
        await ws.OpenAsync(r3.Path);

        var raised = 0;
        ws.OpenRepositoriesChanged += (_, _) => raised++;
        var focusedBefore = ws.Focused;

        ws.Move(a, 2); // a -> end

        Assert.Equal(1, raised);
        Assert.Equal(
            new[] { Norm(r2.Path), Norm(r3.Path), Norm(r1.Path) },
            ws.OpenRepositories.Select(c => Norm(c.RepositoryPath)));
        Assert.Same(focusedBefore, ws.Focused); // reorder does not change focus
        await ws.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task Move_clamps_the_index_and_ignores_a_closed_context()
    {
        var ws = NewWorkspace();
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        var a = await ws.OpenAsync(r1.Path);
        var b = await ws.OpenAsync(r2.Path);

        ws.Move(a, 99); // clamps to the last slot
        Assert.Equal(
            new[] { Norm(r2.Path), Norm(r1.Path) },
            ws.OpenRepositories.Select(c => Norm(c.RepositoryPath)));

        await ws.CloseAsync(b);
        var raised = false;
        ws.OpenRepositoriesChanged += (_, _) => raised = true;
        ws.Move(b, 0); // b is closed → no-op, no event
        Assert.False(raised);
        await ws.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task Move_persists_the_new_order_for_session_restore()
    {
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        var ws = NewWorkspace();
        var a = await ws.OpenAsync(r1.Path);
        await ws.OpenAsync(r2.Path);
        ws.Move(a, 1); // order is now r2, r1
        await Task.Delay(500); // let the debounced writer flush
        await ws.DisposeAsync();

        var node = JsonNode.Parse(File.ReadAllText(AppPaths.SessionFile))!;
        Assert.Equal(
            new[] { Norm(r2.Path), Norm(r1.Path) },
            node["paths"]!.AsArray().Select(n => Norm(n!.GetValue<string>())));
    }

    // ── §V3-9.3: session restore ──

    [Fact(Timeout = 30000)]
    public async Task Session_v1_array_still_loads()
    {
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");
        File.WriteAllText(AppPaths.SessionFile, JsonSerializer.Serialize(new[] { r1.Path, r2.Path }));

        var ws = NewWorkspace();
        await ws.RestoreSessionAsync();

        Assert.Equal(2, ws.OpenRepositories.Count);
        await ws.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task Session_v2_object_loads_and_restores_focus()
    {
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");
        using var r3 = Repo("c.txt");
        var payload = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["paths"] = new JsonArray(r1.Path, r2.Path, r3.Path),
            ["focusedPath"] = r2.Path,
        };
        File.WriteAllText(AppPaths.SessionFile, payload.ToJsonString());

        var ws = NewWorkspace();
        await ws.RestoreSessionAsync();

        Assert.Equal(3, ws.OpenRepositories.Count);
        Assert.Equal(Norm(r2.Path), Norm(ws.Focused!.RepositoryPath)); // focus, not last-opened
        // Prioritizing the focused tab's open must not reorder the tab strip -- it always has
        // to match the saved session order regardless of which tab was focused.
        Assert.Equal(
            new[] { Norm(r1.Path), Norm(r2.Path), Norm(r3.Path) },
            ws.OpenRepositories.Select(c => Norm(c.RepositoryPath)));
        await ws.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task Session_restore_does_not_touch_the_recent_list()
    {
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        // Seed the Recent list with r1 only, then hand-write a session that restores both.
        var seed = NewWorkspace();
        await seed.OpenAsync(r1.Path);
        await Task.Delay(500);
        await seed.DisposeAsync();
        var recentsBefore = _recents!.Items.Select(i => Norm(i.Path)).ToArray();

        File.WriteAllText(AppPaths.SessionFile, new JsonObject
        {
            ["schemaVersion"] = 2,
            ["paths"] = new JsonArray(r1.Path, r2.Path),
            ["focusedPath"] = r2.Path,
        }.ToJsonString());

        var ws = NewWorkspace();
        await ws.RestoreSessionAsync();
        await Task.Delay(500);
        await ws.DisposeAsync();

        // r2 was restored but never explicitly opened, so it must not have joined Recent,
        // and r1's position/timestamp is unchanged.
        Assert.Equal(recentsBefore, _recents!.Items.Select(i => Norm(i.Path)).ToArray());
    }

    [Fact(Timeout = 30000)]
    public async Task Restoring_persists_a_v2_session_file_on_the_next_change()
    {
        using var r1 = Repo("a.txt");

        var ws = NewWorkspace();
        await ws.OpenAsync(r1.Path);
        await Task.Delay(500); // let the debounced writer flush
        await ws.DisposeAsync();

        var node = JsonNode.Parse(File.ReadAllText(AppPaths.SessionFile))!;
        Assert.Equal(2, node["schemaVersion"]!.GetValue<int>());
        Assert.Equal(Norm(r1.Path), Norm(node["focusedPath"]!.GetValue<string>()));
        Assert.Single(node["paths"]!.AsArray());
    }

    // ── v10.1.3: focus-priority interrupt-and-promote ──

    [Fact(Timeout = 20000)]
    public async Task Focusing_away_mid_init_leaves_FirstFullLoad_incomplete_until_retried()
    {
        var identity = new SlowIdentityService();
        var ws = NewWorkspace(identity);
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        var ctx1 = await ws.OpenAsync(r1.Path);
        ctx1.MarkFirstGraphPaintDone(); // skip the first-paint ceiling wait — no graph control here
        var ctx2 = await ws.OpenAsync(r2.Path); // focus switch: interrupts ctx1's in-flight identity resolution
        ctx2.MarkFirstGraphPaintDone();
        identity.Release(r2.Path); // ctx2 resolves quickly and finishes on its own

        Assert.False(ctx1.FirstFullLoad.IsCompleted, "an interrupted attempt must not be marked done");

        identity.Release(r1.Path); // let the pump's retry of ctx1 proceed
        await ctx1.FirstFullLoad.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(ctx1.FirstFullLoad.IsCompleted);

        await ws.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task Focusing_a_previously_interrupted_tab_is_promoted_ahead_of_the_tab_that_displaced_it()
    {
        var identity = new SlowIdentityService();
        var ws = NewWorkspace(identity);
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        var ctx1 = await ws.OpenAsync(r1.Path);
        ctx1.MarkFirstGraphPaintDone();
        var ctx2 = await ws.OpenAsync(r2.Path); // interrupts ctx1, ctx2 becomes foreground
        ctx2.MarkFirstGraphPaintDone();

        ws.Focus(ctx1); // interrupts ctx2 in turn, promotes ctx1 back to foreground immediately

        Assert.False(ctx2.FirstFullLoad.IsCompleted);

        identity.Release(r1.Path); // only ctx1's gate — ctx2's stays blocked the whole test
        await ctx1.FirstFullLoad.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(ctx1.FirstFullLoad.IsCompleted, "promoted tab must not wait behind the tab it displaced");
        Assert.False(ctx2.FirstFullLoad.IsCompleted, "the displaced tab was never released and must still be pending");

        await ws.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task Rapid_focus_toggling_between_two_loading_tabs_does_not_hang_and_both_eventually_finish()
    {
        var identity = new SlowIdentityService();
        var ws = NewWorkspace(identity);
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        var ctx1 = await ws.OpenAsync(r1.Path);
        ctx1.MarkFirstGraphPaintDone();
        var ctx2 = await ws.OpenAsync(r2.Path);
        ctx2.MarkFirstGraphPaintDone();

        for (var i = 0; i < 5; i++)
        {
            ws.Focus(ctx1);
            ws.Focus(ctx2);
        }

        identity.Release(r1.Path);
        identity.Release(r2.Path);

        await ctx1.FirstFullLoad.WaitAsync(TimeSpan.FromSeconds(15));
        await ctx2.FirstFullLoad.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(ctx1.FirstFullLoad.IsCompleted);
        Assert.True(ctx2.FirstFullLoad.IsCompleted);

        await ws.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task Closing_a_tab_while_its_interrupted_attempt_is_queued_does_not_resurrect_it()
    {
        var identity = new SlowIdentityService();
        var ws = NewWorkspace(identity);
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        var ctx1 = await ws.OpenAsync(r1.Path);
        ctx1.MarkFirstGraphPaintDone();
        var ctx2 = await ws.OpenAsync(r2.Path); // interrupts + requeues ctx1
        ctx2.MarkFirstGraphPaintDone();
        identity.Release(r2.Path);

        await ws.CloseAsync(ctx1); // closed while its interrupted attempt sits in _bgInitQueue

        identity.Release(r1.Path);
        await Task.Delay(500); // give the pump a chance to (wrongly) resurrect it, if it were going to
        Assert.DoesNotContain(ctx1, ws.OpenRepositories);

        await ws.DisposeAsync();
    }

    // ── v10.1.3: identity resolution runs concurrently with the first refs/status refresh ──

    [Fact(Timeout = 20000)]
    public async Task RefreshAllAsync_runs_concurrently_with_identity_resolution_not_after_it()
    {
        var identity = new SlowIdentityService(); // blocks every context until released
        var ws = NewWorkspace(identity);
        using var r1 = Repo("a.txt");

        var ctx1 = await ws.OpenAsync(r1.Path);
        ctx1.MarkFirstGraphPaintDone();

        var statusRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx1.State.StatusChanged += (_, _) => statusRaised.TrySetResult();

        // Identity resolution is still blocked (never released) — if RefreshAllAsync waited for it
        // to finish first (the old sequential behavior), this would time out.
        await statusRaised.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(ctx1.FirstFullLoad.IsCompleted, "identity is still blocked, so the attempt as a whole isn't done");
        identity.Release(r1.Path);
        await ctx1.FirstFullLoad.WaitAsync(TimeSpan.FromSeconds(10));

        await ws.DisposeAsync();
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
