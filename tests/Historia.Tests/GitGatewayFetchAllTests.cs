namespace Historia.Tests;

/// <summary>§13.1, §findings: <c>FetchAsync</c> with <see cref="GitFetchOptions.All"/> must route
/// each remote individually — a single untargeted "git fetch --all" can only be routed as
/// <see cref="RouteTransport.AsIs"/> (no remote name means no URL to classify), which silently
/// skips the active profile's credential injection and makes the SSH-Only setting a no-op.</summary>
public sealed class GitGatewayFetchAllTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    public GitGatewayFetchAllTests()
    {
        _repo = TestRepositoryBuilder.Create();
        _repo.WriteFile("f.txt", "x").Stage().Commit("seed");
        _repo.Git("remote", "add", "origin", "https://example.invalid/origin.git");
        _repo.Git("remote", "add", "upstream", "https://example.invalid/upstream.git");
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    private GitGateway MakeGateway(RepositoryContext ctx, IGitCliService cli) => new(
        ctx,
        _host.Provider.GetRequiredService<LibGit2Service>(),
        cli,
        _host.Provider.GetRequiredService<IRepoLayoutService>(),
        _host.Provider.GetRequiredService<ICredentialVaultService>());

    [Fact]
    public async Task Fetch_with_All_issues_one_targeted_fetch_per_remote_never_an_untargeted_all()
    {
        await using var ctx = _host.Open(_repo.Path);
        var cli = new RecordingGitCliService();
        var gw = MakeGateway(ctx, cli);

        await gw.FetchAsync("origin", new GitFetchOptions { All = true }, progress: null);

        var fetches = cli.Requests.Where(r => r.Arguments.Contains("fetch")).ToList();

        Assert.Equal(2, fetches.Count);
        Assert.Contains(fetches, r => r.Arguments.Contains("origin"));
        Assert.Contains(fetches, r => r.Arguments.Contains("upstream"));
        Assert.DoesNotContain(fetches, r => r.Arguments.Contains("--all"));
    }

    [Fact]
    public async Task Fetch_with_All_still_applies_prune_and_tags_per_remote()
    {
        await using var ctx = _host.Open(_repo.Path);
        var cli = new RecordingGitCliService();
        var gw = MakeGateway(ctx, cli);

        await gw.FetchAsync("origin", new GitFetchOptions { All = true, Prune = true, Tags = true }, progress: null);

        var fetches = cli.Requests.Where(r => r.Arguments.Contains("fetch")).ToList();
        Assert.Equal(2, fetches.Count);
        Assert.All(fetches, r =>
        {
            Assert.Contains("--prune", r.Arguments);
            Assert.Contains("--tags", r.Arguments);
        });
    }

    [Fact]
    public async Task Fetch_with_All_reports_the_last_failure_but_still_attempts_every_remote()
    {
        await using var ctx = _host.Open(_repo.Path);
        var cli = new FailingOnRemoteGitCliService("upstream");
        var gw = MakeGateway(ctx, cli);

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(
            () => gw.FetchAsync("origin", new GitFetchOptions { All = true }, progress: null));

        Assert.Equal(GitErrorKind.AuthenticationFailed, ex.Kind);
        // Both remotes were attempted despite upstream failing (upstream may appear twice —
        // https then its ssh fallback, both of which this fake fails).
        var distinct = cli.AttemptedRemotes.Distinct().ToList();
        Assert.Equal(2, distinct.Count);
        Assert.Contains("origin", distinct);
        Assert.Contains("upstream", distinct);
    }

    /// <summary>Fails every fetch naming <paramref name="failRemote"/> (via its <c>-c
    /// remote.&lt;name&gt;.url=</c> rewrite or its bare remote argument) with an
    /// AuthenticationFailed-classified error; every other fetch succeeds.</summary>
    private sealed class FailingOnRemoteGitCliService(string failRemote) : IGitCliService
    {
        public List<string> AttemptedRemotes { get; } = [];

        public bool IsGitAvailable => true;

        public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
            => RunAsync(request, null, null, ct);

        public Task<GitCliResult> RunAsync(
            GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
        {
            var fetchIndex = request.Arguments.ToList().IndexOf("fetch");
            if (fetchIndex < 0)
                return Task.FromResult(new GitCliResult(0, "", "", GitErrorKind.None, TimeSpan.Zero));

            var remote = request.Arguments[fetchIndex + 2]; // "fetch", "--progress", "<remote>"
            AttemptedRemotes.Add(remote);

            return Task.FromResult(remote == failRemote
                ? new GitCliResult(128, "", "fatal: Authentication failed for 'https://example.invalid/'",
                    GitErrorKind.AuthenticationFailed, TimeSpan.Zero)
                : new GitCliResult(0, "", "", GitErrorKind.None, TimeSpan.Zero));
        }
    }
}
