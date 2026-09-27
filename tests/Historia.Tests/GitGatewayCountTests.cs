namespace Historia.Tests;

/// <summary><c>IGitGateway.CountCommitsLeavingAsync</c> — how many commits would leave the
/// current branch if it were reset to a given target, via <c>git rev-list --count
/// &lt;target&gt;..HEAD</c> (CLI-routed, not libgit2's <c>CalculateHistoryDivergence</c>).</summary>
[Trait("tier", "git")]
public sealed class GitGatewayCountTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    private GitGateway MakeGateway(RepositoryContext ctx) => new(
        ctx,
        _host.Provider.GetRequiredService<LibGit2Service>(),
        _host.Provider.GetRequiredService<IGitCliService>(),
        _host.Provider.GetRequiredService<IRepoLayoutService>(),
        _host.Provider.GetRequiredService<ICredentialVaultService>());

    [Fact]
    public async Task Backwards_reset_target_counts_the_commits_that_would_leave()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gateway-count-3-seed", r =>
        {
            r.WriteFile("a.txt", "1").Stage().Commit("first");
            r.WriteFile("a.txt", "2").Stage().Commit("second");
            r.WriteFile("a.txt", "3").Stage().Commit("third");
        });
        var first = repo.Git("rev-parse", "HEAD~2").Trim();

        await using var ctx = _host.Open(repo.Path);
        var gw = MakeGateway(ctx);

        var leaving = await gw.CountCommitsLeavingAsync(first);

        Assert.Equal(2, leaving);
    }

    [Fact]
    public async Task Forward_or_same_commit_target_counts_zero()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gateway-count-2-seed", r =>
        {
            r.WriteFile("a.txt", "1").Stage().Commit("first");
            r.WriteFile("a.txt", "2").Stage().Commit("second");
        });
        var head = repo.HeadSha;

        await using var ctx = _host.Open(repo.Path);
        var gw = MakeGateway(ctx);

        Assert.Equal(0, await gw.CountCommitsLeavingAsync(head));
        // "Forward" isn't reachable without further history here, but resetting to an ancestor
        // of an ancestor (i.e. HEAD itself, a same-commit reset) is exactly the zero case the
        // soft/mixed reset path relies on to skip the destructive confirm.
    }
}
