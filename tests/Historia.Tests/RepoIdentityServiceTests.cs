namespace Historia.Tests;

/// <summary>
/// <see cref="RepoIdentityService"/>'s smallest-reachable-root-SHA resolution runs via a separate
/// <c>git rev-list --max-parents=0</c> process.
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
    public async Task When_git_cli_is_unavailable_throws_and_caches_nothing()
    {
        var svc = new RepoIdentityService(new NoGitCli());
        await using var ctx = _host.Open(_repo.Path);

        await Assert.ThrowsAsync<HistoriaGitException>(() => svc.ResolveAsync(ctx));

        Assert.Null(ctx.IdentityKey);
    }

    private static bool IsRepoIdWrite(GitCliRequest r) =>
        r.Arguments.Contains("config") && r.Arguments.Contains("Historia.repoId")
        && !r.Arguments.Contains("--get");

    private static bool IsRootProbe(GitCliRequest r) => r.Arguments.Count > 0 && r.Arguments[0] == "rev-list";

    private static bool IsRepoIdRead(GitCliRequest r) =>
        r.Arguments.Contains("config") && r.Arguments.Contains("--get") && r.Arguments.Contains("Historia.repoId");

    [Fact]
    public async Task Cancelled_root_probe_throws_and_caches_nothing()
    {
        var cli = new InterceptingGitCliService(_host.Provider.GetRequiredService<IGitCliService>())
        {
            Intercept = r => IsRootProbe(r) ? InterceptingGitCliService.Failure(GitErrorKind.Cancelled, exitCode: -1) : null,
        };
        var svc = new RepoIdentityService(cli);
        await using var ctx = _host.Open(_repo.Path);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.ResolveAsync(ctx));

        Assert.Null(ctx.IdentityKey);
        Assert.DoesNotContain(cli.Requests, IsRepoIdWrite);
    }

    [Fact]
    public async Task Timed_out_root_probe_throws_and_writes_nothing()
    {
        var cli = new InterceptingGitCliService(_host.Provider.GetRequiredService<IGitCliService>())
        {
            Intercept = r => IsRootProbe(r) ? InterceptingGitCliService.Failure(GitErrorKind.TimedOut, exitCode: -1) : null,
        };
        var svc = new RepoIdentityService(cli);
        await using var ctx = _host.Open(_repo.Path);

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() => svc.ResolveAsync(ctx));

        Assert.Equal(GitErrorKind.TimedOut, ex.Kind);
        Assert.Null(ctx.IdentityKey);
        Assert.DoesNotContain(cli.Requests, IsRepoIdWrite);
    }

    [Fact]
    public async Task Failed_repo_id_read_throws()
    {
        var cli = new InterceptingGitCliService(_host.Provider.GetRequiredService<IGitCliService>())
        {
            Intercept = r => IsRepoIdRead(r) ? InterceptingGitCliService.Failure(exitCode: 128) : null,
        };
        var svc = new RepoIdentityService(cli);
        await using var ctx = _host.Open(_repo.Path);

        await Assert.ThrowsAsync<HistoriaGitException>(() => svc.ResolveAsync(ctx));

        Assert.Null(ctx.IdentityKey);
        Assert.DoesNotContain(cli.Requests, IsRootProbe);
    }

    [Fact]
    public async Task Rootless_repo_persists_and_caches_guid()
    {
        using var empty = TestRepositoryBuilder.Create();
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var svc = new RepoIdentityService(cli);
        await using var ctx = _host.Open(empty.Path);

        var key = await svc.ResolveAsync(ctx);

        Assert.True(Guid.TryParseExact(key.Value, "N", out _));
        Assert.Equal(key, ctx.IdentityKey);
        Assert.Equal(key.Value, empty.Git("config", "--local", "--get", "Historia.repoId").Trim());
    }

    [Fact]
    public async Task Failed_repo_id_write_caches_nothing()
    {
        using var empty = TestRepositoryBuilder.Create();
        var cli = new InterceptingGitCliService(_host.Provider.GetRequiredService<IGitCliService>())
        {
            Intercept = r => IsRepoIdWrite(r) ? InterceptingGitCliService.Failure() : null,
        };
        var svc = new RepoIdentityService(cli);
        await using var ctx = _host.Open(empty.Path);

        await Assert.ThrowsAsync<HistoriaGitException>(() => svc.ResolveAsync(ctx));

        Assert.Null(ctx.IdentityKey);
    }

    // ── The CLI path must ignore `git replace` grafts ──

    [Fact(Timeout = 20000)]
    public async Task CLI_path_ignores_git_replace_grafts()
    {
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        Assert.True(cli.IsGitAvailable, "test requires git.exe to be discoverable");

        // Mask the ordinal-smallest root behind a `git replace` graft that gives it a parent —
        // verified manually against a real git.exe before writing this: without
        // GIT_NO_REPLACE_OBJECTS=1, `git rev-list --max-parents=0` transparently substitutes the
        // graft's content for the real root, so the root no longer looks parentless and drops out
        // of the walk's root-candidate set, changing the reported "smallest root" to the other
        // one.
        var smallest = ExpectedSmallestRoot();
        var other = smallest == _rootA ? _rootB : _rootA;
        var smallTree = _repo.Git("rev-parse", $"{smallest}^{{tree}}").Trim();
        var decoyWithParent = _repo.Git("commit-tree", smallTree, "-p", other, "-m", "decoy with a parent").Trim();
        _repo.Git("replace", smallest, decoyWithParent);

        var cliSvc = new RepoIdentityService(cli);
        await using var ctxCli = _host.Open(_repo.Path);
        var cliKey = await cliSvc.ResolveAsync(ctxCli);

        var expected = RepoIdentityKey.FromRootCommit(smallest);
        Assert.Equal(expected, cliKey);
    }
}
