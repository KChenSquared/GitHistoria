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
}
