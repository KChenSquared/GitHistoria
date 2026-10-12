namespace Historia.Tests;

/// <summary>The terminal origin a repository tab starts its shell from.</summary>
[Trait("tier", "git")]
public sealed class RepoTerminalOriginTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];
    private readonly List<RepositoryContext> _contexts = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var ctx in _contexts)
            await ctx.DisposeAsync();
        await _host.DisposeAsync();
        foreach (var repo in _repos)
            repo.Dispose();
    }

    private RepositoryContext OpenNewRepo()
    {
        var repo = TestRepositoryBuilder.FromTemplate("repo-terminal-origin-seed", b =>
        {
            b.WriteFile("a.txt", "one\n").Stage();
            b.Commit("init");
        });
        _repos.Add(repo);
        var ctx = _host.Open(repo.Path);
        _contexts.Add(ctx);
        return ctx;
    }

    [Fact]
    public void A_repository_without_a_stored_identity_gets_a_stable_key_from_its_git_directory()
    {
        var ctx = OpenNewRepo();
        Assert.Null(ctx.IdentityKey);

        var first = RepoTerminalOrigin.For(ctx);
        var second = RepoTerminalOrigin.For(ctx);

        Assert.NotNull(first.RepoKey);
        Assert.Equal(first.RepoKey, second.RepoKey);
        Assert.Equal(ctx.Id, first.TabId);
    }

    [Fact]
    public void Repositories_in_different_directories_get_different_keys()
    {
        var a = RepoTerminalOrigin.For(OpenNewRepo());
        var b = RepoTerminalOrigin.For(OpenNewRepo());

        Assert.NotEqual(a.RepoKey, b.RepoKey);
    }
}
