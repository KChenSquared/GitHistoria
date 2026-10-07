namespace Historia.Tests;

[Trait("tier", "git")]
public sealed class RevisionResolverTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static TestRepositoryBuilder BuildRepo() =>
        TestRepositoryBuilder.FromTemplate("revision-resolver", b =>
        {
            b.WriteFile("a.txt", "one\n");
            b.Stage().Commit("first");
            b.WriteFile("a.txt", "two\n");
            b.Stage().Commit("second");
            b.Git("branch", "feature");
            b.Git("tag", "v1");
        });

    [Fact]
    public async Task Ordinary_revisions_resolve_to_commits()
    {
        using var repo = BuildRepo();
        var head = new GitObjectId(repo.HeadSha);
        var first = new GitObjectId(repo.Git("rev-parse", "HEAD~1").Trim());

        Assert.Equal(head, await RevisionResolver.ResolveCommitAsync(_host.Cli, repo.Path, "HEAD"));
        Assert.Equal(head, await RevisionResolver.ResolveCommitAsync(_host.Cli, repo.Path, repo.HeadSha[..8]));
        Assert.Equal(head, await RevisionResolver.ResolveCommitAsync(_host.Cli, repo.Path, "feature"));
        Assert.Equal(head, await RevisionResolver.ResolveCommitAsync(_host.Cli, repo.Path, "v1"));
        Assert.Equal(first, await RevisionResolver.ResolveCommitAsync(_host.Cli, repo.Path, "HEAD~1"));
    }

    [Fact]
    public async Task An_unknown_revision_is_null()
    {
        using var repo = BuildRepo();

        Assert.Null(await RevisionResolver.ResolveCommitAsync(_host.Cli, repo.Path, "nope"));
    }

    [Fact]
    public async Task A_tree_id_is_not_a_commit()
    {
        using var repo = BuildRepo();
        var tree = repo.Git("rev-parse", "HEAD^{tree}").Trim();

        Assert.Null(await RevisionResolver.ResolveCommitAsync(_host.Cli, repo.Path, tree));
    }

    [Fact]
    public async Task An_option_like_revision_is_rejected_before_git_runs()
    {
        using var repo = BuildRepo();
        var cli = new InterceptingGitCliService(_host.Cli);

        Assert.Null(await RevisionResolver.ResolveCommitAsync(cli, repo.Path, "--all"));
        Assert.Null(await RevisionResolver.ResolveCommitAsync(cli, repo.Path, "-rf"));
        Assert.Empty(cli.Requests);
    }

    [Fact]
    public async Task The_git_arguments_begin_with_the_verify_prefix_and_pass_the_revision_last()
    {
        using var repo = BuildRepo();
        var cli = new InterceptingGitCliService(_host.Cli);

        await RevisionResolver.ResolveCommitAsync(cli, repo.Path, "HEAD");

        var request = Assert.Single(cli.Requests);
        Assert.Equal(["rev-parse", "--verify", "-q", "--end-of-options", "HEAD^{commit}"], request.Arguments);
    }
}
