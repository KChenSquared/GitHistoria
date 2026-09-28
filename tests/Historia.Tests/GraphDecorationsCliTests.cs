namespace Historia.Tests;

public sealed class GraphDecorationsCliTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }
    private sealed class CustomOutputCli(string stdout) : IGitCliService
    {
        public List<GitCliRequest> Requests { get; } = [];
        public bool IsGitAvailable => true;

        public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new GitCliResult(0, stdout, "", GitErrorKind.None, TimeSpan.Zero));
        }

        public Task<GitCliResult> RunAsync(GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
            => RunAsync(request, ct);
    }

    [Fact]
    public void Remote_branches_with_slashes_in_remote_name()
    {
        var commitId = new GitObjectId("1111111111111111111111111111111111111111");
        var refs = new List<RefEntry>
        {
            new("refs/heads/feature/login", commitId, "commit", null, null, null, null, null, null),
            new("refs/remotes/team/fork/feature/login", commitId, "commit", null, null, null, null, null, null),
        };
        var snapshot = new RefSnapshot(refs, token: 1, headSymref: "refs/heads/feature/login", headTarget: commitId);

        var query = new GraphQuery { RefScope = GraphRefScope.AllRefs, IncludeRemoteBranches = true };
        var remoteNames = new[] { "team/fork", "origin" };

        var decos = GraphLayoutService.BuildDecorations(query, snapshot, remoteNames, stashShas: []);

        Assert.True(decos.TryGetValue(commitId, out var badges));
        var badge = Assert.Single(badges);
        Assert.Equal(DecorationType.LocalBranch, badge.Type);
        Assert.Equal("feature/login", badge.Label);
        Assert.True(badge.IsLocal);
        Assert.True(badge.IsHead);
        Assert.NotNull(badge.Remotes);
        Assert.Contains("team/fork", badge.Remotes);
    }

    [Fact]
    public void Orphan_remote_ref()
    {
        var commitId = new GitObjectId("2222222222222222222222222222222222222222");
        var refs = new List<RefEntry>
        {
            new("refs/remotes/deleted-remote/feature/test", commitId, "commit", null, null, null, null, null, null),
        };
        var snapshot = new RefSnapshot(refs, token: 1, headSymref: null, headTarget: null);

        var query = new GraphQuery { RefScope = GraphRefScope.AllRefs, IncludeRemoteBranches = true };
        var remoteNames = new[] { "origin" };

        var decos = GraphLayoutService.BuildDecorations(query, snapshot, remoteNames, stashShas: []);

        Assert.True(decos.TryGetValue(commitId, out var badges));
        var badge = Assert.Single(badges);
        Assert.Equal(DecorationType.RemoteBranch, badge.Type);
        Assert.Equal("feature/test", badge.Label);
        Assert.False(badge.IsLocal);
        Assert.NotNull(badge.Remotes);
        Assert.Contains("deleted-remote", badge.Remotes);
    }

    [Fact]
    public void Stash_badges_with_stashes_on_vs_off()
    {
        var commitId = new GitObjectId("3333333333333333333333333333333333333333");
        var refs = new List<RefEntry>
        {
            new("refs/heads/main", commitId, "commit", null, null, null, null, null, null),
        };
        var snapshot = new RefSnapshot(refs, token: 1, headSymref: "refs/heads/main", headTarget: commitId);

        var stashSha = "3333333333333333333333333333333333333333";

        var queryWithStash = new GraphQuery { RefScope = GraphRefScope.AllRefs, IncludeStashes = true };
        var decosOn = GraphLayoutService.BuildDecorations(queryWithStash, snapshot, [], stashShas: [stashSha]);

        Assert.True(decosOn.TryGetValue(commitId, out var badgesOn));
        Assert.Contains(badgesOn, b => b.Type == DecorationType.Stash && b.Label == "stash@{0}");

        var queryWithoutStash = new GraphQuery { RefScope = GraphRefScope.AllRefs, IncludeStashes = false };
        var decosOff = GraphLayoutService.BuildDecorations(queryWithoutStash, snapshot, [], stashShas: [stashSha]);

        Assert.True(decosOff.TryGetValue(commitId, out var badgesOff));
        Assert.DoesNotContain(badgesOff, b => b.Type == DecorationType.Stash);
    }

    [Fact]
    public async Task ReadStashShas_does_not_spawn_cli_if_refs_stash_is_missing()
    {
        var commitId = new GitObjectId("4444444444444444444444444444444444444444");
        var refs = new List<RefEntry>
        {
            new("refs/heads/main", commitId, "commit", null, null, null, null, null, null),
        };
        var snapshot = new RefSnapshot(refs, token: 1, headSymref: "refs/heads/main", headTarget: commitId);

        var cli = new RecordingGitCliService();
        var shas = await GraphLayoutService.ReadStashShasAsync(cli, "dummyPath", snapshot, CancellationToken.None);

        Assert.Empty(shas);
        Assert.Empty(cli.Requests);
    }

    [Fact]
    public async Task ReadStashShas_spawns_cli_when_refs_stash_is_present()
    {
        var commit1 = "5555555555555555555555555555555555555555";
        var commit2 = "6666666666666666666666666666666666666666";
        var stashTarget = new GitObjectId(commit1);
        var refs = new List<RefEntry>
        {
            new("refs/stash", stashTarget, "commit", null, null, null, null, null, null),
        };
        var snapshot = new RefSnapshot(refs, token: 1, headSymref: null, headTarget: null);

        var cli = new CustomOutputCli($"{commit1}\n{commit2}\n");
        var shas = await GraphLayoutService.ReadStashShasAsync(cli, "dummyPath", snapshot, CancellationToken.None);

        Assert.Equal(2, shas.Count);
        Assert.Equal(commit1, shas[0]);
        Assert.Equal(commit2, shas[1]);

        var req = Assert.Single(cli.Requests);
        Assert.Equal(["log", "-g", "--format=%H", "refs/stash"], req.Arguments);
    }

    [Fact]
    public async Task ReadStashShas_real_repo_with_two_stashes_matches_libgit2()
    {
        using var builder = TestRepositoryBuilder.FromTemplate("stash-real-two", b =>
        {
            b.WriteFile("f.txt", "1\n").Stage().Commit("c1");
            b.WriteFile("f.txt", "stash 1\n");
            b.Git("stash", "push", "-m", "stash 1");
            b.WriteFile("f.txt", "stash 2\n");
            b.Git("stash", "push", "-m", "stash 2");
        });

        await using var ctx = _host.Open(builder.Path);
        var snapshot = await ctx.RefSnapshots.GetAsync();
        var cli = _host.Provider.GetRequiredService<IGitCliService>();

        var shas = await GraphLayoutService.ReadStashShasAsync(cli, builder.Path, snapshot, CancellationToken.None);

        using var repo = new Repository(builder.Path);
        var expected = repo.Stashes.Select(s => s.WorkTree.Id.Sha).ToList();

        Assert.Equal(2, expected.Count);
        Assert.Equal(expected, shas);
    }
}
