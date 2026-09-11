namespace Historia.Tests;

/// <summary>
/// §1 linux cold-prime plan: <see cref="RepoIdentityService"/>'s smallest-reachable-root-SHA
/// resolution (§9.3.1) now runs primarily via a separate <c>git rev-list --max-parents=0</c>
/// process instead of a libgit2 topological revwalk on the repo gate. Both the CLI path and the
/// forced-fallback (no git) libgit2 path must agree on the same repo, since either can run
/// depending on whether git.exe is discoverable.
/// </summary>
public sealed class RepoIdentityServiceTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;
    private readonly string _rootA;
    private readonly string _rootB;

    public RepoIdentityServiceTests()
    {
        _repo = TestRepositoryBuilder.Create();

        // Root A: the repo's very first commit, on main.
        _rootA = _repo.WriteFile("a.txt", "1\n").Stage().Commit("root A");
        for (var i = 0; i < 3; i++)
            _repo.WriteFile("a.txt", $"{i}\n").Stage().Commit($"main {i}");

        // Root B: a second, wholly unrelated root reachable only via its own branch — like a
        // grafted-in or merged-in unrelated history.
        _repo.Git("checkout", "--orphan", "other-root");
        _repo.Git("rm", "-rf", ".");
        _rootB = _repo.WriteFile("b.txt", "1\n").Stage().Commit("root B");
        _repo.Git("checkout", "main");
        _repo.Git("tag", "v1"); // a tag tip, so tag-inclusion in the rev-list args is exercised too
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    private string ExpectedSmallestRoot() => string.CompareOrdinal(_rootA, _rootB) < 0 ? _rootA : _rootB;

    [Fact]
    public async Task Resolves_the_ordinal_smallest_of_two_unrelated_roots_via_cli()
    {
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        Assert.True(cli.IsGitAvailable, "test requires git.exe to be discoverable");

        var svc = new RepoIdentityService(cli);
        await using var ctx = _host.Open(_repo.Path);

        var key = await svc.ResolveAsync(ctx);

        Assert.Equal(RepoIdentityKey.FromRootCommit(ExpectedSmallestRoot()), key);
    }

    [Fact]
    public async Task Cli_path_and_forced_libgit2_fallback_agree_on_the_same_repo()
    {
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        Assert.True(cli.IsGitAvailable, "test requires git.exe to be discoverable");

        var cliSvc = new RepoIdentityService(cli);
        await using var ctxCli = _host.Open(_repo.Path);
        var cliKey = await cliSvc.ResolveAsync(ctxCli);

        // NoGitCli reports git as unavailable, forcing ResolveAsync onto the libgit2
        // (SortBy.None, streamed) fallback walk instead of `git rev-list`.
        var fallbackSvc = new RepoIdentityService(new NoGitCli());
        await using var ctxFallback = _host.Open(_repo.Path);
        var fallbackKey = await fallbackSvc.ResolveAsync(ctxFallback);

        var expected = RepoIdentityKey.FromRootCommit(ExpectedSmallestRoot());
        Assert.Equal(expected, cliKey);
        Assert.Equal(expected, fallbackKey);
    }
}
