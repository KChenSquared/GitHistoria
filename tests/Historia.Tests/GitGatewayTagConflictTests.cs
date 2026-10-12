namespace Historia.Tests;

/// <summary>A fetch that would move a local tag the remote re-pointed fails as
/// <see cref="GitErrorKind.TagConflict"/> naming the tag, and a fetch with
/// <see cref="GitFetchOptions.ReplaceTags"/> moves only that tag to the remote's commit.</summary>
[Trait("tier", "git")]
public sealed class GitGatewayTagConflictTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _upstream = TestRepositoryBuilder.Create();
    private readonly TestRepositoryBuilder _local = TestRepositoryBuilder.Create();

    public async ValueTask DisposeAsync()
    {
        _local.Dispose();
        _upstream.Dispose();
        await _host.DisposeAsync();
    }

    private GitGateway MakeGateway(RepositoryContext ctx, IGitCliService cli) => new(
        ctx,
        cli,
        _host.Provider.GetRequiredService<IRepoLayoutService>(),
        _host.Provider.GetRequiredService<ICredentialVaultService>());

    [Fact]
    public async Task A_moved_remote_tag_is_a_tag_conflict_until_replaced()
    {
        _upstream.WriteFile("f.txt", "1").Stage().Commit("one");
        _upstream.Git("tag", "v1");
        _upstream.Git("tag", "keep");
        _local.Git("remote", "add", "origin", _upstream.Path);
        _local.Git("fetch", "origin");
        _upstream.WriteFile("f.txt", "2").Stage().Commit("two");
        _upstream.Git("tag", "-f", "v1");
        _upstream.Git("tag", "-f", "keep");
        var keepBefore = _local.Git("rev-parse", "keep").Trim();

        await using var ctx = _host.Open(_local.Path);
        var gw = MakeGateway(ctx, _host.Provider.GetRequiredService<IGitCliService>());

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(
            () => gw.FetchAsync("origin", new GitFetchOptions { Tags = true }, progress: null));
        Assert.Equal(GitErrorKind.TagConflict, ex.Kind);
        Assert.Equal(["keep", "v1"], GitErrorClassifier.ClobberedTags(ex.Details).Order(StringComparer.Ordinal));
        // Every branch still moved.
        Assert.Equal(_upstream.Git("rev-parse", "main").Trim(), _local.Git("rev-parse", "origin/main").Trim());

        await gw.FetchAsync("origin", new GitFetchOptions { ReplaceTags = ["v1"] }, progress: null);

        Assert.Equal(_upstream.Git("rev-parse", "v1").Trim(), _local.Git("rev-parse", "v1").Trim());
        Assert.Equal(keepBefore, _local.Git("rev-parse", "keep").Trim());
    }

    [Fact]
    public async Task Replaced_tags_are_passed_as_forced_tag_refspecs()
    {
        _local.Git("remote", "add", "origin", "https://example.invalid/origin.git");
        await using var ctx = _host.Open(_local.Path);
        var cli = new RecordingGitCliService();

        await MakeGateway(ctx, cli).FetchAsync("origin", new GitFetchOptions { ReplaceTags = ["v1", "release/2"] }, progress: null);

        var fetch = Assert.Single(cli.Requests, r => r.Arguments.Contains("fetch"));
        Assert.Contains("+refs/tags/v1:refs/tags/v1", fetch.Arguments);
        Assert.Contains("+refs/tags/release/2:refs/tags/release/2", fetch.Arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-v1")]
    [InlineData("v1:refs/heads/main")]
    [InlineData("v*")]
    [InlineData("v 1")]
    public void A_tag_name_that_would_change_the_refspec_is_refused(string tag)
        => Assert.Throws<ArgumentException>(() => GitGateway.ForcedTagRefSpec(tag));
}
