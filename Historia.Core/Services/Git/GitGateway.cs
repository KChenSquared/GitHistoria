namespace Historia.Services.Git;

/// <summary>
/// <inheritdoc cref="IGitGateway"/>
/// Bound to one <see cref="RepositoryContext"/>; routes each operation to the Git CLI.
/// </summary>
public sealed partial class GitGateway(
    RepositoryContext context,
    IGitCliService cli,
    IRepoLayoutService repoLayout,
    ICredentialVaultService credentialVault) : IGitGateway
{
    public string RepositoryPath => context.RepositoryPath;

    public async Task<RepositoryStatus> GetStatusAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (context.IsBare)
            return RepositoryStatus.Empty with { ComputedAt = DateTimeOffset.Now };

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["status", "--porcelain=v2", "-z", "--branch", "--untracked-files=all", "--renames"],
            WorkingDirectory = context.RepositoryPath,
            Timeout = Timeout.InfiniteTimeSpan,
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            ct.ThrowIfCancellationRequested();
            throw HistoriaGitException.FromCli(result, "Status");
        }

        var parsed = GitStatusParser.Parse(result.StandardOutput);

        IReadOnlyDictionary<string, string> untrackedHashes = ImmutableDictionary<string, string>.Empty;
        if (GitStatusParser.ShouldHashUntracked(parsed))
        {
            var untrackedPaths = string.Join("\n", parsed.Untracked.Select(u => u.Path)) + "\n";
            var hashResult = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["hash-object", "--stdin-paths"],
                WorkingDirectory = context.RepositoryPath,
                StandardInput = untrackedPaths,
                Timeout = Timeout.InfiniteTimeSpan,
            }, ct).ConfigureAwait(false);

            if (hashResult.IsSuccess)
            {
                var lines = hashResult.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == parsed.Untracked.Count)
                {
                    var dict = new Dictionary<string, string>(parsed.Untracked.Count, StringComparer.Ordinal);
                    for (var i = 0; i < parsed.Untracked.Count; i++)
                    {
                        dict[parsed.Untracked[i].Path] = lines[i].Trim();
                    }
                    untrackedHashes = dict;
                }
                else
                {
                    PerfLog.Mark($"GetStatusAsync hash-object count mismatch: got {lines.Length}, expected {parsed.Untracked.Count}");
                }
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                PerfLog.Mark($"GetStatusAsync hash-object failed: exit={hashResult.ExitCode}, kind={hashResult.ErrorKind}");
            }
        }

        var status = GitStatusParser.PairExactMoves(parsed, untrackedHashes);
        return PopulateSizes(status, context.RepositoryPath);
    }

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

    public async Task MergeAsync(string refish, bool noFastForward, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        var args = new List<string> { "merge" };
        if (noFastForward) args.Add("--no-ff");
        args.Add(refish);

        var result = await context.WithWriteLockAsync(innerCt => cli.RunAsync(new GitCliRequest
        {
            Arguments = args,
            WorkingDirectory = context.RepositoryPath,
            // No wall clock: merge runs hooks, and killing it midway leaves a half-updated tree
            // and a stale index.lock. The caller's token still cancels it.
            Timeout = Timeout.InfiniteTimeSpan,
        }, innerCt), ct).ConfigureAwait(false);

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

        var result = await context.WithWriteLockAsync(innerCt => cli.RunAsync(new GitCliRequest
        {
            Arguments = ["reset", flag, target],
            WorkingDirectory = context.RepositoryPath,
            // No wall clock: a --hard reset killed midway leaves a half-updated tree and a
            // stale index.lock. The caller's token still cancels it.
            Timeout = Timeout.InfiniteTimeSpan,
        }, innerCt), ct).ConfigureAwait(false);

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

    public async Task SetUpstreamAsync(string branchName, string upstreamRef, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        var result = await context.WithWriteLockAsync(innerCt => cli.RunAsync(new GitCliRequest
        {
            Arguments = ["branch", $"--set-upstream-to={upstreamRef}", branchName],
            WorkingDirectory = context.RepositoryPath,
        }, innerCt), ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "Set upstream");
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
            // no-op, since the transport plan was decided before it could apply. One
            // remote failing to route doesn't stop the others; the last failure (if any) is what
            // the caller sees, matching git's own "try every remote, report failures" behaviour.
            var remotes = await context.RemoteConfig.GetRemotesAsync(ct).ConfigureAwait(false);
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

    // Deliberately not wrapped in WithWriteLockAsync: pull can raise an interactive askpass
    // credential prompt mid-process. Holding the write lock while the user sits at the prompt
    // would block all other git write operations on the repository. Any concurrency collision
    // with another write surfaces naturally as Git's own index lock error.
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
            // Never plain --force.
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

    public Task PruneRemoteAsync(string name, IProgress<GitProgress>? progress, CancellationToken ct = default)
        => RunNetworkAsync(["remote", "prune", name], "Prune", name, progress, ct);

    /// <summary>Prefixes <paramref name="args"/> with a per-invocation override that makes
    /// <paramref name="url"/> the remote's only URL. remote.&lt;name&gt;.url is multi-valued, so a
    /// bare <c>-c</c> value would be appended to the stored one: fetch and prune would keep using
    /// the stored URL, and push would go to both. The empty value first clears the list, which
    /// Git supports from 2.46 (<see cref="GitExecutableInfo.MinimumVersion"/>).</summary>
    internal static IReadOnlyList<string> WithRemoteUrlOverride(string remote, string url, IReadOnlyList<string> args)
    {
        var key = $"remote.{remote}.url";
        return new[] { "-c", $"{key}=", "-c", $"{key}={url}" }.Concat(args).ToList();
    }

    // SSH connect timeout for a routing attempt so an SSH-unreachable host (port 22
    // blocked) fails fast instead of stalling on the OS TCP timeout before the fallback.
    private const int SshConnectTimeoutSeconds = 10;

    private async Task RunNetworkAsync(
        IReadOnlyList<string> args, string operation, string? remote,
        IProgress<GitProgress>? progress, CancellationToken ct, bool background = false)
    {
        string? remoteUrl = null;
        if (remote is not null)
        {
            var remotes = await context.RemoteConfig.GetRemotesAsync(ct).ConfigureAwait(false);
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

            // Per-invocation only — the stored remote.<name>.url in .git/config is never rewritten.
            var attemptArgs = remote is not null && effectiveUrl is not null && !string.Equals(effectiveUrl, remoteUrl, StringComparison.Ordinal)
                ? WithRemoteUrlOverride(remote, effectiveUrl, args)
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
                Timeout = Timeout.InfiniteTimeSpan, // Network ops are user-cancellable, not wall-clocked.
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
        // *Local config*) is the active selection and the failure could be a credential or
        // reachability problem — never for a background auto-fetch, whose caller has no user
        // to prompt, and never for errors a different profile can't fix (missing ref, rejected
        // push, dirty tree…), which keep their own handling.
        if (context.ActiveProfile is not null && !background && IsProfileFixable(lastError!.Kind))
        {
            throw new HistoriaGitException(lastError!.Kind, lastError.Message, lastError.Details)
            {
                FallbackToLocalConfigAvailable = true,
            };
        }

        throw lastError!;
    }

    internal static bool IsProfileFixable(GitErrorKind kind) => kind is
        GitErrorKind.AuthenticationFailed or GitErrorKind.NetworkUnreachable
        or GitErrorKind.TimedOut or GitErrorKind.Unknown;

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

    private static RepositoryStatus PopulateSizes(RepositoryStatus status, string repoPath)
    {
        return status with
        {
            Staged = PopulateListSizes(status.Staged, repoPath),
            Unstaged = PopulateListSizes(status.Unstaged, repoPath),
            Untracked = PopulateListSizes(status.Untracked, repoPath),
            Conflicted = PopulateListSizes(status.Conflicted, repoPath),
            ComputedAt = DateTimeOffset.Now,
        };
    }

    private static IReadOnlyList<FileStatusEntry> PopulateListSizes(IReadOnlyList<FileStatusEntry> entries, string repoPath)
    {
        if (entries.Count == 0)
            return entries;

        var list = new List<FileStatusEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var size = WorkingTreeFileSize.GetOrZero(Path.Combine(repoPath, entry.Path));
            list.Add(entry.SizeBytes == size ? entry : entry with { SizeBytes = size });
        }
        return list;
    }
}
