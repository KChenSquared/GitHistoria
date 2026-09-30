namespace Historia.Tests;

/// <summary>
/// Push-URL and prune remote operations through <see cref="GitGateway"/>, run against real
/// repositories. Every remote here is a local path, so nothing touches a network.
/// </summary>
[Trait("tier", "git")]
public sealed class GitGatewayRemoteOpsTests : IAsyncDisposable
{
    private const string FetchUrl = "https://example.invalid/origin.git";
    private const string PushUrlA = "https://example.invalid/push-a.git";
    private const string PushUrlB = "https://example.invalid/push-b.git";

    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private TestRepositoryBuilder RepoWithOrigin()
    {
        var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("f.txt", "x").Stage().Commit("seed");
        repo.Git("remote", "add", "origin", FetchUrl);
        return repo;
    }

    private async Task<RemoteInfo> OriginAsync(RepositoryContext ctx)
        => (await ctx.Gateway.GetRemotesAsync()).Single(r => r.Name == "origin");

    [Fact]
    public async Task SetRemotePushUrl_sets_then_replaces()
    {
        using var repo = RepoWithOrigin();
        await using var ctx = _host.Open(repo.Path);

        await ctx.Gateway.SetRemotePushUrlAsync("origin", PushUrlA);
        Assert.Equal(PushUrlA, repo.Git("remote", "get-url", "--push", "origin").Trim());

        await ctx.Gateway.SetRemotePushUrlAsync("origin", PushUrlB);
        Assert.Equal(PushUrlB, repo.Git("remote", "get-url", "--push", "origin").Trim());
        Assert.Single(repo.Git("config", "--get-all", "remote.origin.pushurl")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(FetchUrl, repo.Git("remote", "get-url", "origin").Trim());
    }

    [Fact]
    public async Task SetRemotePushUrl_null_clears_and_is_idempotent()
    {
        using var repo = RepoWithOrigin();
        await using var ctx = _host.Open(repo.Path);

        await ctx.Gateway.SetRemotePushUrlAsync("origin", PushUrlA);
        await ctx.Gateway.SetRemotePushUrlAsync("origin", null);
        Assert.Equal(FetchUrl, repo.Git("remote", "get-url", "--push", "origin").Trim());

        // The key is already absent: git config exits 5, which a clear treats as done.
        await ctx.Gateway.SetRemotePushUrlAsync("origin", null);
        Assert.Equal(FetchUrl, repo.Git("remote", "get-url", "--push", "origin").Trim());
    }

    [Fact]
    public async Task GetRemotes_reports_distinct_push_url()
    {
        using var repo = RepoWithOrigin();
        await using var ctx = _host.Open(repo.Path);

        Assert.False((await OriginAsync(ctx)).HasDistinctPushUrl);

        await ctx.Gateway.SetRemotePushUrlAsync("origin", PushUrlA);
        var withPush = await OriginAsync(ctx);
        Assert.True(withPush.HasDistinctPushUrl);
        Assert.Equal(PushUrlA, withPush.PushUrl);

        await ctx.Gateway.SetRemotePushUrlAsync("origin", null);
        Assert.False((await OriginAsync(ctx)).HasDistinctPushUrl);
    }

    [Fact]
    public async Task PruneRemote_deletes_stale_tracking_ref_only()
    {
        using var origin = TestRepositoryBuilder.Create();
        origin.WriteFile("f.txt", "x").Stage().Commit("seed");
        origin.Git("branch", "a");
        origin.Git("branch", "b");

        using var local = TestRepositoryBuilder.Create();
        local.Git("remote", "add", "origin", origin.Path);
        local.Git("fetch", "origin");
        Assert.Contains("origin/b", local.Git("branch", "-r"));

        origin.Git("branch", "-D", "b");

        await using var ctx = _host.Open(local.Path);
        await ctx.Gateway.PruneRemoteAsync("origin", progress: null);

        var remoteRefs = local.Git("branch", "-r");
        Assert.DoesNotContain("origin/b", remoteRefs);
        Assert.Contains("origin/a", remoteRefs);
        Assert.Contains("origin/main", remoteRefs);
    }
}
