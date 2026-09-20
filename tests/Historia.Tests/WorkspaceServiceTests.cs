namespace Historia.Tests;
[Trait("tier", "git")]
[Collection("serial")]
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

    private static TestRepositoryBuilder Repo(string file) =>
        TestRepositoryBuilder.FromTemplate($"ws-repo-{file}", b =>
        {
            b.WriteFile(file, "x\n").Stage();
            b.Commit("c");
        });

    /// <summary>
    /// Re-opening an already-open repo raises <see cref="IWorkspaceService.FocusedRepositoryChanged"/>
    /// with the lock released, avoiding deadlock with UI thread marshaling.
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

    // ── Clone URL validation ──

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

    /// <summary>F3: ValidateCloneUrl now delegates to CloneValidation.DescribeUrlProblem, which
    /// also rejects a URL with no repo-address shape at all (not just the security-motivated
    /// cases above) — confirms that delegation reaches the new shape check too.</summary>
    [Fact]
    public async Task CloneAsync_rejects_a_url_with_no_repository_shape()
    {
        var ws = NewWorkspace();
        var target = Path.Combine(Path.GetTempPath(), $"historia-clone-test-{Guid.NewGuid():N}");

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(
            () => ws.CloneAsync("just some plain text", target, new Historia.Services.Workspace.CloneOptions(), progress: null));

        Assert.False(Directory.Exists(target));
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

    [Fact]
    public async Task CloneAsync_focuses_an_existing_tab_instead_of_opening_a_duplicate()
    {
        var ws = NewWorkspace();
        using var source = Repo("a.txt");
        var target = Path.Combine(Path.GetTempPath(), $"historia-clone-dedup-{Guid.NewGuid():N}");

        try
        {
            var first = await ws.CloneAsync(source.Path, target, new Historia.Services.Workspace.CloneOptions(), progress: null);
            Assert.Single(ws.OpenRepositories);
            Assert.Same(first, ws.Focused);

            foreach (var f in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(target, recursive: true);

            var second = await ws.CloneAsync(source.Path, target, new Historia.Services.Workspace.CloneOptions(), progress: null);
            Assert.Same(first, second);
            Assert.Single(ws.OpenRepositories);
            Assert.Same(first, ws.Focused);
        }
        finally
        {
            await ws.DisposeAsync();
            try
            {
                if (Directory.Exists(target))
                {
                    foreach (var f in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(target, recursive: true);
                }
            }
            catch { /* best effort */ }
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
    public async Task Restoring_persists_a_v3_session_file_on_the_next_change()
    {
        using var r1 = Repo("a.txt");

        var ws = NewWorkspace();
        await ws.OpenAsync(r1.Path);
        await Task.Delay(500); // let the debounced writer flush
        await ws.DisposeAsync();

        var node = JsonNode.Parse(File.ReadAllText(AppPaths.SessionFile))!;
        Assert.Equal(3, node["schemaVersion"]!.GetValue<int>());
        Assert.Equal(Norm(r1.Path), Norm(node["focusedPath"]!.GetValue<string>()));
        Assert.Single(node["paths"]!.AsArray());
        Assert.Single(node["linkOwners"]!.AsArray());
        Assert.True(node["linkOwners"]!.AsArray()[0] is null);
    }

    // ── Focus-priority interrupt-and-promote ──

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

    // ── Identity resolution runs concurrently with the first refs/status refresh ──

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

    // ── Headless caching for inactive tabs ──

    /// <summary>Overrides one service type while delegating everything else to a real provider —
    /// so <see cref="WorkspaceService"/>'s <c>IGraphLayoutService</c> can be swapped for a
    /// recording fake without standing up a whole second DI container.</summary>
    private sealed class OverrideServiceProvider(IServiceProvider inner, Type overriddenType, object overriddenInstance)
        : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == overriddenType ? overriddenInstance : inner.GetService(serviceType);
    }

    private sealed class RecordingGraphLayoutService : IGraphLayoutService
    {
        public List<RepositoryContext> Enqueued { get; } = [];

        public void EnqueueInactiveTabCaching(RepositoryContext context, GraphQuery query) => Enqueued.Add(context);
        public Task CancelInactiveTabCachingAsync(RepositoryContext context) { Cancelled.Add(context); return Task.CompletedTask; }
        public List<RepositoryContext> Cancelled { get; } = [];

        public Task<GraphLayoutResult> ComputeLayoutAsync(RepositoryContext context, GraphQuery query, int maxRows = 10000, CancellationToken ct = default)
            => throw new NotImplementedException();
        public GraphLayoutResult? TryOptimisticSnapshot(RepositoryContext context, GraphQuery query, int maxRows) => null;
        public Task<GraphLayoutResult> ExtendLayoutAsync(RepositoryContext context, GraphLayoutResult existing, int additionalRows, CancellationToken ct = default, bool background = false)
            => throw new NotImplementedException();
        public Task<GraphLayoutResult> RecomputeAsync(RepositoryContext context, GraphQuery query, GraphLayoutResult previous, InvalidationReason reason, int maxRows = 10000, CancellationToken ct = default)
            => throw new NotImplementedException();
        public void Forget(RepositoryContext context) { }
        public Task Hibernate(RepositoryContext context) => Task.CompletedTask;
        public int ClearPersistentCache() => 0;
        public Task RunHeadlessCacheAsync(RepositoryContext context, GraphQuery query, CancellationToken ct) => Task.CompletedTask;
        public void RefreshCommitGraphAfterFetch(RepositoryContext context) { }

        public Func<RepositoryContext, GraphQuery, IProgress<GitProgress>?, CancellationToken, Task>? PrecacheAsync { get; set; }
        public List<RepositoryContext> Precached { get; } = [];

        public async Task PrecacheClonedRepositoryAsync(RepositoryContext context, GraphQuery query, IProgress<GitProgress>? progress, CancellationToken ct)
        {
            Precached.Add(context);
            if (PrecacheAsync is not null)
                await PrecacheAsync(context, query, progress, ct);
        }
    }

    [Fact(Timeout = 20000)]
    public async Task Focusing_a_tab_queues_every_other_open_tab_for_headless_caching_but_not_itself()
    {
        var fakeGraph = new RecordingGraphLayoutService();
        var provider = new OverrideServiceProvider(_host.Provider, typeof(IGraphLayoutService), fakeGraph);
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var recents = _recents = new RecentRepositoryService(_host.Settings);
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        var identity = new RepoIdentityService(cli);
        var ws = new WorkspaceService(provider, cli, recents, profiles, identity);

        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");
        using var r3 = Repo("c.txt");

        // Force each tab's FirstFullLoad done immediately after it opens, before opening the
        // next one: FocusInternal only requeues an outgoing tab onto WorkspaceService's OWN
        // background-init pump when it's STILL loading, and that pump's own timing is unrelated
        // to what this test cares about (the headless-caching pipeline).
        var ctx1 = await ws.OpenAsync(r1.Path);
        ctx1.MarkFirstFullLoadDone();
        var ctx2 = await ws.OpenAsync(r2.Path);
        ctx2.MarkFirstFullLoadDone();
        var ctx3 = await ws.OpenAsync(r3.Path); // focus now r3 — this is what triggers the pipeline
        ctx3.MarkFirstGraphPaintDone();
        ctx3.MarkFirstFullLoadDone();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (fakeGraph.Enqueued.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        Assert.Equal(2, fakeGraph.Enqueued.Count);
        Assert.DoesNotContain(fakeGraph.Enqueued, c => Norm(c.RepositoryPath) == Norm(r3.Path));

        await ws.DisposeAsync();
    }

    // ── Focus cancels the newly focused context's own inactive-tab headless job ──

    [Fact(Timeout = 20000)]
    public async Task Focus_cancels_the_newly_focused_contexts_inactive_tab_job()
    {
        var fakeGraph = new RecordingGraphLayoutService();
        var provider = new OverrideServiceProvider(_host.Provider, typeof(IGraphLayoutService), fakeGraph);
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var recents = _recents = new RecentRepositoryService(_host.Settings);
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        var identity = new RepoIdentityService(cli);
        var ws = new WorkspaceService(provider, cli, recents, profiles, identity);

        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        var ctx1 = await ws.OpenAsync(r1.Path); // focus lands on ctx1
        ctx1.MarkFirstFullLoadDone();
        var ctx2 = await ws.OpenAsync(r2.Path); // focus moves to ctx2

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (fakeGraph.Cancelled.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        // Both contexts were, at some point, the newly focused one — each focus change must
        // cancel that tab's own inactive-tab job before its foreground graph work can start, even
        // though in this test neither ever actually had one queued.
        Assert.Contains(ctx1, fakeGraph.Cancelled);
        Assert.Contains(ctx2, fakeGraph.Cancelled);

        await ws.DisposeAsync();
    }

    // ── Gate the tab-switch compacting GC on actual heap growth ──

    [Theory]
    [InlineData(0L, 0L, false)]
    [InlineData(1024L * 1024 * 1024, 0L, true)] // heap at the 1 GiB threshold
    [InlineData(1024L * 1024 * 1024 - 1, 0L, false)] // just under the heap threshold, LOH empty
    [InlineData(0L, 256L * 1024 * 1024, true)] // LOH at the 256 MiB threshold
    [InlineData(0L, 256L * 1024 * 1024 - 1, false)] // just under the LOH threshold, heap empty
    [InlineData(2L * 1024 * 1024 * 1024, 1024L, true)] // either threshold alone is sufficient
    public void ShouldCompactAfterHibernate_uses_named_heap_and_LOH_thresholds(long heapBytes, long lohBytes, bool expected)
    {
        Assert.Equal(expected, WorkspaceService.ShouldCompactAfterHibernate(heapBytes, lohBytes));
    }

    // ── A focus switch mid-git-config-write must not cancel the write ──

    /// <summary>A minimal <see cref="IProfileService"/> whose <see cref="ApplyProfileToGitConfigAsync"/>
    /// blocks until released and records the token it was actually invoked with, so the test can
    /// interrupt the caller's attempt token while the "write" is in flight and check that the
    /// write itself never observed the cancellation.</summary>
    private sealed class ConfigWriteProfileService(GitProfile profile) : IProfileService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ReceivedToken { get; private set; }
        public bool WriteCompleted { get; private set; }

        public IReadOnlyList<GitProfile> GetAllProfiles() => [profile];
        public GitProfile? GetProfileById(Guid id) => profile.Id == id ? profile : null;
        public ProfileUsageState? GetUsage(Guid id) => null;
        public Task<GitProfile> CreateProfileAsync(GitProfileCreateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GitProfile> UpdateProfileAsync(Guid id, GitProfileUpdateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProfileDeleteResult> DeleteProfileAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public GitProfile GetGlobalProfile() => profile;
        public Task SyncGlobalIdentityAsync(CancellationToken ct = default) => Task.CompletedTask;
        public GitProfile? GetDefaultProfile() => profile;
        public Task SetDefaultProfileAsync(Guid? profileId, CancellationToken ct = default) => Task.CompletedTask;
        public GitProfile? GetProfileForRepository(RepoIdentityKey repoKey) => null;
        public bool IsLocalConfigSelected(RepoIdentityKey repoKey) => false;
        public Task BindProfileToRepositoryAsync(RepoIdentityKey repoKey, string displayPath, Guid profileId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLocalConfigForRepositoryAsync(RepoIdentityKey repoKey, string displayPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnbindRepositoryAsync(RepoIdentityKey repoKey, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyDictionary<RepoIdentityKey, RepoProfileBinding> GetAllRepoBindings() => new Dictionary<RepoIdentityKey, RepoProfileBinding>();
        public IReadOnlyList<RepoProfileBinding> GetStaleBindings() => [];
        public Task<int> CleanStaleBindingsAsync(CancellationToken ct = default) => Task.FromResult(0);
        public GitProfile? ResolveActiveProfile(RepoIdentityKey? repoKey = null) => profile;

        public async Task ApplyProfileToGitConfigAsync(GitProfile p, string repoPath, GitConfigScope scope, CancellationToken ct = default)
        {
            ReceivedToken = ct;
            Started.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            WriteCompleted = true;
        }

        public Task TouchProfileAsync(Guid profileId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetTokenAsync(Guid profileId, RemoteHost host, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task ExportProfilesAsync(string exportFilePath, bool includeTokens, string? password, CancellationToken ct = default) => Task.CompletedTask;
        public Task ImportProfilesAsync(string importFilePath, string? password, CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler? ProfilesChanged { add { } remove { } }
    }

    private static TestRepositoryBuilder RepoWithoutLocalIdentity()
    {
        var b = Repo("f.txt");
        // §9.6's write path only triggers when the repo has neither user.name nor user.email set
        // locally — TestRepositoryBuilder.Create() sets both so Commit() works, so clear them here.
        b.Git("config", "--local", "--unset", "user.name");
        b.Git("config", "--local", "--unset", "user.email");
        return b;
    }

    [Fact(Timeout = 20000)]
    public async Task Focus_switch_during_git_config_write_does_not_cancel_the_write()
    {
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var recents = _recents = new RecentRepositoryService(_host.Settings);
        var profile = new GitProfile
        {
            Id = Guid.NewGuid(),
            DisplayName = "Test Profile",
            GitUserName = "Profile Name",
            GitUserEmail = "profile@example.com",
            CreatedAt = DateTimeOffset.Now,
        };
        var profiles = new ConfigWriteProfileService(profile);
        var identity = new RepoIdentityService(cli);
        var ws = new WorkspaceService(_host.Provider, cli, recents, profiles, identity);

        using var r1 = RepoWithoutLocalIdentity();
        using var r2 = Repo("b.txt");

        var ctx1 = await ws.OpenAsync(r1.Path);
        ctx1.MarkFirstGraphPaintDone(); // skip the first-paint ceiling wait — no graph control here

        // The config write is now in flight (blocked on Release) inside ctx1's InitializeContextAsync.
        await profiles.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await ws.OpenAsync(r2.Path); // focus switch: interrupts ctx1's attempt token
        await Task.Delay(200); // give FocusInternal's Cancel() a moment to run

        Assert.False(profiles.ReceivedToken.IsCancellationRequested,
            "finding #12: the git-config write's own token must never be cancelled by a focus-loss interrupt");
        Assert.False(profiles.WriteCompleted, "the write should still be blocked on Release at this point");

        profiles.Release.TrySetResult();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!profiles.WriteCompleted && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(profiles.WriteCompleted, "the write must run to completion even though the attempt was interrupted");

        await ws.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task CloneAsync_precaches_before_the_tab_opens()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        var locator = _host.Provider.GetRequiredService<IGitExecutableLocator>();
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var realGraph = new GraphLayoutService(_host.Settings, locator, cli);
        var provider = new OverrideServiceProvider(_host.Provider, typeof(IGraphLayoutService), realGraph);
        var recents = _recents = new RecentRepositoryService(_host.Settings);
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        var identity = new RepoIdentityService(cli);
        var ws = new WorkspaceService(provider, cli, recents, profiles, identity);

        using var source = Repo("a.txt");
        var target = Path.Combine(Path.GetTempPath(), $"historia-clone-precache-{Guid.NewGuid():N}");

        try
        {
            var ctx = await ws.CloneAsync(source.Path, target, new Historia.Services.Workspace.CloneOptions(), progress: null);
            var hdrPath = GraphCachePaths.File(target, GraphMetaStore.HdrExt);
            Assert.True(File.Exists(hdrPath));

            using var doc = JsonDocument.Parse(File.ReadAllBytes(hdrPath));
            Assert.True(doc.RootElement.GetProperty("Exhausted").GetBoolean());
            Assert.Equal(ctx, ws.Focused);
        }
        finally
        {
            await ws.DisposeAsync();
            try { if (Directory.Exists(target)) Directory.Delete(target, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact(Timeout = 20000)]
    public async Task CloneAsync_cancelled_during_caching_still_opens_the_tab()
    {
        using var cts = new CancellationTokenSource();
        var fakeGraph = new RecordingGraphLayoutService
        {
            PrecacheAsync = async (_, _, _, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                await Task.CompletedTask;
            }
        };
        var provider = new OverrideServiceProvider(_host.Provider, typeof(IGraphLayoutService), fakeGraph);
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var recents = _recents = new RecentRepositoryService(_host.Settings);
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        var identity = new RepoIdentityService(cli);
        var ws = new WorkspaceService(provider, cli, recents, profiles, identity);

        using var source = Repo("a.txt");
        var target = Path.Combine(Path.GetTempPath(), $"historia-clone-cancel-precache-{Guid.NewGuid():N}");

        try
        {
            var ctx = await ws.CloneAsync(source.Path, target, new Historia.Services.Workspace.CloneOptions(), progress: null, ct: cts.Token);
            Assert.NotNull(ctx);
            Assert.True(Directory.Exists(Path.Combine(target, ".git")));
            Assert.Equal(ctx, ws.Focused);
        }
        finally
        {
            await ws.DisposeAsync();
            try { if (Directory.Exists(target)) Directory.Delete(target, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── §F9: linked submodule tab groups ──

    /// <summary>A real nested git repo under <paramref name="owner"/>'s working directory —
    /// enough for <c>Repository.Discover</c> to find it, without needing real
    /// <c>git submodule add</c> registration (OpenSubmoduleAsync only cares about the on-disk
    /// working copy, not <c>.gitmodules</c>).</summary>
    private static SubmoduleInfo MakeNestedSubmodule(RepositoryContext owner, string relativePath, string fileName)
    {
        var fullPath = Path.Combine(owner.RepositoryPath, relativePath);
        Directory.CreateDirectory(fullPath);
        LibGit2Sharp.Repository.Init(fullPath);
        using (var repo = new LibGit2Sharp.Repository(fullPath))
        {
            File.WriteAllText(Path.Combine(fullPath, fileName), "x");
            LibGit2Sharp.Commands.Stage(repo, "*");
            var sig = new LibGit2Sharp.Signature("Test User", "test@example.com", DateTimeOffset.Now);
            repo.Commit("c", sig, sig);
        }
        return new SubmoduleInfo
        {
            Name = relativePath,
            Path = relativePath,
            Url = fullPath,
            Status = SubmoduleStatus.Clean,
        };
    }

    [Fact]
    public async Task OpenSubmoduleAsync_inserts_the_new_tab_immediately_after_the_owners_group()
    {
        var ws = NewWorkspace();
        using var ownerRepo = Repo("owner.txt");
        using var otherRepo = Repo("other.txt");

        var owner = await ws.OpenAsync(ownerRepo.Path);
        var other = await ws.OpenAsync(otherRepo.Path);
        var sub = MakeNestedSubmodule(owner, "vendor/lib", "lib.txt");

        var child = await ws.OpenSubmoduleAsync(owner, sub);

        Assert.Equal([owner, child, other], ws.OpenRepositories);
        Assert.Same(owner, child.LinkOwner);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task OpenSubmoduleAsync_on_an_already_open_path_focuses_it_instead_of_duplicating()
    {
        var ws = NewWorkspace();
        using var ownerRepo = Repo("owner.txt");
        var owner = await ws.OpenAsync(ownerRepo.Path);
        var sub = MakeNestedSubmodule(owner, "vendor/lib", "lib.txt");

        var first = await ws.OpenSubmoduleAsync(owner, sub);
        var countAfterFirst = ws.OpenRepositories.Count;

        var second = await ws.OpenSubmoduleAsync(owner, sub);

        Assert.Same(first, second);
        Assert.Equal(countAfterFirst, ws.OpenRepositories.Count);
        Assert.Same(owner, first.LinkOwner);
        Assert.Same(first, ws.Focused);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task OpenSubmoduleAsync_twice_for_two_submodules_keeps_both_contiguous_after_the_owner()
    {
        var ws = NewWorkspace();
        using var ownerRepo = Repo("owner.txt");
        using var otherRepo = Repo("other.txt");
        var owner = await ws.OpenAsync(ownerRepo.Path);
        var other = await ws.OpenAsync(otherRepo.Path);

        var sub1 = MakeNestedSubmodule(owner, "vendor/lib1", "a.txt");
        var sub2 = MakeNestedSubmodule(owner, "vendor/lib2", "b.txt");
        var child1 = await ws.OpenSubmoduleAsync(owner, sub1);
        var child2 = await ws.OpenSubmoduleAsync(owner, sub2);

        Assert.Equal([owner, child1, child2, other], ws.OpenRepositories);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task Move_on_a_group_owner_relocates_the_whole_group_as_one_contiguous_block()
    {
        var ws = NewWorkspace();
        using var ownerRepo = Repo("owner.txt");
        using var otherRepo = Repo("other.txt");
        using var thirdRepo = Repo("third.txt");

        var owner = await ws.OpenAsync(ownerRepo.Path);
        var other = await ws.OpenAsync(otherRepo.Path);
        var third = await ws.OpenAsync(thirdRepo.Path);
        var sub = MakeNestedSubmodule(owner, "vendor/lib", "lib.txt");
        var child = await ws.OpenSubmoduleAsync(owner, sub);
        // _open: [owner, child, other, third]

        ws.Move(owner, newIndex: 3);

        // The owner+child block relocates together, snapping to the boundary nearest index 3
        // (third's own start) — matching the pre-existing (ungrouped) Move's convention of using
        // the target index exactly as computed pre-removal, with no shift adjustment.
        Assert.Equal([other, third, owner, child], ws.OpenRepositories);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task Move_on_a_linked_child_is_clamped_to_its_own_groups_span()
    {
        var ws = NewWorkspace();
        using var ownerRepo = Repo("owner.txt");
        using var otherRepo = Repo("other.txt");
        var owner = await ws.OpenAsync(ownerRepo.Path);
        var other = await ws.OpenAsync(otherRepo.Path);
        var sub1 = MakeNestedSubmodule(owner, "vendor/lib1", "a.txt");
        var sub2 = MakeNestedSubmodule(owner, "vendor/lib2", "b.txt");
        var child1 = await ws.OpenSubmoduleAsync(owner, sub1);
        var child2 = await ws.OpenSubmoduleAsync(owner, sub2);
        // _open: [owner, child1, child2, other]

        ws.Move(child1, newIndex: 10); // far past the owner's group and past `other`

        // child1 can only reorder among the owner's other children — clamped to the group's tail.
        Assert.Equal([owner, child2, child1, other], ws.OpenRepositories);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task Move_never_splits_a_group_when_dropped_in_the_middle_of_one()
    {
        var ws = NewWorkspace();
        using var ownerARepo = Repo("ownerA.txt");
        using var ownerBRepo = Repo("ownerB.txt");
        using var soloRepo = Repo("solo.txt");

        var ownerA = await ws.OpenAsync(ownerARepo.Path);
        var subA = MakeNestedSubmodule(ownerA, "vendor/a", "a.txt");
        var childA = await ws.OpenSubmoduleAsync(ownerA, subA);

        var ownerB = await ws.OpenAsync(ownerBRepo.Path);
        var subB = MakeNestedSubmodule(ownerB, "vendor/b", "b.txt");
        var childB = await ws.OpenSubmoduleAsync(ownerB, subB);

        var solo = await ws.OpenAsync(soloRepo.Path);
        // _open: [ownerA, childA, ownerB, childB, solo]

        ws.Move(solo, newIndex: 2); // 2 is inside groupB's span (ownerB=2, childB=3)

        // solo snaps to groupB's own start boundary — landing before it, never inside it.
        Assert.Equal([ownerA, childA, solo, ownerB, childB], ws.OpenRepositories);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task CloseAsync_on_a_group_owner_detaches_its_children_instead_of_closing_them()
    {
        var ws = NewWorkspace();
        using var ownerRepo = Repo("owner.txt");
        var owner = await ws.OpenAsync(ownerRepo.Path);
        var sub = MakeNestedSubmodule(owner, "vendor/lib", "lib.txt");
        var child = await ws.OpenSubmoduleAsync(owner, sub);

        await ws.CloseAsync(owner);

        Assert.Contains(child, ws.OpenRepositories);
        Assert.Null(child.LinkOwner);
        await ws.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task RestoreSessionAsync_reestablishes_link_groups_from_a_schemaVersion_3_file()
    {
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");
        var payload = new JsonObject
        {
            ["schemaVersion"] = 3,
            ["paths"] = new JsonArray(r1.Path, r2.Path),
            ["linkOwners"] = new JsonArray(null, r1.Path),
            ["focusedPath"] = r1.Path,
        };
        File.WriteAllText(AppPaths.SessionFile, payload.ToJsonString());

        var ws = NewWorkspace();
        await ws.RestoreSessionAsync();

        Assert.Equal(2, ws.OpenRepositories.Count);
        var owner = ws.OpenRepositories.First(c => Norm(c.RepositoryPath) == Norm(r1.Path));
        var child = ws.OpenRepositories.First(c => Norm(c.RepositoryPath) == Norm(r2.Path));
        Assert.Same(owner, child.LinkOwner);
        await ws.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task RestoreSessionAsync_reads_a_schemaVersion_2_file_with_every_tab_ungrouped()
    {
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");
        File.WriteAllText(AppPaths.SessionFile, new JsonObject
        {
            ["schemaVersion"] = 2,
            ["paths"] = new JsonArray(r1.Path, r2.Path),
            ["focusedPath"] = r1.Path,
        }.ToJsonString());

        var ws = NewWorkspace();
        await ws.RestoreSessionAsync();

        Assert.Equal(2, ws.OpenRepositories.Count);
        Assert.All(ws.OpenRepositories, c => Assert.Null(c.LinkOwner));
        await ws.DisposeAsync();
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
