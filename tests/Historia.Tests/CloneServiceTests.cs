namespace Historia.Tests;
[Trait("tier", "git")]

public class CloneServiceTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    private static RepositoryContext CreateTestContext(MiniHost host, out TestRepositoryBuilder repo)
    {
        repo = TestRepositoryBuilder.FromTemplate("clone-seed", r =>
            r.WriteFile("init.txt", "hello").Stage().Commit("initial commit"));
        return host.Open(repo.Path);
    }

    [Fact]
    public void Describe_formats_git_commit_graph_and_caching_phases()
    {
        var noProgress = new CloneStatus("https://example.com/repo.git", "C:/repo", null, false);
        Assert.Equal("Starting clone…", noProgress.Describe());

        var graphPhaseNoPct = new CloneStatus("https://example.com/repo.git", "C:/repo",
            new GitProgress { Phase = CloneStatus.CommitGraphPhase }, false);
        Assert.Equal("Writing commit-graph", graphPhaseNoPct.Describe());

        var graphPhaseWithPct = new CloneStatus("https://example.com/repo.git", "C:/repo",
            new GitProgress { Phase = CloneStatus.CommitGraphPhase, Percent = 100 }, false);
        Assert.Equal("Writing commit-graph 100%", graphPhaseWithPct.Describe());

        var cachingPhase = new CloneStatus("https://example.com/repo.git", "C:/repo",
            new GitProgress { Phase = CloneStatus.CachingPhase, ObjectsDone = 125000, Percent = 50 }, false);
        Assert.Equal("Caching history — 125,000 commits", cachingPhase.Describe());

        var normalPhase = new CloneStatus("https://example.com/repo.git", "C:/repo",
            new GitProgress { Phase = "Receiving objects", Percent = 50 }, false);
        Assert.Equal("Receiving objects 50%", normalPhase.Describe());
    }

    [Fact]
    public async Task Current_reflects_the_latest_phase_to_a_late_subscriber()
    {
        var fakeWs = new ScriptedWorkspaceService();
        var autoFetch = new FakeAutoFetchService();
        var nav = new NavigationBus();
        var notifications = new NotificationService();
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        var identity = new RepoIdentityService(cli);

        var service = new CloneService(fakeWs, profiles, identity, notifications, nav, autoFetch);

        var request = new CloneRequest("https://example.com/repo.git", "C:/repo", new Historia.Services.Workspace.CloneOptions(), null);
        var startTask = service.StartAsync(request);

        // Simulate progress reports while in flight
        fakeWs.LastProgress?.Report(new GitProgress { Phase = "Compressing objects", Percent = 42 });

        // A late reader (e.g. newly mounted HomeView) observes Current directly
        var current = service.Current;
        Assert.NotNull(current);
        Assert.True(service.IsRunning);
        Assert.Equal("Compressing objects", current.Progress?.Phase);
        Assert.Equal(42, current.Progress?.Percent);
        Assert.Equal("Compressing objects 42%", current.Describe());

        await using var ctx = CreateTestContext(_host, out var repo);
        fakeWs.CloneTcs.TrySetResult(ctx);
        await startTask;

        Assert.Null(service.Current);
        Assert.False(service.IsRunning);
        repo.Dispose();
    }

    [Fact]
    public async Task Cancel_while_cloning_publishes_cancelled_and_clears_status()
    {
        var fakeWs = new ScriptedWorkspaceService();
        var autoFetch = new FakeAutoFetchService();
        var nav = new NavigationBus();
        var notifications = new NotificationService();
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        var identity = new RepoIdentityService(cli);

        var service = new CloneService(fakeWs, profiles, identity, notifications, nav, autoFetch);

        var request = new CloneRequest("https://example.com/repo.git", "C:/repo", new Historia.Services.Workspace.CloneOptions(), null);
        var startTask = service.StartAsync(request);

        Assert.True(service.IsRunning);
        service.Cancel();

        await startTask;

        Assert.Null(service.Current);
        Assert.False(service.IsRunning);

        var note = notifications.Current.FirstOrDefault(n => n.Message == "Clone cancelled.");
        Assert.NotNull(note);
        Assert.Equal(NotificationSeverity.Info, note.Severity);
    }

    [Fact]
    public async Task Cancel_during_caching_keeps_the_clone_and_reports_it()
    {
        var fakeWs = new ScriptedWorkspaceService { ThrowOnCancelled = false };
        var autoFetch = new FakeAutoFetchService();
        var nav = new NavigationBus();
        var notifications = new NotificationService();
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        var identity = new RepoIdentityService(cli);

        var service = new CloneService(fakeWs, profiles, identity, notifications, nav, autoFetch);

        var request = new CloneRequest("https://example.com/repo.git", "C:/repo", new Historia.Services.Workspace.CloneOptions(), null);
        var startTask = service.StartAsync(request);

        fakeWs.LastProgress?.Report(new GitProgress { Phase = CloneStatus.CachingPhase, ObjectsDone = 500, Percent = 50 });
        service.Cancel();

        // During caching phase, workspace clone succeeds despite cancellation
        await using var ctx = CreateTestContext(_host, out var repo);
        fakeWs.CloneTcs.TrySetResult(ctx);

        await startTask;

        Assert.Null(service.Current);
        Assert.False(service.IsRunning);

        var note = notifications.Current.FirstOrDefault(n => n.Message.Contains("history caching was cancelled"));
        Assert.NotNull(note);
        Assert.Equal(NotificationSeverity.Info, note.Severity);
        Assert.Equal("Cloned — history caching was cancelled; it finishes in the background.", note.Message);
        repo.Dispose();
    }

    [Fact]
    public async Task Second_start_while_running_is_rejected()
    {
        var fakeWs = new ScriptedWorkspaceService();
        var autoFetch = new FakeAutoFetchService();
        var nav = new NavigationBus();
        var notifications = new NotificationService();
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        var identity = new RepoIdentityService(cli);

        var service = new CloneService(fakeWs, profiles, identity, notifications, nav, autoFetch);

        var request1 = new CloneRequest("https://example.com/repo1.git", "C:/repo1", new Historia.Services.Workspace.CloneOptions(), null);
        var request2 = new CloneRequest("https://example.com/repo2.git", "C:/repo2", new Historia.Services.Workspace.CloneOptions(), null);

        var startTask = service.StartAsync(request1);
        Assert.True(service.IsRunning);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(request2));

        await using var ctx = CreateTestContext(_host, out var repo);
        fakeWs.CloneTcs.TrySetResult(ctx);
        await startTask;
        repo.Dispose();
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("/", true)]
    [InlineData("?intent=clone", true)]
    [InlineData("settings", false)]
    [InlineData("repo", false)]
    public async Task Success_navigates_to_repo_only_from_home(string initialRoute, bool shouldNavigate)
    {
        var fakeWs = new ScriptedWorkspaceService();
        var autoFetch = new FakeAutoFetchService();
        var nav = new NavigationBus();
        var notifications = new NotificationService();
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        var identity = new RepoIdentityService(cli);

        nav.ReportRoute(initialRoute);

        var requestedNavigations = new List<string>();
        nav.NavigationRequested += dest => requestedNavigations.Add(dest);

        var service = new CloneService(fakeWs, profiles, identity, notifications, nav, autoFetch);

        var request = new CloneRequest("https://example.com/repo.git", "C:/repo", new Historia.Services.Workspace.CloneOptions(), null);
        var startTask = service.StartAsync(request);

        await using var ctx = CreateTestContext(_host, out var repo);
        fakeWs.CloneTcs.TrySetResult(ctx);
        await startTask;

        if (shouldNavigate)
        {
            Assert.Contains("repo", requestedNavigations);
        }
        else
        {
            Assert.DoesNotContain("repo", requestedNavigations);
        }
        repo.Dispose();
    }

    [Fact]
    public async Task An_unclassified_clone_failure_is_reported_once_not_doubled()
    {
        var fakeWs = new ScriptedWorkspaceService();
        var autoFetch = new FakeAutoFetchService();
        var nav = new NavigationBus();
        var notifications = new NotificationService();
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        var identity = new RepoIdentityService(cli);

        var service = new CloneService(fakeWs, profiles, identity, notifications, nav, autoFetch);

        var request = new CloneRequest("https://example.com/repo.git", "C:/repo", new Historia.Services.Workspace.CloneOptions(), null);
        var startTask = service.StartAsync(request);

        fakeWs.CloneTcs.TrySetException(new HistoriaGitException(GitErrorKind.Unknown, "Clone failed (Unknown)."));
        await startTask;

        var note = notifications.Current.FirstOrDefault(n => n.Severity == NotificationSeverity.Error);
        Assert.NotNull(note);
        Assert.Equal("Clone failed (Unknown).", note.Message);
        Assert.DoesNotContain("Clone failed: Clone failed", note.Message);
    }

    private sealed class FakeAutoFetchService : IAutoFetchService
    {
        public RepoEntrySource? LastEntrySource { get; private set; }
        public bool IsEnabled => true;
        public void Start() { }
        public void Stop() { }
        public void NotifyRepoEntering(RepoEntrySource source) => LastEntrySource = source;
        public AutoFetchTabState GetTabState(RepositoryContext context) => AutoFetchTabState.NotStarted;
        public event EventHandler? TabStatesChanged { add { } remove { } }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedWorkspaceService : IWorkspaceService
    {
        public TaskCompletionSource<RepositoryContext> CloneTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgress<GitProgress>? LastProgress { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }
        public bool ThrowOnCancelled { get; set; } = true;

        public IReadOnlyList<RepositoryContext> OpenRepositories => [];
        public RepositoryContext? Focused => null;

        public Task<RepositoryContext> OpenAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RepositoryContext> InitAsync(string path, string? initialBranch, CancellationToken ct = default) => throw new NotImplementedException();

        public async Task<RepositoryContext> CloneAsync(
            string url,
            string targetDirectory,
            Historia.Services.Workspace.CloneOptions options,
            IProgress<GitProgress>? progress,
            CancellationToken ct = default)
        {
            LastProgress = progress;
            LastCancellationToken = ct;

            using var reg = ct.Register(() =>
            {
                if (ThrowOnCancelled)
                    CloneTcs.TrySetCanceled(ct);
            });

            return await CloneTcs.Task.ConfigureAwait(false);
        }

        public Task<RepositoryContext> OpenSubmoduleAsync(RepositoryContext owner, SubmoduleInfo submodule, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CloseAsync(RepositoryContext context) => Task.CompletedTask;
        public void Focus(RepositoryContext context) { }
        public void EnsureInitialized(RepositoryContext context) { }
        public void Move(RepositoryContext context, int newIndex) { }
        public Task RestoreSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public event EventHandler<FocusedRepositoryChangedEventArgs>? FocusedRepositoryChanged { add { } remove { } }
        public event EventHandler<OpenRepositoriesChangedEventArgs>? OpenRepositoriesChanged { add { } remove { } }
    }
}
