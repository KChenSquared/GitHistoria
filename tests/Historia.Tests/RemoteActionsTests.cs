namespace Historia.Tests;

/// <summary>The remote operations behind the remote menu: add and edit through the remote dialogs,
/// delete behind its destructive confirmation, the push-URL prompt, and the pure rules.</summary>
[Trait("tier", "git")]
public sealed class RemoteActionsTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<IDisposable> _disposables = [];
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeNotificationService _notify = new();
    private readonly FakeRemoteDialogs _remoteDialogs = new();

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

    private static string Remotes(TestRepositoryBuilder repo) => repo.Git("remote").Trim();

    private static RemoteInfo Remote(string url, string? pushUrl = null, string name = "origin") =>
        new() { Name = name, Url = url, PushUrl = pushUrl ?? url };

    private async Task<(RepositoryContext Ctx, RemoteInfo Remote)> OpenWithRemote(TestRepositoryBuilder repo, string url)
    {
        repo.Git("remote", "add", "origin", url);
        var ctx = _host.Open(repo.Path);
        await ctx.State.RefreshAllAsync(ctx.Lifetime);
        return (ctx, ctx.State.Remotes.Single());
    }

    [Fact]
    public async Task Delete_names_the_remote_and_its_tracking_branch_count_before_removing_it()
    {
        var upstream = NewRepo();
        upstream.Git("branch", "feature");
        var repo = NewRepo();
        var (ctx, remote) = await OpenWithRemote(repo, upstream.Path);
        repo.Git("fetch", "origin");
        await ctx.State.RefreshAllAsync(ctx.Lifetime);
        _dialogs.EnqueueConfirmDestructive(true);

        await RemoteActions.DeleteAsync(remote, ctx, _dialogs, _notify);

        var call = Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.Contains("'origin'", call.Effect);
        Assert.Contains(upstream.Path, call.Effect);
        Assert.Contains("2 remote-tracking branches", call.Effect);
        Assert.Equal("Delete", call.ConfirmLabel);
        Assert.Equal("", Remotes(repo));
        Assert.Equal(NotificationSeverity.Success, Assert.Single(_notify.Published).Severity);
    }

    [Fact]
    public async Task Cancelling_the_delete_confirmation_keeps_the_remote()
    {
        var repo = NewRepo();
        var (ctx, remote) = await OpenWithRemote(repo, "https://example.com/o/r.git");
        _dialogs.EnqueueConfirmDestructive(false);

        await RemoteActions.DeleteAsync(remote, ctx, _dialogs, _notify);

        Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.Equal("origin", Remotes(repo));
        Assert.Empty(_notify.Published);
    }

    [Fact]
    public async Task Add_uses_the_remote_dialogs_and_creates_the_remote()
    {
        var repo = NewRepo();
        var ctx = _host.Open(repo.Path);
        await ctx.State.RefreshAllAsync(ctx.Lifetime);
        var bus = new RepoActionBus();
        var requests = new List<RepoNetworkRequest>();
        bus.NetworkActionRequested += (_, r) => requests.Add(r);
        _remoteDialogs.AddAnswer = new AddRemoteResult("up", "https://example.com/o/up.git", Fetch: true);

        await RemoteActions.AddAsync(ctx, _notify, bus, _remoteDialogs);

        Assert.Single(_remoteDialogs.AddCalls);
        Assert.Equal("up", Remotes(repo));
        Assert.Equal("https://example.com/o/up.git", repo.Git("remote", "get-url", "up").Trim());
        var added = Assert.Single(_notify.Published);
        Assert.Equal("Added remote 'up'.", added.Message);
        var fetch = Assert.Single(requests);
        Assert.Equal(RepoNetworkAction.Fetch, fetch.Action);
        Assert.Equal("up", fetch.Remote);
    }

    [Fact]
    public async Task Add_without_the_fetch_option_does_not_request_a_fetch()
    {
        var repo = NewRepo();
        var ctx = _host.Open(repo.Path);
        var bus = new RepoActionBus();
        var requests = new List<RepoNetworkRequest>();
        bus.NetworkActionRequested += (_, r) => requests.Add(r);
        _remoteDialogs.AddAnswer = new AddRemoteResult("up", "https://example.com/o/up.git", Fetch: false);

        await RemoteActions.AddAsync(ctx, _notify, bus, _remoteDialogs);

        Assert.Equal("up", Remotes(repo));
        Assert.Empty(requests);
    }

    [Fact]
    public async Task Add_cancelled_in_the_dialog_changes_nothing()
    {
        var repo = NewRepo();
        var ctx = _host.Open(repo.Path);

        await RemoteActions.AddAsync(ctx, _notify, new RepoActionBus(), _remoteDialogs);

        Assert.Single(_remoteDialogs.AddCalls);
        Assert.Equal("", Remotes(repo));
        Assert.Empty(_notify.Published);
    }

    [Fact]
    public async Task Edit_uses_the_remote_dialogs_and_applies_rename_url_and_push_url_reset()
    {
        var repo = NewRepo();
        var (ctx, remote) = await OpenWithRemote(repo, "https://example.com/o/r.git");
        repo.Git("remote", "set-url", "--push", "origin", "https://example.com/o/push.git");
        _remoteDialogs.EditAnswer = new EditRemoteResult("upstream", "https://example.com/o/new.git", ResetPushUrl: true);

        await RemoteActions.EditAsync(remote, ctx, _notify, _remoteDialogs);

        Assert.Same(remote, Assert.Single(_remoteDialogs.EditCalls));
        Assert.Equal("upstream", Remotes(repo));
        Assert.Equal("https://example.com/o/new.git", repo.Git("remote", "get-url", "upstream").Trim());
        Assert.Equal("https://example.com/o/new.git", repo.Git("remote", "get-url", "--push", "upstream").Trim());
        Assert.Empty(_notify.Published);
    }

    [Fact]
    public async Task Edit_cancelled_in_the_dialog_changes_nothing()
    {
        var repo = NewRepo();
        var (ctx, remote) = await OpenWithRemote(repo, "https://example.com/o/r.git");

        await RemoteActions.EditAsync(remote, ctx, _notify, _remoteDialogs);

        Assert.Equal("origin", Remotes(repo));
        Assert.Equal("https://example.com/o/r.git", repo.Git("remote", "get-url", "origin").Trim());
    }

    [Fact]
    public async Task Set_push_url_prompts_with_the_fetch_url_and_applies_the_answer()
    {
        var repo = NewRepo();
        var (ctx, remote) = await OpenWithRemote(repo, "https://example.com/o/r.git");
        _dialogs.EnqueuePrompt("https://example.com/o/push.git");

        await RemoteActions.SetPushUrlAsync(remote, ctx, _dialogs, _notify);

        Assert.Contains("origin", Assert.Single(_dialogs.Calls).Detail);
        Assert.Equal("https://example.com/o/push.git", repo.Git("remote", "get-url", "--push", "origin").Trim());
    }

    [Fact]
    public async Task Set_push_url_cancelled_or_unchanged_changes_nothing()
    {
        var repo = NewRepo();
        var (ctx, remote) = await OpenWithRemote(repo, "https://example.com/o/r.git");
        _dialogs.EnqueuePrompt(null).EnqueuePrompt("https://example.com/o/r.git");

        await RemoteActions.SetPushUrlAsync(remote, ctx, _dialogs, _notify);
        await RemoteActions.SetPushUrlAsync(remote, ctx, _dialogs, _notify);

        Assert.Equal("https://example.com/o/r.git", repo.Git("remote", "get-url", "--push", "origin").Trim());
        Assert.Empty(_notify.Published);
    }

    [Fact]
    public void DecidePushUrl_empty_with_distinct_push_url_clears() =>
        Assert.Equal(RemoteActions.PushUrlChange.Clear,
            RemoteActions.DecidePushUrl(Remote("https://a/r.git", "https://b/r.git"), "  "));

    [Fact]
    public void DecidePushUrl_empty_without_push_url_is_noop() =>
        Assert.Equal(RemoteActions.PushUrlChange.NoOp, RemoteActions.DecidePushUrl(Remote("https://a/r.git"), ""));

    [Fact]
    public void DecidePushUrl_fetch_url_clears_or_is_noop()
    {
        Assert.Equal(RemoteActions.PushUrlChange.Clear,
            RemoteActions.DecidePushUrl(Remote("https://a/r.git", "https://b/r.git"), " https://a/r.git "));
        Assert.Equal(RemoteActions.PushUrlChange.NoOp,
            RemoteActions.DecidePushUrl(Remote("https://a/r.git"), "https://a/r.git"));
    }

    [Fact]
    public void DecidePushUrl_current_push_url_is_noop() =>
        Assert.Equal(RemoteActions.PushUrlChange.NoOp,
            RemoteActions.DecidePushUrl(Remote("https://a/r.git", "https://b/r.git"), "https://b/r.git"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecidePushUrl_new_value_sets(bool hasDistinct)
    {
        var remote = hasDistinct ? Remote("https://a/r.git", "https://b/r.git") : Remote("https://a/r.git");
        Assert.Equal(RemoteActions.PushUrlChange.Set("https://c/r.git"), RemoteActions.DecidePushUrl(remote, " https://c/r.git "));
    }

    [Fact]
    public void Delete_effect_names_remote_url_and_branch_count()
    {
        var effect = RemoteActions.DeleteEffect("origin", "https://example.com/o/r.git", 65);

        Assert.Contains("'origin'", effect);
        Assert.Contains("https://example.com/o/r.git", effect);
        Assert.Contains("65 remote-tracking branches", effect);
        Assert.Contains("origin/", effect);
        Assert.Contains("lose their upstream", effect);
        Assert.Contains("become unreferenced locally", effect);
        Assert.DoesNotContain("Are you sure", effect);
    }

    [Fact]
    public void Delete_effect_handles_zero_and_one_branch()
    {
        Assert.Contains("no remote-tracking branches", RemoteActions.DeleteEffect("origin", "u", 0));
        Assert.Contains("1 remote-tracking branch (", RemoteActions.DeleteEffect("origin", "u", 1));
    }
}
