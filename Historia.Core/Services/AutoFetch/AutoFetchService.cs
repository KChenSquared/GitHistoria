using Historia.Services.Settings;
using Historia.Services.Workspace;

namespace Historia.Services.AutoFetch;

/// <inheritdoc cref="IAutoFetchService"/>
public sealed class AutoFetchService : IAutoFetchService
{
    /// <summary>§12.1.2: the quiet period that opens every <c>[AutoFetch]</c> cycle.</summary>
    internal static readonly TimeSpan InitialIdle = TimeSpan.FromMinutes(5);

    /// <summary>§12.1.2: the gap between one open repo's fetch and the next (also the trailing
    /// wait before the cycle restarts).</summary>
    internal static readonly TimeSpan PerRepoStep = TimeSpan.FromMinutes(1);

    private readonly IWorkspaceService _workspace;
    private readonly IAutoFetchHost _host;
    private readonly ISettingsService _settings;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly object _sync = new();
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _cycleCts;
    private Task? _loop;
    private TaskCompletionSource? _firstRepo;
    private RepoEntrySource? _pendingEntry;
    private int _lastCount;
    private bool _started;

    public AutoFetchService(
        IWorkspaceService workspace,
        IAutoFetchHost host,
        ISettingsService settings,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _workspace = workspace;
        _host = host;
        _settings = settings;
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));

        _settings.Changed += OnSettingsChanged;
        _workspace.OpenRepositoriesChanged += OnOpenRepositoriesChanged;
    }

    public bool IsEnabled => _settings.Current.AutoFetchEnabled;

    public void NotifyRepoEntering(RepoEntrySource source)
    {
        lock (_sync)
            _pendingEntry = source;
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_started || !IsEnabled)
                return;
            _started = true;
            _runCts = new CancellationTokenSource();
            var ct = _runCts.Token;
            _loop = Task.Run(() => RunAsync(ct));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? run;
        lock (_sync)
        {
            if (!_started)
                return;
            _started = false;
            run = _runCts;
            _runCts = null;
            _cycleCts?.Cancel();
            _firstRepo?.TrySetCanceled();
        }

        try { run?.Cancel(); }
        catch { /* already disposed */ }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.OldSettings.AutoFetchEnabled == e.NewSettings.AutoFetchEnabled)
            return;
        if (e.NewSettings.AutoFetchEnabled)
            Start();
        else
            Stop();
    }

    private void OnOpenRepositoriesChanged(object? sender, OpenRepositoriesChangedEventArgs e)
    {
        lock (_sync)
        {
            if (e.OpenRepositories.Count > 0)
                _firstRepo?.TrySetResult();

            if (!_started)
                return;

            // §12.1.2: a net add/close restarts an in-progress cycle; a pure reorder (same
            // count) does not.
            if (e.OpenRepositories.Count != _lastCount)
            {
                _lastCount = e.OpenRepositories.Count;
                _cycleCts?.Cancel();
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await WaitForStartConditionAsync(ct).ConfigureAwait(false);
            await AutoFetchLoopAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stop() / disable — expected.
        }
        catch
        {
            // §12.1: the loop must never take the app down.
        }
    }

    // §12.1.1
    private async Task WaitForStartConditionAsync(CancellationToken ct)
    {
        var open = _workspace.OpenRepositories;
        if (open.Count == 0)
        {
            var first = await WaitForFirstRepoAsync(ct).ConfigureAwait(false);
            await _host.WhenFullyLoadedAsync(first, ct).ConfigureAwait(false);

            RepoEntrySource source;
            lock (_sync)
            {
                source = _pendingEntry ?? RepoEntrySource.Opened;
                _pendingEntry = null;
            }

            if (source == RepoEntrySource.Opened)
                await SafeFetchAsync(first, ct).ConfigureAwait(false); // [InitialFetch]
        }
        else
        {
            // Wait for *every* open repo — active or not — to fully load (§3, §12.1.1).
            await Task.WhenAll(open.Select(r => _host.WhenFullyLoadedAsync(r, ct))).ConfigureAwait(false);

            var target = _workspace.Focused ?? _workspace.OpenRepositories.FirstOrDefault();
            if (target is not null)
                await SafeFetchAsync(target, ct).ConfigureAwait(false); // [InitialFetch]
        }

        lock (_sync)
            _lastCount = _workspace.OpenRepositories.Count;
    }

    private async Task<RepositoryContext> WaitForFirstRepoAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var open = _workspace.OpenRepositories;
            if (open.Count > 0)
                return open[0];

            TaskCompletionSource tcs;
            lock (_sync)
            {
                _firstRepo ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                tcs = _firstRepo;
            }

            await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
            lock (_sync)
                _firstRepo = null;
        }
    }

    // §12.1.2
    private async Task AutoFetchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            CancellationTokenSource cycle;
            lock (_sync)
            {
                _cycleCts?.Dispose();
                _cycleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cycle = _cycleCts;
            }

            var cct = cycle.Token;
            try
            {
                await _delay(InitialIdle, cct).ConfigureAwait(false);

                // Positional, not per-tab: the repo at slot `pos` when this step fires is the one
                // fetched — reordering tabs is deliberately not re-targeted (§12.1.2). The count
                // is read once per cycle; a net add/close cancels `cct` and restarts.
                var count = _workspace.OpenRepositories.Count;
                for (var pos = 0; pos < count; pos++)
                {
                    if (pos > 0)
                        await _delay(PerRepoStep, cct).ConfigureAwait(false);

                    var repos = _workspace.OpenRepositories;
                    if (pos < repos.Count)
                        await SafeFetchAsync(repos[pos], cct).ConfigureAwait(false);
                }

                await _delay(PerRepoStep, cct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Add/close restart — rebuild the cycle from the top.
            }
        }
    }

    private async Task SafeFetchAsync(RepositoryContext context, CancellationToken ct)
    {
        try
        {
            await _host.FetchAsync(context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // §12.1: a failed auto-fetch stops quietly.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _settings.Changed -= OnSettingsChanged;
        _workspace.OpenRepositoriesChanged -= OnOpenRepositoriesChanged;
        Stop();

        Task? loop;
        lock (_sync)
            loop = _loop;

        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch { /* teardown */ }
        }
    }
}
