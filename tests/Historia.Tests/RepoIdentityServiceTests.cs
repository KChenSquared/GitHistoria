namespace Historia.Tests;

/// <summary>
/// §1 linux cold-prime plan: <see cref="RepoIdentityService"/>'s smallest-reachable-root-SHA
/// resolution (§9.3.1) now runs primarily via a separate <c>git rev-list --max-parents=0</c>
/// process instead of a libgit2 topological revwalk on the repo gate. Both the CLI path and the
/// forced-fallback (no git) libgit2 path must agree on the same repo, since either can run
/// depending on whether git.exe is discoverable.
/// </summary>
[Trait("tier", "git")]
public sealed class RepoIdentityServiceTests : IAsyncDisposable
{
    private static string? _cachedRootA;
    private static string? _cachedRootB;
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;
    private readonly string _rootA;
    private readonly string _rootB;

    public RepoIdentityServiceTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("repo-identity-fixture", b =>
        {
            // Root A: the repo's very first commit, on main.
            _cachedRootA = b.WriteFile("a.txt", "1\n").Stage().Commit("root A");
            for (var i = 0; i < 3; i++)
                b.WriteFile("a.txt", $"{i}\n").Stage().Commit($"main {i}");

            // Root B: a second, wholly unrelated root reachable only via its own branch — like a
            // grafted-in or merged-in unrelated history.
            b.Git("checkout", "--orphan", "other-root");
            b.Git("rm", "-rf", ".");
            _cachedRootB = b.WriteFile("b.txt", "1\n").Stage().Commit("root B");
            b.Git("checkout", "main");
            b.Git("tag", "v1"); // a tag tip, so tag-inclusion in the rev-list args is exercised too
        });
        _rootA = _cachedRootA!;
        _rootB = _cachedRootB!;
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

    // ── The CLI path must ignore `git replace` grafts, same as the libgit2 fallback ──

    [Fact(Timeout = 20000)]
    public async Task CLI_path_ignores_git_replace_grafts_same_as_the_libgit2_fallback()
    {
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        Assert.True(cli.IsGitAvailable, "test requires git.exe to be discoverable");

        // Mask the ordinal-smallest root behind a `git replace` graft that gives it a parent —
        // verified manually against a real git.exe before writing this: without
        // GIT_NO_REPLACE_OBJECTS=1, `git rev-list --max-parents=0` transparently substitutes the
        // graft's content for the real root, so the root no longer looks parentless and drops out
        // of the walk's root-candidate set, changing the reported "smallest root" to the other
        // one. The libgit2 fallback never resolves replace refs at all, so it always sees the real
        // (still parentless) root.
        var smallest = ExpectedSmallestRoot();
        var other = smallest == _rootA ? _rootB : _rootA;
        var smallTree = _repo.Git("rev-parse", $"{smallest}^{{tree}}").Trim();
        var decoyWithParent = _repo.Git("commit-tree", smallTree, "-p", other, "-m", "decoy with a parent").Trim();
        _repo.Git("replace", smallest, decoyWithParent);

        var cliSvc = new RepoIdentityService(cli);
        await using var ctxCli = _host.Open(_repo.Path);
        var cliKey = await cliSvc.ResolveAsync(ctxCli);

        var fallbackSvc = new RepoIdentityService(new NoGitCli());
        await using var ctxFallback = _host.Open(_repo.Path);
        var fallbackKey = await fallbackSvc.ResolveAsync(ctxFallback);

        var expected = RepoIdentityKey.FromRootCommit(smallest);
        Assert.Equal(expected, cliKey);
        Assert.Equal(expected, fallbackKey);
    }
}
