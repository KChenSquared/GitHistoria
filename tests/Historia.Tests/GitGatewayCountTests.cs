namespace Historia.Tests;

/// <summary>#21: <c>IGitGateway.CountCommitsLeavingAsync</c> — how many commits would leave the
/// current branch if it were reset to a given target, via <c>git rev-list --count
/// &lt;target&gt;..HEAD</c> (CLI-routed, not libgit2's <c>CalculateHistoryDivergence</c>, which
/// finding #15 measured at ~22 s on far-apart commits).</summary>
public sealed class GitGatewayCountTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    public GitGatewayCountTests()
    {
        _repo = TestRepositoryBuilder.Create();
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
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
        var first = _repo.WriteFile("a.txt", "1").Stage().Commit("first");
        _repo.WriteFile("a.txt", "2").Stage().Commit("second");
        _repo.WriteFile("a.txt", "3").Stage().Commit("third");

        await using var ctx = _host.Open(_repo.Path);
        var gw = MakeGateway(ctx);

        var leaving = await gw.CountCommitsLeavingAsync(first);

        Assert.Equal(2, leaving);
    }

    [Fact]
    public async Task Forward_or_same_commit_target_counts_zero()
    {
        var first = _repo.WriteFile("a.txt", "1").Stage().Commit("first");
        var head = _repo.WriteFile("a.txt", "2").Stage().Commit("second");

        await using var ctx = _host.Open(_repo.Path);
        var gw = MakeGateway(ctx);

        Assert.Equal(0, await gw.CountCommitsLeavingAsync(head));
        // "Forward" isn't reachable without further history here, but resetting to an ancestor
        // of an ancestor (i.e. HEAD itself, a same-commit reset) is exactly the zero case the
        // soft/mixed reset path relies on to skip the Rule 00 dialog.
        _ = first;
    }
}
