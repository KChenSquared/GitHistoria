namespace Historia.Tests;

/// <summary>§6.8.2: the watcher's cheap pre-filter must not blanket-drop a tracked <c>bin</c>/
/// <c>obj</c> path — only skip it when it actually looks like build output (a project file sits
/// beside it). See <see cref="FileWatcherService.IsLikelyIgnored"/>.</summary>
[Trait("tier", "git")]
public sealed class FileWatcherServiceTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    public FileWatcherServiceTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("file-watcher-seed", b =>
        {
            b.WriteFile("seed.txt", "x").Stage().Commit("seed");
        });
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Tracked_bin_directory_with_no_adjacent_project_file_is_not_dropped()
    {
        // A common non-.NET layout: a repo-root bin/ of tracked scripts, nothing that would ever
        // build into it.
        _repo.WriteFile("bin/deploy", "#!/bin/sh\n").Stage().Commit("add bin/deploy");
        await using var ctx = _host.Open(_repo.Path);

        Assert.False(ctx.Watcher.IsLikelyIgnored("bin/deploy"));
        Assert.False(ctx.Watcher.IsLikelyIgnored("obj/fixtures/data.json"));
    }

    [Fact]
    public async Task Bin_beside_a_project_file_is_still_treated_as_build_output()
    {
        Directory.CreateDirectory(Path.Combine(_repo.Path, "src", "App", "bin", "Debug"));
        File.WriteAllText(Path.Combine(_repo.Path, "src", "App", "App.csproj"), "<Project />");
        await using var ctx = _host.Open(_repo.Path);

        Assert.True(ctx.Watcher.IsLikelyIgnored("src/App/bin/Debug/App.dll"));
        Assert.True(ctx.Watcher.IsLikelyIgnored("src/App/obj/Debug/App.dll"));
    }

    [Fact]
    public async Task Vendor_and_tooling_directories_are_always_ignored()
    {
        await using var ctx = _host.Open(_repo.Path);

        Assert.True(ctx.Watcher.IsLikelyIgnored("node_modules/left-pad/index.js"));
        Assert.True(ctx.Watcher.IsLikelyIgnored(".vs/state.json"));
        Assert.True(ctx.Watcher.IsLikelyIgnored(".git/foo")); // defence in depth; OnRawEvent routes .git elsewhere anyway
    }

    [Fact]
    public async Task Ordinary_paths_are_never_ignored()
    {
        await using var ctx = _host.Open(_repo.Path);

        Assert.False(ctx.Watcher.IsLikelyIgnored("src/App/Program.cs"));
        Assert.False(ctx.Watcher.IsLikelyIgnored("README.md"));
    }

    [Fact]
    public async Task No_refresh_is_published_after_DisposeAsync()
    {
        await using var ctx = _host.Open(_repo.Path);
        var fakeWs = new FakeWorkspace();
        var watcher = new Historia.Services.Watching.FileWatcherService(ctx, ctx.State, _host.Settings, fakeWs);

        // Arm debounce with a working-tree change
        var onRawEvent = typeof(Historia.Services.Watching.FileWatcherService)
            .GetMethod("OnRawEvent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        onRawEvent.Invoke(watcher, [Path.Combine(_repo.Path, "new-file.txt"), false]);

        // Dispose immediately while debounce timer is armed
        await watcher.DisposeAsync();

        // Wait past debounce window (DebounceMs = 150ms)
        await Task.Delay(250);

        Assert.Equal(0, fakeWs.RefreshCalls);
    }

    [Fact]
    public async Task A_watcher_error_after_disposal_is_ignored()
    {
        await using var ctx = _host.Open(_repo.Path);
        var fakeWs = new FakeWorkspace();
        var watcher = new Historia.Services.Watching.FileWatcherService(ctx, ctx.State, _host.Settings, fakeWs);

        await watcher.DisposeAsync();

        var onError = typeof(Historia.Services.Watching.FileWatcherService)
            .GetMethod("OnWatcherError", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var errorEventArgs = new ErrorEventArgs(new Exception("buffer overflow"));

        // Must not throw
        onError.Invoke(watcher, [watcher, errorEventArgs]);

        await Task.Delay(250);
        Assert.Equal(0, fakeWs.RefreshCalls);
    }

    private sealed class FakeWorkspace : IWorkspaceService
    {
        public int RefreshCalls { get; private set; }
        public IReadOnlyList<RepositoryContext> OpenRepositories => [];
        public RepositoryContext? Focused => null;
        public Task<RepositoryContext> OpenAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RepositoryContext> InitAsync(string path, string? initialBranch, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RepositoryContext> CloneAsync(string url, string targetDirectory, Historia.Services.Workspace.CloneOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CloseAsync(RepositoryContext context) => Task.CompletedTask;
        public void Focus(RepositoryContext context) { }
        public void EnsureInitialized(RepositoryContext context) { }
        public Task RequestBackgroundRefreshAsync(RepositoryContext context, bool full = false, CancellationToken ct = default)
        {
            RefreshCalls++;
            return Task.CompletedTask;
        }
        public void Move(RepositoryContext context, int newIndex) { }
        public Task RestoreSessionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler<FocusedRepositoryChangedEventArgs>? FocusedRepositoryChanged { add { } remove { } }
        public event EventHandler<OpenRepositoriesChangedEventArgs>? OpenRepositoriesChanged { add { } remove { } }
    }
}
