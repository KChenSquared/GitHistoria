namespace Historia.Tests;

/// <summary>M2.1: <c>IGitGateway.GetDivergenceAsync</c> — ahead/behind between two arbitrary refs
/// via <c>git rev-list --left-right --count &lt;local&gt;...&lt;remote&gt;</c> (CLI-routed, not
/// libgit2's <c>CalculateHistoryDivergence</c>).</summary>
[Trait("tier", "git")]
public sealed class GitGatewayDivergenceTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private GitGateway MakeGateway(RepositoryContext ctx) => new(
        ctx,
        _host.Provider.GetRequiredService<LibGit2Service>(),
        _host.Provider.GetRequiredService<IGitCliService>(),
        _host.Provider.GetRequiredService<IRepoLayoutService>(),
        _host.Provider.GetRequiredService<ICredentialVaultService>());

    [Fact]
    public async Task Local_ahead_only()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("a.txt", "1").Stage().Commit("base");
        repo.Checkout("other", create: true);
        repo.Checkout("main");
        repo.WriteFile("a.txt", "2").Stage().Commit("ahead");

        await using var ctx = _host.Open(repo.Path);
        var gw = MakeGateway(ctx);

        var ab = await gw.GetDivergenceAsync("refs/heads/main", "refs/heads/other");

        Assert.Equal(new AheadBehind(1, 0), ab);
    }

    [Fact]
    public async Task Local_behind_only()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("a.txt", "1").Stage().Commit("base");
        repo.Checkout("other", create: true);
        repo.WriteFile("a.txt", "2").Stage().Commit("ahead");
        repo.Checkout("main");

        await using var ctx = _host.Open(repo.Path);
        var gw = MakeGateway(ctx);

        var ab = await gw.GetDivergenceAsync("refs/heads/main", "refs/heads/other");

        Assert.Equal(new AheadBehind(0, 1), ab);
    }

    [Fact]
    public async Task Diverged()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("a.txt", "1").Stage().Commit("base");
        repo.Checkout("other", create: true);
        repo.WriteFile("a.txt", "2").Stage().Commit("other-side");
        repo.Checkout("main");
        repo.WriteFile("b.txt", "1").Stage().Commit("main-side");

        await using var ctx = _host.Open(repo.Path);
        var gw = MakeGateway(ctx);

        var ab = await gw.GetDivergenceAsync("refs/heads/main", "refs/heads/other");

        Assert.Equal(new AheadBehind(1, 1), ab);
    }

    [Fact]
    public async Task Identical_refs_are_zero_and_zero()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("a.txt", "1").Stage().Commit("base");

        await using var ctx = _host.Open(repo.Path);
        var gw = MakeGateway(ctx);

        var ab = await gw.GetDivergenceAsync("refs/heads/main", "refs/heads/main");

        Assert.Equal(new AheadBehind(0, 0), ab);
    }

    [Fact]
    public async Task Unknown_ref_returns_null()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("a.txt", "1").Stage().Commit("base");

        await using var ctx = _host.Open(repo.Path);
        var gw = MakeGateway(ctx);

        var ab = await gw.GetDivergenceAsync("refs/heads/main", "refs/heads/does-not-exist");

        Assert.Null(ab);
    }
}
