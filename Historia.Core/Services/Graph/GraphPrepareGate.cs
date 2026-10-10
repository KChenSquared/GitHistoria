namespace Historia.Services.Graph;

/// <summary>
/// Three-tier priority gate for graph layout preparation (one per <see cref="Workspace.RepositoryContext"/>).
/// Graph turns keep their order (foreground wins, background crawl yields to foreground, disk spill yields to both)
/// without blocking branch/tag enumeration, diff, or status.
/// </summary>
public sealed class GraphPrepareGate(string displayName = "")
{
    private const double GateLogThresholdMs = 5.0;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _displayName = displayName;

    private int _foregroundWaiters;
    private int _backgroundWaiters;
    private volatile string? _activeHolder;
    private volatile string? _lastHolder;

    /// <summary>Test seam: foreground and background callers currently queued for the gate.</summary>
    internal (int Foreground, int Background) WaitersForTesting =>
        (Volatile.Read(ref _foregroundWaiters), Volatile.Read(ref _backgroundWaiters));

    /// <summary>
    /// Longest a background or spill caller will keep yielding to higher priority work
    /// before taking its turn. Defaults to 3 seconds.
    /// </summary>
    public TimeSpan MaxYield { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Highest priority execution: foreground caller always takes the gate ahead of background and spill.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        Func<Task<T>> work,
        CancellationToken ct = default,
        string? label = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "")
    {
        var waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _foregroundWaiters);
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _foregroundWaiters);
        }

        return await HoldAsync("[foreground]", waitStart, label, callerMember, callerFile, work).ConfigureAwait(false);
    }

    public Task<T> ExecuteAsync<T>(
        Func<T> work,
        CancellationToken ct = default,
        string? label = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "")
        => ExecuteAsync(() => Task.FromResult(work()), ct, label, callerMember, callerFile);

    public Task ExecuteAsync(
        Action work,
        CancellationToken ct = default,
        string? label = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "")
        => ExecuteAsync(() => { work(); return true; }, ct, label, callerMember, callerFile);

    public Task ExecuteAsync(
        Func<Task> work,
        CancellationToken ct = default,
        string? label = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "")
        => ExecuteAsync(async () => { await work().ConfigureAwait(false); return true; }, ct, label, callerMember, callerFile);

    /// <summary>
    /// Medium priority execution: background crawl yields to queued foreground callers,
    /// up to <see cref="MaxYield"/>, before taking its turn.
    /// </summary>
    public async Task<T> ExecuteBackgroundAsync<T>(
        Func<Task<T>> work,
        CancellationToken ct = default,
        string? label = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "")
    {
        var yieldStart = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _backgroundWaiters);
        try
        {
            while (true)
            {
                while (Volatile.Read(ref _foregroundWaiters) > 0
                       && System.Diagnostics.Stopwatch.GetElapsedTime(yieldStart) < MaxYield)
                {
                    await Task.Delay(20, ct).ConfigureAwait(false);
                }

                await _gate.WaitAsync(ct).ConfigureAwait(false);

                if (Volatile.Read(ref _foregroundWaiters) > 0
                    && System.Diagnostics.Stopwatch.GetElapsedTime(yieldStart) < MaxYield)
                {
                    _gate.Release();
                    await Task.Delay(20, ct).ConfigureAwait(false);
                    continue;
                }
                break;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _backgroundWaiters);
        }

        return await HoldAsync("[background]", yieldStart, label, callerMember, callerFile, work).ConfigureAwait(false);
    }

    public Task<T> ExecuteBackgroundAsync<T>(
        Func<T> work,
        CancellationToken ct = default,
        string? label = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "")
        => ExecuteBackgroundAsync(() => Task.FromResult(work()), ct, label, callerMember, callerFile);

    public Task ExecuteBackgroundAsync(
        Action work,
        CancellationToken ct = default,
        string? label = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "")
        => ExecuteBackgroundAsync(() => { work(); return true; }, ct, label, callerMember, callerFile);

    public Task ExecuteBackgroundAsync(
        Func<Task> work,
        CancellationToken ct = default,
        string? label = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "")
        => ExecuteBackgroundAsync(async () => { await work().ConfigureAwait(false); return true; }, ct, label, callerMember, callerFile);

    /// <summary>
    /// Lowest priority execution: background history spill yields to foreground callers AND
    /// background crawl callers, up to <see cref="MaxYield"/>, before taking its turn.
    /// </summary>
    public async Task<T> ExecuteSpillAsync<T>(
        Func<Task<T>> work,
        CancellationToken ct = default,
        string? label = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "")
    {
        var yieldStart = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            while ((Volatile.Read(ref _foregroundWaiters) > 0 || Volatile.Read(ref _backgroundWaiters) > 0)
                   && System.Diagnostics.Stopwatch.GetElapsedTime(yieldStart) < MaxYield)
            {
                await Task.Delay(20, ct).ConfigureAwait(false);
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);

            if ((Volatile.Read(ref _foregroundWaiters) > 0 || Volatile.Read(ref _backgroundWaiters) > 0)
                && System.Diagnostics.Stopwatch.GetElapsedTime(yieldStart) < MaxYield)
            {
                _gate.Release();
                await Task.Delay(20, ct).ConfigureAwait(false);
                continue;
            }
            break;
        }

        return await HoldAsync("[spill]", yieldStart, label, callerMember, callerFile, work).ConfigureAwait(false);
    }

    private async Task<T> HoldAsync<T>(
        string tier,
        long waitStart,
        string? label,
        string callerMember,
        string callerFile,
        Func<Task<T>> work)
    {
        var waitMs = System.Diagnostics.Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
        var waitedBehind = _lastHolder ?? _activeHolder ?? "unknown";
        if (waitMs > GateLogThresholdMs && PerfLog.Enabled)
            PerfLog.Mark($"graph prepare wait {waitMs:F1}ms ({_displayName}) behind {waitedBehind}");

        var resolvedLabel = ResolveCallerLabel(label, callerMember, callerFile);
        var holderDesc = PerfLog.Enabled ? $"{tier} {resolvedLabel}" : null;
        if (holderDesc is not null)
            _activeHolder = holderDesc;

        var holdStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            var holdMs = System.Diagnostics.Stopwatch.GetElapsedTime(holdStart).TotalMilliseconds;
            if (holdMs > GateLogThresholdMs && PerfLog.Enabled)
                PerfLog.Mark($"graph prepare hold {tier} {resolvedLabel} {holdMs:F1}ms ({_displayName})");
            if (holderDesc is not null)
            {
                _lastHolder = holderDesc;
                _activeHolder = null;
            }
            _gate.Release();
        }
    }

    public Task<T> ExecuteSpillAsync<T>(
        Func<T> work,
        CancellationToken ct = default,
        string? label = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "")
        => ExecuteSpillAsync(() => Task.FromResult(work()), ct, label, callerMember, callerFile);

    public Task ExecuteSpillAsync(
        Action work,
        CancellationToken ct = default,
        string? label = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "")
        => ExecuteSpillAsync(() => { work(); return true; }, ct, label, callerMember, callerFile);

    public Task ExecuteSpillAsync(
        Func<Task> work,
        CancellationToken ct = default,
        string? label = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "")
        => ExecuteSpillAsync(async () => { await work().ConfigureAwait(false); return true; }, ct, label, callerMember, callerFile);

    private static string ResolveCallerLabel(string? explicitLabel, string callerMember, string callerFile)
    {
        if (!string.IsNullOrEmpty(explicitLabel))
            return explicitLabel;
        if (!string.IsNullOrEmpty(callerFile))
            return $"{Path.GetFileNameWithoutExtension(callerFile)}.{callerMember}";
        if (!string.IsNullOrEmpty(callerMember))
            return callerMember;
        return "unknown";
    }
}
