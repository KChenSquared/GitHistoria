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

        var decos = GraphLayoutService.BuildDecorations(query, snapshot, remoteNames);

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

        var decos = GraphLayoutService.BuildDecorations(query, snapshot, remoteNames);

        Assert.True(decos.TryGetValue(commitId, out var badges));
        var badge = Assert.Single(badges);
        Assert.Equal(DecorationType.RemoteBranch, badge.Type);
        Assert.Equal("feature/test", badge.Label);
        Assert.False(badge.IsLocal);
        Assert.NotNull(badge.Remotes);
        Assert.Contains("deleted-remote", badge.Remotes);
    }

    [Fact]
    public void Stash_badges_are_never_created()
    {
        var commitId = new GitObjectId("3333333333333333333333333333333333333333");
        var refs = new List<RefEntry>
        {
            new("refs/heads/main", commitId, "commit", null, null, null, null, null, null),
        };
        var snapshot = new RefSnapshot(refs, token: 1, headSymref: "refs/heads/main", headTarget: commitId);

        var query = new GraphQuery { RefScope = GraphRefScope.AllRefs };
        var decos = GraphLayoutService.BuildDecorations(query, snapshot, []);

        Assert.True(decos.TryGetValue(commitId, out var badges));
        Assert.DoesNotContain(badges, b => b.Label.StartsWith("stash@", StringComparison.Ordinal));
    }
}

