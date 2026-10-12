namespace Historia.Tests;

/// <summary>
/// The rebase editor view model: loading the todo list, reordering and choosing actions, and the
/// confirmation that guards every rebase that rewrites history. The pick-only, unchanged-order
/// rebase is the one that starts without asking.
/// </summary>
[Trait("tier", "git")]
public sealed class RebaseEditorViewModelTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<IDisposable> _repos = [];
    private readonly List<RepositoryContext> _contexts = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var ctx in _contexts)
            await ctx.DisposeAsync();
        foreach (var repo in _repos)
            repo.Dispose();
        await _host.DisposeAsync();
    }

    private sealed class Harness
    {
        public required RebaseEditorViewModel Vm { get; init; }
        public required RecordingRebaseService Rebase { get; init; }
        public required FakeDialogService Dialogs { get; init; }
        public required FakeNotificationService Notifications { get; init; }
        public required List<string> Navigations { get; init; }
    }

    private Harness Create(bool focused = true, string onto = "main", RecordingRebaseService? rebase = null)
    {
        var workspace = new FakeWorkspace();
        if (focused)
        {
            var repo = TestRepositoryBuilder.FromTemplate("rebase-vm-fixture", b => b.WriteFile("a.txt", "a\n").Stage().Commit("c1"));
            _repos.Add(repo);
            var ctx = _host.Open(repo.Path);
            _contexts.Add(ctx);
            workspace.SetFocused(ctx);
        }

        var nav = new NavigationBus();
        var navigations = new List<string>();
        nav.NavigationRequested += navigations.Add;

        rebase ??= new RecordingRebaseService();
        var dialogs = new FakeDialogService();
        var notifications = new FakeNotificationService();
        return new Harness
        {
            Vm = new RebaseEditorViewModel(rebase, notifications, nav, workspace, dialogs, onto),
            Rebase = rebase,
            Dialogs = dialogs,
            Notifications = notifications,
            Navigations = navigations,
        };
    }

    private static async Task<Harness> Loaded(Harness h)
    {
        await h.Vm.LoadCommand.ExecuteAsync(null);
        return h;
    }

    [Fact]
    public async Task Load_lists_the_commits_oldest_first_as_picks()
    {
        var h = await Loaded(Create());

        Assert.True(h.Vm.IsLoaded);
        Assert.False(h.Vm.HasError);
        Assert.Equal(["first", "second", "third"], h.Vm.Items.Select(i => i.Message));
        Assert.All(h.Vm.Items, i => Assert.Equal(RebaseAction.Pick, i.Action));
        Assert.True(h.Vm.Autostash);
        Assert.Equal("feature onto main — 3 commit(s), oldest first. Squash/fixup melds into the row above.", h.Vm.Summary);
        Assert.Equal(new string('1', 8), h.Vm.Items[0].ShortId);
    }

    [Fact]
    public async Task Load_without_a_repository_or_upstream_reports_why()
    {
        var noRepo = await Loaded(Create(focused: false));
        Assert.False(noRepo.Vm.IsLoaded);
        Assert.Equal("No repository open, or no upstream ref was specified.", noRepo.Vm.ErrorMessage);

        var noOnto = await Loaded(Create(onto: " "));
        Assert.False(noOnto.Vm.IsLoaded);
        Assert.True(noOnto.Vm.HasError);
        Assert.Empty(noOnto.Rebase.BuiltFor);
    }

    [Fact]
    public async Task Load_reports_a_session_that_cannot_be_built()
    {
        var h = await Loaded(Create(rebase: new RecordingRebaseService { BuildFailure = new InvalidOperationException("no merge base") }));

        Assert.False(h.Vm.IsLoaded);
        Assert.Equal("Could not build the rebase session: no merge base", h.Vm.ErrorMessage);
        Assert.Empty(h.Vm.Items);
    }

    [Fact]
    public async Task Rows_can_move_only_within_the_list()
    {
        var h = await Loaded(Create());
        var items = h.Vm.Items;

        Assert.False(items[0].CanMoveUp);
        Assert.True(items[0].CanMoveDown);
        Assert.True(items[1].CanMoveUp);
        Assert.True(items[1].CanMoveDown);
        Assert.True(items[2].CanMoveUp);
        Assert.False(items[2].CanMoveDown);
        Assert.False(items[0].MoveUpCommand.CanExecute(null));

        var second = items[1];
        second.MoveUpCommand.Execute(null);

        Assert.Equal(["second", "first", "third"], items.Select(i => i.Message));
        Assert.False(second.CanMoveUp);
        Assert.True(second.CanMoveDown);
        Assert.True(items[1].CanMoveUp);

        items[2].MoveUpCommand.Execute(null);
        Assert.Equal(["second", "third", "first"], items.Select(i => i.Message));
        Assert.False(items[2].CanMoveDown);
    }

    [Fact]
    public async Task Pick_only_in_the_original_order_starts_without_asking()
    {
        var h = await Loaded(Create());

        await h.Vm.StartCommand.ExecuteAsync(null);

        Assert.Empty(h.Dialogs.ConfirmDestructiveCalls);
        var started = Assert.Single(h.Rebase.Started);
        Assert.Equal(["first", "second", "third"], started.Session.Entries.Select(e => e.MessageShort));
        Assert.True(started.Autostash);
        Assert.Equal(["repo"], h.Navigations);
        Assert.Equal("Rebase complete.", Assert.Single(h.Notifications.Published).Message);
    }

    [Fact]
    public async Task Autostash_choice_reaches_the_rebase()
    {
        var h = await Loaded(Create());
        h.Vm.Autostash = false;

        await h.Vm.StartCommand.ExecuteAsync(null);

        Assert.False(Assert.Single(h.Rebase.Started).Autostash);
    }

    [Fact]
    public async Task A_reorder_asks_before_rewriting_history_and_declining_changes_nothing()
    {
        var h = await Loaded(Create());
        h.Vm.Items[1].MoveUpCommand.Execute(null);

        await h.Vm.StartCommand.ExecuteAsync(null);

        var call = Assert.Single(h.Dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Rewrite history", call.Title);
        Assert.Equal("Start rebase", call.ConfirmLabel);
        Assert.Equal("Rebase feature onto main? This rewrites history: Reordering commits still gives them new SHAs.", call.Effect);
        Assert.Empty(h.Rebase.Started);
        Assert.Empty(h.Navigations);
        Assert.Empty(h.Notifications.Published);
    }

    [Fact]
    public async Task An_accepted_reorder_starts_with_the_new_order()
    {
        var h = await Loaded(Create());
        h.Dialogs.EnqueueConfirmDestructive(true);
        h.Vm.Items[1].MoveUpCommand.Execute(null);

        await h.Vm.StartCommand.ExecuteAsync(null);

        var started = Assert.Single(h.Rebase.Started);
        Assert.Equal(["second", "first", "third"], started.Session.Entries.Select(e => e.MessageShort));
        Assert.Equal(["repo"], h.Navigations);
    }

    [Fact]
    public async Task Moving_a_row_back_to_where_it_was_needs_no_confirmation()
    {
        var h = await Loaded(Create());
        h.Vm.Items[1].MoveUpCommand.Execute(null);
        h.Vm.Items[0].MoveDownCommand.Execute(null);

        await h.Vm.StartCommand.ExecuteAsync(null);

        Assert.Empty(h.Dialogs.ConfirmDestructiveCalls);
        Assert.Single(h.Rebase.Started);
    }

    [Fact]
    public async Task Rewords_drops_and_squashes_are_counted_in_the_confirmation()
    {
        var h = await Loaded(Create());
        h.Vm.Items[0].Action = RebaseAction.Reword;
        h.Vm.Items[1].Action = RebaseAction.Squash;
        h.Vm.Items[2].Action = RebaseAction.Drop;

        await h.Vm.StartCommand.ExecuteAsync(null);

        var call = Assert.Single(h.Dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Rebase feature onto main? This rewrites history: 1 rewritten, 1 dropped, 1 squashed commit(s).", call.Effect);
        Assert.Empty(h.Rebase.Started);
    }

    [Fact]
    public async Task A_single_changed_action_is_enough_to_ask()
    {
        var h = await Loaded(Create());
        h.Vm.Items[2].Action = RebaseAction.Edit;

        await h.Vm.StartCommand.ExecuteAsync(null);

        Assert.Equal("Rebase feature onto main? This rewrites history: 1 rewritten commit(s).",
            Assert.Single(h.Dialogs.ConfirmDestructiveCalls).Effect);
    }

    [Fact]
    public async Task Issues_that_need_confirmation_are_listed_in_the_prompt()
    {
        var rebase = new RecordingRebaseService { Issues = [new RebaseValidationIssue("The first commit cannot be squashed.", RequiresConfirmation: true)] };
        var h = await Loaded(Create(rebase: rebase));
        h.Vm.Items[1].Action = RebaseAction.Fixup;

        await h.Vm.StartCommand.ExecuteAsync(null);

        var effect = Assert.Single(h.Dialogs.ConfirmDestructiveCalls).Effect;
        Assert.EndsWith("1 squashed commit(s).\n\nThe first commit cannot be squashed.", effect);
    }

    [Fact]
    public async Task Blocking_issues_are_reported_and_nothing_is_asked_or_started()
    {
        var rebase = new RecordingRebaseService
        {
            Issues = [new RebaseValidationIssue("Nothing to rebase.", false), new RebaseValidationIssue("Every commit is dropped.", false)],
        };
        var h = await Loaded(Create(rebase: rebase));
        h.Vm.Items[0].Action = RebaseAction.Drop;

        await h.Vm.StartCommand.ExecuteAsync(null);

        Assert.Empty(h.Dialogs.ConfirmDestructiveCalls);
        Assert.Empty(h.Rebase.Started);
        var published = Assert.Single(h.Notifications.Published);
        Assert.Equal(NotificationSeverity.Error, published.Severity);
        Assert.Equal("Nothing to rebase.\nEvery commit is dropped.", published.Message);
    }

    [Fact]
    public async Task A_failing_rebase_is_reported_and_stays_on_the_editor()
    {
        var rebase = new RecordingRebaseService { StartFailure = new InvalidOperationException("sequencer died") };
        var h = await Loaded(Create(rebase: rebase));

        await h.Vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(NotificationSeverity.Error, Assert.Single(h.Notifications.Published).Severity);
        Assert.Empty(h.Navigations);
    }

    [Fact]
    public async Task Start_before_a_session_is_loaded_does_nothing()
    {
        var h = Create();

        await h.Vm.StartCommand.ExecuteAsync(null);

        Assert.Empty(h.Rebase.Started);
        Assert.Empty(h.Dialogs.Calls);
        Assert.Empty(h.Navigations);
    }

    [Fact]
    public async Task Cancel_returns_to_the_repository()
    {
        var h = await Loaded(Create());

        h.Vm.CancelCommand.Execute(null);

        Assert.Equal(["repo"], h.Navigations);
        Assert.Empty(h.Rebase.Started);
    }

    [Fact]
    public async Task A_load_that_finishes_after_dispose_changes_nothing()
    {
        var gate = new TaskCompletionSource();
        var h = Create(rebase: new RecordingRebaseService { BuildGate = gate.Task });

        var load = h.Vm.LoadCommand.ExecuteAsync(null);
        h.Vm.Dispose();
        gate.SetResult();
        await load;

        Assert.False(h.Vm.IsLoaded);
        Assert.Empty(h.Vm.Items);
    }

    private sealed class RecordingRebaseService : IRebaseService
    {
        public Exception? BuildFailure { get; init; }
        public Exception? StartFailure { get; init; }
        public Task? BuildGate { get; init; }
        public IReadOnlyList<RebaseValidationIssue> Issues { get; init; } = [];

        public List<string> BuiltFor { get; } = [];
        public List<(RebaseSession Session, bool Autostash)> Started { get; } = [];

        public async Task<RebaseSession> BuildSessionAsync(RepositoryContext context, string upstreamRef, CancellationToken ct = default)
        {
            BuiltFor.Add(upstreamRef);
            if (BuildGate is not null)
                await BuildGate;
            if (BuildFailure is not null)
                throw BuildFailure;

            RebaseEntry Entry(char id, string message) => new()
            {
                CommitId = new GitObjectId(new string(id, 40)),
                MessageShort = message,
                AuthorName = "A",
                AuthorEmail = "a@example.com",
            };

            return new RebaseSession
            {
                OntoCommitId = new GitObjectId(new string('f', 40)),
                OntoRefName = upstreamRef,
                UpstreamRefName = upstreamRef,
                SourceBranchName = "feature",
                Entries = [Entry('1', "first"), Entry('2', "second"), Entry('3', "third")],
            };
        }

        public IReadOnlyList<RebaseValidationIssue> Validate(RebaseSession session) => Issues;

        public Task StartAsync(RebaseSession session, RepositoryContext context, bool autostash, CancellationToken ct = default)
        {
            if (StartFailure is not null)
                throw StartFailure;
            Started.Add((session, autostash));
            return Task.CompletedTask;
        }
    }
}
