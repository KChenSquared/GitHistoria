namespace Historia.Tests;

/// <summary>
/// Transport routing (SSH-Only, https↔ssh fallback) sends each attempt to a converted URL through
/// a per-invocation <c>remote.&lt;name&gt;.url</c> override. That key is multi-valued, so the
/// override must make the routed URL the remote's only one: appended to the stored URL instead,
/// fetch and prune keep using the stored URL and push goes to both. Every remote here is a local
/// path, so nothing touches a network.
/// </summary>
[Trait("tier", "git")]
public sealed class GitGatewayUrlOverrideTests : IAsyncDisposable
{
    private const string RoutedUrl = "ssh://git@host.example.com/o/r.git";

    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public void Override_clears_the_stored_url_before_setting_the_routed_one()
    {
        var args = GitGateway.WithRemoteUrlOverride("origin", RoutedUrl, ["fetch", "origin"]);

        Assert.Equal(
            ["-c", "remote.origin.url=", "-c", $"remote.origin.url={RoutedUrl}", "fetch", "origin"],
            args);
    }

    [Fact]
    public async Task Overridden_url_is_the_only_one_git_fetches_prunes_and_pushes_to()
    {
        using var stored = TestRepositoryBuilder.Create();
        stored.WriteFile("f.txt", "stored").Stage().Commit("stored");
        using var routed = TestRepositoryBuilder.Create();
        routed.WriteFile("f.txt", "routed").Stage().Commit("routed");
        routed.Git("branch", "gone");

        using var local = TestRepositoryBuilder.Create();
        local.WriteFile("f.txt", "local").Stage().Commit("local");
        local.Git("remote", "add", "origin", stored.Path);

        Task<GitCliResult> RunRouted(params string[] args) => _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = GitGateway.WithRemoteUrlOverride("origin", routed.Path, args),
            WorkingDirectory = local.Path,
        });

        Assert.True((await RunRouted("fetch", "origin")).IsSuccess);
        Assert.Contains("origin/gone", local.Git("branch", "-r"));

        routed.Git("branch", "-D", "gone");
        Assert.True((await RunRouted("remote", "prune", "origin")).IsSuccess);
        Assert.DoesNotContain("origin/gone", local.Git("branch", "-r"));

        Assert.True((await RunRouted("push", "origin", "HEAD:refs/heads/pushed")).IsSuccess);
        Assert.Contains("pushed", routed.Git("branch"));
        Assert.DoesNotContain("pushed", stored.Git("branch"));

        Assert.Equal(stored.Path, local.Git("config", "--get", "remote.origin.url").Trim());
    }
}
