namespace Historia.Tests;

/// <summary>Minimal <see cref="IWorkspaceService"/> double: <see cref="BackgroundWorkGate"/> only
/// ever reads <see cref="Focused"/> and raises/observes <see cref="FocusedRepositoryChanged"/>,
/// so every other member is unused by these tests.</summary>
internal sealed class FakeWorkspace : IWorkspaceService
{
    public IReadOnlyList<RepositoryContext> OpenRepositories => [];
    public RepositoryContext? Focused { get; private set; }

    public event EventHandler<FocusedRepositoryChangedEventArgs>? FocusedRepositoryChanged;
    public event EventHandler<OpenRepositoriesChangedEventArgs>? OpenRepositoriesChanged { add { } remove { } }

    public void SetFocused(RepositoryContext? context)
    {
        var old = Focused;
        Focused = context;
        FocusedRepositoryChanged?.Invoke(this, new FocusedRepositoryChangedEventArgs(old, context));
    }

    public Task<RepositoryContext> OpenAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RepositoryContext> InitAsync(string path, string? initialBranch, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RepositoryContext> CloneAsync(string url, string targetDirectory, Historia.Services.Workspace.CloneOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RepositoryContext> OpenSubmoduleAsync(RepositoryContext owner, SubmoduleInfo submodule, CancellationToken ct = default) => throw new NotSupportedException();
    public Task CloseAsync(RepositoryContext context) => throw new NotSupportedException();
    public void Focus(RepositoryContext context) => SetFocused(context);
    public void EnsureInitialized(RepositoryContext context) { }
    public void Move(RepositoryContext context, int newIndex) { }
    public Task RestoreSessionAsync(CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>The background-write gate. No commit-graph
/// or graph disk-cache write may start until the focused tab is "done loading" — first graph
/// paint reported for the current focus AND <see cref="RepositoryContext.FirstFullLoad"/>.</summary>
[Trait("tier", "git")]
public sealed class BackgroundWorkGateTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo = TestRepositoryBuilder.FromTemplate("empty-repo", _ => { });

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public void Open_with_no_focused_tab()
    {
        var ws = new FakeWorkspace();
        var gate = new BackgroundWorkGate(ws);
        Assert.True(gate.IsOpen);
    }

    [Fact]
    public async Task Closed_until_graph_settled_and_first_full_load()
    {
        var ws = new FakeWorkspace();
        var gate = new BackgroundWorkGate(ws);
        await using var ctx = _host.Open(_repo.Path);

        ws.SetFocused(ctx);
        Assert.False(gate.IsOpen);

        // FirstFullLoad alone isn't enough.
        ctx.MarkFirstFullLoadDone();
        await Task.Delay(50);
        Assert.False(gate.IsOpen);

        // Nor is graph-settled alone, on a DIFFERENT context (never focused).
        await using var other = _host.Open(_repo.Path);
        gate.ReportGraphSettled(other);
        await Task.Delay(50);
        Assert.False(gate.IsOpen);

        gate.ReportGraphSettled(ctx);
        await Task.Delay(50);
        Assert.True(gate.IsOpen);
    }

    [Fact]
    public async Task Closes_on_every_focus_change()
    {
        var ws = new FakeWorkspace();
        var gate = new BackgroundWorkGate(ws);
        await using var ctx1 = _host.Open(_repo.Path);
        await using var ctx2 = _host.Open(_repo.Path);

        ws.SetFocused(ctx1);
        ctx1.MarkFirstFullLoadDone();
        gate.ReportGraphSettled(ctx1);
        await Task.Delay(50);
        Assert.True(gate.IsOpen);

        ws.SetFocused(ctx2);
        Assert.False(gate.IsOpen);

        // A late report for the tab that lost focus must not reopen the gate for ctx2.
        gate.ReportGraphSettled(ctx1);
        await Task.Delay(50);
        Assert.False(gate.IsOpen);
    }

    [Fact]
    public async Task A_waiter_is_released_when_the_gate_opens()
    {
        var ws = new FakeWorkspace();
        var gate = new BackgroundWorkGate(ws);
        await using var ctx = _host.Open(_repo.Path);

        ws.SetFocused(ctx);
        var wait = gate.WaitUntilOpenAsync();
        Assert.False(wait.IsCompleted);

        ctx.MarkFirstFullLoadDone();
        gate.ReportGraphSettled(ctx);

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(wait.IsCompletedSuccessfully);
    }
}
