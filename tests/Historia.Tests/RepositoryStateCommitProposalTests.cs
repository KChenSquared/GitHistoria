namespace Historia.Tests;

[Trait("tier", "git")]
public sealed class RepositoryStateCommitProposalTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;
    private readonly List<RepositoryStateService> _states = [];

    public RepositoryStateCommitProposalTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("commit-proposal-seed", b =>
        {
            b.WriteFile("a.txt", "one\n").Stage();
            b.Commit("init");
        });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _states)
            await s.DisposeAsync();
        await _host.DisposeAsync();
    }

    private (RepositoryStateService State, OffThreadDispatcher Ui) NewState()
    {
        var ctx = _host.Open(_repo.Path);
        var ui = new OffThreadDispatcher();
        var state = new RepositoryStateService(ctx, ui);
        _states.Add(state);
        return (state, ui);
    }

    [Fact]
    public void Propose_raises_once_on_the_dispatcher_with_the_text()
    {
        var (state, ui) = NewState();
        var seen = new List<string>();
        state.CommitMessageProposed += (_, e) => seen.Add(e.Message);

        state.ProposeCommitMessage("feat: x");

        Assert.Empty(seen);
        Assert.Equal(1, ui.QueuedCount);

        ui.Drain();

        Assert.Equal(["feat: x"], seen);
        Assert.Equal("feat: x", state.PendingCommitMessage);
    }

    [Fact]
    public void A_newer_proposal_replaces_the_pending_one()
    {
        var (state, ui) = NewState();

        state.ProposeCommitMessage("first");
        state.ProposeCommitMessage("second");
        ui.Drain();

        Assert.Equal("second", state.PendingCommitMessage);
    }

    [Fact]
    public void Clear_empties_the_pending_message_and_raises_nothing()
    {
        var (state, ui) = NewState();
        state.ProposeCommitMessage("feat: x");
        ui.Drain();

        var raised = 0;
        state.CommitMessageProposed += (_, _) => raised++;
        state.ClearPendingCommitMessage();
        ui.Drain();

        Assert.Null(state.PendingCommitMessage);
        Assert.Equal(0, raised);
        Assert.Equal(0, ui.QueuedCount);
    }

    [Fact]
    public void Proposing_changes_no_selection_status_or_diff_state()
    {
        var (state, ui) = NewState();
        var other = 0;
        state.StatusChanged += (_, _) => other++;
        state.SelectedCommitChanged += (_, _) => other++;
        state.OpenDiffChanged += (_, _) => other++;

        state.ProposeCommitMessage("feat: x");
        ui.Drain();

        Assert.Equal(0, other);
        Assert.Null(state.SelectedCommitId);
        Assert.Null(state.OpenDiff);
    }

    [Fact]
    public void A_proposal_with_no_listener_is_still_held()
    {
        var (state, ui) = NewState();

        state.ProposeCommitMessage("feat: held");
        ui.Drain();

        Assert.Equal("feat: held", state.PendingCommitMessage);
    }

    /// <summary>Reports no thread access, so every raise is queued until <see cref="Drain"/>.</summary>
    private sealed class OffThreadDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _queue = new();

        public int QueuedCount => _queue.Count;
        public bool CheckAccess() => false;
        public void Post(Action action) => _queue.Enqueue(action);

        public void Drain()
        {
            while (_queue.Count > 0)
                _queue.Dequeue()();
        }

        public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new NoTimer();

        private sealed class NoTimer : IUiTimer
        {
            public void Start() { }
            public void Stop() { }
            public void Dispose() { }
        }
    }

    [Fact]
    public void Clearing_a_spent_proposal_keeps_a_newer_one()
    {
        var (state, ui) = NewState();
        state.ProposeCommitMessage("feat: first");
        ui.Drain();
        var spent = state.PendingCommitMessage;

        state.ProposeCommitMessage("feat: second");
        ui.Drain();
        state.ClearPendingCommitMessage(onlyIf: spent);

        Assert.Equal("feat: second", state.PendingCommitMessage);

        state.ClearPendingCommitMessage(onlyIf: state.PendingCommitMessage);

        Assert.Null(state.PendingCommitMessage);
    }
}
