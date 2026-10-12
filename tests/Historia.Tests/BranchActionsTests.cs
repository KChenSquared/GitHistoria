namespace Historia.Tests;

/// <summary>The branch operations behind the branch menus: delete (with its force-delete recovery),
/// the stash-and-retry recovery for a refused checkout, and the checkout, create, rename and merge
/// operations. Each runs against a real repository.</summary>
[Trait("tier", "git")]
public sealed class BranchActionsTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<IDisposable> _disposables = [];
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeNotificationService _notify = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        foreach (var d in _disposables)
            d.Dispose();
    }

    private TestRepositoryBuilder NewRepo()
    {
        var repo = TestRepositoryBuilder.Create();
        _disposables.Add(repo);
        repo.WriteFile("a.txt", "one\n").Stage();
        repo.Commit("c1");
        return repo;
    }

    private static bool BranchExists(TestRepositoryBuilder repo, string name) =>
        repo.Git("branch", "--list", name).Trim().Length > 0;

    private static string Head(TestRepositoryBuilder repo) => repo.Git("rev-parse", "--abbrev-ref", "HEAD").Trim();

    private IStashService Stash() => new StashService(_host.Cli);

    [Fact]
    public async Task Delete_confirms_through_the_destructive_entry_point_naming_the_branch()
    {
        var repo = NewRepo();
        repo.Git("branch", "topic");
        var ctx = _host.Open(repo.Path);
        _dialogs.EnqueueConfirmDestructive(true);

        await BranchActions.DeleteAsync(ctx, _dialogs, _notify, "topic");

        var call = Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.Contains("topic", call.Effect);
        Assert.Equal("Delete", call.ConfirmLabel);
        Assert.False(BranchExists(repo, "topic"));
        Assert.Empty(_notify.Published);
    }

    [Fact]
    public async Task Cancelling_the_delete_confirmation_keeps_the_branch()
    {
        var repo = NewRepo();
        repo.Git("branch", "topic");
        var ctx = _host.Open(repo.Path);
        _dialogs.EnqueueConfirmDestructive(false);

        await BranchActions.DeleteAsync(ctx, _dialogs, _notify, "topic");

        Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.True(BranchExists(repo, "topic"));
        Assert.Empty(_notify.Published);
    }

    [Fact]
    public async Task An_unmerged_branch_offers_force_delete_behind_its_own_destructive_confirmation()
    {
        var repo = NewRepo();
        repo.Git("checkout", "-b", "topic");
        repo.WriteFile("b.txt", "topic only\n").Stage();
        repo.Commit("topic work");
        repo.Git("checkout", "main");
        var ctx = _host.Open(repo.Path);
        _dialogs.EnqueueConfirmDestructive(true).EnqueueConfirmDestructive(true);

        await BranchActions.DeleteAsync(ctx, _dialogs, _notify, "topic");

        Assert.True(BranchExists(repo, "topic"));
        var failure = Assert.Single(_notify.Published);
        Assert.Equal(NotificationSeverity.Error, failure.Severity);
        var force = Assert.Single(failure.Actions);
        Assert.Equal("Force delete", force.Label);

        await force.Invoke();

        Assert.Equal(2, _dialogs.ConfirmDestructiveCalls.Count);
        Assert.Equal("Force delete branch", _dialogs.ConfirmDestructiveCalls[1].Title);
        Assert.False(BranchExists(repo, "topic"));
    }

    [Fact]
    public async Task Declining_the_force_delete_confirmation_keeps_the_branch()
    {
        var repo = NewRepo();
        repo.Git("checkout", "-b", "topic");
        repo.WriteFile("b.txt", "topic only\n").Stage();
        repo.Commit("topic work");
        repo.Git("checkout", "main");
        var ctx = _host.Open(repo.Path);
        _dialogs.EnqueueConfirmDestructive(true).EnqueueConfirmDestructive(false);

        await BranchActions.DeleteAsync(ctx, _dialogs, _notify, "topic");
        await Assert.Single(_notify.Published).Actions[0].Invoke();

        Assert.True(BranchExists(repo, "topic"));
    }

    /// <summary>A repository whose working tree edit blocks checking out the first commit.</summary>
    private (TestRepositoryBuilder Repo, string FirstCommit) RepoWithBlockingEdit()
    {
        var repo = TestRepositoryBuilder.Create();
        _disposables.Add(repo);
        repo.WriteFile("other.txt", "c1\n").Stage();
        var first = repo.Commit("c1");
        repo.WriteFile("other.txt", "c2\n").Stage();
        repo.Commit("c2");
        repo.WriteFile("other.txt", "c2 edited\n");
        return (repo, first);
    }

    [Fact]
    public async Task A_refused_checkout_offers_stash_and_retry_that_completes_the_operation()
    {
        var (repo, first) = RepoWithBlockingEdit();
        var ctx = _host.Open(repo.Path);

        await BranchActions.RunWithStashRecovery(ctx, _notify, Stash(),
            () => ctx.Gateway.CreateBranchAsync("recovered", first, checkout: true), "Create branch");

        var failure = Assert.Single(_notify.Published);
        Assert.Equal(NotificationSeverity.Error, failure.Severity);
        var retry = Assert.Single(failure.Actions);
        Assert.Equal("Stash & retry", retry.Label);
        Assert.Equal("main", Head(repo));

        await retry.Invoke();

        // The edit conflicts with the checked-out commit, so git leaves the stash in the list
        // rather than dropping it; the retry itself still completed.
        Assert.Equal("recovered", Head(repo));
        Assert.Contains("Historia: before Create branch", repo.Git("stash", "list"));
    }

    [Fact]
    public async Task A_checkout_recovered_by_stash_and_retry_runs_on_success_after_the_retry()
    {
        var (repo, first) = RepoWithBlockingEdit();
        var ctx = _host.Open(repo.Path);
        var succeeded = 0;

        await BranchActions.RunWithStashRecovery(ctx, _notify, Stash(),
            () => ctx.Gateway.CreateBranchAsync("recovered", first, checkout: true), "Create branch",
            onSuccess: () => succeeded++);

        Assert.Equal(0, succeeded);
        var retry = Assert.Single(Assert.Single(_notify.Published).Actions);

        await retry.Invoke();

        Assert.Equal("recovered", Head(repo));
        Assert.Equal(1, succeeded);
    }

    [Fact]
    public async Task A_stash_and_retry_whose_retry_fails_does_not_run_on_success()
    {
        var (repo, first) = RepoWithBlockingEdit();
        var ctx = _host.Open(repo.Path);
        var succeeded = 0;
        var attempts = 0;

        await BranchActions.RunWithStashRecovery(ctx, _notify, Stash(), async () =>
        {
            if (++attempts == 1)
                await ctx.Gateway.CreateBranchAsync("recovered", first, checkout: true);
            else
                throw new InvalidOperationException("retry failed");
        }, "Create branch", onSuccess: () => succeeded++);

        await Assert.Single(Assert.Single(_notify.Published).Actions).Invoke();

        Assert.Equal(2, attempts);
        Assert.Equal(0, succeeded);
        Assert.Equal(NotificationSeverity.Error, _notify.Published[^1].Severity);
        Assert.Contains("retry failed", _notify.Published[^1].Message);
    }

    [Fact]
    public async Task A_successful_operation_runs_on_success_once()
    {
        var repo = NewRepo();
        var ctx = _host.Open(repo.Path);
        var succeeded = 0;

        await BranchActions.RunWithStashRecovery(ctx, _notify, Stash(), () => Task.CompletedTask, "Noop", onSuccess: () => succeeded++);

        Assert.Equal(1, succeeded);
    }

    [Fact]
    public async Task A_failed_operation_does_not_run_on_success()
    {
        var repo = NewRepo();
        var ctx = _host.Open(repo.Path);
        var succeeded = 0;

        await BranchActions.RunWithStashRecovery(ctx, _notify, Stash(),
            () => throw new InvalidOperationException("nope"), "Noop", onSuccess: () => succeeded++);

        Assert.Equal(0, succeeded);
        Assert.Equal(NotificationSeverity.Error, Assert.Single(_notify.Published).Severity);
    }

    [Fact]
    public async Task A_successful_operation_publishes_nothing()
    {
        var repo = NewRepo();
        var ctx = _host.Open(repo.Path);
        var ran = false;

        await BranchActions.RunWithStashRecovery(ctx, _notify, Stash(), () => { ran = true; return Task.CompletedTask; }, "Noop");

        Assert.True(ran);
        Assert.Empty(_notify.Published);
    }

    [Fact]
    public async Task Create_branch_prompts_and_checks_the_new_branch_out()
    {
        var repo = NewRepo();
        var sha = repo.HeadSha;
        var ctx = _host.Open(repo.Path);
        _dialogs.EnqueuePrompt("  feature/x  ");

        await BranchActions.CreateBranchAsync(ctx, _dialogs, _notify, Stash(), sha, sha[..7]);

        Assert.Contains(sha[..7], Assert.Single(_dialogs.Calls).Detail);
        Assert.Equal("feature/x", Head(repo));
    }

    [Fact]
    public async Task Create_branch_with_a_blank_name_does_nothing()
    {
        var repo = NewRepo();
        var sha = repo.HeadSha;
        var ctx = _host.Open(repo.Path);
        _dialogs.EnqueuePrompt("   ");

        await BranchActions.CreateBranchAsync(ctx, _dialogs, _notify, Stash(), sha, sha[..7]);

        Assert.Equal("main", Head(repo));
        Assert.Empty(_notify.Published);
    }

    [Fact]
    public async Task Create_tag_prompts_and_tags_the_commit()
    {
        var repo = NewRepo();
        var sha = repo.HeadSha;
        var ctx = _host.Open(repo.Path);
        _dialogs.EnqueuePrompt("v1");

        await BranchActions.CreateTagAsync(ctx, _dialogs, _notify, sha, sha[..7]);

        Assert.Equal(sha, repo.Git("rev-parse", "v1^{commit}").Trim());
        Assert.Empty(_notify.Published);
    }

    [Fact]
    public async Task Rename_moves_the_branch_and_a_failure_is_reported()
    {
        var repo = NewRepo();
        repo.Git("branch", "old");
        var ctx = _host.Open(repo.Path);

        await BranchActions.RenameAsync(ctx, _notify, "old", "new");
        Assert.False(BranchExists(repo, "old"));
        Assert.True(BranchExists(repo, "new"));
        Assert.Empty(_notify.Published);

        await BranchActions.RenameAsync(ctx, _notify, "missing", "other");
        Assert.Equal(NotificationSeverity.Error, Assert.Single(_notify.Published).Severity);
    }

    [Fact]
    public async Task Checkout_switches_to_an_existing_local_branch()
    {
        var repo = NewRepo();
        repo.Git("branch", "topic");
        var ctx = _host.Open(repo.Path);

        await BranchActions.CheckoutAsync(ctx, _notify, Stash(), "topic");

        Assert.Equal("topic", Head(repo));
        Assert.Empty(_notify.Published);
    }

    [Fact]
    public async Task Checkout_tracking_creates_the_local_branch_and_wires_its_upstream()
    {
        var upstream = NewRepo();
        upstream.Git("branch", "feature");
        var repo = TestRepositoryBuilder.Create();
        _disposables.Add(repo);
        repo.Git("remote", "add", "origin", upstream.Path);
        repo.Git("fetch", "origin");
        var ctx = _host.Open(repo.Path);

        await BranchActions.CheckoutTrackingAsync(ctx, _notify, Stash(), "feature", "origin/feature");

        Assert.Empty(_notify.Published);
        Assert.Equal("feature", Head(repo));
        Assert.Equal("origin/feature", repo.Git("rev-parse", "--abbrev-ref", "feature@{upstream}").Trim());
    }

    /// <summary>A repository with a remote <paramref name="remote"/> that has a branch "feature".</summary>
    private async Task<(TestRepositoryBuilder Repo, RepositoryContext Ctx)> RepoWithRemoteFeature(string remote, bool localFeature)
    {
        var upstream = NewRepo();
        upstream.Git("branch", "feature");
        var repo = NewRepo();
        repo.Git("remote", "add", remote, upstream.Path);
        repo.Git("fetch", remote);
        if (localFeature)
            repo.Git("branch", "feature");
        var ctx = _host.Open(repo.Path);
        await ctx.State.RefreshRefsAsync();
        return (repo, ctx);
    }

    private static BranchInfo Remote(RepositoryContext ctx, string name) =>
        ctx.State.Branches.Single(b => b.IsRemote && b.Name == name);

    private static string Upstream(TestRepositoryBuilder repo, string branch) =>
        repo.Git("for-each-ref", "--format=%(upstream:short)", $"refs/heads/{branch}").Trim();

    [Fact]
    public async Task CheckoutBranchAsync_creates_a_tracking_branch_for_a_remote_without_a_local()
    {
        var (repo, ctx) = await RepoWithRemoteFeature("origin", localFeature: false);
        var switched = new List<string>();

        await BranchActions.CheckoutBranchAsync(ctx, _notify, Stash(), Remote(ctx, "origin/feature"), switched.Add);

        Assert.Empty(_notify.Published);
        Assert.Equal("feature", Head(repo));
        Assert.Equal("origin/feature", Upstream(repo, "feature"));
        Assert.Equal(["feature"], switched);
    }

    [Fact]
    public async Task CheckoutBranchAsync_checks_out_the_local_branch_when_one_exists()
    {
        var (repo, ctx) = await RepoWithRemoteFeature("origin", localFeature: true);
        var switched = new List<string>();

        await BranchActions.CheckoutBranchAsync(ctx, _notify, Stash(), Remote(ctx, "origin/feature"), switched.Add);

        Assert.Empty(_notify.Published);
        Assert.Equal("feature", Head(repo));
        Assert.Equal("", Upstream(repo, "feature"));
        Assert.Equal(["feature"], switched);
    }

    [Fact]
    public async Task CheckoutBranchAsync_handles_a_remote_alias_with_a_slash()
    {
        var (repo, ctx) = await RepoWithRemoteFeature("team/infra", localFeature: false);
        var remote = Remote(ctx, "team/infra/feature");
        Assert.Equal("team/infra", remote.RemoteName);

        await BranchActions.CheckoutBranchAsync(ctx, _notify, Stash(), remote);

        Assert.Empty(_notify.Published);
        Assert.Equal("feature", Head(repo));
        Assert.Equal("team/infra/feature", Upstream(repo, "feature"));
    }

    [Fact]
    public async Task CheckoutBranchAsync_checks_out_a_local_branch_by_its_own_name()
    {
        var repo = NewRepo();
        repo.Git("branch", "topic");
        var ctx = _host.Open(repo.Path);
        await ctx.State.RefreshRefsAsync();
        var topic = ctx.State.Branches.Single(b => !b.IsRemote && b.Name == "topic");
        var switched = new List<string>();

        await BranchActions.CheckoutBranchAsync(ctx, _notify, Stash(), topic, switched.Add);

        Assert.Equal("topic", Head(repo));
        Assert.Equal(["topic"], switched);
    }

    [Fact]
    public async Task CheckoutBranchAsync_does_not_report_a_switch_to_the_branch_already_checked_out()
    {
        var (repo, ctx) = await RepoWithRemoteFeature("origin", localFeature: true);
        repo.Git("checkout", "feature");
        await ctx.State.RefreshAllAsync();
        var switched = new List<string>();

        await BranchActions.CheckoutBranchAsync(ctx, _notify, Stash(), Remote(ctx, "origin/feature"), switched.Add);

        Assert.Empty(_notify.Published);
        Assert.Equal("feature", Head(repo));
        Assert.Empty(switched);
    }

    [Fact]
    public async Task A_checkout_blocked_by_local_edits_announces_the_switch_only_after_stash_and_retry()
    {
        var (repo, first) = RepoWithBlockingEdit();
        repo.Git("branch", "older", first);
        var ctx = _host.Open(repo.Path);
        await ctx.State.RefreshRefsAsync();
        var older = ctx.State.Branches.Single(b => b.Name == "older");
        var switched = new List<string>();

        await BranchActions.CheckoutBranchAsync(ctx, _notify, Stash(), older, switched.Add);

        Assert.Empty(switched);
        var retry = Assert.Single(Assert.Single(_notify.Published).Actions);
        Assert.Equal("Stash & retry", retry.Label);

        await retry.Invoke();

        Assert.Equal("older", Head(repo));
        Assert.Equal(["older"], switched);
    }

    [Fact]
    public void LocalName_drops_the_remote_prefix_and_falls_back_to_the_first_segment()
    {
        static BranchInfo Branch(string name, bool remote, string? remoteName) => new()
        {
            Name = name,
            CanonicalName = remote ? $"refs/remotes/{name}" : $"refs/heads/{name}",
            IsRemote = remote,
            IsCurrent = false,
            RemoteName = remoteName,
        };

        Assert.Equal("feature/x", BranchActions.LocalName(Branch("feature/x", false, null)));
        Assert.Equal("feature/x", BranchActions.LocalName(Branch("origin/feature/x", true, "origin")));
        Assert.Equal("feature", BranchActions.LocalName(Branch("team/infra/feature", true, "team/infra")));
        Assert.Equal("feature/x", BranchActions.LocalName(Branch("origin/feature/x", true, null)));
        Assert.Equal("lone", BranchActions.LocalName(Branch("lone", true, null)));
    }

    [Fact]
    public async Task Merge_asks_first_and_declining_changes_nothing()
    {
        var repo = NewRepo();
        repo.Git("checkout", "-b", "topic");
        repo.WriteFile("b.txt", "topic\n").Stage();
        var topicTip = repo.Commit("topic work");
        repo.Git("checkout", "main");
        var ctx = _host.Open(repo.Path);
        _dialogs.EnqueueConfirm(false).EnqueueConfirm(true);

        await BranchActions.MergeIntoCurrentAsync(ctx, _dialogs, _notify, Stash(), "topic", "main");
        Assert.NotEqual(topicTip, repo.HeadSha);
        Assert.Empty(_notify.Published);

        await BranchActions.MergeIntoCurrentAsync(ctx, _dialogs, _notify, Stash(), "topic", "main");

        Assert.Equal(topicTip, repo.HeadSha);
        Assert.Empty(_dialogs.ConfirmDestructiveCalls);
        var merged = Assert.Single(_notify.Published);
        Assert.Equal(NotificationSeverity.Success, merged.Severity);
        Assert.Equal("Merged topic.", merged.Message);
    }
}
