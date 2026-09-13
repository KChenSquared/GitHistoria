namespace Historia.Services.Workspace;

/// <summary>
/// v10 graph memory footprint plan, Phase 1: no background writer (a commit-graph write, or a
/// graph disk-cache write of any kind — <c>.meta.*</c> create/append/truncate/header/order,
/// <c>.json.gz</c> save) for ANY tab may start until the focused tab is "done loading". The gate
/// is open when either no tab is focused, or the focused tab has finished both its graph's first
/// paint for the current focus (<see cref="ReportGraphSettled"/>) and
/// <see cref="RepositoryContext.FirstFullLoad"/>. It closes on every focus change and re-evaluates
/// for the newly focused tab — there is deliberately no "start after N seconds regardless"
/// fallback in the app path (removed per the plan; only test doubles use one).
/// </summary>
public interface IBackgroundWorkGate
{
    bool IsOpen { get; }

    /// <summary>Completes the next time the gate is (or becomes) open. A fresh <see cref="Task"/>
    /// each time the gate is closed — callers should re-check <see cref="IsOpen"/> or simply
    /// await this in a loop around their batch of work.</summary>
    Task WaitUntilOpenAsync(CancellationToken ct = default);

    /// <summary>Called by the graph control from its first-paint attempt's <c>finally</c> — once
    /// per focus, whether the paint succeeded, was cancelled, or failed. A no-op if
    /// <paramref name="context"/> is not the tab currently being waited on (a stale report from a
    /// focus that has since moved on).</summary>
    void ReportGraphSettled(RepositoryContext context);
}

/// <inheritdoc cref="IBackgroundWorkGate"/>
public sealed class BackgroundWorkGate : IBackgroundWorkGate, IDisposable
{
    private readonly IWorkspaceService _workspace;
    private readonly object _sync = new();
    private RepositoryContext? _pending;
    private bool _graphSettled;
    private bool _open = true;
    private TaskCompletionSource _openTcs = Done();

    private static TaskCompletionSource Done()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    public BackgroundWorkGate(IWorkspaceService workspace)
    {
        _workspace = workspace;
        _workspace.FocusedRepositoryChanged += OnFocusChanged;
        // Seed with whatever is already focused (e.g. a restored session's tab focused before
        // this singleton was constructed) rather than assuming "no tab focused".
        OnFocusChanged(this, new FocusedRepositoryChangedEventArgs(null, _workspace.Focused));
    }

    public bool IsOpen { get { lock (_sync) return _open; } }

    public Task WaitUntilOpenAsync(CancellationToken ct = default)
    {
        Task t;
        lock (_sync) t = _openTcs.Task;
        return ct.CanBeCanceled ? t.WaitAsync(ct) : t;
    }

    public void ReportGraphSettled(RepositoryContext context)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(context, _pending))
                return; // a stale report from a focus that has since moved on
            _graphSettled = true;
        }
        _ = TryOpenAsync(context);
    }

    private void OnFocusChanged(object? sender, FocusedRepositoryChangedEventArgs e)
    {
        lock (_sync)
        {
            _pending = e.New;
            _graphSettled = false;
            if (_open)
            {
                _open = false;
                _openTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        Diagnostics.PerfLog.Mark($"background writes: gate closed (focused={e.New?.DisplayName ?? "(none)"})");

        if (e.New is null)
        {
            Open("(none)");
            return;
        }
        _ = TryOpenAsync(e.New);
    }

    private async Task TryOpenAsync(RepositoryContext context)
    {
        try { await context.FirstFullLoad.WaitAsync(context.Lifetime).ConfigureAwait(false); }
        catch { return; } // tab closed before it finished loading — nothing left to open for
        lock (_sync)
        {
            if (!ReferenceEquals(context, _pending) || !_graphSettled)
                return; // focus moved on, or the graph hasn't settled yet — a later call opens it
        }
        Open(context.DisplayName);
    }

    private void Open(string focusedLabel)
    {
        TaskCompletionSource tcs;
        lock (_sync)
        {
            if (_open)
                return;
            _open = true;
            tcs = _openTcs;
        }
        tcs.TrySetResult();
        Diagnostics.PerfLog.Mark($"background writes: gate open (focused={focusedLabel})");
    }

    public void Dispose() => _workspace.FocusedRepositoryChanged -= OnFocusChanged;
}
