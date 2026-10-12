namespace Historia.Tests;

/// <summary>A push never carries tags along with a branch, even with <c>push.followTags</c> set;
/// <see cref="PushSpec.ForTag"/> sends exactly one tag, unforced.</summary>
[Trait("tier", "git")]
public sealed class GitGatewayTagPushTests : IAsyncDisposable
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

    private bool UpstreamHasTag(string tag) =>
        !string.IsNullOrWhiteSpace(_upstream.Git("tag", "--list", tag));

    private void CloneUpstreamWithLocalCommit()
    {
        _upstream.WriteFile("f.txt", "1").Stage().Commit("one");
        _local.Git("remote", "add", "origin", _upstream.Path);
        _local.Git("fetch", "origin");
        _local.Git("checkout", "-b", "feature", "FETCH_HEAD");
        _local.WriteFile("g.txt", "2").Stage().Commit("two");
    }

    [Fact]
    public async Task A_branch_push_leaves_tags_behind_even_with_follow_tags_set()
    {
        CloneUpstreamWithLocalCommit();
        _local.Git("tag", "-a", "annotated", "-m", "release");
        _local.Git("tag", "light");
        _local.Git("config", "push.followTags", "true");

        await using var ctx = _host.Open(_local.Path);
        var gw = MakeGateway(ctx, _host.Provider.GetRequiredService<IGitCliService>());
        await gw.PushAsync("origin", new PushSpec { RefSpec = "feature" }, progress: null);

        Assert.Equal(_local.Git("rev-parse", "feature").Trim(), _upstream.Git("rev-parse", "feature").Trim());
        Assert.False(UpstreamHasTag("annotated"));
        Assert.False(UpstreamHasTag("light"));
    }

    [Fact]
    public async Task A_tag_push_sends_only_that_tag_and_never_overwrites_the_remotes()
    {
        CloneUpstreamWithLocalCommit();
        _local.Git("tag", "v1");
        _local.Git("tag", "v2");
        _upstream.Git("tag", "taken");
        _local.Git("tag", "taken");

        await using var ctx = _host.Open(_local.Path);
        var gw = MakeGateway(ctx, _host.Provider.GetRequiredService<IGitCliService>());
        await gw.PushAsync("origin", PushSpec.ForTag("v1"), progress: null);

        Assert.Equal(_local.Git("rev-parse", "v1").Trim(), _upstream.Git("rev-parse", "v1").Trim());
        Assert.False(UpstreamHasTag("v2"));

        var takenBefore = _upstream.Git("rev-parse", "taken").Trim();
        var ex = await Assert.ThrowsAsync<HistoriaGitException>(
            () => gw.PushAsync("origin", PushSpec.ForTag("taken"), progress: null));
        Assert.Contains("(already exists)", ex.Details);
        Assert.Equal(takenBefore, _upstream.Git("rev-parse", "taken").Trim());
    }

    [Fact]
    public async Task Every_push_passes_no_follow_tags_and_a_tag_push_names_one_tag()
    {
        _local.Git("remote", "add", "origin", "https://example.invalid/origin.git");
        await using var ctx = _host.Open(_local.Path);
        var cli = new RecordingGitCliService();
        var gw = MakeGateway(ctx, cli);

        await gw.PushAsync("origin", new PushSpec { RefSpec = "main" }, progress: null);
        await gw.PushAsync("origin", PushSpec.ForTag("release/2"), progress: null);

        var pushes = cli.Requests.Where(r => r.Arguments.Contains("push")).ToList();
        Assert.Equal(2, pushes.Count);
        Assert.All(pushes, p => Assert.Contains("--no-follow-tags", p.Arguments));
        Assert.All(pushes, p => Assert.DoesNotContain("--tags", p.Arguments));
        Assert.Contains("refs/tags/release/2:refs/tags/release/2", pushes[1].Arguments);
        Assert.DoesNotContain(pushes[1].Arguments, a => a.StartsWith('+'));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-v1")]
    [InlineData("v1:refs/heads/main")]
    [InlineData("v*")]
    public void A_tag_name_that_would_change_the_push_refspec_is_refused(string tag)
        => Assert.Throws<ArgumentException>(() => PushSpec.ForTag(tag));
}
