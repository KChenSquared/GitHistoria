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
}
