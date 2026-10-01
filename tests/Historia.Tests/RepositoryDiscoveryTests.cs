namespace Historia.Tests;

[Trait("tier", "git")]
public sealed class RepositoryDiscoveryTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private IGitCliService Cli => _host.Provider.GetRequiredService<IGitCliService>();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task DiscoverAsync_working_tree_root()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("disc-wt", r =>
            r.WriteFile("file.txt", "content").Stage().Commit("c1"));

        var disc = await RepositoryDiscovery.DiscoverAsync(repo.Path, Cli);

        Assert.False(disc.IsBare);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo.Path)), disc.RepositoryPath);
        Assert.Equal(Path.Combine(disc.RepositoryPath, ".git"), disc.GitDirectory);
    }

    [Fact]
    public async Task DiscoverAsync_started_before_git_resolution_waits_and_succeeds()
    {
        // Session restore opens tabs while git is still being located.
        using var repo = TestRepositoryBuilder.FromTemplate("disc-wt", r =>
            r.WriteFile("file.txt", "content").Stage().Commit("c1"));
        var locator = new DelayedLocator();
        var cli = new GitCliService(locator);

        var discover = RepositoryDiscovery.DiscoverAsync(repo.Path, cli);
        Assert.False(discover.IsCompleted);

        locator.Complete(await new GitExecutableLocator().ResolveAsync(null));
        var disc = await discover;

        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo.Path)), disc.RepositoryPath);
    }

    [Fact]
    public async Task DiscoverAsync_without_git_reports_git_as_required()
    {
        var locator = new DelayedLocator();
        locator.Complete(GitExecutableInfo.NotFound);

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() =>
            RepositoryDiscovery.DiscoverAsync(Path.GetTempPath(), new GitCliService(locator)));

        Assert.Contains("Git is required", ex.Message);
    }

    [Fact]
    public async Task DiscoverAsync_subdirectory_resolves_to_working_tree_root()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("disc-sub", r =>
            r.WriteFile("file.txt", "content").Stage().Commit("c1"));

        var subDir = Path.Combine(repo.Path, "nested", "deep", "dir");
        Directory.CreateDirectory(subDir);

        var disc = await RepositoryDiscovery.DiscoverAsync(subDir, Cli);

        Assert.False(disc.IsBare);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo.Path)), disc.RepositoryPath);
        Assert.Equal(Path.Combine(disc.RepositoryPath, ".git"), disc.GitDirectory);
    }

    [Fact]
    public async Task DiscoverAsync_cwd_inside_dot_git_resolves_to_working_tree()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("disc-dotgit", r =>
            r.WriteFile("file.txt", "content").Stage().Commit("c1"));

        var insideGit = Path.Combine(repo.Path, ".git");

        var disc = await RepositoryDiscovery.DiscoverAsync(insideGit, Cli);

        Assert.False(disc.IsBare);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo.Path)), disc.RepositoryPath);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(insideGit)), disc.GitDirectory);
    }

    [Fact]
    public async Task DiscoverAsync_bare_repository()
    {
        var bareDir = TestScratch.NewDirectory("disc-bare-");
        var init = await Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["init", "--bare", bareDir],
            WorkingDirectory = bareDir,
        });
        Assert.True(init.IsSuccess, init.StandardError);

        var disc = await RepositoryDiscovery.DiscoverAsync(bareDir, Cli);

        Assert.True(disc.IsBare);
        var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bareDir));
        Assert.Equal(expected, disc.RepositoryPath);
        Assert.Equal(expected, disc.GitDirectory);
    }

    [Fact]
    public async Task DiscoverAsync_linked_worktree()
    {
        using var mainRepo = TestRepositoryBuilder.FromTemplate("disc-main-wt", r =>
            r.WriteFile("file.txt", "content").Stage().Commit("c1"));

        var wtDir = TestScratch.NewDirectory("disc-linked-wt-");
        var addWt = await Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["worktree", "add", wtDir, "-b", "wt-branch"],
            WorkingDirectory = mainRepo.Path,
        });
        Assert.True(addWt.IsSuccess, addWt.StandardError);

        var disc = await RepositoryDiscovery.DiscoverAsync(wtDir, Cli);

        Assert.False(disc.IsBare);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(wtDir)), disc.RepositoryPath);
        Assert.Contains("worktrees", disc.GitDirectory.Replace('\\', '/'));
    }

    [Fact]
    public async Task DiscoverAsync_submodule_with_dot_git_file()
    {
        using var subSource = TestRepositoryBuilder.FromTemplate("disc-sub-src", r =>
            r.WriteFile("sub.txt", "sub").Stage().Commit("sub1"));
        using var parentRepo = TestRepositoryBuilder.FromTemplate("disc-parent-sub", r =>
            r.WriteFile("parent.txt", "parent").Stage().Commit("p1"));

        var addSub = await Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["-c", "protocol.file.allow=always", "submodule", "add", subSource.Path, "sub-mod"],
            WorkingDirectory = parentRepo.Path,
        });
        Assert.True(addSub.IsSuccess, addSub.StandardError);

        var subPath = Path.Combine(parentRepo.Path, "sub-mod");
        var disc = await RepositoryDiscovery.DiscoverAsync(subPath, Cli);

        Assert.False(disc.IsBare);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(subPath)), disc.RepositoryPath);
        Assert.Contains("modules", disc.GitDirectory.Replace('\\', '/'));
    }

    [Fact]
    public async Task DiscoverAsync_non_repo_throws_NotAGitRepository()
    {
        var emptyDir = TestScratch.NewDirectory("disc-non-repo-");

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() =>
            RepositoryDiscovery.DiscoverAsync(emptyDir, Cli));

        Assert.Equal(GitErrorKind.NotAGitRepository, ex.Kind);
    }

    [Fact]
    public async Task DiscoverAsync_sha256_repository()
    {
        var shaDir = TestScratch.NewDirectory("disc-sha256-");
        var init = await Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["init", "--object-format=sha256", shaDir],
            WorkingDirectory = shaDir,
        });
        Assert.True(init.IsSuccess, init.StandardError);

        var disc = await RepositoryDiscovery.DiscoverAsync(shaDir, Cli);

        Assert.False(disc.IsBare);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(shaDir)), disc.RepositoryPath);
        Assert.Equal(Path.Combine(disc.RepositoryPath, ".git"), disc.GitDirectory);
    }

    [Fact]
    public void Discover_synchronous_resolves_working_tree()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("disc-sync", r =>
            r.WriteFile("file.txt", "content").Stage().Commit("c1"));

        var disc = RepositoryDiscovery.Discover(repo.Path, Cli);

        Assert.False(disc.IsBare);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo.Path)), disc.RepositoryPath);
    }

    [Fact]
    public async Task One_git_process_discovers_a_working_tree_root_or_subdirectory()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("disc-count", r =>
            r.WriteFile("dir/file.txt", "content").Stage().Commit("c1"));

        foreach (var path in new[] { repo.Path, Path.Combine(repo.Path, "dir") })
        {
            var cli = new InterceptingGitCliService(Cli);
            var disc = await RepositoryDiscovery.DiscoverAsync(path, cli);

            Assert.Single(cli.Requests);
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo.Path)), disc.RepositoryPath);
        }
    }

    [Fact]
    public async Task One_git_process_discovers_a_bare_repository_and_two_from_inside_dot_git()
    {
        var bareDir = TestScratch.NewDirectory("disc-count-bare-");
        Assert.True((await Cli.RunAsync(new GitCliRequest { Arguments = ["init", "--bare", bareDir], WorkingDirectory = bareDir })).IsSuccess);
        using var repo = TestRepositoryBuilder.FromTemplate("disc-count", r =>
            r.WriteFile("dir/file.txt", "content").Stage().Commit("c1"));

        var bareCli = new InterceptingGitCliService(Cli);
        Assert.True((await RepositoryDiscovery.DiscoverAsync(bareDir, bareCli)).IsBare);
        Assert.Single(bareCli.Requests);

        var dotGitCli = new InterceptingGitCliService(Cli);
        var disc = await RepositoryDiscovery.DiscoverAsync(Path.Combine(repo.Path, ".git", "refs"), dotGitCli);
        Assert.Equal(2, dotGitCli.Requests.Count);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo.Path)), disc.RepositoryPath);
    }

    [Fact]
    public async Task One_git_process_discovers_a_linked_worktree()
    {
        using var mainRepo = TestRepositoryBuilder.FromTemplate("disc-count-wt", r =>
            r.WriteFile("file.txt", "content").Stage().Commit("c1"));
        var wtDir = TestScratch.NewDirectory("disc-count-linked-");
        Assert.True((await Cli.RunAsync(new GitCliRequest { Arguments = ["worktree", "add", wtDir, "-b", "wt-count"], WorkingDirectory = mainRepo.Path })).IsSuccess);

        var cli = new InterceptingGitCliService(Cli);
        var disc = await RepositoryDiscovery.DiscoverAsync(wtDir, cli);

        Assert.Single(cli.Requests);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(wtDir)), disc.RepositoryPath);
    }

    [Fact]
    public async Task An_unsafe_repository_is_still_reported_as_unsafe()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("disc-count", r =>
            r.WriteFile("dir/file.txt", "content").Stage().Commit("c1"));
        var cli = new InterceptingGitCliService(Cli)
        {
            Intercept = _ => new GitCliResult(128, "",
                "fatal: detected dubious ownership in repository at 'x'", GitErrorKind.UnsafeRepository, TimeSpan.Zero),
        };

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() => RepositoryDiscovery.DiscoverAsync(repo.Path, cli));

        Assert.Equal(GitErrorKind.UnsafeRepository, ex.Kind);
    }

    private sealed class DelayedLocator : IGitExecutableLocator
    {
        private readonly TaskCompletionSource<GitExecutableInfo> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private GitExecutableInfo _current = GitExecutableInfo.NotFound;

        public GitExecutableInfo Current => _current;

        public Task<GitExecutableInfo> ResolveAsync(string? userOverridePath, CancellationToken ct = default) => _tcs.Task;

        public Task<GitExecutableInfo> WhenResolvedAsync(CancellationToken ct = default) => _tcs.Task;

        public void Complete(GitExecutableInfo info)
        {
            _current = info;
            _tcs.TrySetResult(info);
        }
    }
}
