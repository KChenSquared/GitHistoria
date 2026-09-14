namespace Historia.Services.Git;

/// <summary>
/// <inheritdoc cref="IGitGateway"/>
/// Bound to one <see cref="RepositoryContext"/>; routes each operation to LibGit2Sharp or the
/// Git CLI per §6.4.1. LibGit2Sharp is <b>never</b> used for a network operation.
/// </summary>
public sealed class GitGateway(
    RepositoryContext context,
    LibGit2Service lib,
    IGitCliService cli,
    IRepoLayoutService repoLayout,
    ICredentialVaultService credentialVault) : IGitGateway
{
    public string RepositoryPath => context.RepositoryPath;

    // ── Reads ──

    public Task<RepositoryInfo> GetRepositoryInfoAsync(CancellationToken ct = default)
        => context.WithRepositoryAsync(lib.ProjectInfo, ct);

    public Task<RepositoryStatus> GetStatusAsync(CancellationToken ct = default)
        => context.WithRepositoryAsync(lib.ProjectStatus, ct);

    public Task<IReadOnlyList<CommitSummary>> GetCommitsAsync(CommitQuery query, CancellationToken ct = default)
        => context.WithRepositoryAsync(r => lib.ProjectCommits(r, query), ct);

    public Task<CommitDetail> GetCommitDetailAsync(ObjectId id, CancellationToken ct = default)
        => context.WithRepositoryAsync(r => lib.ProjectCommitDetail(r, id), ct);

    public Task<IReadOnlyList<FileStatusEntry>> GetCommitFilesAsync(ObjectId id, int parentIndex, CancellationToken ct = default)
        => context.WithRepositoryAsync(r => lib.ProjectCommitFiles(r, id, parentIndex), ct);

    public Task<IReadOnlyList<BranchInfo>> GetBranchesAsync(CancellationToken ct = default)
        => context.WithRepositoryAsync(lib.ProjectBranches, ct);

    public Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken ct = default)
        => context.WithRepositoryAsync(lib.ProjectTags, ct);

    public Task<IReadOnlyList<RemoteInfo>> GetRemotesAsync(CancellationToken ct = default)
        => context.WithRepositoryAsync(lib.ProjectRemotes, ct);

    public Task<FileDiff> GetFileDiffAsync(DiffRequest request, CancellationToken ct = default)
        => context.WithRepositoryAsync(r => lib.ProjectDiff(r, request), ct);

    public Task<AheadBehind?> GetAheadBehindAsync(string branchCanonicalName, CancellationToken ct = default)
        => context.WithRepositoryAsync(r => lib.CalculateAheadBehind(r, branchCanonicalName), ct);

    public async Task<(string? Name, string? Email)> GetConfiguredIdentityAsync(CancellationToken ct = default)
    {
        var name = await ReadConfigAsync("--local", "user.name", ct).ConfigureAwait(false);
        var email = await ReadConfigAsync("--local", "user.email", ct).ConfigureAwait(false);
        return (name, email);
    }

    public async Task<(string? Name, string? Email)> GetGlobalIdentityAsync(CancellationToken ct = default)
    {
        var name = await ReadConfigAsync("--global", "user.name", ct).ConfigureAwait(false);
        var email = await ReadConfigAsync("--global", "user.email", ct).ConfigureAwait(false);
        return (name, email);
    }

    public async Task<bool> GetGpgSignConfiguredAsync(CancellationToken ct = default)
    {
        if (!cli.IsGitAvailable)
        {
            var v = await context.WithRepositoryAsync(r => r.Config.Get<bool?>("commit.gpgsign")?.Value, ct).ConfigureAwait(false);
            return v == true;
        }

        // No scope flag → git resolves local over global on its own.
        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "--get", "commit.gpgsign"],
            WorkingDirectory = context.RepositoryPath,
            Timeout = TimeSpan.FromSeconds(10),
        }, ct).ConfigureAwait(false);

        return result.ExitCode == 0
            && string.Equals(result.StandardOutput.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> ReadConfigAsync(string scope, string key, CancellationToken ct)
    {
        if (!cli.IsGitAvailable)
        {
            var level = scope == "--global" ? ConfigurationLevel.Global : ConfigurationLevel.Local;
            return await context.WithRepositoryAsync(r => r.Config.Get<string>(key, level)?.Value, ct).ConfigureAwait(false);
        }

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", scope, "--get", key],
            WorkingDirectory = context.RepositoryPath,
            Timeout = TimeSpan.FromSeconds(10),
        }, ct).ConfigureAwait(false);

        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardOutput.Trim()
            : null;
    }

    // ── Working tree mutations ──

    public async Task StageAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        await context.WithRepositoryAsync(r => lib.Stage(r, paths), ct).ConfigureAwait(false);
    }

    public async Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        var isUnborn = await context.WithRepositoryAsync(r => r.Info.IsHeadUnborn, ct).ConfigureAwait(false);
        if (isUnborn)
        {
            // No HEAD to reset against — fall back to `git rm --cached` (§6.9.1).
            var args = new List<string> { "rm", "--cached", "--" };
            args.AddRange(paths);
            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = args,
                WorkingDirectory = context.RepositoryPath,
            }, ct).ConfigureAwait(false);
            if (!result.IsSuccess)
                throw HistoriaGitException.FromCli(result, "Unstage");
            return;
        }

        await context.WithRepositoryAsync(r => lib.Unstage(r, paths), ct).ConfigureAwait(false);
    }

    public async Task DiscardAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        await context.WithRepositoryAsync(r => lib.DiscardWorkingTree(r, paths), ct).ConfigureAwait(false);
    }

    public async Task<ObjectId> CommitAsync(CommitRequest request, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        // Signed commits and repos with pre-commit / commit-msg hooks route through the CLI —
        // LibGit2Sharp invokes neither the GPG agent nor Git hooks (§6.4.1, §6.9.2).
        var hasHooks = await context.WithRepositoryAsync(
            r => LibGit2Service.RepoHasCommitHooks(r.Info.Path), ct).ConfigureAwait(false);

        if (request.Sign || hasHooks)
        {
            var args = new List<string> { "commit", "-m", request.Message };
            if (request.Sign) args.Add("-S");
            if (request.AmendPreviousCommit) args.Add("--amend");
            if (request.AllowEmptyCommit) args.Add("--allow-empty");
            args.Add("--author");
            args.Add($"{request.AuthorName} <{request.AuthorEmail}>");

            var env = new Dictionary<string, string>
            {
                ["GIT_COMMITTER_NAME"] = request.CommitterName ?? request.AuthorName,
                ["GIT_COMMITTER_EMAIL"] = request.CommitterEmail ?? request.AuthorEmail,
            };

            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = args,
                WorkingDirectory = context.RepositoryPath,
                ExtraEnvironment = env,
            }, ct).ConfigureAwait(false);
            if (!result.IsSuccess)
                throw HistoriaGitException.FromCli(result, "Commit");

            return await context.WithRepositoryAsync(r => r.Head.Tip!.Id, ct).ConfigureAwait(false);
        }

        return await context.WithRepositoryAsync(r => lib.Commit(r, request), ct).ConfigureAwait(false);
    }

    public async Task MergeAsync(string refish, bool noFastForward, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        var args = new List<string> { "merge" };
        if (noFastForward) args.Add("--no-ff");
        args.Add(refish);

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = args,
            WorkingDirectory = context.RepositoryPath,
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess && result.ErrorKind != GitErrorKind.MergeConflict)
        {
            // A plain conflicted merge prints "CONFLICT (…)" / "Automatic merge failed" to
            // stdout, not stderr, so the stderr-only classifier tags it Unknown. Re-check the
            // combined output before treating it as a hard failure — a conflict is an expected
            // outcome the conflict resolver handles, not an exception.
            if (GitErrorClassifier.Classify(result.ExitCode,
                    $"{result.StandardOutput}\n{result.StandardError}") == GitErrorKind.MergeConflict)
                return;
            throw HistoriaGitException.FromCli(result, "Merge");
        }
    }

    public async Task ResetAsync(string target, GitResetMode mode, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        var flag = mode switch
        {
            GitResetMode.Soft => "--soft",
            GitResetMode.Hard => "--hard",
            _ => "--mixed",
        };

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["reset", flag, target],
            WorkingDirectory = context.RepositoryPath,
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "Reset");
    }

    public async Task<int> CountCommitsLeavingAsync(string target, CancellationToken ct = default)
    {
        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["rev-list", "--count", $"{target}..HEAD"],
            WorkingDirectory = context.RepositoryPath,
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "Count commits");

        return int.TryParse(result.StandardOutput.Trim(), out var n) ? n : 0;
    }

    // ── Ref mutations ──

    public async Task CheckoutAsync(string refName, GitCheckoutOptions options, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        await context.WithRepositoryAsync(r => lib.Checkout(r, refName, options), ct).ConfigureAwait(false);
    }

    public async Task<BranchInfo> CreateBranchAsync(string name, string startPoint, bool checkout, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        return await context.WithRepositoryAsync(r => lib.CreateBranch(r, name, startPoint, checkout), ct).ConfigureAwait(false);
    }

    public async Task DeleteBranchAsync(string name, bool force, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        await context.WithRepositoryAsync(r => lib.DeleteBranch(r, name, force), ct).ConfigureAwait(false);
    }

    public async Task RenameBranchAsync(string oldName, string newName, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        await context.WithRepositoryAsync(r => lib.RenameBranch(r, oldName, newName), ct).ConfigureAwait(false);
    }

    public async Task SetUpstreamAsync(string branchName, string upstreamRef, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["branch", $"--set-upstream-to={upstreamRef}", branchName],
            WorkingDirectory = context.RepositoryPath,
        }, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "Set upstream");
    }

    public async Task<TagInfo> CreateTagAsync(string name, string target, string? message, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        return await context.WithRepositoryAsync(r => lib.CreateTag(r, name, target, message,
            message is null ? null : BuildTagger(r)), ct).ConfigureAwait(false);
    }

    public async Task DeleteTagAsync(string name, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        await context.WithRepositoryAsync(r => lib.DeleteTag(r, name), ct).ConfigureAwait(false);
    }

    // ── Remotes ──

    public async Task AddRemoteAsync(string name, string url, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        await context.WithRepositoryAsync(r => lib.AddRemote(r, name, url), ct).ConfigureAwait(false);
    }

    public async Task RemoveRemoteAsync(string name, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        await context.WithRepositoryAsync(r => lib.RemoveRemote(r, name), ct).ConfigureAwait(false);
    }

    public async Task SetRemoteUrlAsync(string name, string url, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        await context.WithRepositoryAsync(r => lib.SetRemoteUrl(r, name, url), ct).ConfigureAwait(false);
    }

    // ── Network (always CLI) ──

    public async Task FetchAsync(string remote, GitFetchOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default)
    {
        if (options.All)
        {
            // Fetch every remote individually rather than a single untargeted "git fetch --all":
            // RunNetworkAsync can only route an untargeted fetch as RouteTransport.AsIs (no
            // remote name means no remote URL to classify), which silently skips the active
            // profile's PAT/SSH key injection and — critically — makes the SSH-Only setting a
            // no-op, since the transport plan was decided before it could apply (§findings). One
            // remote failing to route doesn't stop the others; the last failure (if any) is what
            // the caller sees, matching git's own "try every remote, report failures" behaviour.
            var remotes = await context.WithRepositoryAsync(lib.ProjectRemotes, ct).ConfigureAwait(false);
            HistoriaGitException? lastError = null;
            foreach (var r in remotes)
            {
                try
                {
                    await FetchAsync(r.Name, options with { All = false }, progress, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HistoriaGitException ex)
                {
                    lastError = ex;
                }
            }
            if (lastError is not null)
                throw lastError;
            return;
        }

        var args = new List<string> { "fetch", "--progress", remote };
        if (options.Prune) args.Add("--prune");
        if (options.Tags) args.Add("--tags");

        await RunNetworkAsync(args, "Fetch", remote, progress, ct, options.Background).ConfigureAwait(false);
    }

    public async Task PullAsync(string remote, string branch, GitPullOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default)
    {
        var args = new List<string> { "pull", "--progress" };
        args.Add(options.Rebase ? "--rebase" : "--no-rebase");
        if (options.Prune) args.Add("--prune");
        if (options.AllowUnrelatedHistories) args.Add("--allow-unrelated-histories");
        args.Add(remote);
        args.Add(branch);

        await RunNetworkAsync(args, "Pull", remote, progress, ct).ConfigureAwait(false);
    }

    public async Task PushAsync(string remote, PushSpec spec, IProgress<GitProgress>? progress, CancellationToken ct = default)
    {
        var args = new List<string> { "push", "--progress" };
        if (spec.SetUpstream) args.Add("-u");
        if (spec.ForceWithLease)
        {
            // Never plain --force (§6.13).
            args.Add("--force-with-lease");
            args.Add("--force-if-includes");
        }

        args.Add(remote);
        if (spec.PushTags)
            args.Add("--tags");
        else
            args.Add(spec.RefSpec);

        await RunNetworkAsync(args, "Push", remote, progress, ct).ConfigureAwait(false);
    }

    // §13.1: SSH connect timeout for a routing attempt so an SSH-unreachable host (port 22
    // blocked) fails fast instead of stalling on the OS TCP timeout before the fallback.
    private const int SshConnectTimeoutSeconds = 10;

    private async Task RunNetworkAsync(
        IReadOnlyList<string> args, string operation, string? remote,
        IProgress<GitProgress>? progress, CancellationToken ct, bool background = false)
    {
        string? remoteUrl = null;
        if (remote is not null)
        {
            var remotes = await context.WithRepositoryAsync(lib.ProjectRemotes, ct).ConfigureAwait(false);
            remoteUrl = remotes.FirstOrDefault(r => r.Name == remote)?.Url;
        }

        var urlKind = remoteUrl is null ? RemoteUrlKind.LocalOrOther : RemoteUrlConverter.Classify(remoteUrl);
        var sshOnly = repoLayout.Get(context.RepositoryPath).SshOnly;
        var plan = GitTransportRouting.Plan(urlKind, sshOnly);

        using var _ = context.BeginSelfOperation();

        HistoriaGitException? lastError = null;
        for (var i = 0; i < plan.Count; i++)
        {
            var transport = plan[i];
            var effectiveUrl = transport switch
            {
                RouteTransport.Ssh => RemoteUrlConverter.ToSsh(remoteUrl!).ConvertedUrl ?? remoteUrl!,
                RouteTransport.Https => RemoteUrlConverter.ToHttps(remoteUrl!).ConvertedUrl ?? remoteUrl!,
                _ => remoteUrl,
            };

            // Per-invocation only — the stored remote.<name>.url in .git/config is never rewritten (§13.1).
            var attemptArgs = remote is not null && effectiveUrl is not null && !string.Equals(effectiveUrl, remoteUrl, StringComparison.Ordinal)
                ? (IReadOnlyList<string>)new[] { "-c", $"remote.{remote}.url={effectiveUrl}" }.Concat(args).ToList()
                : args;

            var env = transport switch
            {
                RouteTransport.Https => await BuildCredentialEnvAsync(effectiveUrl, ct).ConfigureAwait(false),
                RouteTransport.Ssh => SshEnv(),
                _ => null, // AsIs — defer entirely to Git's own transport / credential resolution.
            };

            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = attemptArgs,
                WorkingDirectory = context.RepositoryPath,
                Timeout = Timeout.InfiniteTimeSpan, // Network ops are user-cancellable, not wall-clocked (§6.5.1).
                StreamOutput = true,
                ExtraEnvironment = env,
            }, progress, onStdoutLine: null, ct).ConfigureAwait(false);

            if (result.ErrorKind == GitErrorKind.Cancelled)
                throw new OperationCanceledException(ct);
            if (result.IsSuccess)
                return;

            lastError = HistoriaGitException.FromCli(result, operation);
        }

        // Every transport failed. Offer the Local-config fallback when a profile (not
        // *Local config*) is the active selection (§13.1) — never for a background auto-fetch,
        // whose caller has no user to prompt (§12.1).
        if (context.ActiveProfile is not null && !background)
        {
            throw new HistoriaGitException(lastError!.Kind, lastError.Message, lastError.Details)
            {
                FallbackToLocalConfigAvailable = true,
            };
        }

        throw lastError!;
    }

    private IReadOnlyDictionary<string, string> SshEnv()
    {
        var cmd = $"ssh -o ConnectTimeout={SshConnectTimeoutSeconds}";
        var env = new Dictionary<string, string>();
        if (context.ActiveProfile?.SshKeyPath is { } key && !string.IsNullOrWhiteSpace(key))
        {
            env["HISTORIA_SSH_KEY"] = key;
            cmd = $"ssh -i \"$HISTORIA_SSH_KEY\" -o IdentitiesOnly=yes -o ConnectTimeout={SshConnectTimeoutSeconds}";
        }
        env["GIT_SSH_COMMAND"] = cmd;
        return env;
    }

    /// <summary>
    /// Env for the askpass helper when the remote is HTTPS and the active profile carries a PAT
    /// for that host. username = login (fallback "git"), password = the token — the form
    /// accepted by GitHub, GitLab, and Bitbucket app-passwords over HTTPS. Returns null (Git
    /// falls back to its own credential helper) when there is no usable token.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>?> BuildCredentialEnvAsync(string? remoteUrl, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(remoteUrl)
            || context.ActiveProfile is not { } profile
            || !Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return null; // a token is only ever injected over TLS, never plain http:// or git://

        var tokenRef = TokenRefForHost(profile, uri.Host);
        if (string.IsNullOrEmpty(tokenRef))
            return null;

        string? token;
        try { token = await credentialVault.RetrieveTokenAsync(tokenRef, ct).ConfigureAwait(false); }
        catch { return null; }

        if (string.IsNullOrEmpty(token))
            return null;

        return new Dictionary<string, string>
        {
            ["HISTORIA_ASKPASS_USERNAME"] = string.IsNullOrWhiteSpace(profile.GitUserName) ? "git" : profile.GitUserName,
            ["HISTORIA_ASKPASS_PASSWORD"] = token,
            // Pins the answer to the host the token was actually issued for — the askpass
            // helper refuses to answer a prompt naming any other host. uri.Host here is the
            // host TokenRefForHost just matched.
            ["HISTORIA_ASKPASS_HOST"] = uri.Host,
        };
    }

    /// <summary>
    /// The profile token to use for <paramref name="host"/>, or null. Matched by exact host (or
    /// a subdomain) of the three hosted providers — never a substring test, so
    /// <c>github.evil.com</c> does not match <c>github.com</c> — and never a "whichever token we
    /// hold" fallback, so a self-hosted or unknown host gets no token and Git falls back to its
    /// own credential helper.
    /// </summary>
    private static string? TokenRefForHost(GitProfile p, string host)
    {
        if (HostMatches(host, "github.com")) return p.GitHubTokenRef;
        if (HostMatches(host, "gitlab.com")) return p.GitLabTokenRef;
        if (HostMatches(host, "bitbucket.org")) return p.BitbucketTokenRef;
        return null;
    }

    private static bool HostMatches(string host, string domain) =>
        string.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    private static Signature BuildTagger(Repository repo)
    {
        var name = repo.Config.Get<string>("user.name")?.Value ?? "Historia";
        var email = repo.Config.Get<string>("user.email")?.Value ?? "Historia@localhost";
        return new Signature(name, email, DateTimeOffset.Now);
    }
}
