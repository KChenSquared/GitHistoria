namespace Historia.Services.UI;

/// <summary>One network-op progress tick, or (with <c>Progress</c> null) "hide the progress UI" —
/// <c>Repo</c> lets a listener ignore ticks for a repository it isn't currently showing.</summary>
public sealed record RepoNetworkProgress(RepositoryContext Repo, GitProgress? Progress);

/// <summary>
/// Runs the Fetch / Pull / Push flow the shortcuts bar requests via <see cref="IRepoActionBus"/>,
/// against whichever repository is focused. A DI singleton, eagerly resolved once at startup
/// (<c>MainWindow</c>) so it subscribes to <see cref="IRepoActionBus.NetworkActionRequested"/> for
/// the app's whole lifetime — this logic used to live in <c>RepositoryView</c> itself, which meant
/// Fetch/Pull/Push silently did nothing whenever the detail pane showed anything other than the
/// repo view (Home, Settings, …), since that view (and its event subscription) had been disposed.
/// </summary>
public interface IRepoNetworkActionRunner
{
    event EventHandler<RepoNetworkProgress>? ProgressChanged;

    /// <summary>Cancels the in-flight operation, if any.</summary>
    void CancelCurrent();
}

/// <inheritdoc cref="IRepoNetworkActionRunner"/>
public sealed class RepoNetworkActionRunner : IRepoNetworkActionRunner
{
    private readonly IWorkspaceService _workspace;
    private readonly INotificationService _notifications;
    private readonly IDialogService _dialogs;
    private readonly IStashService _stashes;
    private readonly ITerminalSessionService _terminals;
    private readonly IProfileService _profiles;
    private readonly IRepoIdentityService _identity;
    private readonly IGraphLayoutService _graph;
    private readonly ISettingsService _settings;

    private bool _busy;
    private CancellationTokenSource? _cts;

    public event EventHandler<RepoNetworkProgress>? ProgressChanged;

    public RepoNetworkActionRunner(
        IRepoActionBus actionBus,
        IWorkspaceService workspace,
        INotificationService notifications,
        IDialogService dialogs,
        IStashService stashes,
        ITerminalSessionService terminals,
        IProfileService profiles,
        IRepoIdentityService identity,
        IGraphLayoutService graph,
        ISettingsService settings)
    {
        _workspace = workspace;
        _notifications = notifications;
        _dialogs = dialogs;
        _stashes = stashes;
        _terminals = terminals;
        _profiles = profiles;
        _identity = identity;
        _graph = graph;
        _settings = settings;

        actionBus.NetworkActionRequested += (_, request) => _ = RunAsync(request);
    }

    /// <summary>a tab whose selection went stale (profile deleted / default changed)
    /// re-resolves on the next Git operation, not eagerly.</summary>
    private async Task EnsureActiveProfileFreshAsync(RepositoryContext repo)
    {
        try
        {
            var key = repo.IdentityKey ?? await _identity.ResolveAsync(repo, repo.Lifetime);
            var fresh = _profiles.ResolveActiveProfile(key);
            var current = repo.State.ActiveProfile;
            if (fresh?.Id != current?.Id)
                repo.State.SetActiveProfile(fresh);
        }
        catch
        {
            // Resolution failure must not block the operation.
        }
    }

    private void RefreshCommitGraphAfterFetch(RepositoryContext repo, RepoNetworkAction op)
    {
        // A manual Fetch/Pull refreshes only the active tab's commit-graph (/ R3); its refs
        // cache and graph store already refresh through RefreshAllAsync -> RefsChanged.
        // Other tabs are never queued here.
        try { _graph.RefreshCommitGraphAfterFetch(repo); }
        catch { /* best effort */ }

        Historia.Diagnostics.PerfLog.Mark($"manual {op}: commit-graph refresh requested ({repo.DisplayName})");
    }

    internal static PushSpec BuildPushSpec(string branch, string? remoteBranch, bool targetHasUpstream)
    {
        var refspec = remoteBranch is not null && !string.Equals(branch, remoteBranch, StringComparison.Ordinal)
            ? $"{branch}:{remoteBranch}"
            : branch;
        return new PushSpec
        {
            RefSpec = refspec,
            SetUpstream = !targetHasUpstream,
        };
    }

    internal static bool TargetHasUpstream(IEnumerable<BranchInfo> branches, string branch)
    {
        var target = branches.FirstOrDefault(b => !b.IsRemote && string.Equals(b.Name, branch, StringComparison.Ordinal));
        return target?.UpstreamName is not null;
    }

    private async Task ExecOpAsync(RepositoryContext repo, RepoNetworkAction op, string remote, string? branch, string? remoteBranch,
        IProgress<GitProgress> progress, CancellationToken ct)
    {
        switch (op)
        {
            case RepoNetworkAction.Fetch:
                await repo.Gateway.FetchAsync(remote, GitFetchOptions.From(_settings.Current), progress, ct);
                RefreshCommitGraphAfterFetch(repo, op);
                _notifications.Publish(NotificationSeverity.Success, $"Fetched {remote}.");
                break;
            case RepoNetworkAction.Pull when branch is not null:
                if (!string.Equals(branch, repo.State.CurrentBranch?.Name, StringComparison.Ordinal))
                {
                    _notifications.Publish(NotificationSeverity.Warning, "Pull only runs on the checked-out branch.");
                    return;
                }
                var targetRemoteBranch = remoteBranch ?? branch;
                await repo.Gateway.PullAsync(remote, targetRemoteBranch, GitPullOptions.From(_settings.Current), progress, ct);
                await FollowUpFetchAsync(repo, "Pulled", remote, targetRemoteBranch, progress, ct);
                RefreshCommitGraphAfterFetch(repo, op);
                _notifications.Publish(NotificationSeverity.Success, $"Pulled {remote}/{targetRemoteBranch}.");
                break;
            case RepoNetworkAction.Push when branch is not null:
                var hasUpstream = TargetHasUpstream(repo.State.Branches, branch);
                var spec = BuildPushSpec(branch, remoteBranch, hasUpstream);
                await repo.Gateway.PushAsync(remote, spec, progress, ct);
                var pushedRemoteBranch = remoteBranch ?? branch;
                await FollowUpFetchAsync(repo, "Pushed", remote, pushedRemoteBranch, progress, ct);
                RefreshCommitGraphAfterFetch(repo, op);
                _notifications.Publish(NotificationSeverity.Success, $"Pushed to {remote}.");
                break;
            default:
                _notifications.Publish(NotificationSeverity.Warning, "No current branch to operate on.");
                break;
        }
    }

    /// <summary>R5: a pull or push leaves the remote-tracking refs current without a second user
    /// action — the follow-up fetch rides on the pull/push the user just triggered (Rule 0.2: no
    /// network call the user did not initiate), not a new timer or poll. A push that reached the
    /// remote must never be reported as failed because a fetch afterwards did not; the follow-up
    /// failing is a warning, not an error. A cancelled follow-up is a cancelled operation and
    /// propagates so the outer handler reports it the same way as a cancelled pull/push.</summary>
    private async Task FollowUpFetchAsync(RepositoryContext repo, string verb, string remote, string branch,
        IProgress<GitProgress> progress, CancellationToken ct)
    {
        try
        {
            await repo.Gateway.FetchAsync(remote, GitFetchOptions.From(_settings.Current), progress, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Warning,
                $"{verb} {remote}/{branch}, but the follow-up fetch failed.", ex.NotificationDetails());
        }
    }

    /// <summary>
    /// a push rejected as non-fast-forward means <c>remote/branch</c> has commits this
    /// local branch doesn't. Offer the three ways out in a modal the user can't lose behind
    /// later toasts — integrate then re-push (rebase or merge), or overwrite the remote
    /// (force-with-lease). A rebase/merge that stops in conflicts leaves the push for the user
    /// to retry once the resolver panel is dealt with.
    /// </summary>
    private async Task RecoverFromPushRejectionAsync(
        RepositoryContext repo, string remote, string branch, HistoriaGitException ex,
        IProgress<GitProgress> progress, CancellationToken ct)
    {
        if (!string.Equals(branch, repo.State.CurrentBranch?.Name, StringComparison.Ordinal))
        {
            _notifications.Publish(NotificationSeverity.Warning, ex.Message, ex.Details);
            return;
        }

        // Left→right: Fetch & merge (blue default) · Fetch & rebase · Force push (red) · Cancel.
        const int Merge = 0, Rebase = 1, Force = 2;
        var choice = await _dialogs.ChooseAsync(
            "Push rejected",
            $"'{remote}/{branch}' has commits that aren't in your local '{branch}'. "
            + "Bring them in and push again, or overwrite the remote with your local branch.",
            [
                "Fetch & merge, then push",
                "Fetch & rebase, then push",
                "Force push (with lease)",
            ],
            defaultIndex: Merge,
            destructiveIndex: Force);

        if (choice < 0)
        {
            // The modal already explained the rejection; Cancel means "leave it" — the record
            // goes to the dev log, not a card the user didn't ask to see again.
            AppLog.Write(AppLogLevel.Info, "declined", $"Push rejected modal cancelled: {ex.Message}", ex);
            return;
        }

        var setUpstream = !TargetHasUpstream(repo.State.Branches, branch);
        try
        {
            if (choice is Rebase or Merge)
            {
                await repo.Gateway.PullAsync(remote, branch,
                    GitPullOptions.From(_settings.Current) with { Rebase = choice == Rebase }, progress, ct);
                await repo.Gateway.PushAsync(remote,
                    new PushSpec { RefSpec = branch, SetUpstream = setUpstream }, progress, ct);
                _notifications.Publish(NotificationSeverity.Success, $"Pushed to {remote}.");
            }
            else if (choice == Force)
            {
                // Rewrites the *remote* ref: --force-with-lease/--force-if-includes stop us
                // clobbering commits pushed since our last fetch, but not ones we've never had.
                // A second, explicitly destructive confirm (Rule 00: warning glyph, Cancel the
                // primary + Enter button) stands between the modal and an operation that can
                // lose other people's work.
                if (!await _dialogs.ConfirmDestructiveAsync("Force push — destructive",
                        $"This force-pushes {branch} to {remote} and overwrites {remote}/{branch} "
                        + "with your local branch.\n\n"
                        + $"Any commits on {remote}/{branch} that aren't in your local history — "
                        + "including work pushed by other people — are permanently discarded. "
                        + "--force-with-lease only guards against changes made since your last fetch.\n\n"
                        + "Prefer \"Fetch & rebase\" unless you are certain you want to replace the remote branch.",
                        "Force push"))
                    return;
                await repo.Gateway.PushAsync(remote,
                    new PushSpec { RefSpec = branch, ForceWithLease = true, SetUpstream = setUpstream }, progress, ct);
                _notifications.Publish(NotificationSeverity.Success, $"Force-pushed to {remote}.");
            }
        }
        catch (OperationCanceledException)
        {
            AppLog.Write(AppLogLevel.Info, "declined", "Push cancelled");
        }
        catch (HistoriaGitException retryEx) when (retryEx.Kind == GitErrorKind.MergeConflict)
        {
            // The rebase/merge stopped in conflicts — the refresh below surfaces the resolver
            // panel; the push waits until the user finishes and triggers it again.
            _notifications.Publish(NotificationSeverity.Warning,
                "Resolve the conflicts, then push again.", retryEx.Details);
        }
        catch (Exception retryEx)
        {
            _notifications.Publish(NotificationSeverity.Error,
                $"Push failed: {retryEx.Message}", retryEx.NotificationDetails());
        }
        finally
        {
            await repo.State.RefreshAllAsync(repo.Lifetime);
        }
    }

    public void CancelCurrent() => _cts?.Cancel();

    private async Task RunAsync(RepoNetworkRequest request)
    {
        var op = request.Action;
        if (_workspace.Focused is not { } repo || _busy)
            return;

        string remote;
        if (request.Remote is { } targetRemote)
        {
            remote = targetRemote;
        }
        else
        {
            var remotes = repo.State.Remotes;
            if (remotes.Count == 0)
            {
                _notifications.Publish(NotificationSeverity.Warning, "This repository has no remotes configured.");
                return;
            }
            remote = remotes.Any(r => r.Name == "origin") ? "origin" : remotes[0].Name;
        }
        var branch = request.Branch ?? repo.State.CurrentBranch?.Name;

        _busy = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(repo.Lifetime);
        var ct = _cts.Token;
        var progress = new Progress<GitProgress>(p => ProgressChanged?.Invoke(this, new RepoNetworkProgress(repo, p)));
        ProgressChanged?.Invoke(this, new RepoNetworkProgress(repo, new GitProgress { Phase = $"{op}…" }));
        try
        {
            await EnsureActiveProfileFreshAsync(repo);
            await ExecOpAsync(repo, op, remote, branch, request.RemoteBranch, progress, ct);
            await repo.State.RefreshAllAsync(repo.Lifetime);
        }
        catch (OperationCanceledException)
        {
            AppLog.Write(AppLogLevel.Info, "declined", $"{op} cancelled");
        }
        catch (HistoriaGitException ex) when (ex.FallbackToLocalConfigAvailable)
        {
            var previous = repo.State.ActiveProfile;
            var confirmed = await _dialogs.ConfirmAsync(
                "Switch to Local config?",
                $"Couldn't reach '{remote}' as '{previous?.DisplayName ?? "the selected profile"}'. "
                + "Switch this repository to Local config and try again?",
                confirmLabel: "Switch and retry");
            if (confirmed)
            {
                try
                {
                    var key = repo.IdentityKey ?? await _identity.ResolveAsync(repo, repo.Lifetime);
                    await _profiles.SetLocalConfigForRepositoryAsync(key, repo.RepositoryPath, repo.Lifetime);
                    repo.State.SetActiveProfile(null);
                    await ExecOpAsync(repo, op, remote, branch, request.RemoteBranch, progress, ct);
                    await repo.State.RefreshAllAsync(repo.Lifetime);
                }
                catch (Exception retryEx)
                {
                    // persist the switch only if the retry succeeds — roll back on failure.
                    try
                    {
                        var key = repo.IdentityKey ?? await _identity.ResolveAsync(repo, repo.Lifetime);
                        if (previous is not null)
                            await _profiles.BindProfileToRepositoryAsync(key, repo.RepositoryPath, previous.Id, repo.Lifetime);
                        else
                            await _profiles.UnbindRepositoryAsync(key, repo.Lifetime);
                        repo.State.SetActiveProfile(previous);
                    }
                    catch { /* best effort rollback */ }
                    _notifications.Publish(NotificationSeverity.Error, $"{op} failed: {retryEx.Message}", retryEx.NotificationDetails());
                }
            }
            else
            {
                // The user declined "Switch to Local config?" — nothing changed, so a card would
                // just restate the choice they just made.
                AppLog.Write(AppLogLevel.Info, "declined", $"{op}: switch to Local config declined: {ex.Message}", ex);
            }
        }
        catch (HistoriaGitException ex) when (ex.Kind == GitErrorKind.UncommittedChanges && op == RepoNetworkAction.Pull && branch is not null)
        {
            var pullTarget = request.RemoteBranch ?? branch;
            _notifications.Publish(NotificationSeverity.Error, ex.Message, ex.Details,
            [
                new NotificationAction("Stash & retry", () => BranchMenu.StashAndRetryAsync(repo, _notifications, _stashes,
                    () => repo.Gateway.PullAsync(remote, pullTarget, GitPullOptions.From(_settings.Current), null, repo.Lifetime), "Pull")),
            ]);
        }
        catch (HistoriaGitException ex) when (ex.Kind == GitErrorKind.RemoteRejected && op == RepoNetworkAction.Push && branch is not null)
        {
            await RecoverFromPushRejectionAsync(repo, remote, branch, ex, progress, ct);
        }
        catch (HistoriaGitException ex) when (ex.Kind == GitErrorKind.UnrelatedHistories && op == RepoNetworkAction.Pull && branch is not null)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.Message, ex.Details,
            [
                new NotificationAction("Merge anyway", async () =>
                {
                    // finally, not a trailing call: identical trees merge clean, but if they don't,
                    // the merge still throws and the UI needs the refreshed (conflicted) state to
                    // show the resolver panel instead of looking like nothing happened.
                    try { await repo.Gateway.PullAsync(remote, branch, GitPullOptions.From(_settings.Current) with { AllowUnrelatedHistories = true }, null, repo.Lifetime); }
                    finally { await repo.State.RefreshAllAsync(repo.Lifetime); }
                }),
                new NotificationAction($"Reset to {remote}/{branch}", async () =>
                {
                    if (!await _dialogs.ConfirmDestructiveAsync("Reset branch",
                            $"Move {branch} to {remote}/{branch}, discarding its current local commits? This cannot be undone.",
                            "Reset"))
                        return;
                    try { await repo.Gateway.ResetAsync($"{remote}/{branch}", GitResetMode.Hard, repo.Lifetime); }
                    finally { await repo.State.RefreshAllAsync(repo.Lifetime); }
                }),
            ]);
        }
        catch (HistoriaGitException ex) when (ex.Kind == GitErrorKind.HostKeyVerificationFailed)
        {
            // A brand-new SSH remote: the host's key isn't in known_hosts yet, and git's own
            // non-interactive run (GIT_TERMINAL_PROMPT=0) can't prompt to trust it. An interactive
            // `ssh` connection once is what actually fixes this — Historia can't safely automate
            // trusting an unknown host key on the user's behalf.
            _notifications.Publish(NotificationSeverity.Error, ex.Message, ex.Details,
            [
                new NotificationAction("Open terminal", () =>
                {
                    _ = _terminals.OpenOrFocusAsync(Historia.Controls.AppBandControl.RepoOrigin(repo));
                    return Task.CompletedTask;
                }),
            ]);
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"{op} failed: {ex.Message}", ex.NotificationDetails());
        }
        finally
        {
            _busy = false;
            _cts?.Dispose();
            _cts = null;
            ProgressChanged?.Invoke(this, new RepoNetworkProgress(repo, null));
        }
    }
}
