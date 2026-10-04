namespace Historia.Tests;

/// <summary><c>RunNetworkAsync</c> tries the other transport only after a transport failure,
/// surfaces the error from the attempt that reached the remote (else the primary one), and
/// retries an SSH alias over HTTPS against the alias's real host.</summary>
[Trait("tier", "git")]
public sealed class GitGatewayTransportFallbackTests : IAsyncDisposable
{
    private const string NonFastForward =
        " ! [rejected]        main -> main (fetch first)\nerror: failed to push some refs to 'x'";
    private const string Declined =
        "remote: error: File big.bin is 361.72 MB; this exceeds GitHub's file size limit of 100.00 MB\n"
        + " ! [remote rejected] main -> main (pre-receive hook declined)\nerror: failed to push some refs to 'x'";
    private const string Unreachable = "ssh: connect to host example.invalid port 22: Connection timed out\nfatal: Could not read from remote repository.";
    private const string AuthFailed = "fatal: Authentication failed for 'https://example.invalid/o/r.git/'";

    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _repos) r.Dispose();
        await _host.DisposeAsync();
    }

    private RepositoryContext Open(string remoteUrl)
    {
        var repo = TestRepositoryBuilder.FromTemplate("gateway-transport-" + Math.Abs(StringComparer.Ordinal.GetHashCode(remoteUrl)), r =>
        {
            r.WriteFile("f.txt", "x").Stage().Commit("seed");
            r.Git("remote", "add", "origin", remoteUrl);
        });
        _repos.Add(repo);
        return _host.Open(repo.Path);
    }

    private GitGateway Gateway(RepositoryContext ctx, IGitCliService cli, ISshHostResolver hosts, ICredentialVaultService? vault = null) => new(
        ctx,
        cli,
        _host.Provider.GetRequiredService<IRepoLayoutService>(),
        vault ?? _host.Provider.GetRequiredService<ICredentialVaultService>(),
        hosts);

    private static GitCliResult Fail(string stderr) =>
        new(1, "", stderr, GitErrorClassifier.Classify(1, stderr), TimeSpan.Zero);

    private static readonly PushSpec Push = new() { RefSpec = "main" };

    private static GitCliResult Ok() => new(0, "", "", GitErrorKind.None, TimeSpan.Zero);

    [Fact]
    public async Task Declined_push_over_ssh_is_not_retried()
    {
        await using var ctx = Open("git@example.invalid:o/r.git");
        var cli = new ScriptedCli(Fail(Declined), Ok());
        var hosts = new FakeHosts();

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() => Gateway(ctx, cli, hosts).PushAsync("origin", Push, null));

        Assert.Single(cli.NetworkCalls);
        Assert.Equal(GitErrorKind.RemoteDeclined, ex.Kind);
        Assert.Equal(0, hosts.Calls);
    }

    [Fact]
    public async Task Non_fast_forward_over_ssh_is_not_retried()
    {
        await using var ctx = Open("git@example.invalid:o/r.git");
        var cli = new ScriptedCli(Fail(NonFastForward), Ok());

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() => Gateway(ctx, cli, new FakeHosts()).PushAsync("origin", Push, null));

        Assert.Single(cli.NetworkCalls);
        Assert.Equal(GitErrorKind.RemoteRejected, ex.Kind);
    }

    [Fact]
    public async Task Pull_conflict_over_ssh_is_not_retried()
    {
        await using var ctx = Open("git@example.invalid:o/r.git");
        var cli = new ScriptedCli(
            Fail("CONFLICT (content): Merge conflict in f.txt\nAutomatic merge failed; fix conflicts and then commit the result."),
            Fail("error: Pulling is not possible because you have unmerged files."));

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(
            () => Gateway(ctx, cli, new FakeHosts()).PullAsync("origin", "main", new GitPullOptions(), null));

        Assert.Single(cli.NetworkCalls);
        Assert.Equal(GitErrorKind.MergeConflict, ex.Kind);
    }

    [Fact]
    public async Task A_rejection_on_the_fallback_wins_over_the_primary_transport_failure()
    {
        await using var ctx = Open("git@example.invalid:o/r.git");
        var cli = new ScriptedCli(Fail(Unreachable), Fail(NonFastForward));

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() => Gateway(ctx, cli, new FakeHosts()).PushAsync("origin", Push, null));

        Assert.Equal(2, cli.NetworkCalls.Count);
        Assert.Equal(GitErrorKind.RemoteRejected, ex.Kind);
        Assert.StartsWith("HTTPS attempt (https://example.invalid/o/r.git):", ex.Details);
    }

    [Fact]
    public async Task Two_transport_failures_surface_the_primary_and_list_both_attempts()
    {
        await using var ctx = Open("git@example.invalid:o/r.git");
        var cli = new ScriptedCli(Fail(Unreachable), Fail(AuthFailed));

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() => Gateway(ctx, cli, new FakeHosts()).PushAsync("origin", Push, null));

        Assert.Equal(GitErrorKind.NetworkUnreachable, ex.Kind);
        var ssh = ex.Details!.IndexOf("SSH attempt (git@example.invalid:o/r.git):", StringComparison.Ordinal);
        var https = ex.Details.IndexOf("HTTPS attempt (https://example.invalid/o/r.git):", StringComparison.Ordinal);
        Assert.True(ssh >= 0 && https > ssh, ex.Details);
        Assert.Contains("Authentication failed", ex.Details);
    }

    [Fact]
    public async Task An_ssh_alias_falls_back_to_https_on_its_real_host()
    {
        await using var ctx = Open("git@github-alias:o/r.git");
        var cli = new ScriptedCli(Fail(Unreachable), Fail(AuthFailed));
        var hosts = new FakeHosts { ["github-alias"] = "github.com" };

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() => Gateway(ctx, cli, hosts).PushAsync("origin", Push, null));

        Assert.Equal(2, cli.NetworkCalls.Count);
        var retry = cli.NetworkCalls[1].Arguments;
        Assert.Contains("remote.origin.url=https://github.com/o/r.git", retry);
        Assert.DoesNotContain(retry, a => a.Contains("https://github-alias", StringComparison.Ordinal));
        Assert.Contains("Turn on SSH Only", ex.Details);
        Assert.Contains("Retried over HTTPS as github.com", ex.Details);
    }

    [Fact]
    public async Task An_ssh_alias_retry_uses_the_profile_token_for_the_real_host()
    {
        await using var ctx = Open("git@github-alias:o/r.git");
        var vault = new FakeVault();
        await vault.StoreTokenAsync("gh-token", "secret");
        ctx.State.SetActiveProfile(new GitProfile
        {
            Id = Guid.NewGuid(),
            DisplayName = "Work",
            GitUserName = "me",
            GitUserEmail = "me@example.invalid",
            GitHubTokenRef = "gh-token",
        });
        var cli = new ScriptedCli(Fail(Unreachable), Fail(AuthFailed));

        await Assert.ThrowsAsync<HistoriaGitException>(
            () => Gateway(ctx, cli, new FakeHosts { ["github-alias"] = "github.com" }, vault).PushAsync("origin", Push, null));

        var env = cli.NetworkCalls[1].ExtraEnvironment;
        Assert.NotNull(env);
        Assert.Equal("github.com", env!["HISTORIA_ASKPASS_HOST"]);
    }

    [Fact]
    public async Task A_background_fetch_never_takes_the_alias_https_fallback()
    {
        await using var ctx = Open("git@github-alias:o/r.git");
        var cli = new ScriptedCli(Fail(Unreachable), Ok());

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() => Gateway(ctx, cli, new FakeHosts { ["github-alias"] = "github.com" })
            .FetchAsync("origin", new GitFetchOptions { Background = true }, null));

        Assert.Single(cli.NetworkCalls);
        Assert.Equal(GitErrorKind.NetworkUnreachable, ex.Kind);
    }

    [Fact]
    public async Task Ssh_only_makes_one_attempt_and_never_resolves_the_host()
    {
        await using var ctx = Open("git@github-alias:o/r.git");
        await _host.Provider.GetRequiredService<IRepoLayoutService>().UpdateAsync(ctx.RepositoryPath, l => l with { SshOnly = true });
        var cli = new ScriptedCli(Fail(Unreachable), Ok());
        var hosts = new FakeHosts { ["github-alias"] = "github.com" };

        await Assert.ThrowsAsync<HistoriaGitException>(() => Gateway(ctx, cli, hosts).PushAsync("origin", Push, null));

        Assert.Single(cli.NetworkCalls);
        Assert.Equal(0, hosts.Calls);
    }

    [Fact]
    public async Task An_unresolvable_host_falls_back_with_the_host_as_written()
    {
        await using var ctx = Open("git@github-alias:o/r.git");
        var cli = new ScriptedCli(Fail(Unreachable), Fail(AuthFailed));
        var hosts = new FakeHosts();

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() => Gateway(ctx, cli, hosts).PushAsync("origin", Push, null));

        Assert.Equal(1, hosts.Calls);
        Assert.Contains("remote.origin.url=https://github-alias/o/r.git", cli.NetworkCalls[1].Arguments);
        Assert.DoesNotContain("Turn on SSH Only", ex.Details);
    }

    [Fact]
    public async Task Success_on_the_first_attempt_never_resolves_the_host()
    {
        await using var ctx = Open("git@github-alias:o/r.git");
        var cli = new ScriptedCli(Ok());
        var hosts = new FakeHosts { ["github-alias"] = "github.com" };

        await Gateway(ctx, cli, hosts).PushAsync("origin", Push, null);

        Assert.Single(cli.NetworkCalls);
        Assert.Equal(0, hosts.Calls);
    }

    [Fact]
    public async Task An_insteadOf_rewrite_back_to_the_failed_ssh_url_is_not_retried()
    {
        await using var ctx = Open("git@github-alias:o/r.git");
        var cli = new ScriptedCli(Fail(Unreachable), Fail(Unreachable))
        {
            Rewrite = (url, _) => url.StartsWith("https://github.com/", StringComparison.Ordinal) ? "git@github-alias:o/r.git" : url,
        };

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(
            () => Gateway(ctx, cli, new FakeHosts { ["github-alias"] = "github.com" }).PushAsync("origin", Push, null));

        Assert.Single(cli.NetworkCalls);
        Assert.Equal(GitErrorKind.NetworkUnreachable, ex.Kind);
        Assert.DoesNotContain("Retry", ex.Details);
    }

    [Fact]
    public async Task A_non_alias_fallback_rewritten_back_to_ssh_is_not_retried()
    {
        await using var ctx = Open("git@example.invalid:o/r.git");
        var cli = new ScriptedCli(Fail(Unreachable), Fail(Unreachable))
        {
            Rewrite = (url, _) => url == "https://example.invalid/o/r.git" ? "git@example.invalid:o/r.git" : url,
        };

        await Assert.ThrowsAsync<HistoriaGitException>(() => Gateway(ctx, cli, new FakeHosts()).PushAsync("origin", Push, null));

        Assert.Single(cli.NetworkCalls);
    }

    [Fact]
    public async Task A_stored_ssh_pushurl_is_not_pushed_to_twice()
    {
        await using var ctx = Open("git@example.invalid:o/r.git");
        var cli = new ScriptedCli(Fail(Unreachable), Fail(Unreachable))
        {
            Rewrite = (url, push) => push ? "git@stored.invalid:o/r.git" : url,
        };

        await Assert.ThrowsAsync<HistoriaGitException>(() => Gateway(ctx, cli, new FakeHosts()).PushAsync("origin", Push, null));

        Assert.Single(cli.NetworkCalls);
    }

    [Fact]
    public async Task A_rewrite_to_another_ssh_url_is_retried_with_the_ssh_environment()
    {
        await using var ctx = Open("git@github-alias:o/r.git");
        var cli = new ScriptedCli(Fail(Unreachable), Fail(Unreachable))
        {
            Rewrite = (url, _) => url.StartsWith("https://github.com/", StringComparison.Ordinal) ? "git@other.invalid:o/r.git" : url,
        };

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(
            () => Gateway(ctx, cli, new FakeHosts { ["github-alias"] = "github.com" }).PushAsync("origin", Push, null));

        Assert.Equal(2, cli.NetworkCalls.Count);
        var env = cli.NetworkCalls[1].ExtraEnvironment;
        Assert.NotNull(env);
        Assert.Contains("ConnectTimeout=", env!["GIT_SSH_COMMAND"]);
        Assert.False(env.ContainsKey("HISTORIA_ASKPASS_HOST"));
        Assert.Contains("Retry attempt (git@other.invalid:o/r.git):", ex.Details);
        Assert.Contains("sends https://github.com/o/r.git there", ex.Details);
        Assert.DoesNotContain("Retried over HTTPS", ex.Details);
    }

    [Fact]
    public async Task A_push_asks_git_for_its_push_url_and_a_fetch_for_its_fetch_url()
    {
        await using var ctx = Open("git@example.invalid:o/r.git");
        var pushCli = new ScriptedCli(Fail(Unreachable), Fail(AuthFailed));
        var fetchCli = new ScriptedCli(Fail(Unreachable), Fail(AuthFailed));

        await Assert.ThrowsAsync<HistoriaGitException>(() => Gateway(ctx, pushCli, new FakeHosts()).PushAsync("origin", Push, null));
        await Assert.ThrowsAsync<HistoriaGitException>(
            () => Gateway(ctx, fetchCli, new FakeHosts()).FetchAsync("origin", new GitFetchOptions(), null));

        Assert.NotEmpty(pushCli.GetUrlCalls);
        Assert.All(pushCli.GetUrlCalls, c => Assert.True(c.Push));
        Assert.NotEmpty(fetchCli.GetUrlCalls);
        Assert.All(fetchCli.GetUrlCalls, c => Assert.False(c.Push));
    }

    [Fact]
    public async Task The_surfaced_error_is_not_logged_again_on_top_of_each_attempt()
    {
        using var dir = new StateDir();
        // Flow-local: the process-wide log would also capture every test running in parallel.
        using var session = AppLog.StartSessionForTesting(dir.Path);
        await using var ctx = Open("git@example.invalid:o/r.git");
        var cli = new ScriptedCli(Fail(Unreachable), Fail(AuthFailed));

        await Assert.ThrowsAsync<HistoriaGitException>(() => Gateway(ctx, cli, new FakeHosts()).PushAsync("origin", Push, null));

        session.Dispose(); // close the session writer before reading the file back
        var text = File.ReadAllText(session.SessionPath);
        Assert.Equal(2, Regex.Matches(text, Regex.Escape("[git-error]")).Count);
    }

    [Theory]
    [InlineData(Declined, false)]
    [InlineData(Unreachable, true)]
    public async Task Local_config_is_offered_only_for_a_profile_fixable_surfaced_error(string sshStderr, bool offered)
    {
        await using var ctx = Open("git@example.invalid:o/r.git");
        ctx.State.SetActiveProfile(new GitProfile
        {
            Id = Guid.NewGuid(),
            DisplayName = "Work",
            GitUserName = "me",
            GitUserEmail = "me@example.invalid",
        });
        var cli = new ScriptedCli(Fail(sshStderr), Fail(AuthFailed));

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() => Gateway(ctx, cli, new FakeHosts()).PushAsync("origin", Push, null));

        Assert.Equal(offered, ex.FallbackToLocalConfigAvailable);
    }

    /// <summary>Answers each network invocation (push / pull / fetch) with the next scripted
    /// result; answers <c>remote get-url [--push]</c> with <see cref="Rewrite"/> applied to the
    /// override URL, or with the override URL unchanged.</summary>
    private sealed class ScriptedCli(params GitCliResult[] results) : IGitCliService
    {
        private int _next;

        public List<GitCliRequest> NetworkCalls { get; } = [];
        public List<(string Url, bool Push)> GetUrlCalls { get; } = [];
        public Func<string, bool, string>? Rewrite { get; init; }
        public bool IsGitAvailable => true;

        public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
            => RunAsync(request, null, null, ct);

        public Task<GitCliResult> RunAsync(
            GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
        {
            var args = request.Arguments;
            if (args.Contains("remote") && args.Contains("get-url"))
            {
                var url = args.Last(a => a.StartsWith("remote.", StringComparison.Ordinal) && !a.EndsWith('=')).Split('=', 2)[1];
                var push = args.Contains("--push");
                GetUrlCalls.Add((url, push));
                var contacted = Rewrite?.Invoke(url, push) ?? url;
                return Task.FromResult(new GitCliResult(0, contacted + "\n", "", GitErrorKind.None, TimeSpan.Zero));
            }
            if (!args.Any(a => a is "push" or "pull" or "fetch"))
                return Task.FromResult(Ok());

            NetworkCalls.Add(request);
            var result = _next < results.Length ? results[_next] : Ok();
            _next++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeHosts : ISshHostResolver
    {
        private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

        public int Calls { get; private set; }

        public string this[string host] { set => _map[host] = value; }

        public Task<string?> ResolveHostNameAsync(string host, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_map.TryGetValue(host, out var name) ? name : null);
        }
    }
}
